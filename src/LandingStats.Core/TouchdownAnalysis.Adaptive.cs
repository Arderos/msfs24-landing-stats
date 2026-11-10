using System;
using System.Collections.Generic;

namespace LandingStats.Core;

public static partial class TouchdownAnalysis
{
    private const double InertialMachineEpsilon = 2.2204460492503131e-16;

    private enum InertialVelocityModel { LastObservation, Linear, Quadratic }

    private static double SelectInertialVelocity(IReadOnlyList<TelemetrySample> samples,
        int first, int last, double horizon, double acceptedLinearVelocity, out InertialVelocityModel selected)
    {
        selected = InertialVelocityModel.Linear;
        // A comparable past forecast needs four fit points plus its held-out
        // target. Sparse windows retain the already admitted linear result.
        if (last - first < 4) return acceptedLinearVelocity;

        var errors = new double[3];
        var targets = 0;
        for (var target = first + 4; target <= last; target++)
        {
            var end = target - 1;
            var span = TimeOf(samples[end]) - TimeOf(samples[first]);
            var gap = TimeOf(samples[target]) - TimeOf(samples[end]);
            if (span < MinimumAirborneFitSeconds - FitTimeToleranceSeconds ||
                gap > span + FitTimeToleranceSeconds) continue;

            // Every prefix starts at the CURRENT window's first observation:
            // validation cannot reach into an older 200 ms window. These are
            // raw candidate forecasts, without the linear admission test.
            if (!TryLinearFit(samples, first, end, i => samples[i].VelocityWorldYFps,
                    out var intercept, out var slope, out _) ||
                !TryInertialQuadraticFit(samples, first, end, out var quadratic)) continue;
            var expected = samples[target].VelocityWorldYFps;
            var lastError = Math.Abs(samples[end].VelocityWorldYFps - expected);
            var linearError = Math.Abs(intercept + slope * gap - expected);
            var quadraticError = Math.Abs(quadratic.At(gap) - expected);
            if (!IsFinite(lastError) || !IsFinite(linearError) || !IsFinite(quadraticError)) continue;
            errors[0] += lastError;
            errors[1] += linearError;
            errors[2] += quadraticError;
            targets++;
        }
        if (targets == 0 || !IsFinite(errors[0]) || !IsFinite(errors[1]) || !IsFinite(errors[2]))
            return acceptedLinearVelocity;

        // The research comparison uses fpm. Express the same roundoff tolerance
        // in fps here, including its one-fpm floor, so ties have identical units.
        var scale = 1.0 / 60.0;
        for (var i = first; i <= last; i++) scale = Math.Max(scale, Math.Abs(samples[i].VelocityWorldYFps));
        var tolerance = 32 * InertialMachineEpsilon * scale;
        for (var i = 0; i < errors.Length; i++) errors[i] /= targets;
        var best = 0;
        if (errors[1] < errors[best] - tolerance) best = 1;
        var quadraticAvailable = TryInertialQuadraticFit(samples, first, last, out var currentQuadratic);
        var quadraticVelocity = quadraticAvailable ? currentQuadratic.At(horizon) : double.NaN;
        if (IsFinite(quadraticVelocity) && IsFinite(ToDescentFpm(quadraticVelocity)) &&
            errors[2] < errors[best] - tolerance) best = 2;

        // Ties prefer the last measurement, then the straight line. Selection
        // uses only errors on points already observed before the query time.
        selected = (InertialVelocityModel)best;
        return best == 0 ? samples[last].VelocityWorldYFps
            : best == 1 ? acceptedLinearVelocity : quadraticVelocity;
    }

    private readonly struct InertialQuadraticFit
    {
        private readonly double _constant, _linear, _quadratic;

        internal InertialQuadraticFit(double constant, double linear, double quadratic)
        { _constant = constant; _linear = linear; _quadratic = quadratic; }

        internal double At(double seconds)
        {
            var u = seconds / InertialFitWindowSeconds;
            return _constant + u * (_linear + u * _quadratic);
        }
    }

    private static bool TryInertialQuadraticFit(IReadOnlyList<TelemetrySample> samples,
        int first, int last, out InertialQuadraticFit fit)
    {
        fit = default;
        var count = last - first + 1;
        if (count < 4) return false;
        var endTime = TimeOf(samples[last]);
        var velocityOrigin = samples[last].VelocityWorldYFps;
        var q = new double[count, 3];
        var r = new double[3, 3];
        var projected = new double[3];
        var maximumColumnNorm = 0.0;
        for (var row = 0; row < count; row++)
        {
            var u = (TimeOf(samples[first + row]) - endTime) / InertialFitWindowSeconds;
            q[row, 0] = 1;
            q[row, 1] = u;
            q[row, 2] = u * u;
        }
        for (var column = 0; column < 3; column++)
        {
            var squared = 0.0;
            for (var row = 0; row < count; row++) squared += q[row, column] * q[row, column];
            maximumColumnNorm = Math.Max(maximumColumnNorm, Math.Sqrt(squared));
        }

        // Normalized time and twice-orthogonalized QR avoid squaring the design
        // matrix's condition number. Centering velocity preserves constant
        // signals exactly and reduces cancellation for small local changes.
        var rankTolerance = InertialMachineEpsilon * Math.Max(count, 3) * maximumColumnNorm;
        for (var column = 0; column < 3; column++)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                for (var previous = 0; previous < column; previous++)
                {
                    var projection = 0.0;
                    for (var row = 0; row < count; row++) projection += q[row, previous] * q[row, column];
                    r[previous, column] += projection;
                    for (var row = 0; row < count; row++) q[row, column] -= projection * q[row, previous];
                }
            }
            var normSquared = 0.0;
            for (var row = 0; row < count; row++) normSquared += q[row, column] * q[row, column];
            var norm = Math.Sqrt(normSquared);
            if (!IsFinite(norm) || norm <= rankTolerance) return false;
            r[column, column] = norm;
            for (var row = 0; row < count; row++)
            {
                q[row, column] /= norm;
                projected[column] += q[row, column] * (samples[first + row].VelocityWorldYFps - velocityOrigin);
            }
        }
        var coefficients = new double[3];
        for (var row = 2; row >= 0; row--)
        {
            var value = projected[row];
            for (var column = row + 1; column < 3; column++) value -= r[row, column] * coefficients[column];
            coefficients[row] = value / r[row, row];
            if (!IsFinite(coefficients[row])) return false;
        }
        var constant = velocityOrigin + coefficients[0];
        if (!IsFinite(constant)) return false;
        fit = new InertialQuadraticFit(constant, coefficients[1], coefficients[2]);
        return true;
    }
}
