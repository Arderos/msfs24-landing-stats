using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingStats.App.Controls;

internal static class ChartValueSanitizer
{
    public static double[] FiniteValues(IEnumerable<double> values)
    {
        return values.Where(IsFinite).ToArray();
    }

    public static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
