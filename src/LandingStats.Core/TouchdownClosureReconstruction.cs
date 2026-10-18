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
    // A clustered sequence can start on only two channels (e.g. iniBuilds A380);
    // the inferred first cluster's size does not justify the ordinary-gear band.
    internal const double MultiBogieTelemetryUncertaintyFpm = 50.0;

    private const double FitWindowSeconds = 0.250;
    private const double EvaluationOffsetSeconds = -0.075;
    private const double TimeToleranceSeconds = 0.000000001;

    public static bool TryEstimate(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex,
        double estimatedContactTime,
        double longitudinalArmFeet,
        out TouchdownClosureEstimate estimate)
        => TryEstimate(samples, lastAirborneIndex, estimatedContactTime, longitudinalArmFeet, out estimate, out _);

    public static bool TryEstimate(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex,
        double estimatedContactTime,
        double longitudinalArmFeet,
        out TouchdownClosureEstimate estimate,
        out string decision)
    {
        estimate = new TouchdownClosureEstimate();
        decision = "invalid_contact_time_or_arm";
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
            if (!previous.MotionSimulation || previous.OnGround || TouchdownAnalysis.TimeOf(previous) < fitStartTime)
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
            if (sample.MotionSimulation && !sample.OnGround && time >= fitStartTime && time <= estimatedContactTime)
            {
                indices.Add(index);
            }
        }

        // A quadratic is algebraically solvable with three points, but three or
        // four simulator frames leave no useful protection against quantization
        // or a single irregular frame. The frozen reconstruction therefore needs
        // at least five distinct pre-contact samples and has no linear fallback.
        var distinctTimes = CountDistinctTimes(samples, indices);
        if (indices.Count < 5 || distinctTimes < 5)
        {
            decision = distinctTimes < 0 ? "nonmonotonic_precontact_time" : "insufficient_distinct_precontact_samples";
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
            decision = "evaluation_outside_precontact_history";
            return false;
        }

        var filteredGround = GroundAltitudeFilters.ForClosure(samples);
        if (!TryQuadraticFit(samples, indices, estimatedContactTime, sample => sample.VelocityWorldYFps, null, out var velocity) ||
            !TryQuadraticFit(samples, indices, estimatedContactTime, null, filteredGround, out var ground) ||
            !TryQuadraticFit(samples, indices, estimatedContactTime, sample => sample.RotationVelocityBodyXRadiansPerSecond, null, out var pitchRate) ||
            !TryQuadraticFit(samples, indices, estimatedContactTime, sample => DegreesToRadians(sample.PitchDegrees), null, out var pitch) ||
            !TryQuadraticFit(samples, indices, estimatedContactTime, sample => DegreesToRadians(sample.BankDegrees), null, out var bank))
        {
            decision = "quadratic_fit_unavailable";
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
            decision = "nonfinite_reconstructed_velocity";
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
        decision = "quadratic_fit_accepted";
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
                return -1;
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

    private static bool IsFinite(double value) => AnalysisNumericPolicy.IsFinite(value);
}
