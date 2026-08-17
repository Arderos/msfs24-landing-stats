using System;
using System.Collections.Generic;

namespace LandingStats.Core;

internal sealed class TouchdownClosureEstimate
{
    public double ClosureFpm { get; set; }

    public double InertialFpm { get; set; }

    public double TerrainFpm { get; set; }

    public double PitchFpm { get; set; }

    public int FitPointCount { get; set; }
}

internal static class TouchdownClosureReconstruction
{
    internal const string ModelName = "quad250-tc-minus-75ms-pitch-v1";
    internal const double PrimaryUncertaintyFpm = 10.0;
    internal const double FallbackUncertaintyFpm = 15.0;
    // Multi-bogie aircraft can touch on a different wheel within a tilting
    // truck from one landing to the next. When no readable flight-model
    // geometry exists, the telemetry fit may identify a different effective
    // arm on each rollout. Keep the wider empirical band for that fallback;
    // readable configuration geometry uses the ordinary conservative band.
    internal const double MultiBogieTelemetryUncertaintyFpm = 50.0;

    private const double FitWindowSeconds = 0.250;
    private const double EvaluationOffsetSeconds = -0.075;
    private const double GroundOutlierThresholdFeet = 5.0;
    private const double TimeToleranceSeconds = 0.000000001;

    public static bool TryEstimate(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex,
        double estimatedContactTime,
        double longitudinalArmFeet,
        out TouchdownClosureEstimate estimate)
    {
        estimate = new TouchdownClosureEstimate();
        if (!IsFinite(estimatedContactTime) || !IsFinite(longitudinalArmFeet) ||
            lastAirborneIndex < 0 || lastAirborneIndex >= samples.Count)
        {
            return false;
        }

        var firstIndex = lastAirborneIndex;
        var fitStartTime = estimatedContactTime - FitWindowSeconds;
        while (firstIndex > 0)
        {
            var previous = samples[firstIndex - 1];
            if (previous.OnGround || TouchdownAnalysis.TimeOf(previous) < fitStartTime)
            {
                break;
            }

            firstIndex--;
        }

        var indices = new List<int>(lastAirborneIndex - firstIndex + 1);
        for (var index = firstIndex; index <= lastAirborneIndex; index++)
        {
            var sample = samples[index];
            var time = TouchdownAnalysis.TimeOf(sample);
            if (!sample.OnGround && time >= fitStartTime && time <= estimatedContactTime)
            {
                indices.Add(index);
            }
        }

        // A quadratic is algebraically solvable with three points, but three or
        // four simulator frames leave no useful protection against quantization
        // or a single irregular frame. The frozen reconstruction therefore needs
        // at least five distinct pre-contact samples and has no linear fallback.
        if (indices.Count < 5 || CountDistinctTimes(samples, indices) < 5)
        {
            return false;
        }

        // tc-75 ms must be an interpolation point. Extrapolating a quadratic even
        // 20-30 ms outside a short low-FPS history amplifies normal frame noise into
        // a confident-looking tens-of-fpm error.
        var evaluationTime = estimatedContactTime + EvaluationOffsetSeconds;
        var firstFitTime = TouchdownAnalysis.TimeOf(samples[indices[0]]);
        var lastFitTime = TouchdownAnalysis.TimeOf(samples[indices[indices.Count - 1]]);
        if (evaluationTime < firstFitTime - TimeToleranceSeconds ||
            evaluationTime > lastFitTime + TimeToleranceSeconds)
        {
            return false;
        }

        var filteredGround = SanitizeGroundAltitude(samples);
        if (!TryQuadraticFit(samples, indices, estimatedContactTime, sample => sample.VelocityWorldYFps, null, out var velocity) ||
            !TryQuadraticFit(samples, indices, estimatedContactTime, null, filteredGround, out var ground) ||
            !TryQuadraticFit(samples, indices, estimatedContactTime, sample => sample.RotationVelocityBodyXRadiansPerSecond, null, out var pitchRate) ||
            !TryQuadraticFit(samples, indices, estimatedContactTime, sample => DegreesToRadians(sample.PitchDegrees), null, out var pitch) ||
            !TryQuadraticFit(samples, indices, estimatedContactTime, sample => DegreesToRadians(sample.BankDegrees), null, out var bank))
        {
            return false;
        }

        var inertialFpm = -Evaluate(velocity, EvaluationOffsetSeconds) * 60.0;
        var terrainFpm = Derivative(ground, EvaluationOffsetSeconds) * 60.0;
        // Model v1 deliberately freezes the omega-Y (yaw x bank) contribution
        // out by passing zero. Keeping that omission explicit prevents a shared
        // kinematics helper from silently changing the already validated model.
        var pitchFpm = -WorldVerticalRigidBodyProjection.AtLongitudinalOffsetFps(
            Evaluate(pitchRate, EvaluationOffsetSeconds),
            0.0,
            Evaluate(pitch, EvaluationOffsetSeconds),
            Evaluate(bank, EvaluationOffsetSeconds),
            longitudinalArmFeet) * 60.0;
        var closureFpm = inertialFpm + terrainFpm + pitchFpm;
        if (!IsFinite(inertialFpm) || !IsFinite(terrainFpm) ||
            !IsFinite(pitchFpm) || !IsFinite(closureFpm))
        {
            return false;
        }

        estimate = new TouchdownClosureEstimate
        {
            ClosureFpm = closureFpm,
            InertialFpm = inertialFpm,
            TerrainFpm = terrainFpm,
            PitchFpm = pitchFpm,
            FitPointCount = indices.Count,
        };
        return true;
    }

    private static bool TryQuadraticFit(
        IReadOnlyList<TelemetrySample> samples,
        IReadOnlyList<int> indices,
        double originTime,
        Func<TelemetrySample, double>? selectValue,
        IReadOnlyList<double>? values,
        out double[] coefficients)
    {
        var sumX = 0.0;
        var sumX2 = 0.0;
        var sumX3 = 0.0;
        var sumX4 = 0.0;
        var sumY = 0.0;
        var sumXY = 0.0;
        var sumX2Y = 0.0;

        foreach (var index in indices)
        {
            var x = TouchdownAnalysis.TimeOf(samples[index]) - originTime;
            var y = selectValue != null ? selectValue(samples[index]) : values![index];
            if (!IsFinite(x) || !IsFinite(y))
            {
                coefficients = Array.Empty<double>();
                return false;
            }

            var x2 = x * x;
            sumX += x;
            sumX2 += x2;
            sumX3 += x2 * x;
            sumX4 += x2 * x2;
            sumY += y;
            sumXY += x * y;
            sumX2Y += x2 * y;
        }

        var matrix = new[,]
        {
            { (double)indices.Count, sumX, sumX2, sumY },
            { sumX, sumX2, sumX3, sumXY },
            { sumX2, sumX3, sumX4, sumX2Y },
        };
        if (!TrySolveThreeByThree(matrix, out coefficients))
        {
            coefficients = Array.Empty<double>();
            return false;
        }

        return true;
    }

    private static bool TrySolveThreeByThree(double[,] matrix, out double[] solution)
    {
        const double minimumPivot = 1e-14;
        for (var column = 0; column < 3; column++)
        {
            var pivotRow = column;
            var pivotSize = Math.Abs(matrix[pivotRow, column]);
            for (var row = column + 1; row < 3; row++)
            {
                var candidate = Math.Abs(matrix[row, column]);
                if (candidate > pivotSize)
                {
                    pivotRow = row;
                    pivotSize = candidate;
                }
            }

            if (pivotSize <= minimumPivot)
            {
                solution = Array.Empty<double>();
                return false;
            }

            if (pivotRow != column)
            {
                for (var item = column; item < 4; item++)
                {
                    var temporary = matrix[column, item];
                    matrix[column, item] = matrix[pivotRow, item];
                    matrix[pivotRow, item] = temporary;
                }
            }

            var pivot = matrix[column, column];
            for (var item = column; item < 4; item++)
            {
                matrix[column, item] /= pivot;
            }

            for (var row = 0; row < 3; row++)
            {
                if (row == column)
                {
                    continue;
                }

                var factor = matrix[row, column];
                for (var item = column; item < 4; item++)
                {
                    matrix[row, item] -= factor * matrix[column, item];
                }
            }
        }

        solution = new[] { matrix[0, 3], matrix[1, 3], matrix[2, 3] };
        return IsFinite(solution[0]) && IsFinite(solution[1]) && IsFinite(solution[2]);
    }

    private static double[] SanitizeGroundAltitude(IReadOnlyList<TelemetrySample> samples)
    {
        var result = new double[samples.Count];
        var original = new double[samples.Count];
        var outliers = new bool[samples.Count];
        var localMedians = new double[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            result[index] = samples[index].GroundAltitudeFeet;
            original[index] = result[index];
            localMedians[index] = double.NaN;
        }

        for (var index = 0; index < samples.Count; index++)
        {
            var local = new List<double>(5);
            var first = Math.Max(0, Math.Min(index - 2, samples.Count - 5));
            var last = Math.Min(samples.Count - 1, first + 4);
            first = Math.Max(0, last - 4);
            for (var neighbor = first; neighbor <= last; neighbor++)
            {
                if (IsFinite(original[neighbor]))
                {
                    local.Add(original[neighbor]);
                }
            }

            if (local.Count < 3 || !IsFinite(original[index]))
            {
                continue;
            }

            local.Sort();
            var median = local[local.Count / 2];
            localMedians[index] = median;
            outliers[index] = Math.Abs(original[index] - median) > GroundOutlierThresholdFeet;
        }

        for (var index = 0; index < samples.Count; index++)
        {
            if (!outliers[index])
            {
                continue;
            }

            var previousIndex = index - 1;
            while (previousIndex >= 0 && (outliers[previousIndex] || !IsFinite(result[previousIndex])))
            {
                previousIndex--;
            }

            var nextIndex = index + 1;
            while (nextIndex < samples.Count && (outliers[nextIndex] || !IsFinite(original[nextIndex])))
            {
                nextIndex++;
            }

            if (previousIndex >= 0 && nextIndex < samples.Count)
            {
                var previousTime = TouchdownAnalysis.TimeOf(samples[previousIndex]);
                var targetTime = TouchdownAnalysis.TimeOf(samples[index]);
                var nextTime = TouchdownAnalysis.TimeOf(samples[nextIndex]);
                var duration = nextTime - previousTime;
                if (duration > 0.000001)
                {
                    var fraction = (targetTime - previousTime) / duration;
                    result[index] = result[previousIndex] +
                                    (original[nextIndex] - result[previousIndex]) * fraction;
                }
                else
                {
                    result[index] = localMedians[index];
                }
            }
            else if (previousIndex >= 0)
            {
                result[index] = result[previousIndex];
            }
            else if (nextIndex < samples.Count)
            {
                result[index] = original[nextIndex];
            }
            else
            {
                result[index] = localMedians[index];
            }
        }

        return result;
    }

    private static int CountDistinctTimes(
        IReadOnlyList<TelemetrySample> samples,
        IReadOnlyList<int> indices)
    {
        var count = 0;
        var previousTime = double.NaN;
        foreach (var index in indices)
        {
            var time = TouchdownAnalysis.TimeOf(samples[index]);
            if (!IsFinite(time))
            {
                continue;
            }

            if (!double.IsNaN(previousTime) && time < previousTime - TimeToleranceSeconds)
            {
                return 0;
            }

            if (double.IsNaN(previousTime) || time - previousTime > TimeToleranceSeconds)
            {
                count++;
            }

            previousTime = time;
        }

        return count;
    }

    private static double Evaluate(IReadOnlyList<double> coefficients, double x) =>
        coefficients[0] + coefficients[1] * x + coefficients[2] * x * x;

    private static double Derivative(IReadOnlyList<double> coefficients, double x) =>
        coefficients[1] + 2.0 * coefficients[2] * x;

    private static double DegreesToRadians(double value) => value * Math.PI / 180.0;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
