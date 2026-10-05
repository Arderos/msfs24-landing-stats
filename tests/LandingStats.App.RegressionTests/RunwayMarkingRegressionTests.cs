using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class RunwayMarkingRegressionTests
{
    public static void RunAll()
    {
        GuidancePlacement();
        RegionalPatterns();
        Designations();
        foreach (var width in new[] { 6d, 7, 12, 18, 23, 30, 44.99, 45, 49.84298, 60.41475, 120 })
        foreach (var length in new[] { 180d, 500, 799, 800, 1199, 1200, 2399, 2400, 4000 })
        foreach (var airport in new[] { "EDDM", "KJFK" })
        foreach (var both in new[] { false, true })
        {
            var runway = Runway(width, length); runway.AirportIdent = airport;
            runway.PrimaryPapi = new RunwayPapi { StationFromEndMeters = Math.Min(400, length * .3), AngleDegrees = 3 };
            runway.PrimaryPapiQueried = true;
            if (both) { runway.SecondaryPapi = runway.PrimaryPapi; runway.SecondaryPapiQueried = true; }
            foreach (var shape in RunwayMarkingLayout.Build(runway).Shapes)
            {
                Require(shape.Points.All(p => Math.Abs(p.X) <= width / 2 + 1e-9 && Math.Abs(p.Y) <= length / 2 + 1e-9), "paint outside actual pavement");
                Require(shape.Points.All(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y)), "finite paint");
                Require(shape.Points.Select(p => p.X).Distinct().Count() == 2 && shape.Points.Select(p => p.Y).Distinct().Count() == 2, "nonzero paint rectangle");
            }
        }
        var measured = GuidedRunway(45, 4000);
        foreach (var surface in new[] { 1, 2, 12, 27 })
        {
            measured.SurfaceType = surface;
            Require(RunwayMarkingLayout.Build(measured).Shapes.Count == 0, "no white precision markings on grass/water/snow");
        }
        measured = Runway(45, 4000); measured.PrimaryThresholdOffsetMeters = double.NaN;
        Require(!measured.HasValidGeometry && RunwayMarkingLayout.Build(measured).Shapes.Count == 0, "invalid threshold metadata rejected");
        measured.PrimaryThresholdOffsetMeters = 2500; measured.SecondaryThresholdOffsetMeters = 1500;
        Require(!measured.HasValidGeometry, "crossing thresholds invalid");
        ThresholdReplies();
        OptionalMetadataDoesNotLoseRunway();
    }

    private static void GuidancePlacement()
    {
        Near(RunwayMarkingLayout.ReferenceApproachDistance, 290.796523120978);
        var runway = Runway(45, 4000);
        var layout = RunwayMarkingLayout.Build(runway);
        Require(layout.Shapes.Any(p => p.Kind == "Centerline") && layout.Shapes.All(p => p.Kind == "Centerline" || p.Text != null), "no ILS/PAPI: centreline and known designations only");
        runway.PrimaryPapi = new RunwayPapi { StationFromEndMeters = 400, AngleDegrees = 3 };
        Require(RunwayMarkingLayout.ResolveApproach(runway, false) == null, "unverified PAPI not displayed");
        runway.PrimaryPapiQueried = true;
        var approach = RunwayMarkingLayout.ResolveApproach(runway, false)!;
        Require(!approach.UsesIls, "PAPI fallback");
        Near(approach.AimingFromEndMeters, 400);
        Near(approach.ThresholdFromEndMeters, 400 - RunwayMarkingLayout.ReferenceApproachDistance);
        foreach (var angle in new[] { 3d, 3.5, 5.5 })
        {
            runway.PrimaryIlsGlideSlope = GlideSlope(runway, 500, 10, angle, false);
            approach = RunwayMarkingLayout.ResolveApproach(runway, false)!;
            Require(approach.UsesIls, "ILS overrides PAPI");
            Near(approach.AimingFromEndMeters, 500 + 10 / Math.Tan(angle * Math.PI / 180));
            Near(approach.AimingFromEndMeters - approach.ThresholdFromEndMeters, RunwayMarkingLayout.ReferenceApproachDistance);
        }
        runway.PrimaryIlsGlideSlope = GlideSlope(runway, 500, -10, 3, false);
        Near(RunwayMarkingLayout.ResolveApproach(runway, false)!.AimingFromEndMeters, 500 - 10 / Math.Tan(3 * Math.PI / 180));
        runway.PrimaryIlsGlideSlope.AngleDegrees = double.NaN;
        Require(!RunwayMarkingLayout.ResolveApproach(runway, false)!.UsesIls, "invalid ILS falls back to valid PAPI");
        runway.PrimaryIlsGlideSlope = GlideSlope(runway, 500, -1000, 3, false);
        Require(!RunwayMarkingLayout.ResolveApproach(runway, false)!.UsesIls, "off-pavement ILS intercept falls back");
        runway.PrimaryIlsGlideSlope = GlideSlope(runway, 500, 0, 3, false);
        runway.PrimaryIlsGlideSlope.LongitudeDegrees = 1;
        Require(!RunwayMarkingLayout.ResolveApproach(runway, false)!.UsesIls, "remote transverse GS cannot override local PAPI");
        runway.PrimaryIlsGlideSlope = null;
        var before = RunwayMarkingLayout.Build(runway);
        runway.PrimaryThresholdOffsetMeters = 700; runway.SecondaryThresholdOffsetMeters = 900;
        var after = RunwayMarkingLayout.Build(runway);
        var beforeGuided = before.Shapes.Where(p => !p.Secondary && p.Kind != "Centerline").ToArray();
        var afterGuided = after.Shapes.Where(p => !p.Secondary && p.Kind != "Centerline").ToArray();
        Require(beforeGuided.Length == afterGuided.Length && beforeGuided.Zip(afterGuided, (a, b) => a.Points.SequenceEqual(b.Points)).All(v => v),
            "native threshold offsets cannot move the synthetic paint lattice");
        Require(after.Shapes.Where(p => p.Kind != "Centerline" && p.Text == null).All(p => !p.Secondary), "no opposite guidance: no opposite landing paint");
        runway.PrimaryPapi.StationFromEndMeters = 100;
        approach = RunwayMarkingLayout.ResolveApproach(runway, false)!;
        Require(approach.ThresholdFromEndMeters < 0, "do not clamp inferred threshold to pavement");
        layout = RunwayMarkingLayout.Build(runway);
        Require(!layout.Shapes.Any(p => p.Kind == "Threshold"), "off-pavement piano keys omitted, never relocated");
        Near(layout.Shapes.First(p => p.Kind == "Aiming").Points.Min(p => p.Y), -1900);
        runway.PrimaryPapi = null;
        Require(RunwayMarkingLayout.Build(runway).Shapes.All(p => p.Kind == "Centerline" || p.Text != null), "known absence does not invent guidance");

        runway = Runway(60, 4000);
        runway.SecondaryIlsGlideSlope = GlideSlope(runway, 400, 0, 3, true);
        Near(RunwayMarkingLayout.ResolveApproach(runway, true)!.AimingFromEndMeters, 400);
        layout = RunwayMarkingLayout.Build(runway);
        Require(layout.Shapes.Count(p => p.Kind == "Aiming" && p.Secondary) == 2, "reciprocal ILS paint");
        Near(layout.Shapes.First(p => p.Kind == "Aiming").Points.Max(p => p.Y), 1600);

        // Native UUEE 24C fixture: negative GS elevation difference matters.
        runway = new RunwayFacility { AirportIdent = "UUEE", AirportRegion = "UU",
            LatitudeDegrees = 55.973962470889091, LongitudeDegrees = 37.414404004812241,
            HeadingTrueDegrees = 75.099998, LengthMeters = 3555.661865, WidthMeters = 60, AltitudeMeters = 192.000015,
            SecondaryIlsGlideSlope = new RunwayGlideSlope { Ident = "IAD", Region = "UU",
                LatitudeDegrees = 55.976083427667618, LongitudeDegrees = 37.436666786670685,
                AltitudeMeters = 189.280014, AngleDegrees = 3 } };
        var actual = RunwayMarkingLayout.ResolveApproach(runway, true)!;
        Require(actual.UsesIls && Math.Abs(actual.AimingFromEndMeters - 326.680154) < .2, "UUEE actual GS projects to nominal runway plane");
    }

    private static void Designations()
    {
        foreach (var airport in new[] { "EDDM", "KJFK" })
        foreach (var suffix in new[] { "", "L", "C", "R" })
        {
            var runway = GuidedRunway(45, 4000); runway.AirportIdent = airport;
            runway.PrimaryName = "09" + suffix; runway.SecondaryName = "27" + suffix;
            var plan = RunwayMarkingLayout.Build(runway);
            var labels = plan.Shapes.Where(p => p.Text != null).ToArray();
            Require(labels.Length == (suffix.Length == 0 ? 2 : 4), "both numbers and any suffixes restored");
            Require(labels.Any(p => !p.Secondary && p.Text == (airport == "KJFK" ? "9" : "09")) &&
                labels.Any(p => p.Secondary && p.Text == "27"), "regional leading zero and reciprocal number");
            foreach (var label in labels)
            {
                Require(label.Secondary ? label.Points[1].X < label.Points[0].X : label.Points[1].X > label.Points[0].X,
                    "each designation reads towards its own landing direction");
                foreach (var paint in plan.Shapes.Where(p => p.Text == null && (p.Kind == "Centerline" || p.Kind == "Threshold")))
                    Require(!Overlap(label, paint), "designation does not overlap centreline/piano keys");
            }
        }
        var legacy = Runway(45, 4000); legacy.PrimaryThresholdOffsetMeters = 308;
        var first = RunwayMarkingLayout.Build(legacy).Shapes.Single(p => p.Text == "09");
        Near(first.Points.Min(p => p.Y), -2000 + 308 + 48 + 18 + 6 - 1); // Nominal L-1 row; 9's tip extends 1 m below it at x2 scale.
        foreach (var invalid in new[] { "", "N", "00", "37", "9X", "09X", "09LONG" })
        {
            legacy.PrimaryName = invalid;
            Require(!RunwayMarkingLayout.Build(legacy).Shapes.Any(p => p.Text != null && !p.Secondary), "do not invent malformed designation");
        }
        bool Overlap(RunwayMarkingLayout.Paint a, RunwayMarkingLayout.Paint b) =>
            a.Points.Min(p => p.X) < b.Points.Max(p => p.X) && a.Points.Max(p => p.X) > b.Points.Min(p => p.X) &&
            a.Points.Min(p => p.Y) < b.Points.Max(p => p.Y) && a.Points.Max(p => p.Y) > b.Points.Min(p => p.Y);
    }

    private static void RegionalPatterns()
    {
        var measured = GuidedRunway(60.41475, 4000);
        var plan = RunwayMarkingLayout.Build(measured);
        Require(plan.Shapes.Count(p => p.Kind == "Threshold" && !p.Secondary) == 16, "60 m ICAO threshold stripe count");
        Require(plan.Shapes.Count(p => p.Kind == "Aiming" && !p.Secondary) == 2, "paired aiming rectangles");
        foreach (var paint in plan.Shapes.Where(p => p.Kind == "Aiming" && !p.Secondary))
        {
            Near(paint.Points.Min(p => p.Y), -1600);
            Near(paint.Points.Max(p => p.Y) - paint.Points.Min(p => p.Y), 45);
        }
        var threshold = -2000 + 400 - RunwayMarkingLayout.ReferenceApproachDistance;
        var groups = plan.Shapes.Where(p => p.Kind == "TouchdownZone" && !p.Secondary).GroupBy(p => p.Points.Min(v => v.Y)).OrderBy(g => g.Key).ToArray();
        Require(groups.Length == 5, "ICAO six slots less aiming overlap");
        Require(groups.Select(g => g.Count()).SequenceEqual(new[] { 6, 4, 4, 2, 2 }), "ICAO pattern B groups");
        for (var i = 0; i < groups.Length; i++) Near(groups[i].Key - threshold, new[] { 150d, 450, 600, 750, 900 }[i]);
        foreach (var paint in plan.Shapes.Where(p => p.Kind != "Centerline" && p.Text == null && !p.Secondary))
            Require(plan.Shapes.Any(q => q.Secondary && q.Kind == paint.Kind && q.Points.SequenceEqual(paint.Points.Select(v => new System.Windows.Point(-v.X, -v.Y)))), "reciprocal pattern rotates");

        var shortRunway = GuidedRunway(30, 1100);
        shortRunway.SecondaryPapi = null;
        var last = RunwayMarkingLayout.Build(shortRunway).Shapes.Where(p => p.Kind == "TouchdownZone").GroupBy(p => p.Points.Min(v => v.Y)).OrderBy(g => g.Key).Last();
        Require(last.Count() == 2, "shortened ICAO pattern ends in one stripe per side");

        var us = GuidedRunway(45.72, 4000); us.AirportIdent = "KJFK";
        var faa = RunwayMarkingLayout.Build(us);
        Require(faa.UsesFaa, "FAA convention");
        var aim = faa.Shapes.First(p => p.Kind == "Aiming" && !p.Secondary);
        Near(aim.Points.Max(p => p.X) - aim.Points.Min(p => p.X), 9.144);
        Near(aim.Points.Min(p => p.Y), -1600); // Chosen guidance, NOT the default FAA aim.
        var key = faa.Shapes.First(p => p.Kind == "Threshold");
        Near(key.Points.Max(p => p.Y) - key.Points.Min(p => p.Y), 45.72);
        Near(key.Points.Min(p => p.Y) - threshold, 6.096);
        groups = faa.Shapes.Where(p => p.Kind == "TouchdownZone" && !p.Secondary).GroupBy(p => p.Points.Min(v => v.Y)).OrderBy(g => g.Key).ToArray();
        Require(groups.Select(g => g.Count()).SequenceEqual(new[] { 6, 4, 4, 2, 2 }), "FAA fixed five groups, no invented 1020-ft group");
        for (var i = 0; i < groups.Length; i++) Near(groups[i].Key - threshold, new[] { 158.496, 463.296, 615.696, 768.096, 920.496 }[i]);
        foreach (var shape in faa.Shapes.Where(p => p.Kind == "TouchdownZone" && !p.Secondary))
            Require(shape.Points.Max(p => p.Y) <= -274.32, "FAA reciprocal midpoint exclusion");
        us.LengthMeters = 1500;
        Require(RunwayMarkingLayout.Build(us).Shapes.Count(p => p.Kind == "TouchdownZone" && !p.Secondary) == 6, "short FAA reciprocal strip keeps only first group");
        us.SecondaryPapi = null;
        us.LengthMeters = 1501;
        Require(RunwayMarkingLayout.Build(us).Shapes.Count(p => p.Kind == "TouchdownZone") == 10, "FAA one-end clearance keeps two groups at this available length");
        us.WidthMeters = 40;
        Require(RunwayMarkingLayout.Build(us).Shapes.Count(p => p.Kind == "Threshold") == 10, "nonstandard FAA width uses fixed stripe/gap and edge clearance");
        us.WidthMeters = 22.86;
        Require(RunwayMarkingLayout.Build(us).Shapes.Count(p => p.Kind == "Threshold") == 6, "75 ft FAA width has explicit six-stripe exception");
    }

    private static RunwayFacility GuidedRunway(double width, double length)
    {
        var runway = Runway(width, length);
        runway.PrimaryPapiQueried = runway.SecondaryPapiQueried = true;
        runway.PrimaryPapi = runway.SecondaryPapi = new RunwayPapi { StationFromEndMeters = 400, AngleDegrees = 3 };
        return runway;
    }

    private static RunwayGlideSlope GlideSlope(RunwayFacility runway, double station, double height, double angle, bool secondary) =>
        new RunwayGlideSlope { Ident = "TEST", LatitudeDegrees = (station - runway.LengthMeters / 2) * (secondary ? -1 : 1) / 6371008.8 * 180 / Math.PI,
            LongitudeDegrees = 0, AltitudeMeters = runway.AltitudeMeters + height, AngleDegrees = angle };
    private static void Near(double a, double b) => Require(Math.Abs(a - b) < 1e-6, $"Expected {b}, got {a}.");

    private static void ThresholdReplies()
    {
        Require(Marshal.SizeOf(typeof(RunwayThresholdData)) == 12, "native threshold layout");
        foreach (var primary in new[] { true, false })
        {
            var request = new RunwayFacilityRequest(new AirportFacility { Ident = "EGLL" }, 10, primary);
            request.AddThreshold(21, uint.MaxValue, uint.MaxValue, new RunwayThresholdData { Enable = 1, Length = 308, Width = 50 });
            var displaced = Runway(50, 3659); displaced.PrimaryThresholdOffsetMeters = displaced.SecondaryThresholdOffsetMeters = null;
            request.AddRunway(1, 8, 2, displaced, 21);
            request.AddAirport(8, 2);
            request.AddThreshold(20, uint.MaxValue, uint.MaxValue, new RunwayThresholdData { Enable = 0, Length = 99, Width = 50 });
            request.AddRunway(0, 8, 2, Runway(50, 3901), 20);
            Require(request.TryComplete(out var rows) && rows.Count == 2, "out-of-order parent joins");
            Require((primary ? rows[1].PrimaryThresholdOffsetMeters : rows[1].SecondaryThresholdOffsetMeters) == 308, "live EGLL displaced-threshold fixture");
            Require((primary ? rows[0].PrimaryThresholdOffsetMeters : rows[0].SecondaryThresholdOffsetMeters) == 0, "disabled threshold is verified zero");
            Require((primary ? rows[1].SecondaryThresholdOffsetMeters : rows[1].PrimaryThresholdOffsetMeters) == null, "unqueried end remains unknown");
        }
        foreach (var scenario in new[] { "missing", "parent", "duplicate", "length", "index", "size", "enabled", "nonfinite" })
        {
            var request = new RunwayFacilityRequest(new AirportFacility { Ident = "EGLL" }, 10, true);
            request.AddAirport(8, 1); request.AddRunway(0, 8, 1, Runway(50, 3659), 21);
            var data = new RunwayThresholdData { Enable = scenario == "enabled" ? 2 : 1,
                Length = scenario == "length" ? 4000 : scenario == "nonfinite" ? float.NaN : 308, Width = 50 };
            if (scenario != "missing") request.AddThreshold(scenario == "parent" ? 99u : 21u, scenario == "index" ? 1u : 0u, scenario == "size" ? 2u : 1u, data);
            if (scenario == "duplicate") request.AddThreshold(21, 0, 1, data);
            Require(!request.TryComplete(out _), "reject malformed optional transaction: " + scenario);
        }
    }

    private static void OptionalMetadataDoesNotLoseRunway()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var type = recorder.GetType();
        var cache = (Dictionary<string, IReadOnlyList<RunwayFacility>>)type.GetField("_runways", flags)!.GetValue(recorder)!;
        var requests = (Dictionary<uint, RunwayFacilityRequest>)type.GetField("_runwayRequests", flags)!.GetValue(recorder)!;
        cache["EGLL"] = new[] { Runway(50, 3659) };
        type.GetField("_runwayBatchKeys", flags)!.SetValue(recorder, new[] { "EGLL" });
        type.GetField("_runwayBatchActive", flags)!.SetValue(recorder, true);
        requests[100] = new RunwayFacilityRequest(new AirportFacility { Ident = "EGLL" }, -1, true);
        type.GetMethod("ExpireRunwayRequests", flags)!.Invoke(recorder, null);
        Require(recorder.RunwayFacilities.Count == 1 && !recorder.RunwayFacilities[0].PrimaryThresholdOffsetMeters.HasValue, "optional timeout keeps exact geometry, not fake zero");
        var incoming = Runway(50, 3659); incoming.PrimaryThresholdOffsetMeters = 308;
        var merge = type.GetMethod("MergeRunwayThresholds", flags)!;
        merge.Invoke(recorder, new object[] { "EGLL", new[] { incoming }, true });
        Require(cache["EGLL"][0].PrimaryThresholdOffsetMeters == 308, "matching metadata attaches");
        incoming.WidthMeters = 60; incoming.PrimaryThresholdOffsetMeters = 600;
        merge.Invoke(recorder, new object[] { "EGLL", new[] { incoming }, true });
        Require(cache["EGLL"][0].PrimaryThresholdOffsetMeters == 308 && cache["EGLL"][0].WidthMeters == 50, "mismatched scene never changes real width/offset");
    }

    private static RunwayFacility Runway(double width, double length) => new RunwayFacility
    {
        AirportIdent = "EDDM", PrimaryName = "09L", SecondaryName = "27R", SurfaceType = 4,
        WidthMeters = width, LengthMeters = length,
    };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
