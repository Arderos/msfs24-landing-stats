using System;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class RunwayAirportMetadataRegressionTests
{
    public static void RunwayIdentityOverridesNearestAirportGuess()
    {
        var runway = new RunwayFacility
        {
            AirportIdent = "REAL", LatitudeDegrees = 0, LongitudeDegrees = 0,
            HeadingTrueDegrees = 90, LengthMeters = 2200, WidthMeters = 45,
            PrimaryName = "09", SecondaryName = "27",
        };
        var record = Record();
        Assert(MainWindow.TryResolveRunway(record, new[] { runway }), "fixture runway not selected");
        Assert(record.Airport == "REAL" && record.Runway == "09", "nearest ARP identity survived actual runway match");
        Assert(record.AirportDistanceNauticalMiles == null, "distance to a different ARP survived correction");

        var sameAirport = Record();
        sameAirport.Airport = "real";
        Assert(MainWindow.TryResolveRunway(sameAirport, new[] { runway }), "same airport runway not selected");
        Assert(sameAirport.AirportDistanceNauticalMiles == .3, "correct airport distance was discarded");

        var offRunway = Record();
        offRunway.TouchdownLatitudeDegrees = 1;
        Assert(!MainWindow.TryResolveRunway(offRunway, new[] { runway }), "off-runway touchdown acquired geometry");
        Assert(offRunway.Airport == "NEAR" && offRunway.Runway == "—" && offRunway.AirportDistanceNauticalMiles == .3,
            "failed match changed airport metadata");
    }

    private static LandingRecord Record() => new LandingRecord
    {
        Airport = "NEAR", AirportDistanceNauticalMiles = .3, TouchdownLatitudeDegrees = 0, TouchdownLongitudeDegrees = 0,
        Series = Enumerable.Range(0, 21).Select(i => new LandingSeriesPoint
        {
            TimeSeconds = -1 + i * .05, LatitudeDegrees = 0, LongitudeDegrees = (-1 + i * .05) * .0005,
        }).ToList(),
    };

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
