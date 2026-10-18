using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LandingStats.App;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class AutomaticTelemetrySettingsRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-auto-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var repository = new ApplicationSettingsRepository(settingsPath);
            Check(repository.Load().AutomaticTelemetryUpload, "fresh install default");
            File.WriteAllText(settingsPath, "{\"schemaVersion\":3,\"language\":\"en\",\"startWithSimulator\":false}");
            var settings = repository.Load();
            Check(settings.AutomaticTelemetryUpload, "upgrade with absent setting defaults on");
            settings.AutomaticTelemetryUpload = false;
            repository.Save(settings);
            var loaded = repository.Load();
            Check(!loaded.AutomaticTelemetryUpload && !loaded.Clone().AutomaticTelemetryUpload, "explicit opt-out survives restart and snapshots");
            var copy = new ApplicationSettings();
            copy.CopyFrom(loaded);
            Check(!copy.AutomaticTelemetryUpload, "settings rollback/copy preserves opt-out");
            Check(File.ReadAllText(settingsPath).Contains("\"automaticTelemetryUpload\":false"), "false must be serialized, not omitted");

            using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!repository.TryLoad(out var unreadable) && !unreadable.AutomaticTelemetryUpload,
                    "temporarily unreadable opt-out cannot enable automatic telemetry");
            Check(!repository.Load().AutomaticTelemetryUpload, "locked read leaves the saved opt-out intact");
            foreach (var invalid in new[] { "{invalid", "null" })
            {
                File.WriteAllText(settingsPath, invalid);
                Check(!repository.TryLoad(out var corrupt) && !corrupt.AutomaticTelemetryUpload,
                    "corrupt or null settings cannot grant automatic telemetry permission");
            }
            repository.Save(loaded);

            var source = (BugReportCandidate)typeof(Program).GetMethod("BugReportFixture", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { 123L, "journal-contract" })!;
            var journal = new TelemetryDecisionJournal();
            journal.Record(123, "analysis", "fixture", "Synthetic contract fixture; not user telemetry.",
                new System.Collections.Generic.Dictionary<string, double> { ["value"] = 2.5 });
            journal.Record(123, "replay", "fixture_empty_details", "Empty details must stay an object.");
            var candidate = new BugReportCandidate(source.EpisodeId, source.Samples, source.Simulator,
                source.AircraftTitle, source.AircraftType, source.AircraftModel, source.ControlInputSources,
                source.Records, journal.Snapshot());
            VerifyPresentation(candidate);
            var path = new BugReportRepository(root).Create(candidate, DateTime.UtcNow, automatic: true);
            using (var zip = ZipFile.OpenRead(path))
            {
                Check(zip.Entries.Count == 4 && zip.GetEntry("decisions.json") != null, "complete automatic diagnostic bundle");
                using var reader = new StreamReader(zip.GetEntry("decisions.json")!.Open(), Encoding.UTF8);
                var json = reader.ReadToEnd();
                Check(json.Contains("\"details\":{\"value\":2.5}") && json.Contains("\"details\":{}"), "wire format uses JSON objects for details");
                Check(json.Contains("\"episode_id\":123"), "journal tied to results/session episode");
            }
            // Optional output for the local Python receiver contract test; never uploaded.
            var export = Environment.GetEnvironmentVariable("MSFSLS_TEST_ARCHIVE_OUTPUT");
            if (!string.IsNullOrEmpty(export)) File.Copy(path, export, true);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifyPresentation(BugReportCandidate candidate)
    {
        // No MainWindow constructor: no real history, settings, simulator or network services.
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        var button = new Button();
        var status = new TextBlock();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        Set("ReportBugButton", button);
        Set("UploadStatusText", status);
        Set("_runtimeReportEpisode", Guid.NewGuid());
        Set("_startupReady", true);
        var refresh = typeof(MainWindow).GetMethod("RefreshBugReportButton", flags)!;
        Set("_automaticTelemetryEnabled", 1);
        refresh.Invoke(window, null);
        Check(button.Visibility == Visibility.Collapsed && status.Visibility == Visibility.Collapsed,
            "automatic reporting hides both manual button and footer notifications");
        Set("_automaticTelemetryEnabled", 0);
        refresh.Invoke(window, null);
        Check(button.Visibility == Visibility.Visible && button.IsEnabled && status.Visibility == Visibility.Visible,
            "opt-out restores manual reporting for retained landing");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
