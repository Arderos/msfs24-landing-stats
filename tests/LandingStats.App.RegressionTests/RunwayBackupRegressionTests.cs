using System;
using System.IO;
using System.Linq;
using System.Threading;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Models;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static partial class GoogleDriveBackupRegressionTests
{
    public static void RunwayGeometryMetadataMergesDeterministically()
    {
        var earlier = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        var later = earlier.AddHours(1);
        Check(null, Geometry(earlier), true, earlier);
        Check(Geometry(earlier), null, true, earlier);
        Check(Geometry(later), Geometry(earlier), true, earlier);
        Check(Geometry(earlier), Geometry(later), true, earlier);
        var lowerCase = Geometry(later);
        lowerCase.AirportIdent = "lbsf";
        lowerCase.AirportRegion = "lb";
        lowerCase.Source = "simconnect";
        Check(lowerCase, Geometry(earlier), true, earlier);
        var unknownSurface = Geometry(null);
        unknownSurface.SurfaceType = null;
        Check(unknownSurface, Geometry(earlier), true, earlier);
        var knownThreshold = Geometry(earlier);
        knownThreshold.PrimaryThresholdOffsetMeters = 308; knownThreshold.SecondaryThresholdOffsetMeters = 0;
        Check(Geometry(later), knownThreshold, true, earlier);
        Check(knownThreshold, Geometry(later), true, earlier);
        var changedThreshold = Geometry(later); changedThreshold.PrimaryThresholdOffsetMeters = 0;
        Check(knownThreshold, changedThreshold, false, null);
        var crossingThreshold = Geometry(later); crossingThreshold.SecondaryThresholdOffsetMeters = 3400;
        Check(knownThreshold, crossingThreshold, false, null);
        var differentGeometry = Geometry(earlier);
        differentGeometry.WidthMeters = 60;
        Check(Geometry(later), differentGeometry, false, null);
        var differentSurface = Geometry(earlier);
        differentSurface.SurfaceType = 2;
        Check(Geometry(later), differentSurface, false, null);
        var differentSource = Geometry(earlier);
        differentSource.Source = "Other scenery";
        Check(Geometry(later), differentSource, false, null);
        Check(Geometry(later), Geometry(earlier), false, null, differentPose: true);
        var guidance = Geometry(earlier);
        guidance.PrimaryPapiQueried = guidance.SecondaryPapiQueried = true;
        guidance.PrimaryPapi = new RunwayPapi { StationFromEndMeters = 715, AngleDegrees = 3 };
        guidance.SecondaryPapi = new RunwayPapi { StationFromEndMeters = 426, AngleDegrees = 3 };
        guidance.PrimaryIlsGlideSlope = new RunwayGlideSlope { Ident = "ISL", Region = "LB", LatitudeDegrees = 42.697,
            LongitudeDegrees = 23.405, AltitudeMeters = 530.94, AngleDegrees = 3 };
        Check(Geometry(later), guidance, true, earlier);
        Check(guidance, Geometry(later), true, earlier);
        var absentPapi = Geometry(later); absentPapi.PrimaryPapiQueried = true;
        Check(absentPapi, Geometry(earlier), true, earlier);
        Check(guidance, absentPapi, false, null);
        var changedGuidance = Geometry(later);
        changedGuidance.PrimaryIlsGlideSlope = new RunwayGlideSlope { Ident = "ISL", Region = "LB", LatitudeDegrees = 42.697,
            LongitudeDegrees = 23.405, AltitudeMeters = 531.94, AngleDegrees = 3 };
        Check(guidance, changedGuidance, false, null);
        changedGuidance.PrimaryIlsGlideSlope.AltitudeMeters = 530.94;
        changedGuidance.PrimaryIlsGlideSlope.Ident = "isl"; changedGuidance.PrimaryIlsGlideSlope.Region = "lb";
        Check(guidance, changedGuidance, true, earlier);
    }

    private static RunwayFacility Geometry(DateTime? retrieved) => new RunwayFacility
    {
        AirportIdent = "LBSF", AirportRegion = "LB", LatitudeDegrees = 42.695, LongitudeDegrees = 23.405,
        AltitudeMeters = 531, HeadingTrueDegrees = 90, LengthMeters = 3600, WidthMeters = 45,
        PrimaryName = "09", SecondaryName = "27", SurfaceType = 4, RetrievedUtc = retrieved,
    };

    private static void Check(RunwayFacility? firstGeometry, RunwayFacility? secondGeometry,
        bool shouldMerge, DateTime? expectedRetrieval, bool differentPose = false)
    {
        var root = TemporaryDirectory("google-runway-merge");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var firstRepository = new LandingRepository(Path.Combine(root, "first"));
            var secondRepository = new LandingRepository(Path.Combine(root, "second"));
            var firstRecord = Record("shared", 215);
            var secondRecord = Record("shared", 215);
            firstRecord.RunwayGeometry = firstGeometry;
            secondRecord.RunwayGeometry = secondGeometry;
            if (differentPose)
            {
                firstRecord.Series.Add(new LandingSeriesPoint { TimeSeconds = 0, LatitudeDegrees = 42,
                    RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>() });
                secondRecord.Series.Add(new LandingSeriesPoint { TimeSeconds = 0, LatitudeDegrees = 43,
                    RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>() });
            }
            firstRepository.Save(firstRecord);
            secondRepository.Save(secondRecord);
            var firstBytes = firstRepository.ExportForBackup("shared");
            var secondBytes = secondRepository.ExportForBackup("shared");
            var firstSettings = new GoogleDriveCloudSettings();
            var secondSettings = new GoogleDriveCloudSettings();
            using var first = Service(drive, firstRepository, Path.Combine(root, "first-state.json"),
                () => firstSettings, value => firstSettings = value);
            using var second = Service(drive, secondRepository, Path.Combine(root, "second-state.json"),
                () => secondSettings, value => secondSettings = value);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            var result = second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(shouldMerge ? 0 : 1, result.ConflictedLandingIds.Count, "runway conflict decision");
            if (!shouldMerge)
            {
                Assert(firstRepository.ExportForBackup("shared").SequenceEqual(firstBytes), "conflict altered first landing");
                Assert(secondRepository.ExportForBackup("shared").SequenceEqual(secondBytes), "conflict altered second landing");
                return;
            }
            Equal(1, result.ResolvedLandingConflicts, "runway metadata merge count");
            var restored = secondRepository.LoadDetail(secondRepository.LoadAll().Single())!;
            Assert(restored.RunwayGeometry != null, "runway enrichment disappeared");
            Equal(expectedRetrieval, restored.RunwayGeometry!.RetrievedUtc, "earliest retrieval selected");
            Equal("LBSF", restored.RunwayGeometry.AirportIdent, "canonical runway airport");
            Equal(4, restored.RunwayGeometry.SurfaceType, "known surface retained");
            Equal(firstGeometry?.PrimaryThresholdOffsetMeters ?? secondGeometry?.PrimaryThresholdOffsetMeters,
                restored.RunwayGeometry.PrimaryThresholdOffsetMeters, "primary threshold survives serialization and cloud merge");
            Equal(firstGeometry?.SecondaryThresholdOffsetMeters ?? secondGeometry?.SecondaryThresholdOffsetMeters,
                restored.RunwayGeometry.SecondaryThresholdOffsetMeters, "secondary threshold survives serialization and cloud merge");
            Equal(firstGeometry?.PrimaryPapi?.StationFromEndMeters ?? secondGeometry?.PrimaryPapi?.StationFromEndMeters,
                restored.RunwayGeometry.PrimaryPapi?.StationFromEndMeters, "primary PAPI station survives storage/merge");
            Equal(firstGeometry?.SecondaryPapi?.AngleDegrees ?? secondGeometry?.SecondaryPapi?.AngleDegrees,
                restored.RunwayGeometry.SecondaryPapi?.AngleDegrees, "secondary PAPI angle survives storage/merge");
            Equal(firstGeometry?.PrimaryPapiQueried == true || secondGeometry?.PrimaryPapiQueried == true,
                restored.RunwayGeometry.PrimaryPapiQueried, "verified PAPI absence retained");
            Equal(firstGeometry?.PrimaryIlsGlideSlope?.AltitudeMeters ?? secondGeometry?.PrimaryIlsGlideSlope?.AltitudeMeters,
                restored.RunwayGeometry.PrimaryIlsGlideSlope?.AltitudeMeters, "ILS altitude survives storage/merge");
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(firstRepository.ExportForBackup("shared").SequenceEqual(secondRepository.ExportForBackup("shared")),
                "runway merge did not converge byte-for-byte");
            Equal(0, second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult().ConflictedLandingIds.Count,
                "converged runway re-conflicted");
        }
        finally { DeleteDirectory(root); }
    }
}
