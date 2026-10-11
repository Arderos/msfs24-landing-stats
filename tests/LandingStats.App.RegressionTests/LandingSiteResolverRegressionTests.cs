using System;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class LandingSiteResolverRegressionTests
{
    public static void RunAll()
    {
        FinalContactWinsOverApproach();
        ApproachSurvivesMissingFinalLookup();
        AirportDoesNotDependOnRunway();
        ApproachNeedsMeasuredTrack();
        ConflictDoesNotMixAirports();
        RepeatedApplicationPreservesFinalChoice();
        AllBounceContactsShareApproach();
        AllBounceContactsUseContradictingFinalRunway();
        SnapshotIsOwnedByEpisode();
        RecordedApproachResolvesAtGate();
        IncompleteCandidatesCannotProveUniqueRunway();
    }

    private static void FinalContactWinsOverApproach()
    {
        var preliminary = Runway("OLD", 0);
        var final = Runway("NEW", .02);
        var record = Record(.02);
        var journal = new TelemetryDecisionJournal();
        Assert(LandingSiteResolver.Apply(record, Snapshot(preliminary, final), journal), "Final application must change the record.");
        Assert(record.Airport == "NEW" && record.RunwayGeometry!.LatitudeDegrees == .02,
            "Actual contact runway must override an approach toward another airport.");
        Assert(journal.Snapshot().Entries.Any(entry => entry.Code == "actual_runway_selected"), "Source must be journaled.");

        record = Record(0);
        final = Runway("OLD", 0); final.WidthMeters = 60;
        LandingSiteResolver.Apply(record, Snapshot(preliminary, final));
        Assert(record.RunwayGeometry!.WidthMeters == 60, "Agreement must still use final fresh geometry.");
    }

    private static void ApproachSurvivesMissingFinalLookup()
    {
        var runway = Runway("TEST", 0);
        foreach (var completed in new[] { false, true })
        {
            var snapshot = new EpisodeFacilitySnapshot(1, new[] { runway }, new[] { Airport("TEST", 0) }, Approach(),
                Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), completed);
            var record = Record(0);
            LandingSiteResolver.Apply(record, snapshot);
            Assert(record.RunwayGeometry != null && record.Airport == "TEST" && record.Runway == "09",
                "A missing final runway response must retain the intended approach runway.");
        }
        var reciprocal = Approach();
        for (var index = 0; index < reciprocal.Length; index++) reciprocal[index].LongitudeDegrees = .03 - index * .00015;
        var selection = LandingSiteResolver.SelectApproach(reciprocal, new[] { runway }, out var name);
        Assert(selection != null && name == "27", "Preliminary direction must follow westbound movement, not the nose.");
    }

    private static void AirportDoesNotDependOnRunway()
    {
        var record = Record(.005); // Outside the runway but beside the airport.
        var snapshot = new EpisodeFacilitySnapshot(2, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(),
            Array.Empty<TelemetrySample>(), new[] { Runway("TEST", 0) }, new[] { Airport("TEST", 0) }, true);
        LandingSiteResolver.Apply(record, snapshot);
        Assert(record.Airport == "TEST" && record.RunwayGeometry == null && record.AirportDistanceNauticalMiles > 0,
            "Late fresh airport data must update a report even if no runway matches.");
        record = Record(0); record.Series.Clear();
        LandingSiteResolver.Apply(record, snapshot);
        Assert(record.Airport == "TEST" && record.RunwayGeometry == null, "Missing flight track does not prevent airport resolution.");
        record = Record(0); record.TouchdownLatitudeDegrees = double.NaN;
        LandingSiteResolver.Apply(record, snapshot);
        Assert(record.Airport == "Unknown airport", "Invalid contact coordinates must not pick an arbitrary airport.");
    }

    private static void ApproachNeedsMeasuredTrack()
    {
        var runway = Runway("TEST", 0);
        var samples = Approach();
        foreach (var sample in samples) sample.HeadingTrueDegrees = 270;
        Assert(LandingSiteResolver.SelectApproach(samples, new[] { runway }, out var name) != null && name == "09",
            "Crab or wrong nose heading must not alter measured intent.");
        foreach (var sample in samples) sample.LongitudeDegrees = -.03;
        Assert(LandingSiteResolver.SelectApproach(samples, new[] { runway }, out _) == null,
            "Stationary samples with a runway-facing nose do not establish intent.");
        samples = Approach(); samples[samples.Length - 1].LatitudeDegrees = double.NaN;
        Assert(LandingSiteResolver.SelectApproach(samples, new[] { runway }, out _) == null, "Invalid final approach sample must not be bridged.");
        samples = Approach(); samples[samples.Length - 1].SimulationTimeSeconds = 0;
        Assert(LandingSiteResolver.SelectApproach(samples, new[] { runway }, out _) == null, "Clock reset must not make a track.");
        samples = Approach(); samples[samples.Length - 1].FlightSegment = 3;
        Assert(LandingSiteResolver.SelectApproach(samples, new[] { runway }, out _) == null, "Approach cannot bridge a flight change.");
        Assert(LandingSiteResolver.SelectApproach(Array.Empty<TelemetrySample>(), new[] { runway }, out _) == null,
            "A missing approach cannot invent preliminary intent.");
        Assert(LandingSiteResolver.SelectApproach(Approach(), new[] { runway, Runway("OTHER", 0) }, out _) == null,
            "Overlapping facility tracks are ambiguous.");
    }

    private static void ConflictDoesNotMixAirports()
    {
        var snapshot = new EpisodeFacilitySnapshot(1, new[] { Runway("OLD", 0) }, new[] { Airport("OLD", 0) }, Approach(),
            Array.Empty<RunwayFacility>(), new[] { Airport("NEW", .02) }, true);
        var record = Record(.02);
        LandingSiteResolver.Apply(record, snapshot);
        Assert(record.Airport == "NEW" && record.RunwayGeometry == null,
            "An actual airport contradicted by the intended strip must not receive another airport's runway.");

        record = Record(0);
        LandingSiteResolver.Apply(record, snapshot);
        Assert(record.Airport == "OLD" && record.RunwayGeometry != null,
            "A nearby different ARP cannot override a strip containing the actual contact.");
    }

    private static void RepeatedApplicationPreservesFinalChoice()
    {
        var preliminary = Runway("OLD", 0);
        var final = Runway("NEW", .02);
        var record = Record(.02);
        LandingSiteResolver.Apply(record, Snapshot(preliminary, final));
        var previous = record.RunwayGeometry;
        var missing = new EpisodeFacilitySnapshot(1, new[] { preliminary }, new[] { Airport("OLD", 0) }, Approach(),
            Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false);
        Assert(!LandingSiteResolver.Apply(record, missing) && ReferenceEquals(previous, record.RunwayGeometry) && record.Airport == "NEW",
            "A delayed preliminary-only application must not overwrite the final result.");
    }

    private static void AllBounceContactsShareApproach()
    {
        var snapshot = new EpisodeFacilitySnapshot(1, new[] { Runway("TEST", 0) }, new[] { Airport("TEST", 0) }, Approach(),
            Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false);
        for (var index = 1; index <= 3; index++)
        {
            var record = Record(0); record.ContactNumber = index; record.ContactCount = 3;
            record.Series.Clear(); // Later short bounce may not establish another track.
            LandingSiteResolver.Apply(record, snapshot);
            Assert(record.Airport == "TEST" && record.Runway == "09" && record.RunwayGeometry != null,
                "Every contact must receive the same episode's preliminary fallback.");
        }
    }

    private static void AllBounceContactsUseContradictingFinalRunway()
    {
        var snapshot = Snapshot(Runway("APPROACH", 0), Runway("ACTUAL", .02));
        for (var index = 1; index <= 3; index++)
        {
            var record = Record(.02);
            record.ContactNumber = index; record.ContactCount = 3;
            // Ordinary bounces retain continuous measured tracks; successive
            // contacts move farther down the actual strip, not the intended one.
            var longitudeOffset = index * .001;
            record.TouchdownLongitudeDegrees += longitudeOffset;
            foreach (var point in record.Series) point.LongitudeDegrees += longitudeOffset;
            var journal = new TelemetryDecisionJournal();
            LandingSiteResolver.Apply(record, snapshot, journal);
            Assert(record.Airport == "ACTUAL" && record.Runway == "09" &&
                   record.RunwayGeometry?.AirportIdent == "ACTUAL" &&
                   journal.Snapshot().Entries.Any(entry => entry.Code == "actual_runway_selected"),
                "All normal bounce contacts must select actual runway B, overriding shared approach A.");
        }
    }

    private static void SnapshotIsOwnedByEpisode()
    {
        var runway = Runway("TEST", 0); var airport = Airport("TEST", 0); var samples = Approach();
        var snapshot = new EpisodeFacilitySnapshot(1, new[] { runway }, new[] { airport }, samples,
            Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false);
        runway.AirportIdent = "WRONG"; airport.Ident = "WRONG";
        foreach (var sample in samples) sample.LatitudeDegrees = 10;
        var record = Record(0);
        LandingSiteResolver.Apply(record, snapshot);
        Assert(record.Airport == "TEST", "Later cache or sample changes cannot contaminate this episode.");
    }

    private static void RecordedApproachResolvesAtGate()
    {
        // Reduced measured track from the 2026-10-11 LBSF Esc -> menu test,
        // ending at the actual recorder gate (486.55 ft). This is not a track
        // synthesized from the runway heading or the subsequent touchdown.
        var positions = new[] {
            new[] { 353.31092104723211, 42.699637635313358, 23.363611190477982 },
            new[] { 353.56164455029648, 42.699624474411387, 23.363829788767905 },
            new[] { 353.80365815467667, 42.699611798313498, 23.364040772908123 },
            new[] { 354.04258735047188, 42.699599310427885, 23.364249047905172 },
            new[] { 354.30381708440837, 42.699585687123594, 23.364476738557148 },
            new[] { 354.54663615149911, 42.699573052448521, 23.364688359514389 },
            new[] { 354.80429385171738, 42.699559676114838, 23.364912888187863 },
            new[] { 355.05790335533675, 42.699546540372076, 23.365133864301541 },
            new[] { 355.2839557522675, 42.69953485719418, 23.365330808218669 },
        };
        var samples = positions.Select(row => new TelemetrySample { SimulationTimeSeconds = row[0],
            LatitudeDegrees = row[1], LongitudeDegrees = row[2], MotionSimulation = true, FlightSegment = 12 }).ToArray();
        var runway = new RunwayFacility { AirportIdent = "LBSF", AirportRegion = "LB",
            LatitudeDegrees = 42.69644681364298, LongitudeDegrees = 23.41781973838806,
            HeadingTrueDegrees = 274.6463317871094, LengthMeters = 3606.714599609375, WidthMeters = 45,
            AltitudeMeters = 530.8710327148438, PrimaryName = "27", SecondaryName = "09" };
        var journal = new TelemetryDecisionJournal();
        Assert(LandingSiteResolver.SelectApproach(samples, new[] { runway }, out var name, journal) != null && name == "09",
            "The real LBSF approach must identify RWY 09 before touchdown or final facility lookup.");
        var candidate = journal.Snapshot().Entries.Single(entry => entry.Code == "approach_candidate");
        Assert(Math.Abs(candidate.Details["projected_across_m"] + 5.3557475) < .01,
            "Approach projection must preserve the measured ground-track calculation.");
    }

    private static EpisodeFacilitySnapshot Snapshot(RunwayFacility preliminary, RunwayFacility final) => new EpisodeFacilitySnapshot(1,
        new[] { preliminary }, new[] { Airport(preliminary.AirportIdent, preliminary.LatitudeDegrees) }, Approach(),
        new[] { final }, new[] { Airport(final.AirportIdent, final.LatitudeDegrees) }, true);

    private static void IncompleteCandidatesCannotProveUniqueRunway()
    {
        var final = Runway("ACTUAL", 0);
        var snapshot = new EpisodeFacilitySnapshot(200, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), Approach(),
            new[] { final }, new[] { Airport("ACTUAL", 0) }, true, false, false);
        var record = Record(0);
        var journal = new TelemetryDecisionJournal();
        LandingSiteResolver.Apply(record, snapshot, journal);
        Assert(snapshot.FinalRunways.Count == 1 && record.Airport == "ACTUAL" && record.RunwayGeometry == null &&
            journal.Snapshot().Entries.Any(entry => entry.Code == "final_candidates_incomplete"),
            "a complete reply from one airport became a falsely unique runway while its neighbor was unavailable");

        snapshot = new EpisodeFacilitySnapshot(201, new[] { Runway("APP", 0) }, new[] { Airport("APP", 0) }, Approach(),
            new[] { final }, new[] { Airport("ACTUAL", 0) }, true, true, false);
        record = Record(0);
        LandingSiteResolver.Apply(record, snapshot);
        Assert(record.Airport == "APP" && record.RunwayGeometry != null,
            "an incomplete final candidate set discarded an independently complete approach fallback");

        snapshot = new EpisodeFacilitySnapshot(202, new[] { Runway("APP", 0) }, new[] { Airport("APP", 0) }, Approach(),
            Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false, false, false);
        record = Record(0);
        LandingSiteResolver.Apply(record, snapshot);
        Assert(record.Airport == "APP" && record.RunwayGeometry == null,
            "incomplete approach geometry must retain airport identity without asserting runway uniqueness");
    }
    private static RunwayFacility Runway(string ident, double latitude) => new RunwayFacility {
        AirportIdent = ident, LatitudeDegrees = latitude, LongitudeDegrees = 0, HeadingTrueDegrees = 90,
        LengthMeters = 3000, WidthMeters = 45, PrimaryName = "09", SecondaryName = "27", AltitudeMeters = 100 };
    private static AirportFacility Airport(string ident, double latitude) => new AirportFacility {
        Ident = ident, LatitudeDegrees = latitude, LongitudeDegrees = 0 };
    private static TelemetrySample[] Approach() => Enumerable.Range(0, 9).Select(index => new TelemetrySample {
        SimulationTimeSeconds = 100 + index * .25, MotionSimulation = true, LatitudeDegrees = 0,
        LongitudeDegrees = -.03 + index * .00015, HeadingTrueDegrees = 90 }).ToArray();
    private static LandingRecord Record(double latitude)
    {
        var record = new LandingRecord { TouchdownLatitudeDegrees = latitude, TouchdownLongitudeDegrees = 0 };
        for (var index = 0; index <= 6; index++)
        {
            var time = -1 + index * .25;
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = time, LatitudeDegrees = latitude,
                LongitudeDegrees = time * 60 / 111195, HeadingTrueDegrees = 90 });
        }
        return record;
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
