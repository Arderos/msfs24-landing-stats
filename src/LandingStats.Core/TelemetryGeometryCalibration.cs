using static LandingStats.Core.GeometryCalibrationPolicy;
using static LandingStats.Core.GeometryCalibrationMath;
using static LandingStats.Core.GeometryCalibrationInputs;
using static LandingStats.Core.PitchArmCalibration;
using static LandingStats.Core.DatumOffsetCalibration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingStats.Core;

/// <summary>
/// Geometry recovered from the aircraft's own telemetry, without a flight-model
/// configuration file or a touchdown-velocity target.
/// </summary>
public sealed class TelemetryGeometryCalibrationResult
{
    internal TelemetryGeometryCalibrationResult(
        double longitudinalArmFeet,
        double pitchArmFeet,
        double datumOffsetFeet,
        double datumOffsetStandardDeviationFeet,
        int mainOnlySampleCount,
        int datumPhaseCount,
        int datumWindowCount,
        double quality,
        int loadedWheelSampleCount = 0)
    {
        LongitudinalArmFeet = longitudinalArmFeet;
        PitchArmFeet = pitchArmFeet;
        DatumOffsetFeet = datumOffsetFeet;
        DatumOffsetStandardDeviationFeet = datumOffsetStandardDeviationFeet;
        MainOnlySampleCount = mainOnlySampleCount;
        DatumPhaseCount = datumPhaseCount;
        DatumWindowCount = datumWindowCount;
        Quality = quality;
        LoadedWheelSampleCount = loadedWheelSampleCount;
    }

    /// <summary>
    /// Signed longitudinal arm from the velocity reference point to the main-gear axis.
    /// </summary>
    public double LongitudinalArmFeet { get; }

    /// <summary>
    /// Pitch coefficient from the main-gear height regression, in feet per radian.
    /// </summary>
    public double PitchArmFeet { get; }

    /// <summary>
    /// Signed longitudinal offset between the altitude datum and velocity reference point.
    /// </summary>
    public double DatumOffsetFeet { get; }

    /// <summary>
    /// Population standard deviation across four interleaved start-phase estimates.
    /// The estimates use overlapping windows, so this measures numerical agreement,
    /// not an independent statistical sigma.
    /// </summary>
    public double DatumOffsetStandardDeviationFeet { get; }

    public int MainOnlySampleCount { get; }

    /// <summary>Sum of loaded main-wheel observations used by the spatial fallback;
    /// zero for the established main-only calibration.</summary>
    public int LoadedWheelSampleCount { get; }

    public bool UsesSpatialArmInference => LoadedWheelSampleCount > 0;

    public int DatumPhaseCount { get; }

    public int DatumWindowCount { get; }

    /// <summary>
    /// A 0..1 identifiability score, not a statistical probability. It combines
    /// design/refit evidence, four-phase coverage, and agreement across
    /// interleaved datum-phase estimates.
    /// </summary>
    public double Quality { get; }
}

/// <summary>
/// Offset between the simulator altitude datum and the reference point whose
/// velocity is exposed as VELOCITY WORLD Y.
/// </summary>
public sealed class TelemetryDatumOffsetCalibrationResult
{
    internal TelemetryDatumOffsetCalibrationResult(
        double datumOffsetFeet,
        double standardDeviationFeet,
        int phaseCount,
        int windowCount,
        double quality)
    {
        DatumOffsetFeet = datumOffsetFeet;
        StandardDeviationFeet = standardDeviationFeet;
        PhaseCount = phaseCount;
        WindowCount = windowCount;
        Quality = quality;
    }

    public double DatumOffsetFeet { get; }

    public double StandardDeviationFeet { get; }

    public int PhaseCount { get; }

    public int WindowCount { get; }

    /// <summary>
    /// A 0..1 agreement score based on phase coverage and interleaved estimates.
    /// </summary>
    public double Quality { get; }
}

internal sealed class TelemetryGearTopology
{
    public TelemetryGearTopology(
        int[] mainContactPointIndices,
        int[] noseContactPointIndices,
        bool usesClusteredWheelInference = false,
        bool usesSpatialInference = false,
        TelemetrySpatialGearTopology.Evidence? spatialEvidence = null)
    {
        MainContactPointIndices = mainContactPointIndices;
        NoseContactPointIndices = noseContactPointIndices;
        UsesClusteredWheelInference = usesClusteredWheelInference;
        UsesSpatialInference = usesSpatialInference;
        SpatialEvidence = spatialEvidence;
    }

    public int[] MainContactPointIndices { get; }

    public int MainContactPointCount => MainContactPointIndices.Length;

    public int[] NoseContactPointIndices { get; }

    public bool UsesClusteredWheelInference { get; }

    public bool UsesSpatialInference { get; }

    internal TelemetrySpatialGearTopology.Evidence? SpatialEvidence { get; }
}

internal enum TelemetryGeometryFailure
{
    None,
    InsufficientSamples,
    UnsupportedTopology,
    IncompleteSettledGear,
    InsufficientDerotation,
    InsufficientMainOnlyEvidence,
    AmbiguousContactRoles,
    ConflictingSpatialRoles,
    UnstableSpatialArm,
    UnstablePitchArm,
    DatumOffsetUnavailable,
    InvalidArm,
    LowCalibrationQuality,
}

/// <summary>
/// Recovers the effective longitudinal main-gear arm using only recorded telemetry.
/// </summary>
public static class TelemetryGeometryCalibration
{

    /// <summary>
    /// Attempts a target-independent calibration. Contact-point indices have no semantic
    /// meaning here: contact timing/derotation and loaded-wheel spatial fits provide
    /// topology evidence. Repeated contacts must support one consistent set of roles.
    /// Additional multi-main/single-nose and earlier-phase hypotheses also require
    /// stable pitch-arm estimates across temporal subsets of main-only observations.
    /// </summary>
    public static bool TryCalibrate(
        IReadOnlyList<TelemetrySample> samples,
        out TelemetryGeometryCalibrationResult result)
    {
        return TryCalibrate(samples, out result, out _);
    }

    internal static bool TryCalibrate(
        IReadOnlyList<TelemetrySample> samples,
        out TelemetryGeometryCalibrationResult result,
        out TelemetryGeometryFailure failure,
        TelemetryGearTopology? acceptedTopology = null,
        TelemetryDatumCalibrationVersion datumVersion = TelemetryDatumCalibrationVersion.BankedRotation)
    {
        result = null!;
        failure = TelemetryGeometryFailure.InsufficientSamples;
        if (samples is null || samples.Count < MinimumPitchRegressionRows)
        {
            return false;
        }

        // CSV schema 5 can contain repeated simulation times. Keep the latest
        // state at each time without erasing intervening physics boundaries.
        var ordered = Normalize(samples);
        if (ordered.Count < MinimumPitchRegressionRows)
        {
            return false;
        }

        var topology = acceptedTopology;
        if (topology == null && !TryDetectConventionalTopology(ordered, out topology, out failure))
        {
            return false;
        }

        var times = new double[ordered.Count];
        var planeAltitude = new double[ordered.Count];
        var groundAltitude = new double[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            times[index] = TelemetryAnalysisFrames.TimeOf(ordered[index]);
            planeAltitude[index] = ordered[index].MotionSimulation ? ordered[index].PlaneAltitudeFeet : double.NaN;
            groundAltitude[index] = ordered[index].MotionSimulation ? ordered[index].GroundAltitudeFeet : double.NaN;
        }

        GroundAltitudeFilters.ForCalibration(planeAltitude);
        GroundAltitudeFilters.ForCalibration(groundAltitude);

        var pitchRecovered = TryRecoverPitchArm(
                ordered,
                times,
                planeAltitude,
                groundAltitude,
                topology,
                out var pitchArm,
                out var mainOnlySampleCount,
                out var designIndependence);
        failure = TelemetryGeometryFailure.InsufficientMainOnlyEvidence;
        if (pitchRecovered && topology.UsesSpatialInference &&
            !TryRecoverPitchArm(ordered, times, planeAltitude, groundAltitude, topology,
                out _, out _, out _, requireStableArm: true))
        {
            pitchRecovered = false;
            failure = TelemetryGeometryFailure.UnstablePitchArm;
        }
        var loadedWheelSampleCount = 0;
        if (!pitchRecovered)
        {
            var spatial = topology.SpatialEvidence;
            if (spatial == null || !spatial.StableRoles) return false;
            if (!spatial.StableMainArm)
            {
                failure = TelemetryGeometryFailure.UnstableSpatialArm;
                return false;
            }
            pitchArm = spatial.PitchArmFeet;
            mainOnlySampleCount = 0;
            loadedWheelSampleCount = spatial.LoadedWheelSampleCount;
        }

        if (!TryRecoverDatumOffsetCore(
                ordered,
                times,
                planeAltitude,
                out var datumOffset,
                out var datumStandardDeviation,
                out var datumPhaseCount,
                out var datumWindowCount,
                datumVersion: datumVersion))
        {
            failure = TelemetryGeometryFailure.DatumOffsetUnavailable;
            return false;
        }

        var longitudinalArm = pitchArm + datumOffset;
        if (!IsFinite(longitudinalArm) || Math.Abs(longitudinalArm) > MaximumAbsoluteArmFeet)
        {
            failure = TelemetryGeometryFailure.InvalidArm;
            return false;
        }

        // 0.05 is not a fitted aircraft constant. It merely marks a comfortably
        // non-collinear two-predictor design; values below it are degraded smoothly.
        // Spatial acceptance already checks a standardized three-predictor
        // condition number and absolute refit bounds. Its conservative score
        // summarizes refit agreement; it is not a physical error probability.
        var designQuality = loadedWheelSampleCount > 0
            ? 0.8 / (1 + Square(topology.SpatialEvidence!.ArmRefitDeviationFeet))
            : Math.Sqrt(Clamp01(designIndependence / 0.05));
        var phaseCoverage = datumPhaseCount / 4.0;
        var interleavedPhaseAgreement = 1.0 /
            (1.0 + Square(datumStandardDeviation / 0.5));
        var quality = Clamp01(designQuality * phaseCoverage * interleavedPhaseAgreement);
        if (quality < MinimumAcceptedQuality)
        {
            failure = TelemetryGeometryFailure.LowCalibrationQuality;
            return false;
        }

        result = new TelemetryGeometryCalibrationResult(
            longitudinalArm,
            pitchArm,
            datumOffset,
            datumStandardDeviation,
            mainOnlySampleCount,
            datumPhaseCount,
            datumWindowCount,
            quality,
            loadedWheelSampleCount);
        failure = TelemetryGeometryFailure.None;
        return true;
    }

    /// <summary>
    /// Recovers only the altitude-datum to WORLD Y reference offset from
    /// airborne kinematics. Unlike <see cref="TryCalibrate"/>, this does not
    /// infer landing-gear geometry and therefore can be combined with a
    /// readable flight_model.cfg main-gear coordinate.
    /// </summary>
    public static bool TryRecoverDatumOffset(
        IReadOnlyList<TelemetrySample> samples,
        out TelemetryDatumOffsetCalibrationResult result) => TryRecoverDatumOffset(samples, false, out result);

    public static bool TryRecoverDatumOffset(IReadOnlyList<TelemetrySample> samples,
        out TelemetryDatumOffsetCalibrationResult result, TelemetryDatumCalibrationVersion version)
    {
        if (!Enum.IsDefined(typeof(TelemetryDatumCalibrationVersion), version)) throw new ArgumentOutOfRangeException(nameof(version));
        return TryRecoverDatumOffset(samples, false, out result, version);
    }

    // The observed-point model uses the SDK attitude matrix throughout. Its
    // forward-axis vertical displacement is exactly -delta(sin(pitch)); the
    // native-rate projection is independently versioned in archived geometry.
    internal static bool TryRecoverDatumOffsetFromAttitude(
        IReadOnlyList<TelemetrySample> samples,
        out TelemetryDatumOffsetCalibrationResult result) => TryRecoverDatumOffset(samples, true, out result);

    private static bool TryRecoverDatumOffset(
        IReadOnlyList<TelemetrySample> samples,
        bool useAttitudeDifference,
        out TelemetryDatumOffsetCalibrationResult result,
        TelemetryDatumCalibrationVersion datumVersion = TelemetryDatumCalibrationVersion.BankedRotation)
    {
        result = null!;
        if (samples is null || samples.Count < MinimumPitchRegressionRows)
        {
            return false;
        }

        var ordered = Normalize(samples);
        if (ordered.Count < MinimumPitchRegressionRows)
        {
            return false;
        }

        var times = new double[ordered.Count];
        var planeAltitude = new double[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            times[index] = TelemetryAnalysisFrames.TimeOf(ordered[index]);
            planeAltitude[index] = ordered[index].MotionSimulation ? ordered[index].PlaneAltitudeFeet : double.NaN;
        }

        GroundAltitudeFilters.ForCalibration(planeAltitude);
        if (!TryRecoverDatumOffsetCore(
                ordered,
                times,
                planeAltitude,
                out var datumOffset,
                out var standardDeviation,
                out var phaseCount,
                out var windowCount,
                useAttitudeDifference, datumVersion))
        {
            return false;
        }

        if (!TryScoreDatumCalibration(phaseCount, standardDeviation, out var quality))
        {
            return false;
        }

        result = new TelemetryDatumOffsetCalibrationResult(
            datumOffset,
            standardDeviation,
            phaseCount,
            windowCount,
            quality);
        return true;
    }

    internal static bool TryScoreDatumCalibration(
        int phaseCount,
        double standardDeviation,
        out double quality)
    {
        quality = 0.0;
        if (phaseCount < MinimumDatumPhases || phaseCount > 4 ||
            !IsFinite(standardDeviation) || standardDeviation < 0.0)
        {
            return false;
        }

        var phaseCoverage = phaseCount / 4.0;
        var interleavedPhaseAgreement = 1.0 /
            (1.0 + Square(standardDeviation / 0.5));
        quality = Clamp01(phaseCoverage * interleavedPhaseAgreement);
        return quality >= MinimumAcceptedQuality;
    }

    internal static bool TryDetectConventionalTopology(IReadOnlyList<TelemetrySample> samples, out TelemetryGearTopology topology) => TelemetryTopologyInference.TryDetectConventionalTopology(samples, out topology);
    internal static bool TryDetectConventionalTopology(IReadOnlyList<TelemetrySample> samples, out TelemetryGearTopology topology, out TelemetryGeometryFailure failure) => TelemetryTopologyInference.TryDetectConventionalTopology(samples, out topology, out failure);
    internal static bool TryCreateConfiguredTopology(IReadOnlyList<TouchdownMainGearContactPoint>? mains, IReadOnlyList<int>? noses, out TelemetryGearTopology topology) => TelemetryTopologyInference.TryCreateConfiguredTopology(mains, noses, out topology);
    internal static bool IsSustainedMainContact(IReadOnlyList<TelemetrySample> samples, int contact, int previous, TelemetryGearTopology topology) => TelemetryTopologyInference.IsSustainedMainContact(samples, contact, previous, topology);
    internal static bool HasContactEvidence(TelemetrySample sample, int point) => GeometryContactEvidence.HasContactEvidence(sample, point);
    internal static bool HasContactTransition(TelemetrySample previous, TelemetrySample current, int point) => GeometryContactEvidence.HasContactTransition(previous, current, point);
}
