using System;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class RunwayMarkingConventionTests
{
    public static void RunAll()
    {
        foreach (var id in new[] { "KJFK", "PANC", "PFYU", "POLI", "PPIZ", "PHNL", "TJSJ", "TIST",
            "PGUM", "PGSN", "PMDY", "PWAK", "PJON", "NSAS", "NSFQ", "NSTU" })
            Require(UsesFaa(id, ""), "US ICAO family: " + id);
        foreach (var region in new[] { "K1", "K2", "K3", "K4", "K5", "K6", "K7", "PA", "PF", "PP",
            "PO", "PH", "PG", "PM", "PW", "PB", "PJ", "TJ", "TI" })
            Require(UsesFaa("7A3", region), "US facility region supports local identifiers: " + region);

        foreach (var id in new[] { "CYVR", "CYYZ", "LBSF", "UUEE", "EGLL", "NZAA", "YSSY", "NSFA",
            "NSFI", "PLCH", "PTRO", "PKMJ", "PCIS", "K7", "K123", "7A3", "", " K JFK " })
            Require(!UsesFaa(id, ""), "non-US/unknown identifiers retain ICAO default: " + id);
        foreach (var region in new[] { "CY", "NS", "PL", "PT", "PK", "PC", "US", "UU", "LB", "EG",
            "K", "K0", "K8", "K9", "K10", "USA", "", "??" })
            Require(!UsesFaa("LOCAL", region), "region is not an unambiguous FAA jurisdiction: " + region);

        Require(UsesFaa(" kjfk ", ""), "case and whitespace in identifiers");
        Require(UsesFaa("7a3", " k7 "), "case and whitespace in facility regions");
        Require(!RunwayMarkingConvention.UsesFaa(new RunwayFacility { AirportIdent = null!, AirportRegion = null! }),
            "missing serialized metadata retains ICAO default");
    }

    private static bool UsesFaa(string ident, string region) => RunwayMarkingConvention.UsesFaa(
        new RunwayFacility { AirportIdent = ident, AirportRegion = region });

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
