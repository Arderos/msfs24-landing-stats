using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingStats.Core;

/// <summary>
/// Identifies tricycle contact roles from loaded-wheel height, attitude and
/// compression. Arm recovery additionally requires stable, physically signed
/// main-wheel coefficients and agreement on a common longitudinal main axis.
/// </summary>
internal static class TelemetrySpatialGearTopology
{
    private const double WindowSeconds = 15.0;
    private const double MaximumFrameGapSeconds = .15;
    private const int MinimumRows = 20;
    private const double MinimumDurationSeconds = .5;
    private const double MinimumAttitudeVariation = .05 * Math.PI / 180;
    private const double MaximumCondition = 30;
    private const double MaximumHeightResidualFeet = .10;
    private const double MinimumSeparationFeet = 1;
    private const double MaximumCoordinateFeet = 500;
    private const double MaximumArmRefitDeviationFeet = 1;
    private const double MaximumMainAxisDifferenceFeet = 1;

    internal sealed class Evidence
    {
        public int Nose { get; set; }
        public int[] Mains { get; set; } = Array.Empty<int>();
        public bool StableRoles { get; set; }
        public bool StableMainArm { get; set; }
        public double PitchArmFeet { get; set; }
        public double ArmRefitDeviationFeet { get; set; }
        public double MainAxisDifferenceFeet { get; set; }
        public bool NonPhysicalMainCompression { get; set; }
        public int LoadedWheelSampleCount { get; set; }
    }

    private sealed class Observation
    {
        public double Time;
        public double Pitch;
        public double Bank;
        public double Compression;
        public double Height;
    }

    private sealed class Fit
    {
        public double Longitudinal;
        public double Lateral;
        public double Compression;
    }

    internal static bool TryInfer(
        IReadOnlyList<TelemetrySample> samples,
        int[] points,
        out Evidence evidence)
    {
        evidence = null!;
        if (points.Length != 3) return false;
        var contacts = TelemetryContactDetector.Find(samples, 10);
        if (contacts.Count == 0) return false;
        var start = contacts[0].ContactIndex;
        var endTime = TelemetryAnalysisFrames.TimeOf(samples[start]) + WindowSeconds;
        var rows = points.Select(_ => new List<Observation>()).ToArray();
        var extensions = points.Select(point => samples[start].ContactPointPosition[point]).ToArray();
        for (var index = start; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (TelemetryAnalysisFrames.TimeOf(sample) > endTime || !sample.MotionSimulation) break;
            if (index > start)
            {
                var gap = TelemetryAnalysisFrames.TimeOf(sample) - TelemetryAnalysisFrames.TimeOf(samples[index - 1]);
                if (gap <= 0 || gap > MaximumFrameGapSeconds ||
                    sample.FlightSegment != samples[start].FlightSegment) break;
            }

            var theta = sample.PitchDegrees * Math.PI / 180;
            var phi = sample.BankDegrees * Math.PI / 180;
            // Small landing attitudes and a fixed, extended gear are the domain
            // of this rigid-body projection. Do not fit moving/retracting gear.
            if (!Finite(theta) || !Finite(phi) || Math.Abs(theta) > Math.PI / 6 || Math.Abs(phi) > Math.PI / 6)
                return false;
            var vertical = Math.Cos(theta) * Math.Cos(phi);
            var height = (sample.PlaneAltitudeFeet - sample.GroundAltitudeFeet) / vertical;
            if (!Finite(height)) continue;
            for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
            {
                var point = points[pointIndex];
                var extension = sample.ContactPointPosition[point];
                if (!Finite(extension) || !Finite(extensions[pointIndex]) || Math.Abs(extension - extensions[pointIndex]) > .01)
                    return false;
                var compression = sample.ContactPointCompression[point];
                if (!TelemetryGeometryCalibration.HasContactEvidence(sample, point) || !Finite(compression)) continue;
                // H / (cos(theta) cos(phi)) = intercept + L tan(theta)
                //     + Y tan(phi)/cos(theta) - suspension_scale * compression.
                rows[pointIndex].Add(new Observation
                {
                    Time = TelemetryAnalysisFrames.TimeOf(sample),
                    Pitch = Math.Tan(theta),
                    Bank = Math.Tan(phi) / Math.Cos(theta),
                    Compression = compression,
                    Height = height,
                });
            }
        }

        var full = new Fit[3];
        for (var point = 0; point < 3; point++)
            if (!TryFit(rows[point], out full[point])) return false;
        var nose = ForwardPoint(full);
        var mains = Enumerable.Range(0, 3).Where(point => point != nose).ToArray();
        if (!HasForwardSeparation(full, nose) || full.Any(fit => fit.Compression >= 0)) return false;
        var left = Math.Min(full[mains[0]].Lateral, full[mains[1]].Lateral);
        var right = Math.Max(full[mains[0]].Lateral, full[mains[1]].Lateral);
        if (right - left < MinimumSeparationFeet || full[nose].Lateral <= left || full[nose].Lateral >= right)
            return false;

        evidence = new Evidence
        {
            Nose = points[nose], Mains = mains.Select(point => points[point]).ToArray(),
            PitchArmFeet = mains.Average(point => full[point].Longitudinal),
            MainAxisDifferenceFeet = Math.Abs(full[mains[0]].Longitudinal - full[mains[1]].Longitudinal),
            LoadedWheelSampleCount = mains.Sum(point => rows[point].Count),
        };
        var stableArm = Math.Abs(full[mains[0]].Longitudinal - full[mains[1]].Longitudinal) <= MaximumMainAxisDifferenceFeet;
        // The complete fit must describe a forward point between the two mains.
        // Temporal refits test the role we actually use: which point is forward.
        // They do not assert stable absolute coordinates or suspension scales.
        for (var subset = 0; subset < 7; subset++)
        {
            var fits = new Fit[3];
            for (var point = 0; point < 3; point++)
            {
                var observations = rows[point];
                var duration = observations[observations.Count - 1].Time - observations[0].Time;
                var selected = observations.Where(row =>
                {
                    var fraction = (row.Time - observations[0].Time) / duration;
                    var block = Math.Min(4, (int)(fraction * 5));
                    return subset == 0 ? fraction <= .7 : subset == 1 ? fraction >= .3 : block != subset - 2;
                }).ToList();
                if (selected.Count == observations.Count || !TryFit(selected, out fits[point])) return true;
            }

            if (ForwardPoint(fits) != nose || !HasForwardSeparation(fits, nose)) return true;
            foreach (var main in mains)
            {
                evidence.ArmRefitDeviationFeet = Math.Max(evidence.ArmRefitDeviationFeet,
                    Math.Abs(fits[main].Longitudinal - full[main].Longitudinal));
                if (fits[main].Compression >= 0)
                {
                    stableArm = false;
                    evidence.NonPhysicalMainCompression = true;
                }
            }
            evidence.MainAxisDifferenceFeet = Math.Max(evidence.MainAxisDifferenceFeet,
                Math.Abs(fits[mains[0]].Longitudinal - fits[mains[1]].Longitudinal));
            if (Math.Abs(fits[mains[0]].Longitudinal - fits[mains[1]].Longitudinal) > MaximumMainAxisDifferenceFeet)
                stableArm = false;
        }

        evidence.StableRoles = true;
        evidence.StableMainArm = stableArm && evidence.ArmRefitDeviationFeet <= MaximumArmRefitDeviationFeet;
        return true;
    }

    private static int ForwardPoint(Fit[] fits)
    {
        var result = 0;
        for (var index = 1; index < fits.Length; index++)
            if (fits[index].Longitudinal > fits[result].Longitudinal) result = index;
        return result;
    }

    private static bool HasForwardSeparation(Fit[] fits, int nose) =>
        Enumerable.Range(0, fits.Length).Where(point => point != nose)
            .All(point => fits[nose].Longitudinal - fits[point].Longitudinal >= MinimumSeparationFeet);

    private static bool TryFit(List<Observation> rows, out Fit fit)
    {
        fit = null!;
        if (rows.Count < MinimumRows || rows[rows.Count - 1].Time - rows[0].Time < MinimumDurationSeconds)
            return false;
        var means = new[] { rows.Average(r => r.Pitch), rows.Average(r => r.Bank), rows.Average(r => r.Compression) };
        var heightMean = rows.Average(r => r.Height);
        var scales = new double[3];
        foreach (var row in rows)
        {
            scales[0] += Square(row.Pitch - means[0]);
            scales[1] += Square(row.Bank - means[1]);
            scales[2] += Square(row.Compression - means[2]);
        }
        for (var index = 0; index < 3; index++) scales[index] = Math.Sqrt(scales[index] / rows.Count);
        if (scales[0] < MinimumAttitudeVariation || scales[1] < MinimumAttitudeVariation || scales[2] < .1)
            return false;
        var matrix = new double[3, 3];
        var rhs = new double[3];
        foreach (var row in rows)
        {
            var values = new[] { (row.Pitch - means[0]) / scales[0], (row.Bank - means[1]) / scales[1], (row.Compression - means[2]) / scales[2] };
            for (var i = 0; i < 3; i++)
            {
                rhs[i] += values[i] * (row.Height - heightMean) / rows.Count;
                for (var j = 0; j < 3; j++) matrix[i, j] += values[i] * values[j] / rows.Count;
            }
        }

        if (!WellConditioned(matrix)) return false;
        // Solve the centered, standardized 3x3 normal equations with pivoting.
        for (var column = 0; column < 3; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 3; row++)
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column])) pivot = row;
            if (Math.Abs(matrix[pivot, column]) < 1e-12) return false;
            for (var j = 0; j < 3; j++)
            {
                var value = matrix[column, j]; matrix[column, j] = matrix[pivot, j]; matrix[pivot, j] = value;
            }
            var right = rhs[column]; rhs[column] = rhs[pivot]; rhs[pivot] = right;
            for (var row = column + 1; row < 3; row++)
            {
                var ratio = matrix[row, column] / matrix[column, column];
                for (var j = column; j < 3; j++) matrix[row, j] -= ratio * matrix[column, j];
                rhs[row] -= ratio * rhs[column];
            }
        }
        var coefficient = new double[3];
        for (var row = 2; row >= 0; row--)
        {
            var value = rhs[row];
            for (var j = row + 1; j < 3; j++) value -= matrix[row, j] * coefficient[j];
            coefficient[row] = value / matrix[row, row];
        }
        for (var index = 0; index < 3; index++) coefficient[index] /= scales[index];
        if (coefficient.Any(value => !Finite(value)) || Math.Abs(coefficient[0]) > MaximumCoordinateFeet ||
            Math.Abs(coefficient[1]) > MaximumCoordinateFeet) return false;
        var residual = 0.0;
        foreach (var row in rows)
            residual += Square(row.Height - heightMean - coefficient[0] * (row.Pitch - means[0]) -
                coefficient[1] * (row.Bank - means[1]) - coefficient[2] * (row.Compression - means[2]));
        if (Math.Sqrt(residual / rows.Count) > MaximumHeightResidualFeet) return false;
        fit = new Fit { Longitudinal = coefficient[0], Lateral = coefficient[1], Compression = coefficient[2] };
        return true;
    }

    internal static bool WellConditioned(double[,] correlation, double maximumCondition = MaximumCondition)
    {
        var matrix = (double[,])correlation.Clone();
        // Jacobi eigenvalues of a symmetric 3x3 matrix. The square root of its
        // eigenvalue ratio is the condition number of the standardized design.
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var p = 0; var q = 1;
            if (Math.Abs(matrix[0, 2]) > Math.Abs(matrix[p, q])) { p = 0; q = 2; }
            if (Math.Abs(matrix[1, 2]) > Math.Abs(matrix[p, q])) { p = 1; q = 2; }
            if (Math.Abs(matrix[p, q]) < 1e-12) break;
            var angle = .5 * Math.Atan2(2 * matrix[p, q], matrix[q, q] - matrix[p, p]);
            var c = Math.Cos(angle); var s = Math.Sin(angle);
            var pp = matrix[p, p]; var qq = matrix[q, q]; var pq = matrix[p, q];
            for (var k = 0; k < 3; k++)
            {
                if (k == p || k == q) continue;
                var kp = matrix[k, p]; var kq = matrix[k, q];
                matrix[k, p] = matrix[p, k] = c * kp - s * kq;
                matrix[k, q] = matrix[q, k] = s * kp + c * kq;
            }
            matrix[p, p] = c * c * pp - 2 * c * s * pq + s * s * qq;
            matrix[q, q] = s * s * pp + 2 * c * s * pq + c * c * qq;
            matrix[p, q] = matrix[q, p] = 0;
        }
        var minimum = Math.Min(matrix[0, 0], Math.Min(matrix[1, 1], matrix[2, 2]));
        var maximum = Math.Max(matrix[0, 0], Math.Max(matrix[1, 1], matrix[2, 2]));
        return Finite(minimum) && Finite(maximum) && minimum > 0 && maximum / minimum <= maximumCondition * maximumCondition;
    }

    private static double Square(double value) => value * value;
    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);
}
