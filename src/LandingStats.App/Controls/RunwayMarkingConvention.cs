using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.Controls;

internal static class RunwayMarkingConvention
{
    // FAA JO 7350.9, international identifiers; ICAO APA-TFG/16 regional table.
    // NS is shared by Samoa and American Samoa, so it is deliberately excluded.
    private static readonly HashSet<string> UsRegions = new HashSet<string>(StringComparer.Ordinal)
    {
        "PA", "PB", "PF", "PG", "PH", "PJ", "PM", "PO", "PP", "PW", "TI", "TJ"
    };

    public static bool UsesFaa(RunwayFacility runway)
    {
        var region = (runway.AirportRegion ?? string.Empty).Trim().ToUpperInvariant();
        // MSFS divides the contiguous-US ICAO region into K1..K7. This also
        // identifies local airfields whose identifiers are not four-letter ICAOs.
        if (UsRegions.Contains(region) ||
            (region.Length == 2 && region[0] == 'K' && region[1] >= '1' && region[1] <= '7')) return true;

        var id = (runway.AirportIdent ?? string.Empty).Trim().ToUpperInvariant();
        if (id.Length != 4 || !id.All(c => c >= 'A' && c <= 'Z')) return false;
        // FAA Pacific Chart Supplement lists these American Samoa airports;
        // matching NS generally would incorrectly include independent Samoa.
        return id[0] == 'K' || UsRegions.Contains(id.Substring(0, 2)) ||
            id == "NSAS" || id == "NSFQ" || id == "NSTU";
    }
}
