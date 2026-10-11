using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class EpisodeReportLifecycleRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type Processed = typeof(MainWindow).GetNestedType("ProcessedEpisode", BindingFlags.NonPublic)!;

    public static void RunAll()
    {
        ShutdownStillPersistsRawAndDecisionsWithoutWaitingForFacilities();
        OptOutWhileAwaitingFacilitiesPreventsAutomaticArchive();
        FailedAnalysisStillProducesDiagnosticArchive();
        DiagnosticOnlyAnalysisSkipsConfigurationAndHistoryButArchivesRawAndDecisions();
        ReportChainKeepsEveryAcceptedCaptureInTheShutdownBoundary();
        LateAirportDecisionsAreArchivedExactlyOnce();
        LateAirportReplyUpdatesArchivedCopyAndHistoryWithoutRunway();
        PhysicalSegmentsPersistIndependentBounceEpisodesAndExcludeInactiveTail();
        ActualShutdownSettlesFinalFacilitiesBeforeCancellingReportWait();
        ActualShutdownDuringApproachDoesNotInventLanding();
    }

    private static void ActualShutdownSettlesFinalFacilitiesBeforeCancellingReportWait()
    {
        WithWindow((window, directory, lifetime) =>
        {
            using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
            RecorderCall(recorder, "BeginRunwayEpisode", 901L);
            var preliminary = new EpisodeFacilitySnapshot(901, Array.Empty<RunwayFacility>(),
                new[] { new AirportFacility { Ident = "APPROACH", LatitudeDegrees = 40, LongitudeDegrees = 20 } },
                Array.Empty<TelemetrySample>(), Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false);
            var actualAirport = new AirportFacility { Ident = "ACTUAL", LatitudeDegrees = 40, LongitudeDegrees = 20 };
            var actualRunway = new RunwayFacility { AirportIdent = "ACTUAL", PrimaryName = "09", SecondaryName = "27",
                LatitudeDegrees = 40, LongitudeDegrees = 20, HeadingTrueDegrees = 90, LengthMeters = 3000, WidthMeters = 45 };
            RecorderSet(recorder, "_stageAirports", new[] { actualAirport });
            RecorderSet(recorder, "_runwayFreshAirportsReceived", true);
            RecorderSet(recorder, "_runwayBatchKeys", new[] { actualAirport.Key });
            RecorderSet(recorder, "_runwayBatchActive", true);
            ((Dictionary<string, IReadOnlyList<RunwayFacility>>)RecorderGet(recorder, "_runways"))[actualAirport.Key] = new[] { actualRunway };
            ((Dictionary<uint, RunwayFacilityRequest>)RecorderGet(recorder, "_runwayRequests"))
                .Add(901, new RunwayFacilityRequest(actualAirport, double.MaxValue, papiSide: 0));
            var facilities = ((TaskCompletionSource<EpisodeFacilitySnapshot>)RecorderGet(recorder, "_episodeFacilityCompletion")).Task;

            var record = new LandingRecord { TouchdownLatitudeDegrees = 40, TouchdownLongitudeDegrees = 20 };
            for (var index = 0; index <= 8; index++)
            {
                var time = -1 + index * .2;
                record.Series.Add(new LandingSeriesPoint { TimeSeconds = time, LatitudeDegrees = 40,
                    LongitudeDegrees = 20 + time * 60 / (111195 * Math.Cos(40 * Math.PI / 180)),
                    RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(),
                    RawControllerYAxisAgeSeconds = Array.Empty<double>() });
            }
            var repository = (LandingRepository)Get(window, "_repository")!;
            repository.Save(record);
            var journal = new TelemetryDecisionJournal();
            var processed = Processed.GetMethod("Completed")!.Invoke(null, new object[] {
                Array.Empty<AirportFacility>(), new[] { record }, journal.Snapshot() });
            var processing = typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(Processed).Invoke(null, new[] { processed })!;
            var episode = new LandingEpisodeEventArgs(901, new[] { new TelemetrySample { SimulationTimeSeconds = 1, MotionSimulation = true } },
                "MSFS", "Shutdown fixture", "TEST", "TEST", Array.Empty<AirportFacility>(), Array.Empty<string>(),
                facilitiesTask: facilities, initialFacilities: preliminary);
            var report = Queue(window, processing, episode);
            var finalReadyAtCancellation = false;
            using var registration = lifetime.Token.Register(() =>
            {
                // Observe the actual cancellation boundary, not a hand-ordered
                // stand-in for shutdown. Under the old order this is always false.
                finalReadyAtCancellation = facilities.IsCompleted;
            });
            CompleteActualShutdown(window, recorder);
            Complete(report);
            Assert(finalReadyAtCancellation && facilities.Result.FinalRunways.Single().AirportIdent == "ACTUAL",
                "Window shutdown canceled the report wait before recorder disposal froze final base geometry.");
            var candidate = (BugReportCandidate)report.GetType().GetProperty("Result")!.GetValue(report)!;
            Assert(candidate.Records.Single().Airport == "ACTUAL" && candidate.Records[0].RunwayGeometry?.AirportIdent == "ACTUAL",
                "Shutdown report used its preliminary snapshot instead of salvaged final runway geometry.");
            Assert(repository.LoadDetail(repository.LoadAll().Single())!.RunwayGeometry?.AirportIdent == "ACTUAL",
                "Shutdown report and durable landing history disagree on the final runway.");
            using var archive = ZipFile.OpenRead(Directory.GetFiles(directory, "*_auto_raw.zip").Single());
            using var reader = new StreamReader(archive.GetEntry("landing-results.json")!.Open());
            Assert(reader.ReadToEnd().Contains("\"Airport\":\"ACTUAL\""), "Shutdown archived the obsolete preliminary airport.");
        });
    }

    private static void ActualShutdownDuringApproachDoesNotInventLanding()
    {
        WithWindow((window, directory, lifetime) =>
        {
            using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
            RecorderCall(recorder, "BeginRunwayEpisode", 902L);
            RecorderSet(recorder, "_runwayLookupIsPreliminary", true);
            RecorderSet(recorder, "_activeEpisodeId", 902L);
            RecorderSet(recorder, "_episodeSamples", new List<TelemetrySample> { new TelemetrySample {
                SimulationTimeSeconds = 1, MotionSimulation = true, OnGround = false, AboveGroundLevelFeet = 300 } });
            var completedEpisodes = 0;
            recorder.EpisodeCompleted += (_, __) => completedEpisodes++;
            var facilities = ((TaskCompletionSource<EpisodeFacilitySnapshot>)RecorderGet(recorder, "_episodeFacilityCompletion")).Task;
            var readyAtCancellation = false;
            using var registration = lifetime.Token.Register(() => readyAtCancellation = facilities.IsCompleted);
            CompleteActualShutdown(window, recorder);
            Assert(readyAtCancellation && !facilities.Result.FinalLookupCompleted && completedEpisodes == 0,
                "Closing an airborne approach must settle scenery without inventing a physical contact.");
            Assert(((LandingRepository)Get(window, "_repository")!).LoadAll().Count == 0 &&
                (!Directory.Exists(directory) || Directory.GetFiles(directory, "*_auto_raw.zip").Length == 0),
                "Closing a contact-free approach created landing history or a landing archive.");
        });
    }

    private static void CompleteActualShutdown(MainWindow window, SimConnectLandingRecorder recorder)
    {
        Set(window, "_recorder", recorder);
        typeof(MainWindow).GetField("ReplayView", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(window, FormatterServices.GetUninitializedObject(typeof(LandingReplayView)));
        Set(window, "_charts", Array.Empty<LandingChart>());
        foreach (var field in new[] { "_episodeProcessingGate", "_airportMetadataPersistenceGate", "_telemetryUploadClientGate", "_historyOperationsGate" })
            Set(window, field, new object());
        foreach (var field in new[] { "_episodePersistenceTask", "_airportMetadataPersistenceTask", "_initializationPersistenceTask", "_localDeletionTask", "_runwayPersistenceTask" })
            Set(window, field, Task.CompletedTask);
        Set(window, "_settingsOperations", new SettingsOperationQueue());
        Set(window, "_historyOperations", new HashSet<Task>());
        var previousContext = SynchronizationContext.Current;
        // The fixture does not own an actual dispatcher window; storage and
        // report continuations stay asynchronous, with no UI continuation wait.
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            Complete((Task)typeof(MainWindow).GetMethod("DrainWindowShutdownAsync", PrivateInstance)!
                .Invoke(window, Array.Empty<object>())!);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static object RecorderGet(SimConnectLandingRecorder recorder, string field) =>
        typeof(SimConnectLandingRecorder).GetField(field, PrivateInstance)!.GetValue(recorder)!;
    private static void RecorderSet(SimConnectLandingRecorder recorder, string field, object value) =>
        typeof(SimConnectLandingRecorder).GetField(field, PrivateInstance)!.SetValue(recorder, value);
    private static void RecorderCall(SimConnectLandingRecorder recorder, string method, params object[] args) =>
        typeof(SimConnectLandingRecorder).GetMethod(method, PrivateInstance)!.Invoke(recorder, args);

    private static void ShutdownStillPersistsRawAndDecisionsWithoutWaitingForFacilities()
    {
        WithWindow((window, directory, lifetime) =>
        {
            lifetime.Cancel();
            var unavailable = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
            var task = Queue(window, SuccessfulProcessing(), Episode(1, unavailable.Task));
            Complete(task);
            Assert(ReferenceEquals(Get(window, "_bugReportPersistenceTask"), task), "report is absent from durable shutdown tracking");
            var path = Directory.GetFiles(directory, "*_auto_raw.zip").Single();
            using var archive = ZipFile.OpenRead(path);
            Assert(archive.GetEntry("telemetry.csv") != null && archive.GetEntry("landing-results.json") != null,
                "shutdown lost raw or calculated data");
            using var reader = new StreamReader(archive.GetEntry("decisions.json")!.Open());
            var json = reader.ReadToEnd();
            Assert(json.Contains("replay_rejected") && json.Contains("facilities_unavailable"),
                "shutdown diagnostic archive lost analysis or facility outcome");
            Assert(!unavailable.Task.IsCompleted, "test did not actually bypass an unresolved facility lookup");
        });
    }

    private static void OptOutWhileAwaitingFacilitiesPreventsAutomaticArchive()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var facilities = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
            var task = Queue(window, SuccessfulProcessing(), Episode(2, facilities.Task));
            Assert(!task.IsCompleted, "report did not await optional facilities");
            Set(window, "_automaticTelemetryEnabled", 0);
            facilities.SetResult(Array.Empty<RunwayFacility>());
            Complete(task);
            Assert(!Directory.Exists(directory) || Directory.GetFiles(directory, "*_auto_raw.zip").Length == 0,
                "automatic report was archived after opt-out while waiting");
        });
    }

    private static void FailedAnalysisStillProducesDiagnosticArchive()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var failed = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "FromException" && method.IsGenericMethodDefinition)
                .MakeGenericMethod(Processed).Invoke(null, new object[] { new InvalidDataException("Sensitive diagnostic text must not be included.") })!;
            Complete(Queue(window, failed, Episode(3)));
            using var archive = ZipFile.OpenRead(Directory.GetFiles(directory, "*_auto_raw.zip").Single());
            using var reader = new StreamReader(archive.GetEntry("decisions.json")!.Open());
            var json = reader.ReadToEnd();
            Assert(json.Contains("analysis_failed") && json.Contains("InvalidDataException") && !json.Contains("Sensitive"),
                "analysis failure was lost or copied arbitrary exception text into the journal");
        });
    }

    private static void ReportChainKeepsEveryAcceptedCaptureInTheShutdownBoundary()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var facilities = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
            var first = Queue(window, SuccessfulProcessing(), Episode(4, facilities.Task));
            var second = Queue(window, SuccessfulProcessing(), Episode(5));
            Assert(ReferenceEquals(Get(window, "_bugReportPersistenceTask"), second), "newest durable task was not published");
            Assert(!first.IsCompleted && !second.IsCompleted, "later capture escaped its predecessor's durability boundary");
            facilities.SetResult(Array.Empty<RunwayFacility>());
            Complete(second);
            Assert(first.IsCompleted && Directory.GetFiles(directory, "*_auto_raw.zip").Length == 2,
                "draining the latest task did not retain both accepted captures");
        });
    }

    private static void DiagnosticOnlyAnalysisSkipsConfigurationAndHistoryButArchivesRawAndDecisions()
    {
        WithWindow((window, directory, lifetime) =>
        {
            // These uninitialized services would throw if the diagnostic path touched configuration or history.
            Set(window, "_repository", null!);
            Assert(Get(window, "_flightModelGeometryResolver") == null && Get(window, "_repository") == null,
                "diagnostic isolation fixture unexpectedly initialized real analysis services");
            var samples = new[]
            {
                new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 10, MotionSimulation = true, OnGround = false },
                new TelemetrySample { Sequence = 2, SimulationTimeSeconds = 11, MotionSimulation = false, OnGround = false },
                new TelemetrySample { Sequence = 3, SimulationTimeSeconds = 11, MotionSimulation = true, OnGround = true }
            };
            var episode = new LandingEpisodeEventArgs(7, samples, "MSFS", "Test aircraft", "TEST", "TEST",
                Array.Empty<AirportFacility>(), Array.Empty<string>(), decisionJournal: new TelemetryDecisionJournal(),
                isDiagnosticOnly: true);
            var processing = (Task)typeof(MainWindow).GetMethod("ProcessEpisodeAsync", PrivateInstance)!
                .Invoke(window, new object[] { episode, Array.Empty<AirportFacility>(), DateTime.UtcNow })!;
            Complete(processing);
            var processed = processing.GetType().GetProperty("Result")!.GetValue(processing)!;
            var records = (IReadOnlyList<LandingRecord>)Processed.GetProperty("Records")!.GetValue(processed)!;
            Assert(records.Count == 0, "diagnostic-only analysis created a landing record");
            Complete(Queue(window, processing, episode));

            using var archive = ZipFile.OpenRead(Directory.GetFiles(directory, "*_auto_raw.zip").Single());
            using (var reader = new StreamReader(archive.GetEntry("telemetry.csv")!.Open()))
            {
                Assert(reader.ReadLine() == TelemetryCsv.Header, "automatic diagnostic raw header changed");
                foreach (var expected in samples)
                {
                    Assert(TelemetryCsv.TryParse(reader.ReadLine()!, out var actual) && actual.Sequence == expected.Sequence &&
                           actual.MotionSimulation == expected.MotionSimulation && actual.OnGround == expected.OnGround,
                        "automatic diagnostic archive dropped or changed a physics transition");
                }
                Assert(reader.ReadLine() == null, "automatic diagnostic archive duplicated raw observations");
            }
            using (var reader = new StreamReader(archive.GetEntry("landing-results.json")!.Open()))
                Assert(reader.ReadToEnd().Contains("\"records\":[]"), "automatic diagnostic archive contains a false landing");
            using (var reader = new StreamReader(archive.GetEntry("decisions.json")!.Open()))
            {
                var json = reader.ReadToEnd();
                Assert(json.Contains("physics_discontinuity_rejected") && json.Contains("diagnostic_only") &&
                       !json.Contains("analysis_failed"), "diagnostic fast path lost its actual rejection decisions");
            }
        });
    }

    private static void LateAirportDecisionsAreArchivedExactlyOnce()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var journal = new TelemetryDecisionJournal();
            journal.Record(1, "runway.airports", "requested", "Fresh airport list requested.");
            var processing = SuccessfulProcessing(journal);
            var facilities = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
            var task = Queue(window, processing, Episode(6, facilities.Task, journal));
            journal.Record(2, "runway.airports", "ready", "Fresh airport list received after analysis.");
            facilities.SetResult(Array.Empty<RunwayFacility>());
            Complete(task);
            using var archive = ZipFile.OpenRead(Directory.GetFiles(directory, "*_auto_raw.zip").Single());
            using var reader = new StreamReader(archive.GetEntry("decisions.json")!.Open());
            var json = reader.ReadToEnd();
            foreach (var code in new[] { "requested", "ready" })
                Assert(System.Text.RegularExpressions.Regex.Matches(json, "\"code\":\"" + code + "\"").Count == 1,
                    "early and late airport decisions must each be archived exactly once");
        });
    }

    private static void WithWindow(Action<MainWindow, string, CancellationTokenSource> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-stats-report-lifecycle-" + Guid.NewGuid().ToString("N"));
        using var lifetime = new CancellationTokenSource();
        // Avoid all constructor/startup services and real user repositories.
        // Logical closing also makes starting any network upload impossible.
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        Set(window, "_isClosed", true);
        Set(window, "_automaticTelemetryEnabled", 1);
        Set(window, "_lifetimeCancellation", lifetime);
        Set(window, "_bugReportPersistenceGate", new object());
        Set(window, "_bugReportPersistenceTask", Task.CompletedTask);
        Set(window, "_bugReportRepository", new BugReportRepository(directory));
        Set(window, "_repository", new LandingRepository(Path.Combine(directory, "landings")));
        Set(window, "_landingRepositoryGate", new object());
        try { test(window, directory, lifetime); }
        finally
        {
            lifetime.Cancel();
            Complete((Task)Get(window, "_bugReportPersistenceTask")!);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void PhysicalSegmentsPersistIndependentBounceEpisodesAndExcludeInactiveTail()
    {
        foreach (var sdk in new[] { false, true })
        WithWindow((window, directory, lifetime) =>
        {
            // Avoid installed packages and any external simulator/configuration access.
            Set(window, "_flightModelGeometryResolver", new FlightModelGeometryResolver(Array.Empty<string>(), false));
            var samples = new List<TelemetrySample>();
            AddBounce(10, 0);
            AddBounce(14.01, 3000);
            var lastActiveTime = TelemetryAnalysisFrames.TimeOf(samples.Last());
            // The SDK lifecycle state must also win over stale motion=true during menu entry.
            samples.Add(Frame(18.11, false, 0, sdk));
            samples.Last().SimulatorState = sdk ? TelemetrySimulatorState.Inactive : TelemetrySimulatorState.Legacy;
            samples.Last().MotionSimulation = sdk;
            samples.Last().LatitudeDegrees = samples.Last().LongitudeDegrees = 0;
            samples.Last().GForce = 99;
            samples.Add(Frame(18.21, true, 0, sdk));
            samples.Last().MotionSimulation = false;
            samples.Last().LatitudeDegrees = samples.Last().LongitudeDegrees = 0;
            samples.Last().GForce = 99;
            var rawBefore = samples.Select(TelemetryCsv.Format).ToArray();
            var timestamp = new DateTime(2026, 10, 11, 12, 34, 56, DateTimeKind.Utc);
            var episode = new LandingEpisodeEventArgs(42, samples, "MSFS", "Isolated pipeline aircraft", "TEST", "TEST",
                Array.Empty<AirportFacility>(), Array.Empty<string>(), decisionJournal: new TelemetryDecisionJournal());
            var processing = (Task)typeof(MainWindow).GetMethod("ProcessEpisodeAsync", PrivateInstance)!
                .Invoke(window, new object[] { episode, Array.Empty<AirportFacility>(), timestamp })!;
            Complete(processing);
            var processed = processing.GetType().GetProperty("Result")!.GetValue(processing)!;
            var records = (IReadOnlyList<LandingRecord>)Processed.GetProperty("Records")!.GetValue(processed)!;
            Assert(records.Count == 4 && records.All(record => record.TimestampUtc == timestamp && record.ContactCount == 2),
                "Two relocated physical episodes with one bounce each must save four contacts under the original capture UTC.");
            var episodes = records.GroupBy(record => record.RecordingEpisodeId).ToArray();
            Assert(episodes.Length == 2 && episodes.All(group => !string.IsNullOrWhiteSpace(group.Key) &&
                group.Select(record => record.ContactNumber).OrderBy(number => number).SequenceEqual(new[] { 1, 2 })),
                "Distinct physical segments need separate persisted identities while their bounce contacts share one identity.");
            foreach (var physicalEpisode in episodes)
            foreach (var record in physicalEpisode)
            {
                var north = record.TouchdownLatitudeDegrees!.Value > 42.02;
                Assert(record.Series.Count > 2 && record.Series.All(point => point.LatitudeDegrees.HasValue &&
                    (north ? point.LatitudeDegrees > 42.02 : point.LatitudeDegrees >= 42 && point.LatitudeDegrees < 42.01)),
                    "Stored 3D positions crossed into the replacement flight or menu/loading coordinates.");
                Assert(record.Series.All(point => point.GForce < 2) &&
                    record.Series.Last().TimeSeconds + record.EstimatedContactTimeSeconds!.Value <= lastActiveTime + .0001,
                    "Inactive tail samples were stored as replay motion or load evidence.");
                Assert(LandingReplayTrack.TryCreate(record, out var track), "A valid segment lost its usable 3D track.");
                Assert(LandingReplayContacts.ForEpisode(record, records).Select(contact => contact.Id)
                        .SequenceEqual(physicalEpisode.OrderBy(contact => contact.ContactNumber).Select(contact => contact.Id)) &&
                    LandingReplayContacts.GroundPositions(record, track!, records).Count == 2,
                    "Two same-timestamp bounce episodes must produce separate two-marker 3D reconstructions.");
            }
            var repository = (LandingRepository)Get(window, "_repository")!;
            var summaries = new LandingRepository(repository.RootPath).LoadAll();
            Assert(summaries.Count == 4 && summaries.Select(record => record.RecordingEpisodeId).Distinct().Count() == 2 &&
                summaries.All(summary => repository.LoadDetail(summary)!.RecordingEpisodeId == summary.RecordingEpisodeId),
                "Pipeline persistence lost the physical episode identities in history or landing files.");
            Assert(samples.Select(TelemetryCsv.Format).SequenceEqual(rawBefore) && samples.All(sample => sample.AnalysisDiscontinuity == null),
                "Pipeline analysis rewrote the original raw capture while deriving separate segments.");

            void AddBounce(double start, double northMeters)
            {
                for (var index = 0; index <= 40; index++)
                    samples.Add(Frame(start + index * .1, index >= 10 && index < 15 || index >= 20,
                        northMeters + index * 6, sdk));
            }

            TelemetrySample Frame(double time, bool ground, double northMeters, bool withSdk) => new TelemetrySample
            {
                Sequence = samples.Count + 1, HostElapsedSeconds = time,
                SimulationTimeSeconds = withSdk ? time + 100 : time,
                AnalysisTimeSeconds = withSdk ? time : double.NaN,
                SimulatorState = withSdk ? TelemetrySimulatorState.Running : TelemetrySimulatorState.Legacy,
                FlightSegment = withSdk ? 7 : 0, MotionSimulation = true, OnGround = ground,
                LatitudeDegrees = 42 + northMeters / 111195, LongitudeDegrees = 23,
                PlaneAltitudeFeet = 604, GroundAltitudeFeet = 600, AboveGroundLevelFeet = 4,
                HeadingTrueDegrees = 0, GroundSpeedKnots = 120, IndicatedAirspeedKnots = 120,
                VelocityWorldYFps = ground ? 0 : -4, TouchdownNormalVelocityFps = ground ? 4 : 0,
                GForce = ground ? 1.5 : 1,
            };
        });
    }

    private static void LateAirportReplyUpdatesArchivedCopyAndHistoryWithoutRunway()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var record = new LandingRecord { TouchdownLatitudeDegrees = 40, TouchdownLongitudeDegrees = 20 };
            var repository = (LandingRepository)Get(window, "_repository")!;
            repository.Save(record);
            var journal = new TelemetryDecisionJournal();
            var completed = Processed.GetMethod("Completed")!.Invoke(null, new object[] {
                Array.Empty<AirportFacility>(), new[] { record }, journal.Snapshot() });
            var processing = typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(Processed).Invoke(null, new[] { completed })!;
            var facilities = new TaskCompletionSource<EpisodeFacilitySnapshot>();
            var episode = new LandingEpisodeEventArgs(8, new[] { new TelemetrySample { SimulationTimeSeconds = 8, MotionSimulation = true } },
                "MSFS", "Test aircraft", "TEST", "TEST", Array.Empty<AirportFacility>(), Array.Empty<string>(),
                decisionJournal: journal, facilitiesTask: facilities.Task);
            var task = Queue(window, processing, episode);
            Assert(!task.IsCompleted, "Report did not wait for late episode facilities.");
            facilities.SetResult(new EpisodeFacilitySnapshot(8, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(),
                Array.Empty<TelemetrySample>(), Array.Empty<RunwayFacility>(), new[] {
                    new AirportFacility { Ident = "LATE", LatitudeDegrees = 40, LongitudeDegrees = 20 } }, true));
            Complete(task);
            var candidate = (BugReportCandidate)task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert(candidate.Records.Single().Airport == "LATE" && candidate.Records[0].RunwayGeometry == null,
                "Frozen report copy kept Unknown when late airports arrived without a runway match.");
            var saved = repository.LoadDetail(repository.LoadAll().Single())!;
            Assert(saved.Airport == "LATE", "History and report must use the same late facility result.");
            using var archive = ZipFile.OpenRead(Directory.GetFiles(directory, "*_auto_raw.zip").Single());
            using var reader = new StreamReader(archive.GetEntry("landing-results.json")!.Open());
            Assert(reader.ReadToEnd().Contains("\"Airport\":\"LATE\""), "Actual uploaded archive retained stale airport metadata.");
        });
    }

    private static object SuccessfulProcessing(TelemetryDecisionJournal? journal = null)
    {
        journal ??= new TelemetryDecisionJournal();
        journal.Record(1, "analysis", "replay_rejected", "Synthetic rejected capture for lifecycle testing.");
        var result = Processed.GetMethod("Replay", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { journal.Snapshot() });
        return typeof(Task).GetMethod("FromResult", BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(Processed).Invoke(null, new[] { result })!;
    }

    private static LandingEpisodeEventArgs Episode(long id, Task<IReadOnlyList<RunwayFacility>>? facilities = null,
        TelemetryDecisionJournal? journal = null) => new LandingEpisodeEventArgs(
        id, new[] { new TelemetrySample { SimulationTimeSeconds = id, MotionSimulation = true } },
        "MSFS", "Test aircraft", "TEST", "TEST", Array.Empty<AirportFacility>(), Array.Empty<string>(),
        decisionJournal: journal, runwayFacilitiesTask: facilities);

    private static Task Queue(MainWindow window, object processing, LandingEpisodeEventArgs episode) =>
        (Task)typeof(MainWindow).GetMethod("QueueEpisodeReport", PrivateInstance)!
            .Invoke(window, new[] { processing, episode, (object)DateTime.UtcNow })!;

    private static object? Get(MainWindow window, string name) => typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window);
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(window, value);
    private static void Complete(Task task)
    {
        Assert(task.Wait(TimeSpan.FromSeconds(5)), "report lifecycle test timed out");
        task.GetAwaiter().GetResult();
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
