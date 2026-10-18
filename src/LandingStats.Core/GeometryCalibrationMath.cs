using System;
using System.Collections.Generic;
using System.Linq;
using static LandingStats.Core.GeometryCalibrationPolicy;

namespace LandingStats.Core;

// Pure component extracted without changing the established estimator equations.
internal static class GeometryCalibrationMath
{
    internal static bool TryFitTwoPredictors(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second,
        IReadOnlyList<double> response,
        out double firstSlope,
        out double secondSlope,
        out double normalizedDeterminant)
    {
        firstSlope = double.NaN;
        secondSlope = double.NaN;
        normalizedDeterminant = 0.0;
        if (first.Count != second.Count || first.Count != response.Count || first.Count < 3)
        {
            return false;
        }

        var firstMean = Mean(first);
        var secondMean = Mean(second);
        var responseMean = Mean(response);
        var firstSquare = 0.0;
        var secondSquare = 0.0;
        var cross = 0.0;
        var firstResponse = 0.0;
        var secondResponse = 0.0;
        for (var index = 0; index < first.Count; index++)
        {
            var x = first[index] - firstMean;
            var z = second[index] - secondMean;
            var y = response[index] - responseMean;
            firstSquare += x * x;
            secondSquare += z * z;
            cross += x * z;
            firstResponse += x * y;
            secondResponse += z * y;
        }

        if (firstSquare <= 0.0 || secondSquare <= 0.0)
        {
            return false;
        }

        var product = firstSquare * secondSquare;
        var determinant = product - (cross * cross);
        if (!IsFinite(product) || !IsFinite(determinant) || determinant <= 0.0)
        {
            return false;
        }

        normalizedDeterminant = Clamp01(determinant / product);
        if (normalizedDeterminant < MinimumDesignIndependence)
        {
            return false;
        }

        firstSlope = ((firstResponse * secondSquare) - (secondResponse * cross)) / determinant;
        secondSlope = ((secondResponse * firstSquare) - (firstResponse * cross)) / determinant;
        return IsFinite(firstSlope) && IsFinite(secondSlope);
    }

    internal static bool TryFitLine(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        out double slope)
    {
        slope = double.NaN;
        if (x.Count != y.Count || x.Count < 2)
        {
            return false;
        }

        var xMean = Mean(x);
        var yMean = Mean(y);
        var square = 0.0;
        var cross = 0.0;
        for (var index = 0; index < x.Count; index++)
        {
            var centeredX = x[index] - xMean;
            square += centeredX * centeredX;
            cross += centeredX * (y[index] - yMean);
        }

        if (!IsFinite(square) || square <= 0.0)
        {
            return false;
        }

        slope = cross / square;
        return IsFinite(slope);
    }

    internal static int LowerBound(IReadOnlyList<double> sorted, double value)
    {
        var low = 0;
        var high = sorted.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (sorted[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    internal static double Mean(IReadOnlyList<double> values)
    {
        var sum = 0.0;
        for (var index = 0; index < values.Count; index++)
        {
            sum += values[index];
        }

        return sum / values.Count;
    }

    internal static double Range(IReadOnlyList<double> values)
    {
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        for (var index = 0; index < values.Count; index++)
        {
            minimum = Math.Min(minimum, values[index]);
            maximum = Math.Max(maximum, values[index]);
        }

        return maximum - minimum;
    }

    internal static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }

    internal static bool IsFinite(double value)
    {
        return AnalysisNumericPolicy.IsFinite(value);
    }

    internal static double Clamp01(double value)
    {
        return Math.Max(0.0, Math.Min(1.0, value));
    }

    internal static double Square(double value)
    {
        return value * value;
    }
}
