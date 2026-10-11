using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App;

public partial class MainWindow
{
    // Stay disabled until persisted settings are loaded (including a previous opt-out).
    private int _automaticTelemetryEnabled;
    private bool _changingTelemetrySelection;
    private bool AutomaticTelemetryUploadEnabled => Volatile.Read(ref _automaticTelemetryEnabled) != 0;

    private void ApplyTelemetryPreference(bool enabled)
    {
        Volatile.Write(ref _automaticTelemetryEnabled, enabled ? 1 : 0);
        Volatile.Read(ref _telemetryUploadClient)?.SetAutomaticUploadEnabled(enabled);
        if (!_isClosed) RefreshBugReportButton();
    }

    private void RefreshTelemetrySettingsPresentation()
    {
        _changingTelemetrySelection = true;
        try { SettingsTelemetryCheckBox.IsChecked = _settings.AutomaticTelemetryUpload; }
        finally { _changingTelemetrySelection = false; }
        RefreshBugReportButton();
    }

    private async void OnTelemetrySelectionChanged(object sender, RoutedEventArgs args)
    {
        if (_changingTelemetrySelection || !_startupReady || SettingsBusy || _settingsOperations.IsStopping) return;
        var enabled = SettingsTelemetryCheckBox.IsChecked == true;
        if (enabled == _settings.AutomaticTelemetryUpload) return;
        SettingsTelemetryErrorText.Visibility = Visibility.Collapsed;
        // Opt-out takes effect before disk I/O. Opt-in starts only after durable save.
        if (!enabled) ApplyTelemetryPreference(false);
        string? error = null;
        await RunSettingsOperationAsync(async () =>
        {
            var snapshot = _settings.Clone();
            snapshot.AutomaticTelemetryUpload = enabled;
            try
            {
                await SettingsPersistence.SaveAsync(_settingsRepository, snapshot);
                _settings.CopyFrom(snapshot);
            }
            catch (Exception exception) { error = exception.Message; }
            ApplyTelemetryPreference(_settings.AutomaticTelemetryUpload);
            return error == null;
        });
        if (_isClosed) return;
        if (error != null)
        {
            SettingsTelemetryErrorText.Text = LocalizationManager.Format("Settings.SaveErrorFormat", error);
            SettingsTelemetryErrorText.Visibility = Visibility.Visible;
        }
        if (AutomaticTelemetryUploadEnabled) StartPendingBugReportRetry();
    }

    private Task<BugReportCandidate?> QueueEpisodeReport(Task<ProcessedEpisode> processing,
        LandingEpisodeEventArgs episode, DateTime timestampUtc)
    {
        lock (_bugReportPersistenceGate)
        {
            var predecessor = _bugReportPersistenceTask;
            var report = Task.Run(async () =>
            {
                try { await predecessor.ConfigureAwait(false); }
                catch { /* A previous report failure must not discard later reports. */ }
                return await BuildEpisodeReportAsync(processing, episode, timestampUtc).ConfigureAwait(false);
            });
            _bugReportPersistenceTask = report;
            return report;
        }
    }

    private async Task<BugReportCandidate?> BuildEpisodeReportAsync(Task<ProcessedEpisode> processing,
        LandingEpisodeEventArgs episode, DateTime timestampUtc)
    {
        TelemetryDecisionJournal journal;
        TelemetryDecisionSnapshot? analysisDecisions = null;
        IReadOnlyList<LandingRecord> records;
        var time = episode.Samples.Count == 0 ? 0 : episode.Samples[episode.Samples.Count - 1].SimulationTimeSeconds;
        try
        {
            var processed = await processing.ConfigureAwait(false);
            analysisDecisions = processed.Decisions;
            journal = new TelemetryDecisionJournal(analysisDecisions);
            records = processed.ReportRecords;
        }
        catch (Exception exception)
        {
            journal = episode.DecisionJournal ?? new TelemetryDecisionJournal();
            journal.Record(time, "analysis", "analysis_failed", exception.GetType().Name);
            records = Array.Empty<LandingRecord>();
        }

        try
        {
            var site = await AwaitEpisodeFacilitiesAsync(episode).ConfigureAwait(false);
            var candidateCount = site.FinalRunways.Count + site.PreliminaryRunways.Count;
            // Airport replies can arrive after the analyzer snapshot. Preserve
            // these real lookup decisions without duplicating earlier entries.
            if (episode.DecisionJournal != null && !ReferenceEquals(journal, episode.DecisionJournal))
                foreach (var entry in episode.DecisionJournal.Snapshot().Entries.Where(entry =>
                    entry.Component.StartsWith("runway.", StringComparison.Ordinal) &&
                    analysisDecisions?.Entries.Contains(entry) != true))
                    journal.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, entry.Details);
            journal.Record(time, "runway", episode.IsDiagnosticOnly ? "lookup_not_required" : candidateCount > 0 ? "facilities_received" : "facilities_unavailable",
                episode.IsDiagnosticOnly ? "Rejected ground transition retained for diagnostics only; no landing or runway lookup." :
                candidateCount > 0 ? "Used this episode's preliminary and final simulator facility snapshots." :
                _lifetimeCancellation.IsCancellationRequested ? "Application closed before runway geometry became available." :
                "Simulator returned no usable geometry or the facility lookup expired.",
                new Dictionary<string, double> { ["candidates"] = candidateCount });
            foreach (var record in records)
            {
                var runwayJournal = new TelemetryDecisionJournal();
                LandingSiteResolver.Apply(record, site, runwayJournal);
                foreach (var entry in runwayJournal.Snapshot().Entries)
                {
                    var details = entry.Details.ToDictionary(pair => pair.Key, pair => pair.Value);
                    details["contact_number"] = record.ContactNumber;
                    details["relative_contact_time"] = 1;
                    journal.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, details);
                }
            }
            // Resolve the archived copies AND durable history from the same
            // result. A fresh ARP reply remains useful when no runway matched.
            try
            {
                if (records.Count > 0) LandingSiteMetadataPersistence.Save(_repository, _landingRepositoryGate, records);
            }
            catch (Exception exception)
            {
                journal.Record(time, "storage", "site_metadata_save_failed", exception.GetType().Name);
            }
            foreach (var record in records) LandingReplayDiagnostics.Record(record, records, journal);
        }
        catch (Exception exception)
        {
            journal.Record(time, "replay", "diagnostics_failed", exception.GetType().Name);
        }

        var candidate = new BugReportCandidate(episode.EpisodeId, episode.Samples, episode.Simulator,
            episode.AircraftTitle, episode.AircraftType, episode.AircraftModel,
            episode.ControlInputSources, records, journal.Snapshot());
        if (AutomaticTelemetryUploadEnabled && episode.Samples.Count > 0)
        {
            try
            {
                // This worker is part of the durable shutdown boundary, including
                // episodes completed by recorder.Dispose. No UI/HTTP waits here.
                var path = AutomaticTelemetryArchive.TryCreate(_bugReportRepository, candidate, timestampUtc);
                if (path != null && !_isClosed) StartPendingBugReportRetry();
            }
            catch (Exception exception) { Debug.WriteLine(exception); }
        }
        return candidate;
    }

    private async Task<EpisodeFacilitySnapshot> AwaitEpisodeFacilitiesAsync(LandingEpisodeEventArgs episode)
    {
        if (episode.FacilitiesTask.IsCompleted) return await episode.FacilitiesTask.ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var wait = Task.Delay(TimeSpan.FromSeconds(60), deadline.Token);
        await Task.WhenAny(episode.FacilitiesTask, wait).ConfigureAwait(false);
        if (episode.FacilitiesTask.IsCompleted)
        {
            deadline.Cancel();
            return await episode.FacilitiesTask.ConfigureAwait(false);
        }
        return episode.InitialFacilities;
    }
}
