using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ReplayControlEpisodeRegressionTests
{
    public static void RunAll()
    {
        var first = Contact(1);
        var second = Contact(2);
        var expected = ReplayControlInputs.Resolve(first);
        Require(expected.Pitch?.SourceIndex == 1 && expected.Roll?.SourceIndex == 0 && expected.Yaw?.SourceIndex == 2,
            "canonical fixture must resolve the original physical stick");
        var independent = ReplayControlInputs.Resolve(second);
        Require(independent.Pitch?.SourceIndex != expected.Pitch?.SourceIndex || independent.Roll?.SourceIndex != expected.Roll?.SourceIndex,
            "the truncated second-contact window must reproduce independent-selection disagreement");
        foreach (var contacts in new[] { new[] { first, second }, new[] { second, first } })
        {
            var a = ReplayControlInputs.Resolve(first, contacts);
            var journal = new TelemetryDecisionJournal();
            var b = ReplayControlInputs.Resolve(second, contacts, journal);
            Require(Sources(a).SequenceEqual(Sources(expected)) && Sources(b).SequenceEqual(Sources(expected)),
                "contact selection and collection order cannot change episode bindings");
            var point = second.Series[17];
            var sampled = b.Sample(point.TimeSeconds);
            Require(Math.Abs(sampled.PitchPercent!.Value - point.RawControllerAxesPercent[2]) < 1e-9 &&
                Math.Abs(sampled.RollPercent!.Value + point.RawControllerAxesPercent[3]) < 1e-9 &&
                Math.Abs(sampled.YawPercent!.Value - point.RawControllerAxesPercent[1]) < 1e-9,
                "remapping retains the selected contact's column, raw amplitude, polarity and own sample time");
            Require(journal.Snapshot().Entries.Any(e => e.Code == "episode_binding_source" && e.Details["binding_contact_number"] == 1) &&
                journal.Snapshot().Entries.Count(e => e.Code == "episode_axis_mapped") == 3,
                "the decision journal identifies the episode source and each remapped role");
        }

        var foreign = Contact(1); foreign.TimestampUtc = foreign.TimestampUtc.AddSeconds(1);
        Require(Sources(ReplayControlInputs.Resolve(second, new[] { foreign })).SequenceEqual(Sources(independent)),
            "foreign episodes cannot provide physical-axis bindings");
        var summary = Contact(1); summary.IsSummaryOnly = true; summary.Series.Clear();
        Require(Sources(ReplayControlInputs.Resolve(second, new[] { summary })).SequenceEqual(Sources(independent)),
            "a summary-only first contact is not used as matching evidence");

        var missing = Contact(2);
        RemoveColumn(missing, missing.RawControllerAxesSourceIndices.IndexOf(1));
        var missingInputs = ReplayControlInputs.Resolve(missing, new[] { first });
        Require(missingInputs.Pitch == null && missingInputs.Roll?.SourceIndex == 0 && missingInputs.Yaw?.SourceIndex == 2,
            "a missing selected axis stays unavailable instead of switching to another device");
        Require(ReplayControlInputs.Resolve(missing).Pitch != null, "missing-axis fixture retains independent alternatives");

        var duplicate = Contact(2); duplicate.RawControllerAxesSourceIndices[0] = duplicate.RawControllerAxesSourceIndices[1];
        Require(Sources(ReplayControlInputs.Resolve(duplicate, new[] { first })).All(source => source == -1),
            "duplicate column provenance cannot be remapped");
        var replaced = Contact(2); replaced.ControlInputSources[0] = "replacement device";
        Require(Sources(ReplayControlInputs.Resolve(replaced, new[] { first })).All(source => source == -1),
            "the same slot with a different recorded device is not the episode binding");
        var duplicateName = Contact(2); duplicateName.ControlInputSources[1] = duplicateName.ControlInputSources[0];
        Require(Sources(ReplayControlInputs.Resolve(duplicateName, new[] { first })).All(source => source == -1),
            "ambiguous device names cannot provide remapping provenance");
        var unnamed = Contact(2); unnamed.ControlInputSources.Clear();
        Require(Sources(ReplayControlInputs.Resolve(unnamed, new[] { first })).All(source => source == -1),
            "one-sided missing device identity cannot silently establish a replacement");
        var oldFirst = Contact(1); oldFirst.ControlInputSources.Clear();
        Require(Sources(ReplayControlInputs.Resolve(unnamed, new[] { oldFirst })).SequenceEqual(Sources(expected)),
            "native source indices still support old episode records without device names");

        var invalid = Contact(2); invalid.Series[10].TimeSeconds = double.NaN;
        Require(ReplayControlInputs.Resolve(invalid, new[] { first }).Sample(0).PitchPercent == null,
            "episode reuse cannot bypass recorded-clock validation");
        var stale = Contact(2); foreach (var point in stale.Series) point.RawControllerAxesAgeSeconds[2] = 1;
        Require(ReplayControlInputs.Resolve(stale, new[] { first }).Sample(stale.Series[17].TimeSeconds).PitchPercent == null,
            "episode reuse cannot bypass raw-axis validity and freshness");
        Legacy();
        ExplicitIdentity(first, second, expected, independent);
    }

    private static void ExplicitIdentity(LandingRecord first, LandingRecord second,
        ReplayControlInputs expected, ReplayControlInputs independent)
    {
        first.RecordingEpisodeId = second.RecordingEpisodeId = "capture:1";
        var otherFirst = Contact(1); otherFirst.RecordingEpisodeId = "capture:2";
        var otherSecond = Contact(2); otherSecond.RecordingEpisodeId = "capture:2";
        var contacts = new[] { otherFirst, second, otherSecond, first };
        Require(Sources(ReplayControlInputs.Resolve(second, contacts)).SequenceEqual(Sources(expected)),
            "same-timestamp physical episodes do not hide each other's canonical first contact");
        Require(Sources(ReplayControlInputs.Resolve(second, new[] { otherFirst, otherSecond })).SequenceEqual(Sources(independent)),
            "an explicitly different physical episode cannot supply axis matching evidence");
        var legacyFirst = Contact(1);
        Require(Sources(ReplayControlInputs.Resolve(second, new[] { legacyFirst })).SequenceEqual(Sources(independent)),
            "a missing identity cannot supply axis matching evidence to a new episode");
    }

    private static LandingRecord Contact(int number)
    {
        var record = new LandingRecord { TimestampUtc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc),
            AircraftTitle = "Episode fixture", AircraftType = "fixture", ContactCount = 2, ContactNumber = number };
        record.ControlInputSources.AddRange(new[] { "device 0", "device 1", "device 2", "device 3" });
        var sources = number == 1 ? new[] { 0, 1, 2, 6, 7, 23 } : new[] { 23, 2, 1, 0, 7, 6 };
        record.RawControllerAxesSourceIndices.AddRange(sources);
        // Shared physical timeline, but separate capture windows, sample phases
        // and compact column ordering. Simulator responses change after landing.
        for (var i = 0; i < (number == 1 ? 1020 : 110); i++)
        {
            var t = number == 1 ? i * .05 : 40.075 + i * .1;
            var pitch = 9 * Math.Sin(t * .73) + 2 * Math.Cos(t * 1.41);
            var roll = 5 * Math.Cos(t * .97) + 3 * Math.Sin(t * 2.13);
            var yaw = 8 * Math.Sin(t * 1.17) + 2 * Math.Cos(t * 2.21);
            var throttle = 10 * Math.Sin(t * 7.31);
            var other = 13 * Math.Cos(t * 5.51);
            var values = new Dictionary<int, double> { [0] = -roll, [1] = pitch, [2] = yaw, [6] = other, [7] = 0, [23] = throttle };
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = t - (number == 1 ? 40 : 46),
                OnGround = t >= 40 && t < 42 || t >= 46,
                PilotPitchPercent = t < 40 ? pitch : throttle, PilotRollPercent = t < 40 ? roll : other, PilotYawPercent = yaw,
                RawControllerAxesPercent = sources.Select(source => values[source]).ToArray(),
                RawControllerAxesValid = Enumerable.Repeat(true, sources.Length).ToArray(), RawControllerAxesAgeSeconds = new double[sources.Length] });
        }
        return record;
    }

    private static void Legacy()
    {
        var first = Contact(1); var second = Contact(2);
        foreach (var record in new[] { first, second })
        {
            record.RawControllerAxesSourceIndices.Clear(); record.ControlInputSources.Clear();
            record.RawControllerSourceIndices.AddRange(record.ContactNumber == 1 ? new[] { 0, 1 } : new[] { 1, 0 });
            record.RawPitchInputSourceIndex = record.ContactNumber == 1 ? 1 : 0;
            record.RawPitchInputCorrelation = -.6; record.RawPitchInputLagSeconds = .1;
            foreach (var point in record.Series)
            {
                point.RawControllerYAxisPercent = record.RawControllerSourceIndices.Select(source => source == 1 ? 17.0 : 29.0).ToArray();
                point.RawControllerYAxisValid = new[] { true, true }; point.RawControllerYAxisAgeSeconds = new double[2];
            }
        }
        var inputs = ReplayControlInputs.Resolve(second, new[] { first });
        Require(inputs.Pitch?.SourceIndex == 7 && inputs.Pitch.Legacy && inputs.Roll == null && inputs.Yaw == null &&
            inputs.Sample(second.Series[17].TimeSeconds).PitchPercent == -17,
            "historical Y-only episode bindings keep saved source, polarity and remapped compact column");
    }

    private static IEnumerable<int> Sources(ReplayControlInputs inputs) => new[] { inputs.Pitch?.SourceIndex ?? -1,
        inputs.Roll?.SourceIndex ?? -1, inputs.Yaw?.SourceIndex ?? -1 };

    private static void RemoveColumn(LandingRecord record, int column)
    {
        record.RawControllerAxesSourceIndices.RemoveAt(column);
        foreach (var point in record.Series)
        {
            point.RawControllerAxesPercent = point.RawControllerAxesPercent.Where((_, i) => i != column).ToArray();
            point.RawControllerAxesValid = point.RawControllerAxesValid.Where((_, i) => i != column).ToArray();
            point.RawControllerAxesAgeSeconds = point.RawControllerAxesAgeSeconds.Where((_, i) => i != column).ToArray();
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
