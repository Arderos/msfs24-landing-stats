using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

// Resolves one immutable episode snapshot. Runway evidence outranks an airport
// reference point; absence of a runway match must not discard airport evidence.
internal static class LandingSiteResolver
{
    internal static bool Apply(LandingRecord record, EpisodeFacilitySnapshot snapshot,
        TelemetryDecisionJournal? journal = null)
    {
        if (record.IsSummaryOnly) return false;
        var oldAirport = record.Airport;
        var oldDistance = record.AirportDistanceNauticalMiles;
        var oldRunway = record.Runway;
        var oldGeometry = record.RunwayGeometry;
        journal?.Record(0, "landing_site", "resolution_sources", "Contact and approach facilities belong to the same episode.",
            new Dictionary<string, double> { ["episode_id"] = snapshot.EpisodeId,
                ["contact_number"] = record.ContactNumber, ["preliminary_runways"] = snapshot.PreliminaryRunways.Count,
                ["preliminary_airports"] = snapshot.PreliminaryAirports.Count, ["final_runways"] = snapshot.FinalRunways.Count,
                ["final_airports"] = snapshot.FinalAirports.Count, ["final_lookup_completed"] = snapshot.FinalLookupCompleted ? 1 : 0,
                ["preliminary_candidates_complete"] = snapshot.PreliminaryRunwayCandidatesComplete ? 1 : 0,
                ["final_candidates_complete"] = snapshot.FinalRunwayCandidatesComplete ? 1 : 0 });

        var finalRunway = snapshot.FinalRunwayCandidatesComplete ? RunwayResolver.Select(record, snapshot.FinalRunways, journal) : null;
        if (!snapshot.FinalRunwayCandidatesComplete)
            journal?.Record(0, "landing_site", "final_candidates_incomplete",
                "Complete airport replies are retained, but missing neighboring runway replies cannot prove a unique final runway.");
        if (finalRunway != null)
        {
            AssignRunway(record, finalRunway, RunwayResolver.NameForDirection(record, finalRunway));
            journal?.Record(0, "landing_site", "actual_runway_selected", record.Airport + " / " + record.Runway);
            return Changed();
        }

        var finalAirport = Nearest(record, snapshot.FinalAirports, out var finalDistance);
        // A previous final selection must not be downgraded by a repeated or
        // incomplete application. A conflicting fresh airport is relevant only
        // when the recorded track also contradicts that runway's geometry.
        if (record.RunwayGeometry != null)
        {
            if (finalAirport != null && !SameAirport(finalAirport.Ident, record.RunwayGeometry.AirportIdent) &&
                RunwayResolver.Select(record, new[] { record.RunwayGeometry }) == null)
            {
                record.RunwayGeometry = null;
                record.Runway = "—";
                AssignAirport(record, finalAirport, finalDistance);
                journal?.Record(0, "landing_site", "existing_runway_conflicts_with_actual_airport", record.Airport);
            }
            else
            {
                record.Airport = record.RunwayGeometry.AirportIdent;
                journal?.Record(0, "landing_site", "existing_runway_preserved", record.Airport + " / " + record.Runway);
            }
            return Changed();
        }

        var preliminaryName = "—";
        var preliminary = snapshot.PreliminaryRunwayCandidatesComplete
            ? SelectApproach(snapshot.ApproachSamples, snapshot.PreliminaryRunways, out preliminaryName, journal) : null;
        if (!snapshot.PreliminaryRunwayCandidatesComplete)
            journal?.Record(0, "landing_site", "approach_candidates_incomplete",
                "The approach runway candidate set was incomplete; airport identity remains usable independently.");
        if (preliminary != null)
        {
            // Nearby helipads/ARPs are not stronger evidence than a strip that
            // actually contains the contact. Conversely, never attach an old
            // intended strip to a contact established at a different airport.
            var contradiction = finalAirport != null && !SameAirport(finalAirport.Ident, preliminary.AirportIdent) &&
                RunwayResolver.Select(record, new[] { preliminary }) == null;
            if (!contradiction)
            {
                AssignRunway(record, preliminary, preliminaryName);
                journal?.Record(0, "landing_site", "approach_runway_fallback", record.Airport + " / " + record.Runway);
                return Changed();
            }
            journal?.Record(0, "landing_site", "approach_runway_conflicts_with_actual_airport",
                preliminary.AirportIdent + " / " + preliminaryName + "; actual airport: " + finalAirport!.Ident);
        }

        var airport = finalAirport;
        var distance = finalDistance;
        var source = "final_airport_selected";
        if (airport == null)
        {
            airport = Nearest(record, snapshot.PreliminaryAirports, out distance);
            source = "approach_airport_fallback";
        }
        if (airport != null)
        {
            AssignAirport(record, airport, distance);
            journal?.Record(0, "landing_site", source, airport.Ident,
                new Dictionary<string, double> { ["distance_nm"] = distance });
        }
        else journal?.Record(0, "landing_site", "site_unresolved", "No usable runway or airport evidence in this episode.");
        return Changed();

        bool Changed() => !string.Equals(oldAirport, record.Airport, StringComparison.Ordinal) ||
            oldDistance != record.AirportDistanceNauticalMiles || oldRunway != record.Runway ||
            !ReferenceEquals(oldGeometry, record.RunwayGeometry);
    }

    // Extrapolate recorded movement to the runway threshold, never aircraft
    // nose direction. Use the same strip/heading allowances as RunwayResolver.
    // A crossing/parallel ambiguity has no preliminary result, not a guess.
    internal static RunwayFacility? SelectApproach(IReadOnlyList<TelemetrySample> samples,
        IReadOnlyList<RunwayFacility> runways, out string runwayName, TelemetryDecisionJournal? journal = null)
    {
        runwayName = "—";
        if (!TryApproachTrack(samples, out var last, out var eastVelocity, out var northVelocity))
        {
            journal?.Record(0, "landing_site", "approach_track_unavailable", "No contiguous moving airborne approach track.");
            return null;
        }
        var track = Math.Atan2(eastVelocity, northVelocity) * 180 / Math.PI;
        RunwayFacility? match = null;
        foreach (var runway in runways)
        {
            if (runway == null || !runway.HasValidGeometry || runway.IsWater) continue;
            var difference = Math.Abs(NormalizeAngle(track - runway.HeadingTrueDegrees));
            if (Math.Min(difference, 180 - difference) > 20) continue;
            var heading = runway.HeadingTrueDegrees * Math.PI / 180;
            var alongVelocity = eastVelocity * Math.Sin(heading) + northVelocity * Math.Cos(heading);
            var acrossVelocity = eastVelocity * Math.Cos(heading) - northVelocity * Math.Sin(heading);
            var primary = alongVelocity > 0;
            RunwayResolver.OffsetMeters(runway.LatitudeDegrees, runway.LongitudeDegrees,
                last!.LatitudeDegrees, last.LongitudeDegrees, out var east, out var north);
            var along = east * Math.Sin(heading) + north * Math.Cos(heading);
            var across = east * Math.Cos(heading) - north * Math.Sin(heading);
            var threshold = primary ? -runway.LengthMeters / 2 + (runway.PrimaryThresholdOffsetMeters ?? 0) :
                runway.LengthMeters / 2 - (runway.SecondaryThresholdOffsetMeters ?? 0);
            // Once over the strip, use its current station. A runway completely
            // behind the aircraft is not the intended landing runway.
            if ((primary && along > runway.LengthMeters / 2) || (!primary && along < -runway.LengthMeters / 2)) continue;
            var timeToStrip = Math.Max(0, (threshold - along) / alongVelocity);
            var projectedAcross = across + acrossVelocity * timeToStrip;
            if (!Finite(projectedAcross) || Math.Abs(projectedAcross) > runway.WidthMeters / 2 + 10) continue;
            journal?.Record(0, "landing_site", "approach_candidate", runway.AirportIdent + " / " +
                (primary ? runway.PrimaryName : runway.SecondaryName), new Dictionary<string, double> {
                    ["projected_across_m"] = projectedAcross, ["time_to_strip_s"] = timeToStrip,
                    ["track_heading_deg"] = (track + 360) % 360 });
            if (match != null)
            {
                journal?.Record(0, "landing_site", "approach_runways_ambiguous", "More than one strip intersects the approach track.");
                runwayName = "—";
                return null;
            }
            match = runway;
            runwayName = primary ? runway.PrimaryName : runway.SecondaryName;
        }
        if (match == null) journal?.Record(0, "landing_site", "approach_runway_unresolved", "No strip intersects the approach track.");
        return match;
    }

    private static bool TryApproachTrack(IReadOnlyList<TelemetrySample> samples, out TelemetrySample? last,
        out double eastVelocity, out double northVelocity)
    {
        last = null;
        eastVelocity = northVelocity = 0;
        if (samples.Count < 2) return false;
        last = samples[samples.Count - 1];
        if (!Eligible(last)) return false;
        var first = last;
        for (var index = samples.Count - 2; index >= 0; index--)
        {
            var sample = samples[index];
            if (!Eligible(sample)) break;
            var elapsed = first.SimulationTimeSeconds - sample.SimulationTimeSeconds;
            if (elapsed <= 0 || elapsed > .5 || first.FlightSegment != sample.FlightSegment) break;
            if (last.SimulationTimeSeconds - sample.SimulationTimeSeconds > 2) break;
            first = sample;
        }
        var duration = last.SimulationTimeSeconds - first.SimulationTimeSeconds;
        if (duration < .2) return false;
        RunwayResolver.OffsetMeters(first.LatitudeDegrees, first.LongitudeDegrees,
            last.LatitudeDegrees, last.LongitudeDegrees, out var east, out var north);
        var distance = Math.Sqrt(east * east + north * north);
        if (distance < 5 || distance / duration > 200) return false;
        eastVelocity = east / duration;
        northVelocity = north / duration;
        return true;
    }

    private static bool Eligible(TelemetrySample sample) => sample.MotionSimulation && !sample.OnGround &&
        sample.SimulatorState != TelemetrySimulatorState.Paused && sample.SimulatorState != TelemetrySimulatorState.Inactive &&
        Finite(sample.SimulationTimeSeconds) && ValidPosition(sample.LatitudeDegrees, sample.LongitudeDegrees);

    private static AirportFacility? Nearest(LandingRecord record, IReadOnlyList<AirportFacility> airports, out double distance)
    {
        distance = double.NaN;
        if (!record.TouchdownLatitudeDegrees.HasValue || !record.TouchdownLongitudeDegrees.HasValue ||
            !ValidPosition(record.TouchdownLatitudeDegrees.Value, record.TouchdownLongitudeDegrees.Value)) return null;
        var valid = airports.Where(airport => airport != null && !string.IsNullOrWhiteSpace(airport.Ident) &&
            ValidPosition(airport.LatitudeDegrees, airport.LongitudeDegrees)).ToArray();
        return AirportResolver.FindNearest(record.TouchdownLatitudeDegrees.Value, record.TouchdownLongitudeDegrees.Value, valid, out distance);
    }

    private static void AssignRunway(LandingRecord record, RunwayFacility runway, string name)
    {
        if (!SameAirport(record.Airport, runway.AirportIdent)) record.AirportDistanceNauticalMiles = null;
        record.Airport = runway.AirportIdent;
        record.Runway = name;
        record.RunwayGeometry = runway;
    }

    private static void AssignAirport(LandingRecord record, AirportFacility airport, double distance)
    {
        record.Airport = airport.Ident;
        record.AirportDistanceNauticalMiles = distance;
    }

    private static bool SameAirport(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool ValidPosition(double latitude, double longitude) => Finite(latitude) && Math.Abs(latitude) <= 90 &&
        Finite(longitude) && Math.Abs(longitude) <= 180;
    private static double NormalizeAngle(double degrees) => ((degrees + 180) % 360 + 360) % 360 - 180;
}
