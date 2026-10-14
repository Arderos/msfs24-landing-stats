using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingStats.Core;

/// <summary>
/// Bounds a linear prediction over a bounded polytope containing the origin.
/// An active-set search supplies nonnegative dual multipliers. The returned
/// upper bound includes the dual residual times explicit variable bounds, so
/// an approximate or degenerate solve cannot silently narrow the interval.
/// </summary>
internal sealed class BoundedLinearPrediction
{
    private readonly double[][] a;
    private readonly double[] b, variableBounds;

    internal BoundedLinearPrediction(IReadOnlyList<double[]> constraints, IReadOnlyList<double> limits,
        double[] maximumAbsoluteVariables)
    {
        variableBounds = (double[])maximumAbsoluteVariables.Clone();
        a = new double[constraints.Count][]; b = new double[constraints.Count];
        for (var i = 0; i < a.Length; i++)
        {
            var norm = Math.Sqrt(Dot(constraints[i], constraints[i]));
            a[i] = constraints[i].Select(v => v / norm).ToArray();
            b[i] = limits[i] / norm;
        }
    }

    internal bool TryInterval(double[] objective, out double lower, out double upper)
    {
        lower = upper = double.NaN;
        if (!TryUpperBound(objective, out upper) ||
            !TryUpperBound(objective.Select(v => -v).ToArray(), out var negativeLower)) return false;
        lower = -negativeLower;
        return lower <= upper + 1e-8;
    }

    private bool TryUpperBound(double[] objective, out double upper)
    {
        upper = double.NaN;
        var n = variableBounds.Length;
        if (objective.Length != n || objective.Any(v => !Finite(v)) ||
            variableBounds.Any(v => !Finite(v) || v < 0) ||
            b.Any(v => !Finite(v) || v < 0) || a.Any(row => row.Length != n || row.Any(v => !Finite(v)))) return false;
        var norm = Math.Sqrt(Dot(objective, objective));
        if (!Finite(norm)) return false;
        if (norm == 0) { upper = 0; return true; }
        var c = objective.Select(v => v / norm).ToArray();
        var x = new double[n]; var active = new List<int>();
        for (var iteration = 0; iteration < 512; iteration++)
        {
            var gram = new double[active.Count, active.Count]; var rhs = new double[active.Count];
            for (var i = 0; i < active.Count; i++)
            {
                rhs[i] = Dot(a[active[i]], c);
                for (var j = 0; j < active.Count; j++) gram[i, j] = Dot(a[active[i]], a[active[j]]);
            }
            if (!Solve(gram, rhs, out var multipliers)) return false;
            var direction = (double[])c.Clone();
            for (var i = 0; i < active.Count; i++)
                for (var j = 0; j < n; j++) direction[j] -= multipliers[i] * a[active[i]][j];
            if (Math.Sqrt(Dot(direction, direction)) <= 1e-9)
            {
                var remove = -1;
                for (var i = 0; i < active.Count; i++)
                    if (multipliers[i] < -1e-9 && (remove < 0 || multipliers[i] < multipliers[remove])) remove = i;
                if (remove >= 0) { active.RemoveAt(remove); continue; }
                // Weak duality is the acceptance certificate. Clip negative
                // roundoff and cover the remaining dual-equation error using
                // the caller's finite bounds on every optimization variable.
                var residual = (double[])c.Clone(); var bound = 0.0;
                for (var i = 0; i < active.Count; i++)
                {
                    var lambda = Math.Max(0, multipliers[i]);
                    bound += lambda * b[active[i]];
                    for (var j = 0; j < n; j++) residual[j] -= lambda * a[active[i]][j];
                }
                for (var j = 0; j < n; j++) bound += Math.Abs(residual[j]) * variableBounds[j];
                if (a.Where((row, i) => Dot(row, x) > b[i] + 1e-7).Any()) return false;
                var arithmeticScale = 1.0 + Math.Abs(bound);
                for (var j = 0; j < n; j++)
                {
                    arithmeticScale += Math.Abs(c[j]) * variableBounds[j];
                    for (var i = 0; i < active.Count; i++)
                        arithmeticScale += Math.Abs(multipliers[i] * a[active[i]][j]) * variableBounds[j];
                }
                upper = norm * (bound + 1e-12 * arithmeticScale) + 1e-7 * (1 + norm);
                return Finite(upper);
            }
            if (active.Count >= n) return false;
            var step = double.PositiveInfinity; var blocking = -1;
            for (var i = 0; i < a.Length; i++)
            {
                if (active.Contains(i)) continue;
                var denominator = Dot(a[i], direction);
                if (denominator <= 1e-12) continue;
                var slack = b[i] - Dot(a[i], x);
                if (slack < -1e-7) return false;
                var candidate = Math.Max(0, slack) / denominator;
                if (candidate < step - 1e-10) { step = candidate; blocking = i; }
            }
            if (blocking < 0 || !Finite(step)) return false;
            for (var j = 0; j < n; j++) x[j] += step * direction[j];
            active.Add(blocking);
        }
        return false;
    }

    private static bool Solve(double[,] matrix, double[] rhs, out double[] solution)
    {
        var n = rhs.Length; var m = new double[n, n + 1]; solution = new double[n];
        for (var i = 0; i < n; i++)
        { for (var j = 0; j < n; j++) m[i, j] = matrix[i, j]; m[i, n] = rhs[i]; }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var i = col + 1; i < n; i++) if (Math.Abs(m[i, col]) > Math.Abs(m[pivot, col])) pivot = i;
            if (!Finite(m[pivot, col]) || Math.Abs(m[pivot, col]) < 1e-12) return false;
            for (var j = col; j <= n; j++) { var v = m[col, j]; m[col, j] = m[pivot, j]; m[pivot, j] = v; }
            var scale = m[col, col];
            for (var j = col; j <= n; j++) m[col, j] /= scale;
            for (var i = 0; i < n; i++)
            {
                if (i == col) continue;
                var factor = m[i, col];
                for (var j = col; j <= n; j++) m[i, j] -= factor * m[col, j];
            }
        }
        for (var i = 0; i < n; i++) solution[i] = m[i, n];
        return solution.All(Finite);
    }

    private static double Dot(double[] x, double[] y)
    { var sum = 0.0; for (var i = 0; i < x.Length; i++) sum += x[i] * y[i]; return sum; }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
