using System;
using System.IO;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class LandingSitePersistenceRegressionTests
{
    public static void RunAll()
    {
        using var fixture = new Fixture();
        DeletedRecordsStayDeleted(fixture);
        MetadataDoesNotOverwriteLaterTelemetry(fixture);
        ReportInheritsSameRecordLateAirport(fixture);
        FinalContactSupersedesInitialAirport(fixture);
        ReportInheritsCurrentRunwayOnlyForSameAirport(fixture);
    }

    private static void DeletedRecordsStayDeleted(Fixture fixture)
    {
        var record = Record();
        fixture.Repository.Save(record);
        var resolved = Clone(record); resolved.Airport = "FINAL";
        Assert(fixture.Repository.Delete(record.Id), "Fixture landing must be deletable.");
        LandingSiteMetadataPersistence.Save(fixture.Repository, fixture.Gate, new[] { resolved });
        Assert(!fixture.Repository.Contains(record.Id) && fixture.Repository.LoadAll().All(item => item.Id != record.Id),
            "A facility result arriving after deletion must not resurrect detail or index entries.");
    }

    private static void MetadataDoesNotOverwriteLaterTelemetry(Fixture fixture)
    {
        var record = Record();
        fixture.Repository.Save(record);
        var resolved = Clone(record);
        resolved.Airport = "FINAL"; resolved.Runway = "09"; resolved.RunwayGeometry = Runway("FINAL");
        // A later operation has already persisted a fuller record while the
        // report worker still owns the earlier analyzer snapshot.
        record.Series.Add(Point(1, 1.4, 1.001));
        record.InertialFpm = 237;
        record.ControlInputSources.Add("latest input source");
        fixture.Repository.Save(record);
        LandingSiteMetadataPersistence.Save(fixture.Repository, fixture.Gate, new[] { resolved });
        var current = fixture.Reload(record.Id);
        Assert(current.Series.Count == 2 && current.Series[1].GForce == 1.4 && current.InertialFpm == 237 &&
               current.ControlInputSources.SequenceEqual(new[] { "latest input source" }),
            "Facility persistence must not replace newer series, analysis, or input metadata with a stale report copy.");
        Assert(current.Airport == "FINAL" && current.Runway == "09" && current.RunwayGeometry!.AirportIdent == "FINAL",
            "Terminal landing site must still be durable in the same detail file.");
        resolved.RunwayGeometry.WidthMeters = 99;
        Assert(fixture.Reload(record.Id).RunwayGeometry!.WidthMeters == 45,
            "Caller mutation after saving must not change durable runway geometry.");
    }

    private static void ReportInheritsSameRecordLateAirport(Fixture fixture)
    {
        var record = Record();
        fixture.Repository.Save(record);
        var unresolvedReport = Clone(record);
        var summary = fixture.Repository.LoadAll().Single(item => item.Id == record.Id);
        summary.Airport = "LATE"; summary.AirportDistanceNauticalMiles = .25;
        fixture.Repository.UpdateSummaries(new[] { summary });
        LandingSiteMetadataPersistence.Save(fixture.Repository, fixture.Gate, new[] { unresolvedReport });
        Assert(unresolvedReport.Airport == "LATE" && unresolvedReport.AirportDistanceNauticalMiles == .25,
            "A report frozen before the ARP reply must inherit the same landing's late airport update.");
        Assert(fixture.Reload(record.Id).Airport == "LATE", "Late ARP enrichment must remain durable.");

        var other = Record(); fixture.Repository.Save(other);
        LandingSiteMetadataPersistence.Save(fixture.Repository, fixture.Gate, new[] { other });
        Assert(other.Airport == "Unknown airport", "Another landing's known airport must not leak into this report.");
    }

    private static void FinalContactSupersedesInitialAirport(Fixture fixture)
    {
        var record = Record(); record.Airport = "INITIAL"; record.Runway = "27";
        record.RunwayGeometry = Runway("INITIAL");
        fixture.Repository.Save(record);
        var final = Clone(record); final.Airport = "ACTUAL"; final.Runway = "09";
        final.RunwayGeometry = Runway("ACTUAL"); final.AirportDistanceNauticalMiles = null;
        LandingSiteMetadataPersistence.Save(fixture.Repository, fixture.Gate, new[] { final });
        var current = fixture.Reload(record.Id);
        Assert(current.Airport == "ACTUAL" && current.Runway == "09" && current.RunwayGeometry!.AirportIdent == "ACTUAL",
            "The terminal contact site must replace an earlier, contradictory airport/runway identity.");
    }

    private static void ReportInheritsCurrentRunwayOnlyForSameAirport(Fixture fixture)
    {
        var record = Record(); record.Airport = "KNOWN"; record.Runway = "09"; record.RunwayGeometry = Runway("KNOWN");
        fixture.Repository.Save(record);
        var report = Clone(record); report.Runway = "—"; report.RunwayGeometry = null;
        LandingSiteMetadataPersistence.Save(fixture.Repository, fixture.Gate, new[] { report });
        Assert(report.RunwayGeometry?.AirportIdent == "KNOWN" && report.Runway == "09",
            "A same-airport late runway result should enrich an otherwise incomplete report.");
        report.Airport = "ACTUAL"; report.Runway = "—"; report.RunwayGeometry = null;
        LandingSiteMetadataPersistence.Save(fixture.Repository, fixture.Gate, new[] { report });
        var current = fixture.Reload(record.Id);
        Assert(current.Airport == "ACTUAL" && current.RunwayGeometry == null && current.Runway == "—",
            "An old runway must never be attached to a newly resolved different airport.");
    }

    private static LandingRecord Record()
    {
        var record = new LandingRecord { TouchdownLatitudeDegrees = 1, TouchdownLongitudeDegrees = 2, InertialFpm = 100 };
        record.Series.Add(Point(0, 1.1, 1));
        return record;
    }
    private static LandingSeriesPoint Point(double time, double g, double latitude) => new LandingSeriesPoint {
        TimeSeconds = time, GForce = g, LatitudeDegrees = latitude, LongitudeDegrees = 2,
        RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(),
        RawControllerYAxisAgeSeconds = Array.Empty<double>() };
    private static LandingRecord Clone(LandingRecord record) => LandingRecordFile.FromRecord(record).ToRecord();
    private static RunwayFacility Runway(string airport) => new RunwayFacility { AirportIdent = airport, PrimaryName = "09",
        SecondaryName = "27", LatitudeDegrees = 1, LongitudeDegrees = 2, HeadingTrueDegrees = 90, LengthMeters = 3000, WidthMeters = 45 };
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "landing-site-persistence-" + Guid.NewGuid().ToString("N"));
        public Fixture() { Repository = new LandingRepository(_root); }
        public LandingRepository Repository { get; }
        public object Gate { get; } = new object();
        public LandingRecord Reload(string id)
        {
            var repository = new LandingRepository(_root);
            return repository.LoadDetail(repository.LoadAll().Single(record => record.Id == id))!;
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
