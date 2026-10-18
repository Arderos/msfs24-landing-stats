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
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static partial class EpisodeReportLifecycleRegressionTests
{
    private static void PhysicalSegmentsPersistIndependentBounceEpisodesAndExcludeInactiveTail()
    {
        foreach (var sdk in new[] { false, true })
        {
            // Avoid installed packages and any external simulator/configuration access.
            using var fixture = new ReportPipelineFixture();
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
                Array.Empty<AirportFacility>(), Array.Empty<string>(), decisionJournal: new TelemetryDecisionJournal(), persistentId: System.Guid.NewGuid());
            var processed = ReportPipelineFixture.Complete(fixture.Processor.ProcessAsync(episode, Array.Empty<AirportFacility>(), timestamp));
            var records = processed.Records;
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
            var repository = fixture.Repository;
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
        }
    }

}
