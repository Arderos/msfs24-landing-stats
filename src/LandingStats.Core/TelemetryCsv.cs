using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LandingStats.Core;

public static class TelemetryCsv
{
    public const int SchemaVersion = 3;

    public const string LegacyHeader =
        "sequence,host_elapsed_s,simulation_time_s,simulation_delta_s,on_ground,motion_simulation," +
        "touchdown_normal_velocity_fps,vertical_speed_fps,velocity_world_y_fps,velocity_body_y_fps," +
        "g_force,max_g_force,semibody_loadfactor_y,acceleration_body_y_fps2,agl_ft,pitch_deg,bank_deg," +
        "latitude_deg,longitude_deg,airspeed_indicated_kt,ground_speed_kt,simulation_rate";

    public static readonly string DiagnosticWithoutPositionHeader = BuildHeader(includeContactPosition: false, includeBlackBoxChannels: false);

    public static readonly string PreviousHeader = BuildHeader(includeContactPosition: true, includeBlackBoxChannels: false);

    public static readonly string Header = BuildHeader(includeContactPosition: true, includeBlackBoxChannels: true);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Format(TelemetrySample sample)
    {
        var values = new List<string>
        {
            sample.Sequence.ToString(Invariant),
            Number(sample.HostElapsedSeconds),
            Number(sample.SimulationTimeSeconds),
            Number(sample.SimulationDeltaSeconds),
            sample.OnGround ? "1" : "0",
            sample.MotionSimulation ? "1" : "0",
            Number(sample.TouchdownNormalVelocityFps),
            Number(sample.VerticalSpeedFps),
            Number(sample.VelocityWorldYFps),
            Number(sample.VelocityBodyYFps),
            Number(sample.GForce),
            Number(sample.MaxGForce),
            Number(sample.SemibodyLoadFactorY),
            Number(sample.AccelerationBodyYFps2),
            Number(sample.AboveGroundLevelFeet),
            Number(sample.PitchDegrees),
            Number(sample.BankDegrees),
            Number(sample.LatitudeDegrees),
            Number(sample.LongitudeDegrees),
            Number(sample.IndicatedAirspeedKnots),
            Number(sample.GroundSpeedKnots),
            Number(sample.SimulationRate),
            Number(sample.PlaneAltitudeFeet),
            Number(sample.GroundAltitudeFeet),
            Number(sample.AboveGroundMinusCgFeet),
            Number(sample.AccelerationWorldYFps2),
            Number(sample.RotationVelocityBodyXRadiansPerSecond),
            Number(sample.RotationVelocityBodyYRadiansPerSecond),
            Number(sample.RotationVelocityBodyZRadiansPerSecond),
            Number(sample.TouchdownPitchDegrees),
            Number(sample.TouchdownBankDegrees),
            Number(sample.VelocityWorldXFps),
            Number(sample.VelocityWorldZFps),
            Number(sample.VelocityBodyXFps),
            Number(sample.VelocityBodyZFps),
            Number(sample.AccelerationWorldXFps2),
            Number(sample.AccelerationWorldZFps2),
            Number(sample.AccelerationBodyXFps2),
            Number(sample.AccelerationBodyZFps2),
            Number(sample.RotationAccelerationBodyXRadiansPerSecond2),
            Number(sample.RotationAccelerationBodyYRadiansPerSecond2),
            Number(sample.RotationAccelerationBodyZRadiansPerSecond2),
            Number(sample.SemibodyLoadFactorX),
            Number(sample.SemibodyLoadFactorZ),
            Number(sample.SemibodyLoadFactorYDot),
            Number(sample.HeadingTrueDegrees),
            Number(sample.TrueAirspeedKnots),
            Number(sample.Mach),
            Number(sample.AngleOfAttackDegrees),
            Number(sample.SideslipDegrees),
            Number(sample.AmbientWindVelocityKnots),
            Number(sample.AmbientWindDirectionDegrees),
            Number(sample.ElevatorPosition),
            Number(sample.ElevatorTrimRadians),
            Number(sample.AileronPosition),
            Number(sample.RudderPosition),
            Number(sample.SpoilersLeftPosition),
            Number(sample.SpoilersRightPosition),
            Number(sample.FlapsHandlePercent),
            Number(sample.FlapsLeftPercent),
            Number(sample.FlapsRightPercent),
            Number(sample.BrakeLeftPosition),
            Number(sample.BrakeRightPosition),
            Number(sample.GearHandlePosition),
            Number(sample.GearTotalPercentExtended),
            Number(sample.GearCenterPosition),
            Number(sample.GearLeftPosition),
            Number(sample.GearRightPosition),
            Number(sample.TotalWeightPounds),
            Number(sample.CgPercent),
            sample.OnAnyRunway ? "1" : "0",
            sample.SurfaceType.ToString(Invariant),
            sample.SurfaceCondition.ToString(Invariant),
            sample.SpoilersArmed ? "1" : "0",
            sample.NumberOfEngines.ToString(Invariant),
            Number(sample.PilotRollInputPercent),
            Number(sample.PilotPitchInputPercent),
            Number(sample.RudderPedalInputPercent),
        };

        for (var index = 0; index < TelemetrySample.CapturedEngineCount; index++)
        {
            values.Add(Number(sample.EngineThrottlePercent[index]));
        }

        for (var index = 0; index < TelemetrySample.CapturedEngineCount; index++)
        {
            values.Add(Number(sample.EngineN1Percent[index]));
        }

        for (var index = 0; index < TelemetrySample.CapturedEngineCount; index++)
        {
            values.Add(Number(sample.EngineRpm[index]));
        }

        for (var index = 0; index < TelemetrySample.CapturedEngineCount; index++)
        {
            values.Add(Number(sample.EngineReversePercent[index]));
        }

        for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
        {
            values.Add(Number(sample.ContactPointCompression[index]));
        }

        for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
        {
            values.Add(Number(sample.ContactPointPosition[index]));
        }

        for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
        {
            values.Add(sample.ContactPointOnGround[index] ? "1" : "0");
        }

        return string.Join(",", values);
    }

    public static IReadOnlyList<TelemetrySample> ReadFile(string path)
    {
        var samples = new List<TelemetrySample>();
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (!string.Equals(header, Header, StringComparison.Ordinal) &&
            !string.Equals(header, PreviousHeader, StringComparison.Ordinal) &&
            !string.Equals(header, DiagnosticWithoutPositionHeader, StringComparison.Ordinal) &&
            !string.Equals(header, LegacyHeader, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The telemetry CSV header is missing or belongs to an unsupported version.");
        }

        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParse(line, out var sample))
            {
                throw new InvalidDataException($"Invalid telemetry data at line {lineNumber}.");
            }

            samples.Add(sample);
        }

        return samples;
    }

    public static bool TryParse(string line, out TelemetrySample sample)
    {
        sample = new TelemetrySample();
        var values = line.Split(',');
        var isLegacy = values.Length == 22;
        var isDiagnosticWithoutPosition = values.Length == DiagnosticWithoutPositionHeader.Split(',').Length;
        var isPrevious = values.Length == PreviousHeader.Split(',').Length;
        var expectedCurrentColumns = Header.Split(',').Length;
        var isCurrent = values.Length == expectedCurrentColumns;
        if ((!isLegacy && !isDiagnosticWithoutPosition && !isPrevious && !isCurrent) ||
            !long.TryParse(values[0], NumberStyles.Integer, Invariant, out var sequence) ||
            !TryNumber(values[1], out var hostElapsed) ||
            !TryNumber(values[2], out var simulationTime) ||
            !TryNumber(values[3], out var simulationDelta) ||
            !TryBoolean(values[4], out var onGround) ||
            !TryBoolean(values[5], out var motionSimulation) ||
            !TryNumber(values[6], out var touchdownVelocity) ||
            !TryNumber(values[7], out var verticalSpeed) ||
            !TryNumber(values[8], out var worldY) ||
            !TryNumber(values[9], out var bodyY) ||
            !TryNumber(values[10], out var gForce) ||
            !TryNumber(values[11], out var maxGForce) ||
            !TryNumber(values[12], out var loadFactor) ||
            !TryNumber(values[13], out var accelerationBodyY) ||
            !TryNumber(values[14], out var agl) ||
            !TryNumber(values[15], out var pitch) ||
            !TryNumber(values[16], out var bank) ||
            !TryNumber(values[17], out var latitude) ||
            !TryNumber(values[18], out var longitude) ||
            !TryNumber(values[19], out var indicatedAirspeed) ||
            !TryNumber(values[20], out var groundSpeed) ||
            !TryNumber(values[21], out var simulationRate))
        {
            return false;
        }

        sample.Sequence = sequence;
        sample.HostElapsedSeconds = hostElapsed;
        sample.SimulationTimeSeconds = simulationTime;
        sample.SimulationDeltaSeconds = simulationDelta;
        sample.OnGround = onGround;
        sample.MotionSimulation = motionSimulation;
        sample.TouchdownNormalVelocityFps = touchdownVelocity;
        sample.VerticalSpeedFps = verticalSpeed;
        sample.VelocityWorldYFps = worldY;
        sample.VelocityBodyYFps = bodyY;
        sample.GForce = gForce;
        sample.MaxGForce = maxGForce;
        sample.SemibodyLoadFactorY = loadFactor;
        sample.AccelerationBodyYFps2 = accelerationBodyY;
        sample.AboveGroundLevelFeet = agl;
        sample.PitchDegrees = pitch;
        sample.BankDegrees = bank;
        sample.LatitudeDegrees = latitude;
        sample.LongitudeDegrees = longitude;
        sample.IndicatedAirspeedKnots = indicatedAirspeed;
        sample.GroundSpeedKnots = groundSpeed;
        sample.SimulationRate = simulationRate;

        if (!isLegacy)
        {
            var index = 22;
            if (!TryNumber(values[index++], out var planeAltitude) ||
                !TryNumber(values[index++], out var groundAltitude) ||
                !TryNumber(values[index++], out var aglMinusCg) ||
                !TryNumber(values[index++], out var accelerationWorldY) ||
                !TryNumber(values[index++], out var rotationX) ||
                !TryNumber(values[index++], out var rotationY) ||
                !TryNumber(values[index++], out var rotationZ) ||
                !TryNumber(values[index++], out var touchdownPitch) ||
                !TryNumber(values[index++], out var touchdownBank) ||
                !TryNumber(values[index++], out var velocityWorldX) ||
                !TryNumber(values[index++], out var velocityWorldZ) ||
                !TryNumber(values[index++], out var velocityBodyX) ||
                !TryNumber(values[index++], out var velocityBodyZ) ||
                !TryNumber(values[index++], out var accelerationWorldX) ||
                !TryNumber(values[index++], out var accelerationWorldZ) ||
                !TryNumber(values[index++], out var accelerationBodyX) ||
                !TryNumber(values[index++], out var accelerationBodyZ) ||
                !TryNumber(values[index++], out var rotationAccelerationX) ||
                !TryNumber(values[index++], out var rotationAccelerationY) ||
                !TryNumber(values[index++], out var rotationAccelerationZ) ||
                !TryNumber(values[index++], out var loadFactorX) ||
                !TryNumber(values[index++], out var loadFactorZ) ||
                !TryNumber(values[index++], out var loadFactorYDot) ||
                !TryNumber(values[index++], out var headingTrue) ||
                !TryNumber(values[index++], out var trueAirspeed) ||
                !TryNumber(values[index++], out var mach) ||
                !TryNumber(values[index++], out var angleOfAttack) ||
                !TryNumber(values[index++], out var sideslip) ||
                !TryNumber(values[index++], out var windVelocity) ||
                !TryNumber(values[index++], out var windDirection) ||
                !TryNumber(values[index++], out var elevator) ||
                !TryNumber(values[index++], out var elevatorTrim) ||
                !TryNumber(values[index++], out var aileron) ||
                !TryNumber(values[index++], out var rudder) ||
                !TryNumber(values[index++], out var spoilersLeft) ||
                !TryNumber(values[index++], out var spoilersRight) ||
                !TryNumber(values[index++], out var flapsHandle) ||
                !TryNumber(values[index++], out var flapsLeft) ||
                !TryNumber(values[index++], out var flapsRight) ||
                !TryNumber(values[index++], out var brakeLeft) ||
                !TryNumber(values[index++], out var brakeRight) ||
                !TryNumber(values[index++], out var gearHandlePosition) ||
                !TryNumber(values[index++], out var gearTotal) ||
                !TryNumber(values[index++], out var gearCenter) ||
                !TryNumber(values[index++], out var gearLeft) ||
                !TryNumber(values[index++], out var gearRight) ||
                !TryNumber(values[index++], out var totalWeight) ||
                !TryNumber(values[index++], out var cgPercent) ||
                !TryBoolean(values[index++], out var onAnyRunway) ||
                !int.TryParse(values[index++], NumberStyles.Integer, Invariant, out var surfaceType) ||
                !int.TryParse(values[index++], NumberStyles.Integer, Invariant, out var surfaceCondition) ||
                !TryBoolean(values[index++], out var spoilersArmed))
            {
                return false;
            }

            sample.PlaneAltitudeFeet = planeAltitude;
            sample.GroundAltitudeFeet = groundAltitude;
            sample.AboveGroundMinusCgFeet = aglMinusCg;
            sample.AccelerationWorldYFps2 = accelerationWorldY;
            sample.RotationVelocityBodyXRadiansPerSecond = rotationX;
            sample.RotationVelocityBodyYRadiansPerSecond = rotationY;
            sample.RotationVelocityBodyZRadiansPerSecond = rotationZ;
            sample.TouchdownPitchDegrees = touchdownPitch;
            sample.TouchdownBankDegrees = touchdownBank;
            sample.VelocityWorldXFps = velocityWorldX;
            sample.VelocityWorldZFps = velocityWorldZ;
            sample.VelocityBodyXFps = velocityBodyX;
            sample.VelocityBodyZFps = velocityBodyZ;
            sample.AccelerationWorldXFps2 = accelerationWorldX;
            sample.AccelerationWorldZFps2 = accelerationWorldZ;
            sample.AccelerationBodyXFps2 = accelerationBodyX;
            sample.AccelerationBodyZFps2 = accelerationBodyZ;
            sample.RotationAccelerationBodyXRadiansPerSecond2 = rotationAccelerationX;
            sample.RotationAccelerationBodyYRadiansPerSecond2 = rotationAccelerationY;
            sample.RotationAccelerationBodyZRadiansPerSecond2 = rotationAccelerationZ;
            sample.SemibodyLoadFactorX = loadFactorX;
            sample.SemibodyLoadFactorZ = loadFactorZ;
            sample.SemibodyLoadFactorYDot = loadFactorYDot;
            sample.HeadingTrueDegrees = headingTrue;
            sample.TrueAirspeedKnots = trueAirspeed;
            sample.Mach = mach;
            sample.AngleOfAttackDegrees = angleOfAttack;
            sample.SideslipDegrees = sideslip;
            sample.AmbientWindVelocityKnots = windVelocity;
            sample.AmbientWindDirectionDegrees = windDirection;
            sample.ElevatorPosition = elevator;
            sample.ElevatorTrimRadians = elevatorTrim;
            sample.AileronPosition = aileron;
            sample.RudderPosition = rudder;
            sample.SpoilersLeftPosition = spoilersLeft;
            sample.SpoilersRightPosition = spoilersRight;
            sample.FlapsHandlePercent = flapsHandle;
            sample.FlapsLeftPercent = flapsLeft;
            sample.FlapsRightPercent = flapsRight;
            sample.BrakeLeftPosition = brakeLeft;
            sample.BrakeRightPosition = brakeRight;
            sample.GearHandlePosition = gearHandlePosition;
            sample.GearTotalPercentExtended = gearTotal;
            sample.GearCenterPosition = gearCenter;
            sample.GearLeftPosition = gearLeft;
            sample.GearRightPosition = gearRight;
            sample.TotalWeightPounds = totalWeight;
            sample.CgPercent = cgPercent;
            sample.OnAnyRunway = onAnyRunway;
            sample.SurfaceType = surfaceType;
            sample.SurfaceCondition = surfaceCondition;
            sample.SpoilersArmed = spoilersArmed;

            if (isCurrent)
            {
                if (!int.TryParse(values[index++], NumberStyles.Integer, Invariant, out var numberOfEngines) ||
                    !TryNumber(values[index++], out var pilotRoll) ||
                    !TryNumber(values[index++], out var pilotPitch) ||
                    !TryNumber(values[index++], out var rudderPedal))
                {
                    return false;
                }

                sample.NumberOfEngines = numberOfEngines;
                sample.PilotRollInputPercent = pilotRoll;
                sample.PilotPitchInputPercent = pilotPitch;
                sample.RudderPedalInputPercent = rudderPedal;

                for (var engineIndex = 0; engineIndex < TelemetrySample.CapturedEngineCount; engineIndex++)
                {
                    if (!TryNumber(values[index++], out var throttle))
                    {
                        return false;
                    }

                    sample.EngineThrottlePercent[engineIndex] = throttle;
                }

                for (var engineIndex = 0; engineIndex < TelemetrySample.CapturedEngineCount; engineIndex++)
                {
                    if (!TryNumber(values[index++], out var n1))
                    {
                        return false;
                    }

                    sample.EngineN1Percent[engineIndex] = n1;
                }

                for (var engineIndex = 0; engineIndex < TelemetrySample.CapturedEngineCount; engineIndex++)
                {
                    if (!TryNumber(values[index++], out var rpm))
                    {
                        return false;
                    }

                    sample.EngineRpm[engineIndex] = rpm;
                }

                for (var engineIndex = 0; engineIndex < TelemetrySample.CapturedEngineCount; engineIndex++)
                {
                    if (!TryNumber(values[index++], out var reverse))
                    {
                        return false;
                    }

                    sample.EngineReversePercent[engineIndex] = reverse;
                }
            }

            for (var contactIndex = 0; contactIndex < TelemetrySample.CapturedContactPointCount; contactIndex++)
            {
                if (!TryNumber(values[index++], out var compression))
                {
                    return false;
                }

                sample.ContactPointCompression[contactIndex] = compression;
            }

            if (!isDiagnosticWithoutPosition)
            {
                for (var contactIndex = 0; contactIndex < TelemetrySample.CapturedContactPointCount; contactIndex++)
                {
                    if (!TryNumber(values[index++], out var position))
                    {
                        return false;
                    }

                    sample.ContactPointPosition[contactIndex] = position;
                }
            }

            for (var contactIndex = 0; contactIndex < TelemetrySample.CapturedContactPointCount; contactIndex++)
            {
                if (!TryBoolean(values[index++], out var contactOnGround))
                {
                    return false;
                }

                sample.ContactPointOnGround[contactIndex] = contactOnGround;
            }
        }

        return true;
    }

    private static string BuildHeader(bool includeContactPosition, bool includeBlackBoxChannels)
    {
        var columns = new List<string>(LegacyHeader.Split(','))
        {
            "plane_altitude_ft",
            "ground_altitude_ft",
            "agl_minus_cg_ft",
            "acceleration_world_y_fps2",
            "rotation_velocity_body_x_rad_s",
            "rotation_velocity_body_y_rad_s",
            "rotation_velocity_body_z_rad_s",
            "touchdown_pitch_deg",
            "touchdown_bank_deg",
            "velocity_world_x_fps",
            "velocity_world_z_fps",
            "velocity_body_x_fps",
            "velocity_body_z_fps",
            "acceleration_world_x_fps2",
            "acceleration_world_z_fps2",
            "acceleration_body_x_fps2",
            "acceleration_body_z_fps2",
            "rotation_acceleration_body_x_rad_s2",
            "rotation_acceleration_body_y_rad_s2",
            "rotation_acceleration_body_z_rad_s2",
            "semibody_loadfactor_x",
            "semibody_loadfactor_z",
            "semibody_loadfactor_ydot",
            "heading_true_deg",
            "airspeed_true_kt",
            "mach",
            "angle_of_attack_deg",
            "sideslip_deg",
            "ambient_wind_velocity_kt",
            "ambient_wind_direction_deg",
            "elevator_position",
            "elevator_trim_rad",
            "aileron_position",
            "rudder_position",
            "spoilers_left_position",
            "spoilers_right_position",
            "flaps_handle_percent",
            "flaps_left_percent",
            "flaps_right_percent",
            "brake_left_position",
            "brake_right_position",
            "gear_handle_position",
            "gear_total_percent_extended",
            "gear_center_position",
            "gear_left_position",
            "gear_right_position",
            "total_weight_lb",
            "cg_percent",
            "on_any_runway",
            "surface_type",
            "surface_condition",
            "spoilers_armed",
        };

        if (includeBlackBoxChannels)
        {
            columns.Add("number_of_engines");
            columns.Add("pilot_roll_input_percent");
            columns.Add("pilot_pitch_input_percent");
            columns.Add("rudder_pedal_input_percent");

            for (var index = 1; index <= TelemetrySample.CapturedEngineCount; index++)
            {
                columns.Add($"engine_{index}_throttle_percent");
            }

            for (var index = 1; index <= TelemetrySample.CapturedEngineCount; index++)
            {
                columns.Add($"engine_{index}_n1_percent");
            }

            for (var index = 1; index <= TelemetrySample.CapturedEngineCount; index++)
            {
                columns.Add($"engine_{index}_rpm");
            }

            for (var index = 1; index <= TelemetrySample.CapturedEngineCount; index++)
            {
                columns.Add($"engine_{index}_reverse_percent");
            }
        }

        for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
        {
            columns.Add($"contact_{index}_compression");
        }

        if (includeContactPosition)
        {
            for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
            {
                columns.Add($"contact_{index}_position");
            }
        }

        for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
        {
            columns.Add($"contact_{index}_on_ground");
        }

        return string.Join(",", columns);
    }

    private static string Number(double value)
    {
        return value.ToString("R", Invariant);
    }

    private static bool TryNumber(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, Invariant, out result);
    }

    private static bool TryBoolean(string value, out bool result)
    {
        if (value == "0")
        {
            result = false;
            return true;
        }

        if (value == "1")
        {
            result = true;
            return true;
        }

        result = false;
        return false;
    }
}
