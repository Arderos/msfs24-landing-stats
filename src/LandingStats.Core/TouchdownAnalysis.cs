using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingStats.Core;

public static class TouchdownAnalysis
{
    private const double EpisodeGapSeconds = 10.0;
    private const double GWindowAfterSeconds = 2.0;
    private const double LatchUpdateWindowSeconds = 2.0;
    private const double InertialFitWindowSeconds = 0.2;
    private const double MinimumAirborneFitSeconds = 0.15;
    private const double GroundFitWindowSeconds = 0.2;
    private const double GroundOutlierThresholdFeet = 5.0;

    public static IReadOnlyList<TouchdownResult> Analyze(IReadOnlyList<TelemetrySample> samples) =>
        Analyze(samples, null);

    public static IReadOnlyList<TouchdownResult> Analyze(
        IReadOnlyList<TelemetrySample> samples,
        TouchdownAnalysisOptions? options)
    {
        samples = TelemetryAnalysisFrames.Prepare(samples, options?.DecisionJournal);
        var starts = TelemetryKinematicDiscontinuity.FindSegmentStarts(samples);
        if (starts.Count == 1) return AnalyzeSegment(samples, options);

        // A raw-debug archive may span several flights and even reset clocks.
        // Calibrate each independently, not merely its contact transitions.
        starts.Add(samples.Count);
        var combined = new List<TouchdownResult>();
        var episodeOffset = 0;
        for (var segment = 0; segment + 1 < starts.Count; segment++)
        {
            var part = new List<TelemetrySample>(starts[segment + 1] - starts[segment]);
            for (var index = starts[segment]; index < starts[segment + 1]; index++) part.Add(samples[index]);
            options?.DecisionJournal?.Record(TimeOf(part[0]), "analysis", "flight_segment_isolated",
                "Analyze and calibrate this physical segment without borrowing telemetry across SDK flight changes, unexplained physics stops or clock/position replacements.",
                new Dictionary<string, double>
                {
                    ["flight_segment"] = part[0].FlightSegment,
                    ["episode_number_offset"] = episodeOffset,
                    ["sample_count"] = part.Count,
                });
            if (segment > 0)
            {
                foreach (var entry in part)
                {
                    if (!entry.MotionSimulation || entry.SimulatorState == TelemetrySimulatorState.Inactive) continue;
                    if (entry.OnGround)
                        options?.DecisionJournal?.Record(TimeOf(entry), "contact", "physics_discontinuity_rejected",
                            "The replacement physical segment starts on the ground without an airborne witness; do not create a contact from the preceding segment.",
                            new Dictionary<string, double> { ["sample_index"] = starts[segment] });
                    break;
                }
            }
            var results = AnalyzeSegment(part, options);
            var maximumEpisode = episodeOffset;
            foreach (var result in results)
            {
                result.EpisodeNumber += episodeOffset;
                maximumEpisode = Math.Max(maximumEpisode, result.EpisodeNumber);
                combined.Add(result);
            }
            episodeOffset = maximumEpisode;
        }
        return combined;
    }

    private static IReadOnlyList<TouchdownResult> AnalyzeSegment(
        IReadOnlyList<TelemetrySample> samples,
        TouchdownAnalysisOptions? options)
    {
        var journal = options?.DecisionJournal;
        var candidates = TelemetryContactDetector.Find(samples, EpisodeGapSeconds, journal);
        var analysisTime = samples.Count > 0 ? TimeOf(samples[samples.Count - 1]) : 0;

        var longitudinalArmFeet = double.NaN;
        var geometryQuality = double.NaN;
        var armRecoveredFromTelemetry = false;
        var spatialArmRecovered = false;
        var geometrySource = TouchdownGeometrySource.Unavailable;
        TelemetryGearTopology? gearTopology = null;
        IReadOnlyList<TouchdownMainGearContactPoint>? configuredMainPoints = null;
        if (candidates.Count > 0 && options != null &&
            TelemetryGeometryCalibration.TryCreateConfiguredTopology(
                options.MainGearContactPoints,
                options.NoseGearContactPointIndices,
                out var configuredTopology))
        {
            // Most aircraft expose CONTACT POINT compression with the final
            // flight_model.cfg indices. Some complex add-ons, however, publish
            // extra visual-bogie channels whose numbering no longer maps one to
            // one to the readable wheel definitions. Trust configured roles only
            // when they actually explain a sustained touchdown transition; the
            // configured median arm remains useful with telemetry-inferred roles.
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (!TelemetryGeometryCalibration.IsSustainedMainContact(
                        samples,
                        candidate.ContactIndex,
                        candidate.PreviousAirborneIndex,
                        configuredTopology))
                {
                    continue;
                }

                gearTopology = configuredTopology;
                configuredMainPoints = options.MainGearContactPoints;
                break;
            }

            journal?.Record(analysisTime, "geometry",
                gearTopology != null ? "configured_topology_accepted" : "configured_topology_rejected",
                gearTopology != null
                    ? "Configured main and nose roles explain a sustained main-gear contact."
                    : "Configured contact-point roles do not explain a sustained main-gear contact; trying telemetry inference.");
        }

        if (gearTopology == null && candidates.Count > 0)
        {
            if (TelemetryGeometryCalibration.TryDetectConventionalTopology(samples, out var detectedTopology, out var failure))
            {
                gearTopology = detectedTopology;
                var details = new Dictionary<string, double>
                {
                    ["main_contact_point_count"] = gearTopology.MainContactPointCount,
                    ["clustered_wheel_inference"] = gearTopology.UsesClusteredWheelInference ? 1 : 0,
                    ["spatial_role_inference"] = gearTopology.UsesSpatialInference ? 1 : 0
                };
                for (var index = 0; index < gearTopology.MainContactPointIndices.Length; index++)
                    details["main_contact_point_" + index] = gearTopology.MainContactPointIndices[index];
                for (var index = 0; index < gearTopology.NoseContactPointIndices.Length; index++)
                    details["nose_contact_point_" + index] = gearTopology.NoseContactPointIndices[index];
                journal?.Record(analysisTime, "geometry", "telemetry_topology_accepted",
                    "Recovered conventional gear roles from contact-point telemetry.", details);
            }
            else
            {
                RecordGeometryFailure(journal, analysisTime, failure);
            }
        }

        if (gearTopology != null &&
            options?.LongitudinalMainGearArmFeet is double suppliedArm &&
            IsFinite(suppliedArm))
        {
            longitudinalArmFeet = suppliedArm;
            geometrySource = options.LongitudinalMainGearArmSource;
            if (options.LongitudinalMainGearArmQuality is double suppliedQuality &&
                IsFinite(suppliedQuality))
            {
                geometryQuality = suppliedQuality;
            }
        }
        else if (gearTopology != null &&
                 (options == null || options.RecoverLongitudinalMainGearArmFromTelemetry))
        {
            if (TelemetryGeometryCalibration.TryCalibrate(samples, out var calibration, out var failure))
            {
                longitudinalArmFeet = calibration.LongitudinalArmFeet;
                geometryQuality = calibration.Quality;
                armRecoveredFromTelemetry = true;
                spatialArmRecovered = calibration.UsesSpatialArmInference;
                geometrySource = TouchdownGeometrySource.Telemetry;
                if (spatialArmRecovered)
                    journal?.Record(analysisTime, "geometry", "spatial_arm_calibrated",
                        "Recovered a stable main axis from loaded-wheel height, attitude and compression, then corrected the airborne datum offset.",
                        new Dictionary<string, double>
                        {
                            ["loaded_wheel_sample_count"] = calibration.LoadedWheelSampleCount,
                            ["pitch_arm_feet"] = calibration.PitchArmFeet,
                            ["datum_offset_feet"] = calibration.DatumOffsetFeet,
                        });
            }
            else
            {
                RecordGeometryFailure(journal, analysisTime, failure, gearTopology.SpatialEvidence);
            }
        }

        journal?.Record(analysisTime, "geometry", IsFinite(longitudinalArmFeet) ? "arm_selected" : "arm_unavailable",
            IsFinite(longitudinalArmFeet) ? "Selected longitudinal arm source: " + geometrySource + "." :
                gearTopology == null ? "No supported gear topology; reconstruction has no validated contact geometry." :
                options != null && !options.RecoverLongitudinalMainGearArmFromTelemetry
                    ? "No finite supplied arm and recovery from telemetry is disabled."
                    : "No finite supplied arm and telemetry calibration did not produce a validated arm.",
            new Dictionary<string, double>
            {
                ["longitudinal_arm_feet"] = longitudinalArmFeet,
                ["quality"] = geometryQuality,
                ["source"] = (int)geometrySource
            });

        var results = new List<TouchdownResult>(candidates.Count);
        TelemetryContactPointReconstruction? pointModel = null;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var exclusiveEndTime = double.PositiveInfinity;
            if (index + 1 < candidates.Count && candidates[index + 1].EpisodeNumber == candidate.EpisodeNumber)
            {
                exclusiveEndTime = TimeOf(samples[candidates[index + 1].ContactIndex]);
            }

            var result = CreateResult(
                samples,
                candidate.ContactIndex,
                candidate.PreviousAirborneIndex,
                candidate.EpisodeNumber,
                candidate.ContactNumber,
                exclusiveEndTime,
                longitudinalArmFeet,
                geometryQuality,
                armRecoveredFromTelemetry,
                spatialArmRecovered,
                geometrySource,
                gearTopology,
                configuredMainPoints,
                journal);
            // Prefer the observed point at contact time when its geometry and
            // motion pass validation. Retain the longitudinal reconstruction if
            // that evidence is insufficient, and never replace supplied geometry.
            if ((options == null || options.RecoverLongitudinalMainGearArmFromTelemetry) &&
                !(options?.LongitudinalMainGearArmFeet is double externalArm && IsFinite(externalArm)))
            {
                pointModel ??= new TelemetryContactPointReconstruction(samples,
                    TimeOf(samples[candidates[0].ContactIndex]));
                var accepted = pointModel.TryEstimate(result.SimulationTimeSeconds, result.EstimatedContactTimeSeconds,
                    out var pointEstimate, out var pointDecision);
                var details = new Dictionary<string, double>
                {
                    ["episode_number"] = result.EpisodeNumber, ["contact_number"] = result.ContactNumber,
                    ["calibration_duration_seconds"] = pointModel.CalibrationDurationSeconds,
                    ["calibration_loaded_rows"] = pointModel.CalibrationRowCounts.Values.Sum()
                };
                if (pointEstimate != null)
                {
                    details["candidate_closure_fpm"] = pointEstimate.Closure;
                    details["candidate_uncertainty_fpm"] = pointEstimate.Uncertainty;
                }
                if (accepted && pointEstimate != null)
                {
                    result.ClosureReconstructionAvailable = true;
                    result.ClosureReconstructionModel = pointEstimate.KinematicMotion ? TelemetryContactPointReconstruction.KinematicModelName :
                        pointEstimate.BoundedGeometry ? TelemetryContactPointReconstruction.BoundedModelName : TelemetryContactPointReconstruction.ModelName;
                    result.ReconstructedClosureFpm = pointEstimate.Closure;
                    result.ReconstructedInertialFpm = pointEstimate.Inertial;
                    result.ReconstructedTerrainFpm = pointEstimate.Terrain;
                    result.ReconstructedPitchFpm = double.NaN;
                    result.ReconstructedRotationFpm = pointEstimate.Rotation;
                    result.ClosureReconstructionContactPointIndices = pointEstimate.Points;
                    result.ClosureReconstructionUncertaintyFpm = pointEstimate.Uncertainty;
                    result.ClosureReconstructionLongitudinalArmFeet = pointEstimate.Arm;
                    result.ClosureReconstructionGeometryQuality = pointEstimate.Quality;
                    result.ClosureReconstructionFitPointCount = pointEstimate.FitPointCount;
                    result.ClosureReconstructionGeometrySource = TouchdownGeometrySource.Telemetry;
                    result.ClosureReconstructionArmRecoveredFromTelemetry = true;
                    result.ClosureReconstructionResidualFpm = result.LatchUpdateDetected
                        ? result.LatchedNormalFpm - pointEstimate.Closure : double.NaN;
                    details["closure_fpm"] = pointEstimate.Closure;
                    details["uncertainty_fpm"] = pointEstimate.Uncertainty;
                    details["geometry_sensitivity_fpm"] = pointEstimate.GeometryUncertainty;
                    details["precontact_sensitivity_fpm"] = pointEstimate.TemporalUncertainty;
                    details["altitude_velocity_disagreement_fpm"] = pointEstimate.AltitudeDisagreement;
                    for (var p = 0; p < pointEstimate.Points.Length; p++) details["contact_point_" + p] = pointEstimate.Points[p];
                }
                journal?.Record(result.SimulationTimeSeconds, "reconstruction", pointDecision,
                    accepted ? "Recovered surface-relative vertical closure at the observed contact points; the reporting band includes geometry, pre-contact fit and contact-time sensitivity."
                        : "The contact-point model does not have sufficient stable evidence; calibration boundary: " + pointModel.CalibrationStopReason + ".", details);
                if (!accepted && result.ClosureReconstructionAvailable)
                    journal?.Record(result.SimulationTimeSeconds, "reconstruction", "longitudinal_fallback_selected",
                        "The observed-point model was unavailable; retained the validated longitudinal reconstruction at contact time minus 75 ms.",
                        new Dictionary<string, double>
                        {
                            ["episode_number"] = result.EpisodeNumber, ["contact_number"] = result.ContactNumber,
                            ["closure_fpm"] = result.ReconstructedClosureFpm,
                            ["uncertainty_fpm"] = result.ClosureReconstructionUncertaintyFpm
                        });
            }
            results.Add(result);
        }

        return results;
    }

    private static void RecordGeometryFailure(
        TelemetryDecisionJournal? journal,
        double time,
        TelemetryGeometryFailure failure,
        TelemetrySpatialGearTopology.Evidence? spatial = null)
    {
        if (journal == null) return;
        var (code, reason) = failure switch
        {
            TelemetryGeometryFailure.InsufficientSamples => ("insufficient_geometry_samples", "Too few usable telemetry samples to recover geometry."),
            TelemetryGeometryFailure.IncompleteSettledGear => ("incomplete_settled_gear", "The recording does not end with all required gear points in sustained contact."),
            TelemetryGeometryFailure.InsufficientDerotation => ("insufficient_derotation_evidence", "No continuous nose-free loading phase shows sufficient positive derotation before sustained nose contact."),
            TelemetryGeometryFailure.InsufficientMainOnlyEvidence => ("insufficient_main_only_evidence", "Sustained main-only observations are too short, too few or cannot identify a pitch/compression fit."),
            TelemetryGeometryFailure.AmbiguousContactRoles => ("ambiguous_contact_roles", "Different loading phases support different nose points; telemetry does not identify unique gear roles."),
            TelemetryGeometryFailure.ConflictingSpatialRoles => ("conflicting_spatial_roles", "Contact timing and loaded-wheel spatial evidence disagree; temporal spatial refits do not yet establish stable replacement roles."),
            TelemetryGeometryFailure.UnstableSpatialArm => ("unstable_spatial_arm", "Spatial roles are stable, but the main-only fit is unavailable or unstable and loaded-wheel refits do not support a stable, physically signed common main axis."),
            TelemetryGeometryFailure.UnstablePitchArm => ("unstable_pitch_arm", "The additional topology hypothesis does not produce an identifiable, stable arm across temporal subsets."),
            TelemetryGeometryFailure.DatumOffsetUnavailable => ("datum_offset_unavailable", "Airborne kinematics do not identify the altitude-datum to velocity-reference offset."),
            TelemetryGeometryFailure.InvalidArm => ("invalid_longitudinal_arm", "The recovered longitudinal arm is non-finite or outside the accepted range."),
            TelemetryGeometryFailure.LowCalibrationQuality => ("insufficient_geometry_quality", "The geometry fit does not meet the identifiability and datum-agreement limits."),
            _ => ("unsupported_gear_topology", "Sustained contact timing does not establish a supported gear topology."),
        };
        journal.Record(time, "geometry", code, reason, spatial == null ? null : new Dictionary<string, double>
        {
            ["arm_refit_deviation_feet"] = spatial.ArmRefitDeviationFeet,
            ["main_axis_difference_feet"] = spatial.MainAxisDifferenceFeet,
            ["nonphysical_main_compression"] = spatial.NonPhysicalMainCompression ? 1 : 0,
        });
    }

    private static TouchdownResult CreateResult(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        int previousAirborneIndex,
        int episodeNumber,
        int contactNumber,
        double exclusiveEndTime,
        double longitudinalArmFeet,
        double geometryQuality,
        bool armRecoveredFromTelemetry,
        bool spatialArmRecovered,
        TouchdownGeometrySource geometrySource,
        TelemetryGearTopology? gearTopology,
        IReadOnlyList<TouchdownMainGearContactPoint>? configuredMainPoints,
        TelemetryDecisionJournal? journal)
    {
        var contact = samples[contactIndex];
        var lastAirborne = samples[previousAirborneIndex];
        var contactTime = TimeOf(contact);
        var contactArmFeet = SelectContactArm(
            lastAirborne,
            contact,
            configuredMainPoints,
            longitudinalArmFeet);
        var estimatedContactTime = EstimateContactTime(
            samples,
            contactIndex,
            previousAirborneIndex,
            out var contactTimeEstimatedFromCompression,
            out var contactTimeEstimatePointCount,
            out var contactTimeEstimateSpreadSeconds);
        var inertialVerticalFpm = EstimateInertialVerticalFpm(
            samples,
            previousAirborneIndex,
            estimatedContactTime,
            contactTimeEstimatedFromCompression,
            out var inertialVerticalExtrapolated,
            out var inertialFitDurationSeconds,
            out var inertialDecision);
        var latchedVelocity = FindUpdatedLatch(
            samples,
            contactIndex,
            lastAirborne.TouchdownNormalVelocityFps,
            exclusiveEndTime,
            out var latchTime,
            out var latchUpdateDetected);
        var latchedNormalFpm = latchUpdateDetected ? ToNormalDescentFpm(latchedVelocity) : double.NaN;
        var surfaceRelativeDeltaFpm = latchUpdateDetected
            ? latchedNormalFpm - inertialVerticalFpm
            : double.NaN;
        var terrainContributionFpm = EstimateTerrainContributionFpm(samples, previousAirborneIndex);
        var peakG = FindPeakG(samples, contactIndex, GWindowAfterSeconds, exclusiveEndTime, out var peakGTime);
        var reconstruction = new TouchdownClosureEstimate();
        var reconstructed = false;
        string reconstructionDecision;
        if (gearTopology == null)
            reconstructionDecision = "unsupported_gear_topology";
        else if (gearTopology.NoseContactPointIndices.Any(point => TelemetryGeometryCalibration.HasContactEvidence(samples[contactIndex], point)))
            reconstructionDecision = "nose_contact_present";
        else if (!TelemetryGeometryCalibration.IsSustainedMainContact(samples, contactIndex, previousAirborneIndex, gearTopology))
            reconstructionDecision = "main_contact_not_sustained";
        else if (!IsFinite(contactArmFeet))
            reconstructionDecision = "longitudinal_arm_unavailable";
        else
            reconstructed = TouchdownClosureReconstruction.TryEstimate(
                samples, previousAirborneIndex, estimatedContactTime, contactArmFeet,
                out reconstruction, out reconstructionDecision);

        var impact = TouchdownImpactAnalysis.Analyze(samples, contactIndex);
        if (journal != null)
        {
            Record("contact_time", contactTimeEstimatedFromCompression ? "compression_interpolation" : "last_airborne_sample",
                contactTimeEstimatedFromCompression
                    ? "Estimated contact time from the median of bounded compression-onset estimates."
                    : "No usable compression-onset estimate; using the last airborne sample time.",
                new Dictionary<string, double>
                {
                    ["estimated_time_seconds"] = estimatedContactTime,
                    ["offset_seconds"] = estimatedContactTime - contactTime,
                    ["point_count"] = contactTimeEstimatePointCount,
                    ["spread_seconds"] = contactTimeEstimateSpreadSeconds
                });
            Record("inertial_velocity", inertialDecision,
                inertialVerticalExtrapolated ? "Extrapolated the pre-contact velocity fit to the estimated contact time."
                    : "Using the last airborne world-vertical velocity; contact detection itself is unchanged.",
                new Dictionary<string, double> { ["vertical_fpm"] = inertialVerticalFpm, ["fit_duration_seconds"] = inertialFitDurationSeconds });
            Record("touchdown_latch", latchUpdateDetected ? "fresh_update" : "no_fresh_update",
                latchUpdateDetected ? "Found a changed simulator touchdown latch before the next contact boundary."
                    : "No fresh touchdown latch in this contact's ownership window; the earlier value is not reused.",
                new Dictionary<string, double> { ["normal_fpm"] = latchedNormalFpm, ["update_offset_seconds"] = latchTime - contactTime });
            Record("reconstruction", reconstructionDecision,
                reconstructed ? "Accepted the pre-contact quadratic closure reconstruction."
                    : "Closure reconstruction is unavailable for the stated reason; no reconstructed value is substituted.",
                new Dictionary<string, double>
                {
                    ["longitudinal_arm_feet"] = contactArmFeet,
                    ["fit_point_count"] = reconstructed ? reconstruction.FitPointCount : 0,
                    ["closure_fpm"] = reconstructed ? reconstruction.ClosureFpm : double.NaN
                });
            Record("impact_window", impact.Status.ToString(),
                "Adaptive initial-contact response: " + impact.Status + "; classification: " + impact.Category + ".",
                new Dictionary<string, double>
                {
                    ["peak_g"] = impact.PeakG, ["peak_offset_seconds"] = impact.PeakOffsetSeconds,
                    ["end_offset_seconds"] = impact.EndOffsetSeconds, ["complete"] = impact.IsComplete ? 1 : 0
                });
        }

        return new TouchdownResult
        {
            Impact = impact,
            ClosureReconstructionModel = TouchdownClosureReconstruction.ModelName,
            EpisodeNumber = episodeNumber,
            ContactNumber = contactNumber,
            Sequence = contact.Sequence,
            SimulationTimeSeconds = contact.SimulationTimeSeconds,
            EstimatedContactTimeSeconds = estimatedContactTime,
            EstimatedContactOffsetSeconds = estimatedContactTime - contactTime,
            ContactTimeEstimatedFromCompression = contactTimeEstimatedFromCompression,
            ContactTimeEstimatePointCount = contactTimeEstimatePointCount,
            ContactTimeEstimateSpreadSeconds = contactTimeEstimateSpreadSeconds,
            InertialVerticalFpm = inertialVerticalFpm,
            InertialVerticalExtrapolated = inertialVerticalExtrapolated,
            InertialFitDurationSeconds = inertialFitDurationSeconds,
            LatchUpdateDetected = latchUpdateDetected,
            LatchedNormalFpm = latchedNormalFpm,
            LatchedUpdateOffsetSeconds = latchUpdateDetected ? latchTime - contactTime : double.NaN,
            SurfaceRelativeDeltaFpm = surfaceRelativeDeltaFpm,
            TerrainContributionFpm = terrainContributionFpm,
            UnresolvedSurfaceDeltaFpm = double.IsNaN(terrainContributionFpm)
                ? double.NaN
                : surfaceRelativeDeltaFpm - terrainContributionFpm,
            ClosureReconstructionAvailable = reconstructed,
            ReconstructedClosureFpm = reconstructed ? reconstruction.ClosureFpm : double.NaN,
            ReconstructedInertialFpm = reconstructed ? reconstruction.InertialFpm : double.NaN,
            ReconstructedTerrainFpm = reconstructed ? reconstruction.TerrainFpm : double.NaN,
            ReconstructedPitchFpm = reconstructed ? reconstruction.PitchFpm : double.NaN,
            ClosureReconstructionResidualFpm = reconstructed && latchUpdateDetected
                ? latchedNormalFpm - reconstruction.ClosureFpm
                : double.NaN,
            ClosureReconstructionUncertaintyFpm = reconstructed
                ? spatialArmRecovered
                    ? TouchdownClosureReconstruction.FallbackUncertaintyFpm
                    : gearTopology!.UsesClusteredWheelInference || gearTopology.MainContactPointCount > 4
                    ? geometrySource == TouchdownGeometrySource.Telemetry
                        ? TouchdownClosureReconstruction.MultiBogieTelemetryUncertaintyFpm
                        : TouchdownClosureReconstruction.FallbackUncertaintyFpm
                    : contactTimeEstimatedFromCompression &&
                      contactNumber == 1 &&
                      double.IsPositiveInfinity(exclusiveEndTime) &&
                      gearTopology.MainContactPointCount == 2
                        ? TouchdownClosureReconstruction.PrimaryUncertaintyFpm
                        : TouchdownClosureReconstruction.FallbackUncertaintyFpm
                : double.NaN,
            ClosureReconstructionFitPointCount = reconstructed ? reconstruction.FitPointCount : 0,
            ClosureReconstructionLongitudinalArmFeet = reconstructed ? contactArmFeet : double.NaN,
            ClosureReconstructionGeometryQuality = reconstructed ? geometryQuality : double.NaN,
            ClosureReconstructionArmRecoveredFromTelemetry = reconstructed && armRecoveredFromTelemetry,
            ClosureReconstructionGeometrySource = reconstructed
                ? geometrySource
                : TouchdownGeometrySource.Unavailable,
            LastAirToFirstGroundSeconds = contactTime - TimeOf(lastAirborne),
            FirstGroundToNextFrameSeconds = FindNextFrameDuration(samples, contactIndex),
            LastAirborneIndicatedFpm = ToDescentFpm(lastAirborne.VerticalSpeedFps),
            FirstGroundIndicatedFpm = ToDescentFpm(contact.VerticalSpeedFps),
            LastAirborneWorldFpm = ToDescentFpm(lastAirborne.VelocityWorldYFps),
            FirstGroundWorldFpm = ToDescentFpm(contact.VelocityWorldYFps),
            AglAverage100MsFpm = CalculateAglRate(samples, previousAirborneIndex, contactTime, 0.1),
            AglAverage150MsFpm = CalculateAglRate(samples, previousAirborneIndex, contactTime, 0.15),
            GAtFirstGroundSample = contact.GForce,
            PeakG = peakG,
            PeakGOffsetSeconds = peakGTime - contactTime,
            PeakG50Milliseconds = FindPeakG(samples, contactIndex, 0.05, exclusiveEndTime, out _),
            PeakG100Milliseconds = FindPeakG(samples, contactIndex, 0.1, exclusiveEndTime, out _),
            PeakG150Milliseconds = FindPeakG(samples, contactIndex, 0.15, exclusiveEndTime, out _),
            PeakG250Milliseconds = FindPeakG(samples, contactIndex, 0.25, exclusiveEndTime, out _),
            PeakG500Milliseconds = FindPeakG(samples, contactIndex, 0.5, exclusiveEndTime, out _),
            PitchDegrees = contact.PitchDegrees,
            BankDegrees = contact.BankDegrees,
            IndicatedAirspeedKnots = contact.IndicatedAirspeedKnots,
            GroundSpeedKnots = contact.GroundSpeedKnots,
        };

        void Record(string component, string code, string reason, Dictionary<string, double> details)
        {
            if (journal == null) return;
            details["episode_number"] = episodeNumber;
            details["contact_number"] = contactNumber;
            journal.Record(contactTime, component, code, reason, details);
        }
    }

    private static double EstimateContactTime(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        int previousAirborneIndex,
        out bool estimatedFromCompression,
        out int estimatePointCount,
        out double estimateSpreadSeconds)
    {
        var firstGround = samples[contactIndex];
        var firstGroundTime = TimeOf(firstGround);
        var lastAirborneTime = TimeOf(samples[previousAirborneIndex]);
        var estimates = new List<double>();

        for (var point = 0; point < TelemetrySample.CapturedContactPointCount; point++)
        {
            if (!firstGround.ContactPointOnGround[point] || firstGround.ContactPointCompression[point] <= 0)
            {
                continue;
            }

            for (var index = contactIndex + 1; index < samples.Count; index++)
            {
                var next = samples[index];
                if (!next.MotionSimulation) break;
                var nextTime = TimeOf(next);
                var duration = nextTime - firstGroundTime;
                if (duration <= 0)
                {
                    continue;
                }

                if (duration > 0.1 || !next.ContactPointOnGround[point])
                {
                    break;
                }

                var compressionRate =
                    (next.ContactPointCompression[point] - firstGround.ContactPointCompression[point]) / duration;
                if (compressionRate > 0.000001)
                {
                    var estimate = firstGroundTime - firstGround.ContactPointCompression[point] / compressionRate;
                    if (estimate >= lastAirborneTime && estimate <= firstGroundTime)
                    {
                        estimates.Add(estimate);
                    }
                }

                break;
            }
        }

        if (estimates.Count == 0)
        {
            estimatedFromCompression = false;
            estimatePointCount = 0;
            estimateSpreadSeconds = double.NaN;
            return lastAirborneTime;
        }

        estimates.Sort();
        estimatedFromCompression = true;
        estimatePointCount = estimates.Count;
        estimateSpreadSeconds = estimates[estimates.Count - 1] - estimates[0];
        var middle = estimates.Count / 2;
        return estimates.Count % 2 == 0
            ? (estimates[middle - 1] + estimates[middle]) / 2.0
            : estimates[middle];
    }

    private static double EstimateInertialVerticalFpm(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex,
        double estimatedContactTime,
        bool extrapolateToContact,
        out bool extrapolated,
        out double fitDurationSeconds,
        out string decision)
    {
        var last = samples[lastAirborneIndex];
        extrapolated = false;
        fitDurationSeconds = 0;
        decision = "no_compression_time_estimate";
        if (!extrapolateToContact)
        {
            return ToDescentFpm(last.VelocityWorldYFps);
        }

        var continuousAirStartIndex = lastAirborneIndex;
        for (var index = lastAirborneIndex - 1; index >= 0; index--)
        {
            if (!samples[index].MotionSimulation || samples[index].OnGround)
            {
                break;
            }

            continuousAirStartIndex = index;
        }

        var continuousAirDuration = TimeOf(last) - TimeOf(samples[continuousAirStartIndex]);
        if (continuousAirDuration < MinimumAirborneFitSeconds)
        {
            decision = "insufficient_continuous_airborne_history";
            return ToDescentFpm(last.VelocityWorldYFps);
        }

        var fitStartTime = TimeOf(last) - InertialFitWindowSeconds;
        var fitStartIndex = lastAirborneIndex;
        for (var index = lastAirborneIndex - 1; index >= continuousAirStartIndex; index--)
        {
            if (TimeOf(samples[index]) < fitStartTime)
            {
                break;
            }

            fitStartIndex = index;
        }

        if (!TryLinearFit(
                samples,
                fitStartIndex,
                lastAirborneIndex,
                sample => sample.VelocityWorldYFps,
                out var interceptAtLast,
                out var slope,
                out fitDurationSeconds))
        {
            decision = "precontact_linear_fit_unavailable";
            return ToDescentFpm(last.VelocityWorldYFps);
        }

        var estimatedVelocity = interceptAtLast + slope * (estimatedContactTime - TimeOf(last));
        extrapolated = true;
        decision = "precontact_linear_fit_extrapolated";
        return ToDescentFpm(estimatedVelocity);
    }

    private static double EstimateTerrainContributionFpm(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex)
    {
        var last = samples[lastAirborneIndex];
        if (Math.Abs(last.PlaneAltitudeFeet) < 0.000001 && Math.Abs(last.GroundAltitudeFeet) < 0.000001)
        {
            return double.NaN;
        }

        var fitStartTime = TimeOf(last) - GroundFitWindowSeconds;
        var fitStartIndex = lastAirborneIndex;
        for (var index = lastAirborneIndex - 1; index >= 0; index--)
        {
            if (!samples[index].MotionSimulation || samples[index].OnGround || TimeOf(samples[index]) < fitStartTime)
            {
                break;
            }

            fitStartIndex = index;
        }

        if (!TryLinearFit(
                samples,
                fitStartIndex,
                lastAirborneIndex,
                sample => FilteredGroundAltitude(samples, sample),
                out _,
                out var slope,
                out _))
        {
            return double.NaN;
        }

        return slope * 60.0;
    }

    private static double FilteredGroundAltitude(
        IReadOnlyList<TelemetrySample> samples,
        TelemetrySample target)
    {
        var targetIndex = -1;
        for (var index = 0; index < samples.Count; index++)
        {
            if (ReferenceEquals(samples[index], target))
            {
                targetIndex = index;
                break;
            }
        }

        if (targetIndex < 0)
        {
            return target.GroundAltitudeFeet;
        }

        var values = new List<double>(5);
        var first = targetIndex;
        var last = targetIndex;
        while (first > Math.Max(0, targetIndex - 2) && samples[first - 1].MotionSimulation) first--;
        while (last < Math.Min(samples.Count - 1, targetIndex + 2) && samples[last + 1].MotionSimulation) last++;
        for (var index = first; index <= last; index++)
        {
            values.Add(samples[index].GroundAltitudeFeet);
        }

        values.Sort();
        var median = values[values.Count / 2];
        if (Math.Abs(target.GroundAltitudeFeet - median) <= GroundOutlierThresholdFeet ||
            targetIndex == first || targetIndex == last)
        {
            return target.GroundAltitudeFeet;
        }

        var previous = samples[targetIndex - 1];
        var next = samples[targetIndex + 1];
        var duration = TimeOf(next) - TimeOf(previous);
        if (duration <= 0.000001)
        {
            return median;
        }

        var fraction = (TimeOf(target) - TimeOf(previous)) / duration;
        return previous.GroundAltitudeFeet +
               (next.GroundAltitudeFeet - previous.GroundAltitudeFeet) * fraction;
    }

    private static bool TryLinearFit(
        IReadOnlyList<TelemetrySample> samples,
        int startIndex,
        int endIndex,
        Func<TelemetrySample, double> selectValue,
        out double interceptAtEnd,
        out double slope,
        out double durationSeconds)
    {
        var endTime = TimeOf(samples[endIndex]);
        var count = 0;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXX = 0.0;
        var sumXY = 0.0;
        var earliestTime = endTime;

        for (var index = startIndex; index <= endIndex; index++)
        {
            var x = TimeOf(samples[index]) - endTime;
            var y = selectValue(samples[index]);
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
            earliestTime = Math.Min(earliestTime, TimeOf(samples[index]));
            count++;
        }

        durationSeconds = endTime - earliestTime;
        var denominator = count * sumXX - sumX * sumX;
        if (count < 3 || Math.Abs(denominator) <= 0.000000000001)
        {
            interceptAtEnd = double.NaN;
            slope = double.NaN;
            return false;
        }

        slope = (count * sumXY - sumX * sumY) / denominator;
        interceptAtEnd = (sumY - slope * sumX) / count;
        return true;
    }

    private static double FindNextFrameDuration(IReadOnlyList<TelemetrySample> samples, int contactIndex)
    {
        var contactTime = TimeOf(samples[contactIndex]);
        for (var index = contactIndex + 1; index < samples.Count; index++)
        {
            if (!samples[index].MotionSimulation) break;
            var duration = TimeOf(samples[index]) - contactTime;
            if (duration > 0.000001)
            {
                return duration;
            }
        }

        return double.NaN;
    }

    private static double FindUpdatedLatch(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        double previousValue,
        double exclusiveEndTime,
        out double latchTime,
        out bool updateDetected)
    {
        var contactTime = TimeOf(samples[contactIndex]);
        updateDetected = false;

        for (var index = contactIndex; index < samples.Count; index++)
        {
            var sample = samples[index];
            var sampleTime = TimeOf(sample);
            if (!sample.MotionSimulation || sampleTime >= exclusiveEndTime || sampleTime - contactTime > LatchUpdateWindowSeconds)
            {
                break;
            }

            if (Math.Abs(sample.TouchdownNormalVelocityFps - previousValue) > 0.000001)
            {
                latchTime = sampleTime;
                updateDetected = true;
                return sample.TouchdownNormalVelocityFps;
            }
        }

        latchTime = double.NaN;
        return double.NaN;
    }

    private static double FindPeakG(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        double windowAfterSeconds,
        double exclusiveEndTime,
        out double peakGTime)
    {
        var contactTime = TimeOf(samples[contactIndex]);
        var peakG = double.NegativeInfinity;
        peakGTime = contactTime;

        for (var index = contactIndex; index < samples.Count; index++)
        {
            var sample = samples[index];
            var sampleTime = TimeOf(sample);
            if (!sample.MotionSimulation || sampleTime >= exclusiveEndTime || sampleTime - contactTime > windowAfterSeconds)
            {
                break;
            }

            UpdatePeak(sample, ref peakG, ref peakGTime);
        }

        return double.IsNegativeInfinity(peakG) ? samples[contactIndex].GForce : peakG;
    }

    private static double CalculateAglRate(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex,
        double contactTime,
        double windowSeconds)
    {
        var targetTime = contactTime - windowSeconds;
        var startIndex = lastAirborneIndex;
        var bestDistance = Math.Abs(TimeOf(samples[startIndex]) - targetTime);

        for (var index = lastAirborneIndex - 1; index >= 0; index--)
        {
            var sample = samples[index];
            if (!sample.MotionSimulation || sample.OnGround || contactTime - TimeOf(sample) > 0.25)
            {
                break;
            }

            var distance = Math.Abs(TimeOf(sample) - targetTime);
            if (distance < bestDistance)
            {
                startIndex = index;
                bestDistance = distance;
            }
        }

        var start = samples[startIndex];
        var end = samples[lastAirborneIndex];
        var duration = TimeOf(end) - TimeOf(start);
        if (duration <= 0.000001)
        {
            return double.NaN;
        }

        return (start.AboveGroundLevelFeet - end.AboveGroundLevelFeet) / duration * 60.0;
    }

    private static void UpdatePeak(TelemetrySample sample, ref double peakG, ref double peakGTime)
    {
        if (sample.GForce > peakG)
        {
            peakG = sample.GForce;
            peakGTime = TimeOf(sample);
        }
    }

    private static double ToDescentFpm(double velocityFeetPerSecond)
    {
        return -60.0 * velocityFeetPerSecond;
    }

    private static double ToNormalDescentFpm(double velocityFeetPerSecond)
    {
        return Math.Abs(60.0 * velocityFeetPerSecond);
    }

    internal static double TimeOf(TelemetrySample sample)
    {
        return TelemetryAnalysisFrames.TimeOf(sample);
    }

    private static double SelectContactArm(
        TelemetrySample previous,
        TelemetrySample contact,
        IReadOnlyList<TouchdownMainGearContactPoint>? configuredMainPoints,
        double fallbackArmFeet)
    {
        if (configuredMainPoints is null || configuredMainPoints.Count == 0)
        {
            return fallbackArmFeet;
        }

        var arms = new List<double>(configuredMainPoints.Count);
        foreach (var point in configuredMainPoints)
        {
            if (point != null &&
                IsFinite(point.LongitudinalArmFeet) &&
                TelemetryGeometryCalibration.HasContactTransition(
                    previous,
                    contact,
                    point.ContactPointIndex))
            {
                arms.Add(point.LongitudinalArmFeet);
            }
        }

        if (arms.Count == 0)
        {
            foreach (var point in configuredMainPoints)
            {
                if (point != null &&
                    IsFinite(point.LongitudinalArmFeet) &&
                    TelemetryGeometryCalibration.HasContactEvidence(contact, point.ContactPointIndex))
                {
                    arms.Add(point.LongitudinalArmFeet);
                }
            }
        }

        if (arms.Count == 0)
        {
            return fallbackArmFeet;
        }

        arms.Sort();
        return arms.Count % 2 == 1
            ? arms[arms.Count / 2]
            : 0.5 * (arms[arms.Count / 2 - 1] + arms[arms.Count / 2]);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
