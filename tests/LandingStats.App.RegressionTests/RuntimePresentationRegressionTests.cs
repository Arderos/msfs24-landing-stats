using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RuntimePresentationRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "rp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var oldMode = Application.Current.ShutdownMode; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var oldContext = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null; LandingRuntime? runtime = null; var done = false;
        try
        {
            window = new MainWindow(); // Never shown or started.
            var repository = new LandingRepository(Path.Combine(root, "h")); var gate = new object();
            var records = Enumerable.Range(1, 40).Select(number => new LandingRecord
            { Id = Guid.NewGuid().ToString("N"), AircraftTitle = "UI recovery fixture " + number, TimestampUtc = new DateTime(2026, 1, 1, 0, number, 0, DateTimeKind.Utc) }).ToArray();
            foreach (var record in records) repository.Save(record);
            Set(window, "_repository", repository); Set(window, "_landingRepositoryGate", gate);
            Set(window, "_historyLoading", false);
            runtime = new LandingRuntime(Path.Combine(root, "r"), AppDomain.CurrentDomain.BaseDirectory, Path.Combine(root, "i"), repository, gate,
                (_, __) => Task.CompletedTask, (_, __) => { });
            Set(window, "_runtime", runtime);
            var episodes = records.Select((record, index) => new RuntimeEpisodeNotice(new RuntimeEpisodeUpdate(Guid.NewGuid(),
                ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { record }, new TelemetryDecisionJournal().Snapshot()),
                new BugReportCandidate(index + 1, Array.Empty<TelemetrySample>(), "fixture", "fixture", "fixture", "fixture", Array.Empty<string>(), new[] { record }),
                record.TimestampUtc, index + 1, false))).ToArray();
            var current = episodes.Last(); var status = new RecorderStatusEventArgs(RecorderState.Connected, "Recorder.Connected");
            var snapshot = new RuntimePresentationSnapshot(1, status, false, current.Episode, current.Ordinal, false,
                episodes.Skip(8).ToArray(), Array.Empty<SupervisionSnapshot>(), Guid.Empty, null);
            var apply = (Func<RuntimePresentationSnapshot, CancellationToken, Task>)Delegate.CreateDelegate(typeof(Func<RuntimePresentationSnapshot, CancellationToken, Task>), window,
                typeof(MainWindow).GetMethod("ApplyRuntimeSnapshotAsync", Private)!);
            var recording = new RuntimePresentationSnapshot(0, new RecorderStatusEventArgs(RecorderState.Recording, "Recorder.ContactRollout"),
                false, current.Episode, current.Ordinal, false, Array.Empty<RuntimeEpisodeNotice>(), Array.Empty<SupervisionSnapshot>(), Guid.Empty, null);
            var pending = Task.Run(() => apply(recording, CancellationToken.None));
            Pump(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Check(Get<RecorderState>(window, "_lastRecorderState") == RecorderState.Recording, "Active capture was not presented as Recording.");
            pending = Task.Run(() => apply(snapshot, CancellationToken.None));
            Pump(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            typeof(MainWindow).GetMethod("RefreshLocalizedPresentation", Private)!.Invoke(window, Array.Empty<object>());
            Check(Get<RecorderState>(window, "_lastRecorderState") == RecorderState.Connected &&
                Get<TextBlock>(window, "RecorderModeText").Text == LocalizationManager.Text("Recorder.Armed"),
                "Saved landing retained or restored Recording after presentation refresh.");
            Check(Get<LandingRecord[]>(window, "_landings").Length == 40, "Coalesced notices lost previously committed history.");
            Check(Get<Guid>(window, "_runtimeReportEpisode") == current.Episode, "Window did not expose the active committed report.");
            Check(Get<string>(window, "_connectionStatusKey") == "Recorder.LandingSaved", "Completed episode did not show its saved status.");
            Set(window, "_startupReady", true);
            typeof(MainWindow).GetMethod("RefreshBugReportButton", Private)!.Invoke(window, Array.Empty<object>());
            Check(Get<Button>(window, "ReportBugButton").Visibility == Visibility.Visible && typeof(MainWindow).Assembly.GetType("LandingStats.App.Telemetry.SimConnectLandingRecorder") == null,
                "Window still owns a simulator or cannot offer a committed report.");
            Set(window, "_startupReady", false); // Prevent any real OAuth/cloud path in this fixture.
            var next = new RuntimePresentationSnapshot(2, status, true, Guid.NewGuid(), 41, false, snapshot.Episodes.ToArray(), Array.Empty<SupervisionSnapshot>(), Guid.Empty, null);
            pending = Task.Run(() => apply(next, CancellationToken.None)); Pump(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Check(Get<Guid>(window, "_runtimeReportEpisode") == Guid.Empty, "A previous landing report survived the new active episode boundary.");
            Check(Get<string>(window, "_connectionStatusKey") == status.MessageKey,
                "Coalesced capture transitions left the previous landing's saved status on a new active episode.");
            // This final metadata update is absent from the bounded notice set.
            // A later history publication must still recover it from storage.
            records[0].Airport = "EVICTED-FINAL"; repository.Save(records[0]);
            // Trigger the refresh through another record; the updated first
            // record deliberately has no surviving presentation notice.
            var unseen = new RuntimeEpisodeNotice(new RuntimeEpisodeUpdate(Guid.NewGuid(),
                ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { records[1] }, new TelemetryDecisionJournal().Snapshot()),
                null, DateTime.UtcNow, 42, false));
            var bounded = new RuntimePresentationSnapshot(3, status, true, next.ActiveEpisode, next.ActiveOrdinal, false,
                snapshot.Episodes.Skip(1).Concat(new[] { unseen }).ToArray(), Array.Empty<SupervisionSnapshot>(), Guid.Empty, null);
            pending = Task.Run(() => apply(bounded, CancellationToken.None)); Pump(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Check(Get<LandingRecord[]>(window, "_landings").Single(value => value.Id == records[0].Id).Airport == "EVICTED-FINAL",
                "A full notice projection preserved stale metadata for an evicted episode.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            pending = Task.Run(() => apply(snapshot, cancelled.Token)); Pump(() => pending.IsCompleted);
            try { pending.GetAwaiter().GetResult(); throw new InvalidOperationException("Cancelled UI generation was applied."); }
            catch (OperationCanceledException) { }
            Check(Get<Guid>(window, "_runtimeReportEpisode") == Guid.Empty, "Retired presentation reintroduced an old report.");
            var downloaded = new LandingRecord { Id = Guid.NewGuid().ToString("N"), TimestampUtc = DateTime.UtcNow, AircraftTitle = "Cloud coalescing fixture" };
            repository.Save(downloaded);
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            projection.Cloud(new RuntimeCloudNotice(1, DateTime.UtcNow, true));
            projection.Cloud(new RuntimeCloudNotice(2, DateTime.UtcNow, false, new CloudSyncSummary { LandingConflicts = 2 }));
            pending = Task.Run(() => apply(projection.Snapshot, CancellationToken.None)); Pump(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Check(Get<LandingRecord[]>(window, "_landings").Length == 41 && Get<long>(window, "_runtimeCloudHistoryRevision") == 1,
                "A later unchanged cloud sync erased the unseen history revision.");
            Check(!string.IsNullOrWhiteSpace(Get<string?>(window, "_googleDriveWarningDetail")), "Background cloud completion lost its conflict warning.");
            done = true;
        }
        finally
        {
            if (window != null)
            {
                Set(window, "_isClosed", true);
                typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, Array.Empty<object>());
                var history = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!; Pump(() => history.IsCompleted);
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
                window.Close();
            }
            if (runtime != null) { var stop = runtime.StopAsync(); Pump(() => stop.IsCompleted); stop.GetAwaiter().GetResult(); }
            Application.Current.Resources.MergedDictionaries.Remove(resources); Application.Current.ShutdownMode = oldMode;
            SynchronizationContext.SetSynchronizationContext(oldContext);
            var diagnostics = EpisodeTimeline.FlushAsync(); Pump(() => diagnostics.IsCompleted);
            if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    public static void RunLandingPublication()
    {
        PendingAnalysisProjection();
        var root = Path.Combine(Path.GetTempPath(), "rph-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var resources = Review089UiRegressionTests.LoadApplicationResources(); Application.Current.Resources.MergedDictionaries.Add(resources);
        var oldMode = Application.Current.ShutdownMode; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var oldContext = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null; SemaphoreSlim? details = null; var detailsHeld = false; var done = false;
        try
        {
            window = new MainWindow(); // Never shown: all reads/writes use the fixture repository.
            var repository = new LandingRepository(Path.Combine(root, "h")); var gate = new object();
            Set(window, "_repository", repository); Set(window, "_landingRepositoryGate", gate); Set(window, "_historyLoading", false);
            var record = new LandingRecord { Id = Guid.NewGuid().ToString("N"), AircraftTitle = "Publication fixture", Airport = "EARLY", TimestampUtc = DateTime.UtcNow };
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = 0, LatitudeDegrees = 42, LongitudeDegrees = 23,
                PlaneAltitudeFeet = 1000, GroundAltitudeFeet = 900, HeadingTrueDegrees = 0, GForce = 1 });
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = .1, LatitudeDegrees = 42.0001, LongitudeDegrees = 23,
                PlaneAltitudeFeet = 1000, GroundAltitudeFeet = 900, HeadingTrueDegrees = 0, GForce = 1 });
            foreach (var point in record.Series)
            {
                point.RawControllerYAxisPercent = Array.Empty<double>(); point.RawControllerYAxisValid = Array.Empty<bool>(); point.RawControllerYAxisAgeSeconds = Array.Empty<double>();
                point.RawControllerAxesPercent = Array.Empty<double>(); point.RawControllerAxesValid = Array.Empty<bool>(); point.RawControllerAxesAgeSeconds = Array.Empty<double>();
            }
            repository.Save(record);
            var episode = Guid.NewGuid(); var status = new RecorderStatusEventArgs(RecorderState.Connected, "Recorder.ConnectedGate");
            var apply = (Func<RuntimePresentationSnapshot, CancellationToken, Task>)Delegate.CreateDelegate(typeof(Func<RuntimePresentationSnapshot, CancellationToken, Task>), window,
                typeof(MainWindow).GetMethod("ApplyRuntimeSnapshotAsync", Private)!);
            RuntimeEpisodeNotice Notice(Guid id, LandingRecord value, bool reportReady) => new RuntimeEpisodeNotice(new RuntimeEpisodeUpdate(id,
                ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { value }, new TelemetryDecisionJournal().Snapshot()),
                reportReady ? new BugReportCandidate(1, Array.Empty<TelemetrySample>(), "fixture", "fixture", "fixture", "fixture", Array.Empty<string>(), new[] { value }) : null,
                value.TimestampUtc, 1, false));
            void Present(long revision, Guid active, params RuntimeEpisodeNotice[] notices)
            {
                var snapshot = new RuntimePresentationSnapshot(revision, status, false, active, revision, false, notices, Array.Empty<SupervisionSnapshot>(), Guid.Empty, null);
                var applying = Task.Run(() => apply(snapshot, CancellationToken.None)); Pump(() => applying.IsCompleted); applying.GetAwaiter().GetResult();
            }
            void DrainDetails()
            {
                // Same-record refresh keeps _detailLoading false. Wait for the
                // actual selection coroutine to finish applying preparation,
                // including a second task scheduled after its storage await.
                Pump(() => Get<CancellationTokenSource?>(window, "_detailSelectionCancellation") == null &&
                    ((Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!).IsCompleted);
                var barrier = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Pump(() => barrier.Task.IsCompleted); barrier.Task.GetAwaiter().GetResult();
            }
            var list = Get<ListBox>(window, "LandingHistoryList");
            Check(Get<TextBox>(window, "SessionFilterBox").Text.Length == 0, "Publication fixture must use an empty history filter.");
            details = Get<SemaphoreSlim>(window, "_detailReadQueue"); details.Wait(); detailsHeld = true;
            Present(1, episode, Notice(episode, record, false));
            Check(list.Items.Cast<LandingRecord>().Any(value => value.Id == record.Id) &&
                Get<string>(window, "_connectionStatusKey") == "Recorder.LandingSaved" && window.DataContext is not LandingRecord && Get<bool>(window, "_detailLoading"),
                "A committed row waited for the blocked detail read, or success was not associated with a visible row.");
            details.Release(); detailsHeld = false;
            Pump(() => window.DataContext is LandingRecord current && current.Id == record.Id && !Get<bool>(window, "_detailLoading"));
            DrainDetails();
            var originalDetail = (LandingRecord)window.DataContext;
            Check(originalDetail.Airport == "EARLY", "Fixture did not cache the first committed detail.");
            var replay = Get<LandingReplayView>(window, "ReplayView"); replay.SetTime(.05);
            typeof(LandingReplayView).GetField("_azimuth", Private)!.SetValue(replay, 1.234);
            typeof(LandingReplayView).GetField("_distance", Private)!.SetValue(replay, 456d);
            Check(replay.IsPlaybackAvailable && replay.CurrentTimeSeconds == .05, "Fixture replay did not accept the user's selected time.");

            // The same episode's report commits final facilities after its first
            // history notice. No cloud event or new episode may be needed.
            record.Airport = "LATE"; record.Runway = "08";
            record.RunwayGeometry = new RunwayFacility { AirportIdent = "LATE", LatitudeDegrees = 42, LongitudeDegrees = 23,
                HeadingTrueDegrees = 0, LengthMeters = 2000, WidthMeters = 45, AltitudeMeters = 274, PrimaryName = "08", SecondaryName = "26" };
            repository.Save(record);
            details.Wait(); detailsHeld = true;
            var final = Notice(episode, record, true);
            Present(2, episode, final);
            Check(list.Items.Cast<LandingRecord>().Single().Airport == "LATE" && ((LandingRecord)list.SelectedItem).Id == record.Id,
                "Final report metadata did not refresh the existing row or changed the selected landing.");
            Check(ReferenceEquals(Get<System.Collections.Generic.Dictionary<string, LandingRecord>>(window, "_loadedDetails")[record.Id], originalDetail),
                "Final metadata invalidated unchanged telemetry/replay objects.");
            Pump(() => window.DataContext is LandingRecord current && current.Airport == "LATE" && current.Runway == "08" &&
                current.RunwayGeometry?.AirportIdent == "LATE" && !Get<bool>(window, "_detailLoading"));
            DrainDetails();
            Check(ReferenceEquals(originalDetail, window.DataContext) && replay.CurrentTimeSeconds == .05 &&
                (double)typeof(LandingReplayView).GetField("_azimuth", Private)!.GetValue(replay)! == 1.234 &&
                (double)typeof(LandingReplayView).GetField("_distance", Private)!.GetValue(replay)! == 456,
                "Final metadata discarded the selected detail object or reset the replay clock/camera.");
            details.Release(); detailsHeld = false;
            var version = Get<long>(window, "_historyLoadVersion"); var detail = window.DataContext;
            Present(3, episode, final);
            Check(Get<long>(window, "_historyLoadVersion") == version && ReferenceEquals(detail, window.DataContext),
                "An identical final report repeatedly reloaded history or invalidated prepared details.");

            var following = new LandingRecord { Id = Guid.NewGuid().ToString("N"), AircraftTitle = "Following fixture", TimestampUtc = record.TimestampUtc.AddMinutes(1) };
            repository.Save(following); var followingEpisode = Guid.NewGuid();
            Present(4, followingEpisode, final, Notice(followingEpisode, following, false));
            Check(list.Items.Cast<LandingRecord>().Any(value => value.Id == following.Id) && ((LandingRecord)list.SelectedItem).Id == record.Id,
                "Publishing a new landing hid its row or displaced the established selection.");
            DrainDetails();
            // Detail deserialization normally creates a separate but equivalent
            // runway object from the repository's index. Preserve that view
            // when a different episode's final metadata is published.
            var selectedGeometry = originalDetail.RunwayGeometry!.Snapshot(); originalDetail.RunwayGeometry = selectedGeometry;
            list.Items.Cast<LandingRecord>().Single(value => value.Id == record.Id).RunwayGeometry = selectedGeometry;
            typeof(MainWindow).GetMethod("ApplySessionFilter", Private)!.Invoke(window, new object?[] { record.Id });
            DrainDetails();
            var prepared = typeof(LandingReplayView).GetField("_prepared", Private)!.GetValue(replay);
            following.Airport = "B-FINAL"; repository.Save(following);
            Present(5, followingEpisode, final, Notice(followingEpisode, following, true));
            DrainDetails();
            Check(list.Items.Cast<LandingRecord>().Single(value => value.Id == following.Id).Airport == "B-FINAL" &&
                ((LandingRecord)list.SelectedItem).Id == record.Id && ReferenceEquals(originalDetail.RunwayGeometry, selectedGeometry) &&
                ReferenceEquals(prepared, typeof(LandingReplayView).GetField("_prepared", Private)!.GetValue(replay)) && replay.CurrentTimeSeconds == .05 &&
                (double)typeof(LandingReplayView).GetField("_azimuth", Private)!.GetValue(replay)! == 1.234 &&
                (double)typeof(LandingReplayView).GetField("_distance", Private)!.GetValue(replay)! == 456,
                "Finalizing another landing replaced the selected landing's geometry, scene, clock or camera.");
            var coalescedFinal = new LandingRecord { Id = Guid.NewGuid().ToString("N"), AircraftTitle = "Coalesced final fixture",
                Airport = "C-EARLY", TimestampUtc = record.TimestampUtc.AddMinutes(2) };
            repository.Save(coalescedFinal); var coalescedEpisode = Guid.NewGuid();
            var followingFinal = Notice(followingEpisode, following, true);
            Present(6, coalescedEpisode, final, followingFinal, Notice(coalescedEpisode, coalescedFinal, false));
            DrainDetails();
            var newlySaved = new LandingRecord { Id = Guid.NewGuid().ToString("N"), AircraftTitle = "Coalesced new fixture",
                TimestampUtc = record.TimestampUtc.AddMinutes(3) };
            coalescedFinal.Airport = "C-FINAL"; repository.Save(coalescedFinal); repository.Save(newlySaved);
            var newlySavedEpisode = Guid.NewGuid();
            Present(7, newlySavedEpisode, final, followingFinal, Notice(coalescedEpisode, coalescedFinal, true),
                Notice(newlySavedEpisode, newlySaved, false));
            DrainDetails();
            Check(list.Items.Cast<LandingRecord>().Single(value => value.Id == coalescedFinal.Id).Airport == "C-FINAL" &&
                list.Items.Cast<LandingRecord>().Any(value => value.Id == newlySaved.Id) &&
                ((LandingRecord)list.SelectedItem).Id == record.Id && ReferenceEquals(originalDetail.RunwayGeometry, selectedGeometry) &&
                ReferenceEquals(prepared, typeof(LandingReplayView).GetField("_prepared", Private)!.GetValue(replay)) && replay.CurrentTimeSeconds == .05 &&
                (double)typeof(LandingReplayView).GetField("_azimuth", Private)!.GetValue(replay)! == 1.234 &&
                (double)typeof(LandingReplayView).GetField("_distance", Private)!.GetValue(replay)! == 456,
                "Coalesced final metadata and a new landing lost a row or rebuilt the unrelated selected replay.");
            var pendingEpisode = Guid.NewGuid();
            foreach (var failed in new[] { false, true })
            foreach (var source in new[] { status, new RecorderStatusEventArgs(RecorderState.Recording, "Recorder.ContactRollout"),
                new RecorderStatusEventArgs(RecorderState.Connected, "Recorder.ReplayActive"),
                new RecorderStatusEventArgs(RecorderState.Connected, "Recorder.ReplayChecking"),
                new RecorderStatusEventArgs(RecorderState.Waiting, "Recorder.WaitingRetry") })
            {
                var snapshot = new RuntimePresentationSnapshot(8, source, false, pendingEpisode, 3, false, new[] { final },
                    Array.Empty<SupervisionSnapshot>(), Guid.Empty, null, analysisPending: true, analysisFailed: failed);
                var applying = Task.Run(() => apply(snapshot, CancellationToken.None)); Pump(() => applying.IsCompleted); applying.GetAwaiter().GetResult();
                var replayStatus = source.MessageKey == "Recorder.ReplayActive" || source.MessageKey == "Recorder.ReplayChecking";
                Check(Get<string>(window, "_connectionStatusKey") == (source.State == RecorderState.Connected && !replayStatus
                    ? failed ? "Recorder.AnalysisIncomplete" : "Recorder.AnalyzingLanding" : source.MessageKey),
                    "Pending/failed analysis was invisible or overrode current recording/disconnection status.");
                if (replayStatus)
                {
                    Check(Get<System.Windows.Controls.Grid>(window, "ReplayUnsupportedOverlay").Visibility == Visibility.Collapsed,
                        "Live replay status opened a blocking overlay.");
                    var savedDuringReplay = new RuntimePresentationSnapshot(8, source, false, episode, 3, false, new[] { final },
                        Array.Empty<SupervisionSnapshot>(), Guid.Empty, null);
                    applying = Task.Run(() => apply(savedDuringReplay, CancellationToken.None)); Pump(() => applying.IsCompleted); applying.GetAwaiter().GetResult();
                    Check(Get<string>(window, "_connectionStatusKey") == source.MessageKey, "Saved landing replaced ongoing replay status.");
                }
            }
            Present(9, pendingEpisode, final, Notice(pendingEpisode, newlySaved, true));
            DrainDetails();
            Check(Get<string>(window, "_connectionStatusKey") == "Recorder.LandingSaved",
                "Committed history did not replace the incomplete-analysis warning.");
            // Facilities obtained during a different landing also enrich an old
            // unknown airport, without replacing its selected replay objects.
            record.Airport = "Unknown airport"; record.TouchdownLatitudeDegrees = 42.695; record.TouchdownLongitudeDegrees = 23.408;
            repository.Save(record); originalDetail.Airport = "Unknown airport";
            list.Items.Cast<LandingRecord>().Single(value => value.Id == record.Id).Airport = "Unknown airport";
            var facilities = new[] { new AirportFacility { Ident = "LBSF", LatitudeDegrees = 42.695, LongitudeDegrees = 23.408 } };
            HistoryCommands.LoadSummaries(repository, gate, facilities, CancellationToken.None);
            var another = new LandingRecord { Id = Guid.NewGuid().ToString("N"), TimestampUtc = record.TimestampUtc.AddMinutes(4) };
            repository.Save(another); var anotherEpisode = Guid.NewGuid();
            Present(10, anotherEpisode, final, Notice(anotherEpisode, another, true)); DrainDetails();
            Check(list.Items.Cast<LandingRecord>().Single(value => value.Id == record.Id).Airport == "LBSF" && originalDetail.Airport == "LBSF",
                "A different episode's facilities left the old row/detail unresolved until restart.");
            Check(ReferenceEquals(originalDetail.RunwayGeometry, selectedGeometry) && replay.CurrentTimeSeconds == .05,
                "Airport enrichment replaced unrelated runway geometry or the replay clock.");
            Get<TextBox>(window, "SessionFilterBox").Text = "LBSF";
            Check(list.Items.Cast<LandingRecord>().Any(value => value.Id == record.Id), "Enriched old landing did not match the airport search.");
            done = true;
        }
        finally
        {
            if (detailsHeld) details!.Release();
            if (window != null)
            {
                Set(window, "_isClosed", true); typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, null);
                var history = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!; Pump(() => history.IsCompleted);
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!); window.Close();
            }
            Application.Current.Resources.MergedDictionaries.Remove(resources); Application.Current.ShutdownMode = oldMode; SynchronizationContext.SetSynchronizationContext(oldContext);
            var diagnostics = EpisodeTimeline.FlushAsync(); Pump(() => diagnostics.IsCompleted);
            if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private static void PendingAnalysisProjection()
    {
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 1000, 1000));
        var episode = StableRuntimeIdentity.Episode(identity.CaptureSessionId, 1);
        var record = new LandingRecord();
        var update = new RuntimeEpisodeUpdate(episode, ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { record }, new TelemetryDecisionJournal().Snapshot()),
            null, DateTime.UtcNow, 1, false);
        var capture = new RuntimeCapturedEpisode(new LandingEpisodeEventArgs(1, Array.Empty<TelemetrySample>(), "fixture", "fixture", "fixture", "fixture",
            Array.Empty<AirportFacility>(), Array.Empty<string>(), persistentId: episode), DateTime.UtcNow, false);
        var started = new RecordingRuntimeEffects(); started.Presentation.Add(new LandingEpisodeStartedEventArgs(1));
        var completed = new RecordingRuntimeEffects(); completed.Captures.Add(capture);
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        Check(!projection.Snapshot.AnalysisPending, "Fresh/recovered projection invented pending analysis without a committed capture.");
        projection.Recording(identity, started);
        Check(!projection.Snapshot.AnalysisPending, "Beginning capture prematurely advertised analysis.");
        var before = projection.Snapshot.Revision;
        projection.Recording(identity, completed);
        Check(projection.Snapshot.AnalysisPending && projection.Snapshot.Revision > before, "Capture-only effects did not publish durable analysis work.");
        projection.Failure("storage.retention", episode, "unrelated component");
        projection.Failure("analysis.episodes", Guid.NewGuid(), "unrelated episode");
        Check(!projection.Snapshot.AnalysisFailed, "An unrelated failure changed the active episode's analysis status.");
        projection.Failure("analysis.episodes", episode, "analysis fixture failure");
        Check(projection.Snapshot.AnalysisPending && projection.Snapshot.AnalysisFailed, "Failed pending analysis was still advertised as active analysis.");
        projection.Failure("storage.retention", Guid.NewGuid(), "later unrelated failure");
        Check(projection.Snapshot.AnalysisFailed, "A later unrelated failure erased the active episode's incomplete-analysis warning.");
        projection.Episode(update);
        Check(!projection.Snapshot.AnalysisPending && !projection.Snapshot.AnalysisFailed, "Committed history did not clear pending/failed analysis.");
        var race = new RuntimePresentation((_, __) => Task.CompletedTask);
        race.Episode(update); race.Recording(identity, started); race.Recording(identity, completed);
        Check(!race.Snapshot.AnalysisPending, "Late recording publication resurrected already completed analysis.");
        var next = new RecordingRuntimeEffects(); next.Presentation.Add(new LandingEpisodeStartedEventArgs(2));
        var pending = new RuntimePresentation((_, __) => Task.CompletedTask); pending.Recording(identity, started); pending.Recording(identity, completed);
        pending.Failure("analysis.episodes", episode, "previous episode failure");
        pending.Recording(identity, next);
        Check(!pending.Snapshot.AnalysisPending && !pending.Snapshot.AnalysisFailed, "An older pending/failed episode was presented as analysis of the new active episode.");
        var diagnostic = new RecordingRuntimeEffects(); diagnostic.Captures.Add(new RuntimeCapturedEpisode(new LandingEpisodeEventArgs(2,
            Array.Empty<TelemetrySample>(), "fixture", "fixture", "fixture", "fixture", Array.Empty<AirportFacility>(), Array.Empty<string>(),
            isDiagnosticOnly: true, persistentId: StableRuntimeIdentity.Episode(identity.CaptureSessionId, 2)), DateTime.UtcNow, true));
        pending.Recording(identity, diagnostic);
        Check(!pending.Snapshot.AnalysisPending, "Diagnostic-only capture promised a landing analysis.");
    }
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Pump(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, __) => { if (complete() || watch.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(complete(), "Runtime UI fixture timed out.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
