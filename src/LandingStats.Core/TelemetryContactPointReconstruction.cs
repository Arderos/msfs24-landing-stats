using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingStats.Core;

/// <summary>
/// Reconstructs an observed contact point, independent of nose/main labels.
/// Fits the height of each loaded point and tests the uncertainty of its velocity
/// prediction, rather than requiring every individual coordinate to be stable.
/// </summary>
internal sealed class TelemetryContactPointReconstruction
{
    internal const string ModelName = "quad250-tc-contact-point-v2";
    internal const string BoundedModelName = "quad250-tc-contact-point-bounds-v3";
    internal const string KinematicModelName = "quad250-tc-contact-point-kinematic-v4";
    private const double MaximumGap = .15;
    private const double CalibrationSeconds = 15;
    private const double MaximumCrabRadians = 10 * Math.PI / 180;
    private const double MaximumResidualFeet = .10;
    private const double MinimumResidualFeet = .01;
    private const double MaximumUncertaintyFpm = 50;
    private const double FeetPerDegree = 364812.21;
    private const int Vy = 0, Ground = 1, Altitude = 2, Pitch = 3, Bank = 4,
        OmegaX = 5, OmegaY = 6, Heading = 7, North = 8, East = 9, Speed = 10, OmegaZ = 11;

    private readonly IReadOnlyList<TelemetrySample> samples;
    private readonly Dictionary<int, PointModel> models = new Dictionary<int, PointModel>();
    private readonly Dictionary<int, PointModel> boundedModels = new Dictionary<int, PointModel>();
    private readonly TelemetryDatumOffsetCalibrationResult? datum;
    internal string CalibrationStopReason { get; private set; } = "window_complete";
    internal double CalibrationDurationSeconds { get; private set; }
    internal Dictionary<int, int> CalibrationRowCounts { get; } = new Dictionary<int, int>();

    private sealed class Row
    {
        internal double Time, Height;
        internal double[] X = Array.Empty<double>();
    }

    private sealed class Fit
    {
        internal double[] Coefficients = Array.Empty<double>();
        internal double[] Means = Array.Empty<double>();
        internal double[] Scales = Array.Empty<double>();
        internal double[,] Inverse = new double[0, 0];
        internal double Predict(Row row) => Coefficients[0] + Dot(Coefficients, row.X, 1);

        internal double Sensitivity(double[] query)
        {
            var v = new double[3];
            for (var i = 0; i < 3; i++)
                v[i] = ((i < 2 ? query[i + 1] : 0) - query[0] * Means[i]) / Scales[i];
            var variance = query[0] * query[0];
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++) variance += v[i] * Inverse[i, j] * v[j];
            return Math.Sqrt(Math.Max(0, variance));
        }
    }

    private sealed class PointModel
    {
        internal Fit Full = null!;
        internal List<Fit> Refits = new List<Fit>();
        internal double Residual;
        internal double Extension;
        internal BoundedLinearPrediction? Bounds;

        internal bool TryPredictionInterval(double[] query, out double lower, out double upper)
        {
            lower = upper = double.NaN;
            var objective = new double[4]; objective[0] = query[0];
            for (var i = 0; i < 3; i++)
                objective[i + 1] = ((i < 2 ? query[i + 1] : 0) - query[0] * Full.Means[i]) / Full.Scales[i];
            if (Bounds == null || !Bounds.TryInterval(objective, out lower, out upper)) return false;
            var center = Dot(Full.Coefficients, query); lower += center; upper += center;
            return Finite(lower) && Finite(upper);
        }
    }

    internal sealed class Estimate
    {
        internal double Closure, Inertial, Terrain, Rotation, Uncertainty, Arm, Quality;
        internal double GeometryUncertainty, TemporalUncertainty, AltitudeDisagreement;
        internal int FitPointCount;
        internal int[] Points = Array.Empty<int>();
        internal bool BoundedGeometry, KinematicMotion;
    }

    internal TelemetryContactPointReconstruction(IReadOnlyList<TelemetrySample> source, double firstContactTime)
    {
        samples = TelemetryDeduplicator.Deduplicate(source);
        TelemetryGeometryCalibration.TryRecoverDatumOffsetFromAttitude(samples, out datum);
        if (datum == null) { CalibrationStopReason = "datum_unavailable"; return; }
        var start = -1;
        for (var i = 0; i < samples.Count; i++)
            if (Math.Abs(TouchdownAnalysis.TimeOf(samples[i]) - firstContactTime) < 1e-7) { start = i; break; }
        if (start < 0) { CalibrationStopReason = "contact_not_found"; return; }
        var window = new List<TelemetrySample>();
        for (var i = start; i < samples.Count; i++)
        {
            var s = samples[i];
            if (TouchdownAnalysis.TimeOf(s) > firstContactTime + CalibrationSeconds) break;
            if (!s.MotionSimulation || s.FlightSegment != samples[start].FlightSegment)
            { CalibrationStopReason = "physics_boundary"; break; }
            if (window.Count > 0 && !Continuous(window[window.Count - 1], s))
            { CalibrationStopReason = "sample_gap"; break; }
            window.Add(s);
        }

        var rows = Enumerable.Range(0, TelemetrySample.CapturedContactPointCount).Select(_ => new List<Row>()).ToArray();
        var valid = Enumerable.Repeat(true, rows.Length).ToArray();
        var extensions = samples[start].ContactPointPosition;
        var localFirst = 0; var localLast = 0;
        for (var i = 0; i < window.Count; i++)
        {
            var s = window[i];
            var time = TouchdownAnalysis.TimeOf(s);
            while (localFirst < window.Count && time - TouchdownAnalysis.TimeOf(window[localFirst]) > .15) localFirst++;
            while (localLast < window.Count && TouchdownAnalysis.TimeOf(window[localLast]) - time <= .15) localLast++;
            var local = window.GetRange(localFirst, localLast - localFirst);
            // At a low frame rate the first/last centered windows can be too
            // short. They supply no slope observation, not a physics boundary.
            if (local.Count < 5) continue;
            if (!Polynomial.TryFit(local, time, 5, out var trajectory)) { CalibrationStopReason = "local_trajectory_unavailable"; break; }
            // Position updates can lag a frame. A derivative inconsistent with
            // native ground speed is not an observation of the surface slope.
            if (!trajectory.ReliableGroundMotion(0)) continue;
            // Use the recorded attitude in the height constraint; smoothing here
            // would erase suspension/attitude transients we need to validate.
            if (!trajectory.TryBasis(0, out var basis, out _, s.PitchDegrees * Math.PI / 180,
                    s.BankDegrees * Math.PI / 180, s.HeadingTrueDegrees * Math.PI / 180)) { CalibrationStopReason = "surface_projection_domain"; break; }
            var height = (s.PlaneAltitudeFeet - s.GroundAltitudeFeet) / basis[0];
            if (!Finite(height) || basis[0] < .5) { CalibrationStopReason = "height_unavailable"; break; }
            CalibrationDurationSeconds = time - firstContactTime;
            for (var point = 0; point < rows.Length; point++)
            {
                if (!Finite(s.ContactPointPosition[point]) || !Finite(extensions[point]) ||
                    Math.Abs(s.ContactPointPosition[point] - extensions[point]) > .01) valid[point] = false;
                if (!TelemetryGeometryCalibration.HasContactEvidence(s, point) || !Finite(s.ContactPointCompression[point])) continue;
                rows[point].Add(new Row { Time = time, Height = height,
                    X = new[] { basis[1] / basis[0], basis[2] / basis[0], s.ContactPointCompression[point] } });
            }
        }
        for (var point = 0; point < rows.Length; point++)
        {
            if (rows[point].Count > 0) CalibrationRowCounts[point] = rows[point].Count;
            if (valid[point] && TryModel(rows[point], out var model))
            {
                model.Extension = extensions[point];
                models[point] = model;
            }
            if (valid[point] && TryBoundedModel(rows[point], out var bounded))
            {
                bounded.Extension = extensions[point];
                boundedModels[point] = bounded;
            }
        }
    }

    // A rejected candidate may be returned for the decision journal only. The
    // boolean return is the sole availability decision.
    internal bool TryEstimate(double firstGroundTime, double contactTime, out Estimate estimate, out string decision)
    {
        if (TryEstimateCore(firstGroundTime, contactTime, false, false, out estimate, out decision, out var failure)) return true;
        if (failure != EstimateFailure.GeometryUnavailable && failure != EstimateFailure.PredictionUncertain) return false;
        var accepted = TryEstimateCore(firstGroundTime, contactTime, true, false, out var bounded, out var boundedDecision, out failure);
        if (bounded != null) { estimate = bounded; decision = boundedDecision; }
        if (accepted || failure != EstimateFailure.PredictionUncertain) return accepted;
        accepted = TryEstimateCore(firstGroundTime, contactTime, true, true, out var kinematic, out var kinematicDecision, out _);
        if (kinematic != null) { estimate = kinematic; decision = kinematicDecision; }
        return accepted;
    }

    private enum EstimateFailure { Other, GeometryUnavailable, PredictionUncertain }
    private bool TryEstimateCore(double firstGroundTime, double contactTime, bool useBounds, bool kinematicMotion, out Estimate estimate, out string decision, out EstimateFailure failure)
    {
        failure = EstimateFailure.Other;
        estimate = null!;
        decision = "point_datum_unavailable";
        if (datum == null) return false;
        decision = "point_precontact_unavailable";
        var contact = -1;
        for (var i = 1; i < samples.Count; i++)
            if (Math.Abs(TouchdownAnalysis.TimeOf(samples[i]) - firstGroundTime) < 1e-7) { contact = i; break; }
        if (contact < 1 || !samples[contact].OnGround) return false;
        var last = contact - 1;
        if (samples[last].OnGround || !Continuous(samples[last], samples[contact]) ||
            firstGroundTime - TouchdownAnalysis.TimeOf(samples[last]) > .075 + 1e-9 ||
            contactTime < TouchdownAnalysis.TimeOf(samples[last]) - 1e-9 || contactTime > firstGroundTime + 1e-9) return false;
        var first = last;
        while (first > 0 && !samples[first - 1].OnGround && Continuous(samples[first - 1], samples[first]) &&
            TouchdownAnalysis.TimeOf(samples[first - 1]) >= contactTime - .25) first--;
        var pre = samples.Skip(first).Take(last - first + 1).ToList();
        if (!Polynomial.TryFit(pre, contactTime, 5, out var trajectory) ||
            !trajectory.TryQuery(0, out var query, out var flatQuery, kinematicMotion)) return false;

        var points = Enumerable.Range(0, TelemetrySample.CapturedContactPointCount)
            .Where(p => TelemetryGeometryCalibration.HasContactEvidence(samples[contact], p)).ToArray();
        decision = "point_contact_not_sustained";
        if (points.Length == 0) return false;
        foreach (var p in points)
        {
            if (TelemetryGeometryCalibration.HasContactEvidence(samples[last], p)) return false;
            var end = contact;
            while (end + 1 < samples.Count && TouchdownAnalysis.TimeOf(samples[end]) < firstGroundTime + .04 &&
                Continuous(samples[end], samples[end + 1]) && samples[end + 1].OnGround &&
                TelemetryGeometryCalibration.HasContactEvidence(samples[end + 1], p)) end++;
            if (end == contact || TouchdownAnalysis.TimeOf(samples[end]) - firstGroundTime < .04 - 1e-9) return false;
        }

        // Omitted-frame fits and the complete observed contact-time interval
        // expose extrapolation sensitivity without using post-impact velocities.
        var alternatives = new List<Polynomial>();
        decision = "point_precontact_refit_unavailable";
        for (var omit = 0; omit < pre.Count; omit++)
        {
            if (!Polynomial.TryFit(pre.Where((_, i) => i != omit).ToList(), contactTime, 4, out var refit)) return false;
            alternatives.Add(refit);
        }
        var terms = new List<Estimate>();
        foreach (var point in points)
        {
            decision = "point_geometry_unavailable";
            if (!(useBounds ? boundedModels : models).TryGetValue(point, out var model) || pre.Any(s =>
                !Finite(s.ContactPointPosition[point]) || Math.Abs(s.ContactPointPosition[point] - model.Extension) > .01))
            { failure = EstimateFailure.GeometryUnavailable; return false; }
            decision = "point_temporal_prediction_nonfinite";
            var co = model.Full.Coefficients;
            var rotation = Dot(co, query);
            var deviation = useBounds ? 0 : model.Refits.Max(f => Math.Abs(Dot(f.Coefficients, query) - rotation));
            // Do not divide by sqrt(N): correlated terrain/model error does not
            // become independent information when the frame rate increases.
            var geometry = Math.Max(MinimumResidualFeet, model.Residual) * model.Full.Sensitivity(query) + deviation;
            var value = trajectory.Closure(0, co, datum.DatumOffsetFeet, kinematicMotion);
            var temporal = 0.0;
            foreach (var refit in alternatives)
            {
                var candidate = refit.Closure(0, co, datum.DatumOffsetFeet, kinematicMotion);
                if (!Finite(candidate)) return false;
                temporal = Math.Max(temporal, Math.Abs(candidate - value));
            }
            foreach (var t in new[] { TouchdownAnalysis.TimeOf(samples[last]), firstGroundTime })
            {
                var candidate = trajectory.Closure(t - contactTime, co, datum.DatumOffsetFeet, kinematicMotion);
                if (!Finite(candidate)) return false;
                temporal = Math.Max(temporal, Math.Abs(candidate - value));
            }
            var weight = trajectory.DatumWeight(0, kinematicMotion);
            var inertial = -trajectory.Value(Vy, 0) * 60;
            var rigidRotation = Dot(co, flatQuery) + datum.DatumOffsetFeet * weight;
            var terrain = trajectory.Derivative(Ground, 0) * 60 + rotation - Dot(co, flatQuery);
            var altitudeDisagreement = Math.Abs((-trajectory.Derivative(Altitude, 0) + trajectory.Derivative(Ground, 0)) * 60 + rotation - value);
            var band = Math.Max(15, geometry + temporal + altitudeDisagreement + datum.StandardDeviationFeet * Math.Abs(weight));
            if (kinematicMotion)
            {
                decision = "point_kinematic_evidence_unavailable";
                if (!TryKinematicUncertainty(model, trajectory, alternatives, pre, contactTime, firstGroundTime, co, query,
                    value, rotation, weight, ref band, ref geometry, ref altitudeDisagreement)) return false;
            }
            else if (useBounds)
            {
                decision = "point_prediction_bounds_unavailable";
                if (!TryBoundedUncertainty(model, trajectory, alternatives, pre, contactTime, firstGroundTime, co, query,
                    value, rotation, weight, ref band, ref geometry, ref altitudeDisagreement)) return false;
            }
            terms.Add(new Estimate { Closure = value, Inertial = inertial, Terrain = terrain, Rotation = rigidRotation,
                Uncertainty = band, Arm = co[1] + datum.DatumOffsetFeet, GeometryUncertainty = geometry,
                TemporalUncertainty = temporal, AltitudeDisagreement = altitudeDisagreement });
        }
        var mean = terms.Average(e => e.Closure);
        var uncertainty = terms.Max(e => Math.Abs(e.Closure - mean) + e.Uncertainty);
        estimate = new Estimate { Closure = mean, Inertial = terms.Average(e => e.Inertial), Terrain = terms.Average(e => e.Terrain),
            Rotation = terms.Average(e => e.Rotation), Uncertainty = uncertainty, Arm = terms.Average(e => e.Arm),
            Quality = datum.Quality / (1 + Math.Pow(uncertainty / 15, 2)), FitPointCount = pre.Count, Points = points,
            BoundedGeometry = useBounds, KinematicMotion = kinematicMotion,
            GeometryUncertainty = terms.Max(e => e.GeometryUncertainty), TemporalUncertainty = terms.Max(e => e.TemporalUncertainty),
            AltitudeDisagreement = terms.Max(e => e.AltitudeDisagreement) };
        decision = "point_prediction_uncertain";
        if (!Finite(mean) || !Finite(uncertainty) || uncertainty > MaximumUncertaintyFpm || mean <= uncertainty)
        { failure = EstimateFailure.PredictionUncertain; return false; }
        decision = kinematicMotion ? "contact_point_kinematic_accepted" :
            useBounds ? "contact_point_bounds_accepted" : "contact_point_fit_accepted";
        return true;
    }


    private bool TryKinematicUncertainty(PointModel model, Polynomial trajectory, IReadOnlyList<Polynomial> alternatives, List<TelemetrySample> pre,
        double contactTime, double firstGroundTime, double[] co, double[] query, double value, double rotation, double weight,
        ref double band, ref double geometry, ref double altitudeDisagreement)
    {
        var selectedDatum = datum!;
        var localAltitudeDisagreement = altitudeDisagreement;
        // Native body rates provide an independent observation of the
        // attitude derivative. Both that observation and the integral
        // of VY must agree with the original pre-contact samples.
        var envelope = 0.0; localAltitudeDisagreement = 0;
        bool IncludeKinematic(Polynomial candidate, double offset)
        {
            if (!candidate.TryQuery(offset, out var candidateQuery, out _, true) ||
                !candidate.TryKinematicDisagreement(pre, contactTime, offset, selectedDatum.DatumOffsetFeet,
                    out var angularError, out var heightError) ||
                !candidate.TryAngularGradients(offset, out var gradients, out _, out var datumGradient) ||
                !model.TryPredictionInterval(candidateQuery, out var lower, out var upper)) return false;
            var center = candidate.Closure(offset, co, selectedDatum.DatumOffsetFeet, true);
            var baseline = center - Dot(co, candidateQuery);
            var rotationError = 0.0;
            for (var axis = 0; axis < 3; axis++)
            {
                var direction = gradients[axis].Select(v => v * angularError[axis]).ToArray();
                if (!model.TryPredictionInterval(direction, out var lo, out var hi)) return false;
                var datumError = axis == 0 ? selectedDatum.DatumOffsetFeet * datumGradient * angularError[axis] : 0;
                rotationError += Math.Max(Math.Abs(lo + datumError), Math.Abs(hi + datumError));
            }
            var datumBand = selectedDatum.StandardDeviationFeet *
                (Math.Abs(candidate.DatumWeight(offset, true)) + Math.Abs(datumGradient * angularError[0]));
            var error = heightError + rotationError + datumBand;
            envelope = Math.Max(envelope, Math.Max(value - (baseline + lower - error), baseline + upper + error - value));
            localAltitudeDisagreement = Math.Max(localAltitudeDisagreement, heightError);
            return Finite(envelope) && Finite(error);
        }
        if (!IncludeKinematic(trajectory, 0)) return false;
        foreach (var candidate in alternatives) if (!IncludeKinematic(candidate, 0)) return false;
        foreach (var t in new[] { TouchdownAnalysis.TimeOf(pre[pre.Count - 1]), firstGroundTime })
            if (!IncludeKinematic(trajectory, t - contactTime)) return false;
        band = Math.Max(15, envelope);
        if (!model.TryPredictionInterval(query, out var low, out var high)) return false;
        geometry = Math.Max(rotation - low, high - rotation);
        altitudeDisagreement = localAltitudeDisagreement;
        return true;
    }

    private bool TryBoundedUncertainty(PointModel model, Polynomial trajectory, IReadOnlyList<Polynomial> alternatives, List<TelemetrySample> pre,
        double contactTime, double firstGroundTime, double[] co, double[] query, double value, double rotation, double weight,
        ref double band, ref double geometry, ref double altitudeDisagreement)
    {
        var selectedDatum = datum!;
        var localAltitudeDisagreement = altitudeDisagreement;
        if (!model.TryPredictionInterval(query, out var low, out var high)) return false;
        geometry = Math.Max(rotation - low, high - rotation);
        var envelope = geometry; var datumBand = selectedDatum.StandardDeviationFeet * Math.Abs(weight);
        bool Include(Polynomial candidate, double offset)
        {
            if (!candidate.TryQuery(offset, out var candidateQuery, out _) ||
                !model.TryPredictionInterval(candidateQuery, out var lower, out var upper)) return false;
            var center = candidate.Closure(offset, co, selectedDatum.DatumOffsetFeet);
            var baseline = center - Dot(co, candidateQuery);
            envelope = Math.Max(envelope, Math.Max(value - (baseline + lower), baseline + upper - value));
            var independent = (-candidate.Derivative(Altitude, offset) + candidate.Derivative(Ground, offset)) * 60 + Dot(co, candidateQuery);
            localAltitudeDisagreement = Math.Max(localAltitudeDisagreement, Math.Abs(independent - center));
            datumBand = Math.Max(datumBand, selectedDatum.StandardDeviationFeet * Math.Abs(candidate.DatumWeight(offset)));
            return Finite(envelope) && Finite(localAltitudeDisagreement);
        }
        foreach (var candidate in alternatives) if (!Include(candidate, 0)) return false;
        foreach (var t in new[] { TouchdownAnalysis.TimeOf(pre[pre.Count - 1]), firstGroundTime })
            if (!Include(trajectory, t - contactTime)) return false;
        band = Math.Max(15, envelope + localAltitudeDisagreement + datumBand);
        altitudeDisagreement = localAltitudeDisagreement;
        return true;
    }

    private static bool TryBoundedModel(List<Row> rows, out PointModel model)
    {
        model = null!;
        if (!TryFit(rows, out var full)) return false;
        var co = full.Coefficients;
        var lower = new[] { 0.0, -500, -500, -500 }; var upper = new[] { 500.0, 500, 500, 0 };
        if (Enumerable.Range(0, 4).Any(i => co[i] < lower[i] || co[i] > upper[i])) return false;
        var constraints = new List<double[]>(); var limits = new List<double>();
        foreach (var row in rows)
        {
            var residual = row.Height - full.Predict(row);
            // A pointwise bound is stronger than the RMS fit condition. The
            // full fit supplies a feasible origin; no row is removed or clipped.
            if (Math.Abs(residual) > MaximumResidualFeet) return false;
            var z = new[] { 1.0, (row.X[0] - full.Means[0]) / full.Scales[0],
                (row.X[1] - full.Means[1]) / full.Scales[1], (row.X[2] - full.Means[2]) / full.Scales[2] };
            constraints.Add(z); limits.Add(MaximumResidualFeet + residual);
            constraints.Add(z.Select(v => -v).ToArray()); limits.Add(MaximumResidualFeet - residual);
        }
        for (var i = 0; i < 4; i++)
        {
            var z = new double[4];
            if (i == 0)
            { z[0] = 1; for (var j = 0; j < 3; j++) z[j + 1] = -full.Means[j] / full.Scales[j]; }
            else z[i] = 1 / full.Scales[i - 1];
            constraints.Add(z); limits.Add(upper[i] - co[i]);
            constraints.Add(z.Select(v => -v).ToArray()); limits.Add(co[i] - lower[i]);
        }
        var delta = Enumerable.Range(0, 4).Select(i => Math.Max(co[i] - lower[i], upper[i] - co[i])).ToArray();
        var variableBounds = new double[4]; variableBounds[0] = delta[0];
        for (var i = 0; i < 3; i++)
        { variableBounds[0] += Math.Abs(full.Means[i]) * delta[i + 1]; variableBounds[i + 1] = full.Scales[i] * delta[i + 1]; }
        model = new PointModel { Full = full, Bounds = new BoundedLinearPrediction(constraints, limits, variableBounds) };
        return true;
    }

    private static bool TryModel(List<Row> rows, out PointModel model)
    {
        model = null!;
        if (!TryFit(rows, out var full)) return false;
        var result = new PointModel { Full = full };
        var duration = rows[rows.Count - 1].Time - rows[0].Time;
        for (var subset = 0; subset < 7; subset++)
        {
            var selected = new List<Row>(); var held = new List<Row>();
            foreach (var row in rows)
            {
                var fraction = (row.Time - rows[0].Time) / duration;
                var block = Math.Min(4, (int)(5 * fraction));
                var use = subset == 0 ? fraction <= .7 : subset == 1 ? fraction >= .3 : block != subset - 2;
                (use ? selected : held).Add(row);
            }
            if (held.Count == 0 || !TryFit(selected, out var fit)) return false;
            var residual = Math.Sqrt(held.Average(r => Math.Pow(r.Height - fit.Predict(r), 2)));
            if (!Finite(residual) || residual > MaximumResidualFeet) return false;
            result.Residual = Math.Max(result.Residual, residual);
            result.Refits.Add(fit);
        }
        model = result;
        return true;
    }

    private static bool TryFit(List<Row> rows, out Fit fit)
    {
        fit = null!;
        if (rows.Count < 20 || rows[rows.Count - 1].Time - rows[0].Time < .5) return false;
        var means = Enumerable.Range(0, 3).Select(i => rows.Average(r => r.X[i])).ToArray();
        var scales = Enumerable.Range(0, 3).Select(i => Math.Sqrt(rows.Average(r => Math.Pow(r.X[i] - means[i], 2)))).ToArray();
        if (scales[0] < .05 * Math.PI / 180 || scales[1] < .05 * Math.PI / 180 || scales[2] < .1) return false;
        var height = rows.Average(r => r.Height);
        var matrix = new double[3, 3]; var rhs = new double[3];
        foreach (var row in rows)
        {
            var z = Enumerable.Range(0, 3).Select(i => (row.X[i] - means[i]) / scales[i]).ToArray();
            for (var i = 0; i < 3; i++)
            {
                rhs[i] += z[i] * (row.Height - height) / rows.Count;
                for (var j = 0; j < 3; j++) matrix[i, j] += z[i] * z[j] / rows.Count;
            }
        }
        // This limit protects the numerical solve (normal-equation amplification
        // <= 1e6), not coordinate identifiability. The latter is evaluated in
        // the direction of the requested velocity through Sensitivity/refits.
        if (!TelemetrySpatialGearTopology.WellConditioned(matrix, 1000) || !TryInverse(matrix, out var inverse)) return false;
        var beta = new double[3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++) beta[i] += inverse[i, j] * rhs[j] / scales[i];
        // Positive fitted deflection is not physical. A zero-travel boundary is
        // a valid constrained solution for stiff gear; refit the other terms.
        if (beta[2] > 0)
        {
            var determinant = matrix[0, 0] * matrix[1, 1] - matrix[0, 1] * matrix[1, 0];
            if (determinant <= 1e-12) return false;
            beta[0] = (rhs[0] * matrix[1, 1] - rhs[1] * matrix[0, 1]) / determinant / scales[0];
            beta[1] = (rhs[1] * matrix[0, 0] - rhs[0] * matrix[1, 0]) / determinant / scales[1];
            beta[2] = 0;
        }
        var co = new[] { height - Dot(means, beta), beta[0], beta[1], beta[2] };
        if (co.Any(v => !Finite(v)) || co[0] < 0 || co.Take(3).Any(v => Math.Abs(v) > 500)) return false;
        var candidate = new Fit { Coefficients = co, Means = means, Scales = scales, Inverse = inverse };
        if (Math.Sqrt(rows.Average(r => Math.Pow(r.Height - candidate.Predict(r), 2))) > MaximumResidualFeet) return false;
        fit = candidate;
        return true;
    }

    private sealed class Polynomial
    {
        private double[,] coefficients = new double[3, 12];
        internal double Value(int field, double time) => coefficients[0, field] + time * (coefficients[1, field] + time * coefficients[2, field]);
        internal double Derivative(int field, double time) => coefficients[1, field] + 2 * time * coefficients[2, field];

        internal static bool TryFit(List<TelemetrySample> rows, double origin, int minimum, out Polynomial polynomial)
        {
            polynomial = null!;
            if (rows.Count < minimum) return false;
            var matrix = new double[3, 3]; var rhs = new double[3, 12];
            var baseRow = rows[0];
            var heading = baseRow.HeadingTrueDegrees * Math.PI / 180;
            var latitude = baseRow.LatitudeDegrees * Math.PI / 180;
            if (!Finite(latitude) || Math.Abs(latitude) >= Math.PI / 2) return false;
            var values = new List<double[]>();
            foreach (var row in rows)
            {
                var v = new[] { row.VelocityWorldYFps, row.GroundAltitudeFeet, row.PlaneAltitudeFeet,
                    row.PitchDegrees * Math.PI / 180, row.BankDegrees * Math.PI / 180,
                    row.RotationVelocityBodyXRadiansPerSecond, row.RotationVelocityBodyYRadiansPerSecond,
                    heading + Angle(row.HeadingTrueDegrees * Math.PI / 180 - heading),
                    (row.LatitudeDegrees - baseRow.LatitudeDegrees) * FeetPerDegree,
                    Angle((row.LongitudeDegrees - baseRow.LongitudeDegrees) * Math.PI / 180) * 180 / Math.PI * FeetPerDegree * Math.Cos(latitude),
                    row.GroundSpeedKnots * 1.6878098571, row.RotationVelocityBodyZRadiansPerSecond };
                // Z is required only by the kinematic fallback. Retain the
                // existing models when this extra channel was not recorded.
                if (v.Take(11).Any(x => !Finite(x))) return false;
                values.Add(v);
            }
            var means = Enumerable.Range(0, 12).Select(k => values.Average(v => v[k])).ToArray();
            for (var index = 0; index < rows.Count; index++)
            {
                var t = TouchdownAnalysis.TimeOf(rows[index]) - origin;
                if (!Finite(t) || !rows[index].MotionSimulation || (index > 0 && !Continuous(rows[index - 1], rows[index]))) return false;
                var z = new[] { 1.0, t, t * t };
                for (var i = 0; i < 3; i++)
                {
                    for (var j = 0; j < 3; j++) matrix[i, j] += z[i] * z[j];
                    for (var k = 0; k < 12; k++) rhs[i, k] += z[i] * (values[index][k] - means[k]);
                }
            }
            if (!TryInverse(matrix, out var inverse)) return false;
            var result = new Polynomial();
            for (var i = 0; i < 3; i++)
                for (var k = 0; k < 12; k++)
                    for (var j = 0; j < 3; j++) result.coefficients[i, k] += inverse[i, j] * rhs[j, k];
            for (var k = 0; k < 12; k++) result.coefficients[0, k] += means[k];
            polynomial = result;
            return true;
        }

        internal bool TryBasis(double t, out double[] basis, out double[] flat,
            double? pitch = null, double? bank = null, double? heading = null)
        {
            basis = flat = Array.Empty<double>();
            var th = pitch ?? Value(Pitch, t); var ph = bank ?? Value(Bank, t);
            var vn = Derivative(North, t); var ve = Derivative(East, t);
            var speed = Math.Sqrt(vn * vn + ve * ve);
            var delta = Angle(Math.Atan2(ve, vn) - (heading ?? Value(Heading, t)));
            var slope = Derivative(Ground, t) / speed;
            if (!Finite(slope) || !ReliableGroundMotion(t) ||
                Math.Abs(delta) > MaximumCrabRadians || Math.Abs(slope) > .1 ||
                Math.Abs(th) > Math.PI / 6 || Math.Abs(ph) > Math.PI / 6) return false;
            var sf = slope * Math.Cos(delta); var sr = slope * Math.Sin(delta);
            // SDK yaw/pitch/roll body-to-world matrix: vertical components of
            // the forward, right and up axes are -sin(th), sin(ph)*cos(th),
            // cos(ph)*cos(th). X is forward, Y is negative body-right, and b0
            // is down. Project the same rotated point onto the local surface.
            flat = new[] { Math.Cos(th) * Math.Cos(ph), Math.Sin(th), Math.Sin(ph) * Math.Cos(th) };
            basis = new[] { flat[0] - sf * Math.Cos(ph) * Math.Sin(th) + sr * Math.Sin(ph),
                flat[1] + sf * Math.Cos(th),
                flat[2] - sf * Math.Sin(ph) * Math.Sin(th) - sr * Math.Cos(ph) };
            return basis.All(Finite) && basis[0] > .5;
        }

        internal bool ReliableGroundMotion(double t)
        {
            var vn = Derivative(North, t); var ve = Derivative(East, t);
            var speed = Math.Sqrt(vn * vn + ve * ve);
            return Finite(speed) && speed >= 5 && Math.Abs(speed - Value(Speed, t)) <= Math.Max(5, .1 * Value(Speed, t));
        }

        internal bool TryQuery(double t, out double[] query, out double[] flatQuery, bool kinematic = false)
        {
            query = flatQuery = Array.Empty<double>();
            const double epsilon = .0001;
            if (!TryBasis(t + epsilon, out var after, out var flatAfter) ||
                !TryBasis(t - epsilon, out var before, out var flatBefore)) return false;
            query = Enumerable.Range(0, 3).Select(i => (after[i] - before[i]) * 60 / (2 * epsilon)).ToArray();
            flatQuery = Enumerable.Range(0, 3).Select(i => (flatAfter[i] - flatBefore[i]) * 60 / (2 * epsilon)).ToArray();
            if (kinematic)
            {
                if (!TryNativeRates(t, out var rates) || !TryAngularGradients(t, out var gradients, out var flatGradients, out _)) return false;
                var fields = new[] { Pitch, Bank, Heading };
                for (var axis = 0; axis < 3; axis++)
                for (var i = 0; i < 3; i++)
                {
                    var difference = rates[axis] - Derivative(fields[axis], t);
                    query[i] += difference * gradients[axis][i];
                    flatQuery[i] += difference * flatGradients[axis][i];
                }
            }
            return true;
        }

        internal double DatumWeight(double t, bool kinematic = false) => Math.Cos(Value(Pitch, t)) *
            (kinematic ? (TryNativeRates(t, out var rates) ? rates[0] : double.NaN) : Derivative(Pitch, t)) * 60;

        internal double Closure(double t, double[] co, double datumOffset, bool kinematic = false) => TryQuery(t, out var query, out _, kinematic)
            ? (-Value(Vy, t) + Derivative(Ground, t)) * 60 + Dot(co, query) + datumOffset * DatumWeight(t, kinematic)
            : double.NaN;

        private static double[] NativeRates(double th, double ph, double q, double r, double p) => new[]
        {
            q * Math.Cos(ph) - r * Math.Sin(ph),
            p + (q * Math.Sin(ph) + r * Math.Cos(ph)) * Math.Tan(th),
            (q * Math.Sin(ph) + r * Math.Cos(ph)) / Math.Cos(th),
        };

        private bool TryNativeRates(double t, out double[] rates)
        {
            rates = NativeRates(Value(Pitch, t), Value(Bank, t), Value(OmegaX, t), Value(OmegaY, t), Value(OmegaZ, t));
            return rates.All(Finite);
        }

        internal bool TryAngularGradients(double t, out double[][] gradients, out double[][] flatGradients, out double datumGradient)
        {
            gradients = flatGradients = Array.Empty<double[]>(); datumGradient = double.NaN;
            if (!TryBasis(t, out _, out _)) return false;
            var th = Value(Pitch, t); var ph = Value(Bank, t);
            var vn = Derivative(North, t); var ve = Derivative(East, t);
            var delta = Angle(Math.Atan2(ve, vn) - Value(Heading, t));
            var slope = Derivative(Ground, t) / Math.Sqrt(vn * vn + ve * ve);
            var sf = slope * Math.Cos(delta); var sr = slope * Math.Sin(delta);
            var ct = Math.Cos(th); var st = Math.Sin(th); var cb = Math.Cos(ph); var sb = Math.Sin(ph);
            flatGradients = new[] { new[] { -cb * st * 60, ct * 60, -sb * st * 60 },
                new[] { -sb * ct * 60, 0.0, cb * ct * 60 }, new double[3] };
            gradients = new[]
            {
                new[] { (-cb * st - sf * cb * ct) * 60, (ct - sf * st) * 60, (-sb * st - sf * sb * ct) * 60 },
                new[] { (-sb * ct + sf * sb * st + sr * cb) * 60, 0.0, (cb * ct - sf * cb * st + sr * sb) * 60 },
                new[] { (-cb * st * sr - sb * sf) * 60, ct * sr * 60, (-sb * st * sr + cb * sf) * 60 },
            };
            datumGradient = ct * 60;
            return gradients.SelectMany(g => g).All(Finite);
        }

        internal bool TryKinematicDisagreement(List<TelemetrySample> rows, double origin, double offset, double datumOffset,
            out double[] angularError, out double heightError)
        {
            angularError = new double[3]; heightError = 0;
            var fitError = new double[3]; var integralError = new double[3];
            var residuals = new List<double>(); var times = new List<double>();
            double[]? previousAngles = null, previousRates = null;
            foreach (var row in rows)
            {
                var t = TouchdownAnalysis.TimeOf(row) - origin;
                var angles = new[] { row.PitchDegrees * Math.PI / 180, row.BankDegrees * Math.PI / 180, row.HeadingTrueDegrees * Math.PI / 180 };
                if (angles.Any(v => !Finite(v)) || Math.Abs(angles[0]) > Math.PI / 6 || Math.Abs(angles[1]) > Math.PI / 6) return false;
                var actual = NativeRates(angles[0], angles[1], row.RotationVelocityBodyXRadiansPerSecond,
                    row.RotationVelocityBodyYRadiansPerSecond, row.RotationVelocityBodyZRadiansPerSecond);
                if (actual.Any(v => !Finite(v)) || !TryNativeRates(t, out var fitted)) return false;
                var expected = coefficients[0, Vy] * t + coefficients[1, Vy] * t * t / 2 + coefficients[2, Vy] * t * t * t / 3
                    - datumOffset * Math.Sin(angles[0]);
                var residual = row.PlaneAltitudeFeet - expected;
                if (!Finite(residual)) return false;
                for (var axis = 0; axis < 3; axis++) fitError[axis] = Math.Max(fitError[axis], Math.Abs(actual[axis] - fitted[axis]));
                if (previousAngles != null && previousRates != null)
                {
                    var dt = t - times[times.Count - 1];
                    if (dt <= 1e-9 || dt > MaximumGap) return false;
                    for (var axis = 0; axis < 3; axis++)
                        integralError[axis] = Math.Max(integralError[axis],
                            Math.Abs(Angle(angles[axis] - previousAngles[axis]) / dt - (actual[axis] + previousRates[axis]) / 2));
                    heightError = Math.Max(heightError, Math.Abs(residual - residuals[residuals.Count - 1]) * 60 / dt);
                }
                times.Add(t); residuals.Add(residual); previousAngles = angles; previousRates = actual;
            }
            // Detect residual drift at the requested time as well as every
            // measured interval. Validate omitted-frame fits on ALL original
            // frames; an omitted observation cannot hide a channel mismatch.
            var matrix = new double[3, 3]; var rhs = new double[3]; var mean = residuals.Average();
            for (var index = 0; index < times.Count; index++)
            {
                var t = times[index]; var z = new[] { 1.0, t, t * t };
                for (var i = 0; i < 3; i++)
                {
                    rhs[i] += z[i] * (residuals[index] - mean);
                    for (var j = 0; j < 3; j++) matrix[i, j] += z[i] * z[j];
                }
            }
            if (!TryInverse(matrix, out var inverse)) return false;
            var drift = 0.0;
            for (var i = 0; i < 3; i++) drift += (inverse[1, i] + 2 * offset * inverse[2, i]) * rhs[i];
            heightError = Math.Max(heightError, Math.Abs(drift) * 60);
            for (var axis = 0; axis < 3; axis++) angularError[axis] = fitError[axis] + integralError[axis];
            return Finite(heightError) && angularError.All(Finite);
        }
    }

    private static bool TryInverse(double[,] source, out double[,] inverse)
    {
        var m = new double[3, 6]; inverse = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 6; j++) m[i, j] = j < 3 ? source[i, j] : (j - 3 == i ? 1 : 0);
        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var i = col + 1; i < 3; i++) if (Math.Abs(m[i, col]) > Math.Abs(m[pivot, col])) pivot = i;
            if (!Finite(m[pivot, col]) || Math.Abs(m[pivot, col]) < 1e-14) return false;
            for (var j = 0; j < 6; j++) { var v = m[col, j]; m[col, j] = m[pivot, j]; m[pivot, j] = v; }
            var scale = m[col, col];
            for (var j = 0; j < 6; j++) m[col, j] /= scale;
            for (var i = 0; i < 3; i++)
            {
                if (i == col) continue;
                var factor = m[i, col];
                for (var j = 0; j < 6; j++) m[i, j] -= factor * m[col, j];
            }
        }
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++) inverse[i, j] = m[i, j + 3];
        return true;
    }

    private static bool Continuous(TelemetrySample before, TelemetrySample after) =>
        before.MotionSimulation && after.MotionSimulation && before.FlightSegment == after.FlightSegment &&
        TouchdownAnalysis.TimeOf(after) - TouchdownAnalysis.TimeOf(before) > 1e-9 &&
        TouchdownAnalysis.TimeOf(after) - TouchdownAnalysis.TimeOf(before) <= MaximumGap;
    private static double Dot(double[] a, double[] b, int offset = 0)
    {
        var sum = 0.0;
        for (var i = 0; i < b.Length; i++) sum += a[i + offset] * b[i];
        return sum;
    }
    private static double Angle(double value) => Math.Atan2(Math.Sin(value), Math.Cos(value));
    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);
}
