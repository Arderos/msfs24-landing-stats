using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class LandingPresentationRegressionTests
{
    internal static void RunAll()
    {
        // This test enumerates the contract, so adding a scalar field without
        // updating either projection or non-finite normalization fails here.
        var fields = typeof(LandingRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.IsDefined(typeof(DataMemberAttribute), false)).ToArray();
        var detailOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LandingRecord.Series), nameof(LandingRecord.Engines), nameof(LandingRecord.ContactPoints),
            nameof(LandingRecord.ControllerTimeline), nameof(LandingRecord.SourceEvidenceTimeline),
        };
        var collections = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LandingRecord.ControlInputSources), nameof(LandingRecord.RawControllerSourceIndices),
            nameof(LandingRecord.RawControllerAxesSourceIndices), nameof(LandingRecord.ClosureReconstructionContactPointIndices),
            nameof(LandingRecord.RunwayGeometry),
        };
        var record = new LandingRecord();
        var ordinal = 0;
        foreach (var property in fields.Where(property => !detailOnly.Contains(property.Name) && !collections.Contains(property.Name)))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object value = type == typeof(double) ? 10.125 + ordinal++ :
                type == typeof(int) ? 7 : type == typeof(bool) ? true :
                type == typeof(string) ? "contract-" + property.Name :
                type == typeof(DateTime) ? new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc) :
                throw new InvalidOperationException("Define summary/detail projection for new field: " + property.Name);
            property.SetValue(record, value);
        }
        record.ControlInputSources = new List<string> { "synthetic-controller" };
        record.RawControllerSourceIndices = new List<int> { 2, 4 };
        record.RawControllerAxesSourceIndices = new List<int> { 1, 3 };
        record.ClosureReconstructionContactPointIndices = new[] { 0, 2 };
        var summary = LandingRepository.CreateSummary(record);
        var restored = LandingRecordFile.SanitizedSummaryCopy(record);
        LandingRecordFile.RestoreSummary(restored);
        foreach (var property in fields.Where(property => !detailOnly.Contains(property.Name)))
        {
            Check(Same(property.GetValue(record), property.GetValue(summary)), "Index projection omitted " + property.Name);
            Check(Same(property.GetValue(record), property.GetValue(restored)), "Storage normalization changed " + property.Name);
        }
        foreach (var property in fields.Where(property => property.PropertyType == typeof(double) || property.PropertyType == typeof(double?)))
        {
            property.SetValue(record, double.NaN);
        }
        var sanitized = LandingRecordFile.SanitizedSummaryCopy(record);
        foreach (var property in fields.Where(property => property.PropertyType == typeof(double) || property.PropertyType == typeof(double?)))
            Check(Equals(property.GetValue(sanitized), LandingRecord.NonFiniteStorageSentinel), "Non-finite storage leak in " + property.Name);
        LandingRecordFile.RestoreSummary(sanitized);
        foreach (var property in fields.Where(property => property.PropertyType == typeof(double) || property.PropertyType == typeof(double?)))
            Check(double.IsNaN((double)property.GetValue(sanitized)!), "Non-finite restoration omitted " + property.Name);

        Check(!typeof(LandingRecord).GetProperties().Any(property => property.Name.EndsWith("Display", StringComparison.Ordinal) ||
            property.Name.EndsWith("Tooltip", StringComparison.Ordinal)), "Persistence DTO still contains localized presentation.");
        foreach (var name in new[] { "RawCaptureSession", "BoundedTelemetryQueue", "LastLandingBugReportBuffer", "AirportMetadataPersistence", "RunwayMetadataPersistence" })
            Check(typeof(RawCaptureRepository).Assembly.GetType("LandingStats.App.Storage." + name) == null,
                "Historical fixture is still compiled into production: " + name);
        Check(!typeof(LandingPresentation).IsDefined(typeof(DataContractAttribute), false) &&
            !typeof(LandingPresentation).GetProperties().Any(property => property.IsDefined(typeof(DataMemberAttribute), false)),
            "The UI wrapper must not become a persistence contract.");
        var view = new LandingPresentation(new LandingRecord { InertialFpm = -321, PeakG150Milliseconds = 1.6 });
        Check(ReferenceEquals(view.Record, ((LandingPresentation)new LandingPresentationConverter().Convert(view.Record,
            typeof(object), null!, CultureInfo.InvariantCulture)!).Record), "Projection replaced the replay record identity.");
        var language = LocalizationManager.EffectiveLanguage;
        try
        {
            LocalizationManager.Apply("en");
            Check(view.InertialDisplay == "+321" && view.LandingFeelDisplay == "FIRM", "English presentation changed.");
            LocalizationManager.Apply("ru");
            Check(view.LandingFeelDisplay == "ЖЁСТКАЯ", "An existing wrapper does not reflect language changes.");
        }
        finally { LocalizationManager.Apply(language); }
    }

    private static bool Same(object? expected, object? actual)
    {
        if (expected is System.Collections.IEnumerable left && actual is System.Collections.IEnumerable right && expected is not string)
            return left.Cast<object>().SequenceEqual(right.Cast<object>());
        return Equals(expected, actual);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
