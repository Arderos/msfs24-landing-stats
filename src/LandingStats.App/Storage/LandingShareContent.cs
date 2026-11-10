using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Storage;

/// <summary>The portable core is frozen independently of the application's history model.
/// Feature payloads remain authoritative bytes, including those this reader understands.</summary>
internal static class LandingShareContent
{
    private const string Prefix = "org.landingstats.";
    private static readonly string[] KnownTypes = { "metrics", "series", "engines", "contact-points", "pose", "inputs", "controllers", "source-state" };

    internal static LandingShareData Create(LandingRecord normalizedSource)
    {
        if (normalizedSource == null) throw new ArgumentNullException(nameof(normalizedSource));
        var file = LandingRecordFile.FromRecord(normalizedSource);
        var core = CoreV1.From(normalizedSource);
        core.ApplyTo(new LandingRecord());
        var result = new LandingShareData { CorePayload = Encode(core), Blocks = new List<LandingShareBlock>() };
        file.Summary.ShareData = null;
        Add("metrics", file.Summary);
        Add("series", new SeriesV1 { Columns = file.Series, SourceIndices = file.Summary.RawControllerSourceIndices.ToArray() });
        if (file.Engines.Count != 0) Add("engines", new EnginesV1 { Columns = file.Engines.ToArray() });
        if (file.Contacts.Count != 0) Add("contact-points", new ContactsV1 { Columns = file.Contacts.ToArray() });
        var time = normalizedSource.Series.Select(point => point.TimeSeconds).ToArray();
        if (file.Pose != null) Add("pose", new PoseV1 { Time = time, Columns = file.Pose,
            Pitch = normalizedSource.Series.Select(point => Finite(point.PitchDegrees)).ToArray(),
            Bank = normalizedSource.Series.Select(point => Finite(point.BankDegrees)).ToArray(),
            OnGround = normalizedSource.Series.Select(point => point.OnGround).ToArray() });
        if (file.Inputs != null) Add("inputs", new InputsV1 { Time = time, Columns = file.Inputs,
            SourceIndices = normalizedSource.RawControllerAxesSourceIndices.ToArray(), AxisCount = normalizedSource.ControllerAxisCount });
        if (file.ControllerCatalogs != null) Add("controllers", new ControllersV1 { Time = time, Timeline = file.ControllerCatalogs }, file.ControllerCatalogs.Version);
        if (file.SourceState != null) Add("source-state", new SourceStateV1 { Time = time, Data = file.SourceState });
        return result;

        void Add<T>(string type, T value, int version = 1) => result.Blocks.Add(new LandingShareBlock
        { Type = Prefix + type, Version = version, Required = false, Payload = Encode(value) });
    }

    internal static LandingRecord Read(LandingShareData data, LandingShareReadBudget? budget = null)
    {
        budget ??= new LandingShareReadBudget();
        if (data == null || data.Blocks == null) throw new InvalidDataException("The landing share content is missing.");
        var core = Decode<CoreV1>(data.CorePayload, budget);
        var blocks = new Dictionary<string, LandingShareBlock>(StringComparer.Ordinal);
        foreach (var block in data.Blocks)
        {
            if (block == null || string.IsNullOrWhiteSpace(block.Type) || block.Version < 1 || block.Payload == null || blocks.ContainsKey(block.Type))
                throw new InvalidDataException("The landing share feature descriptor is invalid or duplicated.");
            blocks.Add(block.Type, block);
            if (block.Required && (block.Version != 1 || !KnownTypes.Any(type => Prefix + type == block.Type)))
                throw new InvalidDataException("A required landing share feature is unsupported.");
        }
        var metrics = Get<LandingRecord>("metrics");
        var record = metrics ?? CoreOnlyRecord();
        if (metrics != null)
        {
            if (record.FormatVersion < 1 || record.FormatVersion > LandingRecord.CurrentFormatVersion)
                throw new InvalidDataException("The landing metrics feature uses an unsupported measurement version.");
            LandingRecordFile.RestoreSummary(record);
        }
        core.ApplyTo(record);
        record.IsImported = false; record.ImportedSourceId = null; record.ImportedSourceEpisodeId = null; record.ImportedPackageHash = null;
        record.IsSummaryOnly = false; record.ShareData = null;
        record.Series = new List<LandingSeriesPoint>(); record.Engines = new List<LandingEngineSeries>(); record.ContactPoints = new List<LandingContactSeries>();
        record.RawControllerSourceIndices = new List<int>(); record.RawControllerAxesSourceIndices = new List<int>();
        record.ControllerTimeline = null; record.SourceEvidenceTimeline = null;
        double[]? timeline = null;
        var series = Get<SeriesV1>("series");
        if (series != null)
        {
            if (series.Columns == null) throw new InvalidDataException("The landing series feature is incomplete.");
            LandingPhysicalInputColumns.ValidateMapping(series.SourceIndices);
            series.Columns.Validate(series.SourceIndices.Length);
            ValidateTime(series.Columns.Time);
            budget.ReserveSeries(series.Columns.Time.Length, series.SourceIndices.Length);
            timeline = series.Columns.Time;
            record.RawControllerSourceIndices = series.SourceIndices.ToList();
            record.Series = series.Columns.ToPoints();
        }
        var engines = Get<EnginesV1>("engines");
        if (engines != null)
        {
            if (engines.Columns == null || engines.Columns.Any(value => value == null)) throw new InvalidDataException("The landing engine feature is incomplete.");
            foreach (var value in engines.Columns)
            { value.Validate(); ValidateTime(value.Time); budget.ReserveAuxiliaryPoints(value.Time.Length); record.Engines.Add(value.ToSeries()); }
        }
        var contacts = Get<ContactsV1>("contact-points");
        if (contacts != null)
        {
            if (contacts.Columns == null || contacts.Columns.Any(value => value == null)) throw new InvalidDataException("The landing contact feature is incomplete.");
            foreach (var value in contacts.Columns)
            { value.Validate(); ValidateTime(value.Time); budget.ReserveAuxiliaryPoints(value.Time.Length); record.ContactPoints.Add(value.ToSeries()); }
        }
        var pose = Get<PoseV1>("pose");
        if (pose != null)
        {
            EnsureTimeline(pose.Time);
            if (pose.Columns == null || pose.Pitch == null || pose.Bank == null || pose.OnGround == null ||
                pose.Pitch.Length != timeline!.Length || pose.Bank.Length != timeline.Length || pose.OnGround.Length != timeline.Length)
                throw new InvalidDataException("The landing pose feature is incomplete.");
            pose.Columns.ApplyTo(record.Series);
            for (var i = 0; i < record.Series.Count; i++)
            {
                var pitch = Metric(pose.Pitch[i]); var bank = Metric(pose.Bank[i]);
                if (series != null && (!record.Series[i].PitchDegrees.Equals(pitch) || !record.Series[i].BankDegrees.Equals(bank) || record.Series[i].OnGround != pose.OnGround[i]))
                    throw new InvalidDataException("The landing pose and series features disagree.");
                record.Series[i].PitchDegrees = pitch; record.Series[i].BankDegrees = bank; record.Series[i].OnGround = pose.OnGround[i];
            }
        }
        var inputs = Get<InputsV1>("inputs");
        if (inputs != null)
        {
            EnsureTimeline(inputs.Time);
            LandingPhysicalInputColumns.ValidateMapping(inputs.SourceIndices);
            if (inputs.Columns == null || inputs.AxisCount != 6 && inputs.AxisCount != 8)
                throw new InvalidDataException("The landing input feature is incomplete or unsupported.");
            inputs.Columns.Validate(record.Series.Count, inputs.SourceIndices.Length);
            budget.ReserveInputs(record.Series.Count, inputs.SourceIndices.Length);
            record.ControllerAxisCount = inputs.AxisCount; record.RawControllerAxesSourceIndices = inputs.SourceIndices.ToList();
            inputs.Columns.ApplyTo(record.Series);
        }
        var controllers = Get<ControllersV1>("controllers");
        if (controllers != null)
        {
            EnsureTimeline(controllers.Time);
            if (controllers.Timeline == null) throw new InvalidDataException("The controller catalog feature is incomplete.");
            controllers.Timeline.Validate(record.Series.Count); record.ControllerTimeline = controllers.Timeline;
        }
        var source = Get<SourceStateV1>("source-state");
        if (source != null)
        {
            EnsureTimeline(source.Time);
            budget.ReserveSourceObservations(record.Series.Count);
            ReplaySourceTimelineCodec.DecodeText(source.Data, record.Series.Count); record.SourceEvidenceTimeline = source.Data;
        }
        // Reuse the history model's geometry and cross-column invariants before publication.
        _ = LandingRecordFile.FromRecord(record);
        record.ShareData = data.Copy();
        return record;

        T? Get<T>(string type) where T : class => blocks.TryGetValue(Prefix + type, out var block) && block.Version == 1 ? Decode<T>(block.Payload, budget) : null;
        void EnsureTimeline(double[] time)
        {
            ValidateTime(time);
            if (timeline != null)
            {
                if (!timeline.SequenceEqual(time)) throw new InvalidDataException("Landing feature timelines disagree.");
                return;
            }
            timeline = time;
            budget.ReserveSeries(time.Length);
            record.Series = time.Select(UnknownPoint).ToList();
        }
    }

    private static LandingSharePayload Encode<T>(T value) => LandingSharePayload.FromBytes(LandingShareArchive.Serialize(value, LandingShareArchive.MaximumDecodedBytes));
    private static T Decode<T>(LandingSharePayload payload, LandingShareReadBudget budget)
    {
        if (payload == null || payload.ByteCount > LandingShareArchive.MaximumDecodedBytes)
            throw new InvalidDataException("The landing share feature exceeds its size limit.");
        using var input = payload.OpenRead();
        return LandingShareArchive.Deserialize<T>(input, budget);
    }
    private static double? Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) || value == LandingRecord.NonFiniteStorageSentinel ? null : value;
    private static double Metric(double? value)
    {
        if (value.HasValue && !Finite(value.Value).HasValue) throw new InvalidDataException("The landing core contains a non-finite metric.");
        return value ?? double.NaN;
    }
    private static void ValidateTime(double[] time)
    {
        if (time == null || time.Any(value => !Finite(value).HasValue)) throw new InvalidDataException("The landing feature timeline is invalid.");
    }
    private static LandingRecord CoreOnlyRecord() => new LandingRecord
    {
        // A core-only view makes no claim to newer analysis/quality semantics.
        FormatVersion = 1, WeightPounds = double.NaN, CgPercent = double.NaN, ApproachGateSeconds = double.NaN,
        InertialFitDurationSeconds = double.NaN, LatchUpdateOffsetSeconds = double.NaN, GroundSpeedKnots = double.NaN,
        AngleOfAttackDegrees = double.NaN, RawPitchInputCorrelation = double.NaN, RawPitchInputLagSeconds = double.NaN,
        WindSpeedKnotsAtContact = double.NaN, WindDirectionDegreesAtContact = double.NaN,
    };
    private static LandingSeriesPoint UnknownPoint(double time) => new LandingSeriesPoint
    {
        TimeSeconds = time, InertialFpm = double.NaN, IndicatedFpm = double.NaN, GForce = double.NaN, AglFeet = double.NaN,
        GroundSpeedKnots = double.NaN, PitchDegrees = double.NaN, BankDegrees = double.NaN, AngleOfAttackDegrees = double.NaN,
        SideslipDegrees = double.NaN, PilotRollPercent = double.NaN, PilotPitchPercent = double.NaN, PilotYawPercent = double.NaN,
        AileronPercent = double.NaN, ElevatorPercent = double.NaN, RudderPercent = double.NaN, SpoilersLeftPercent = double.NaN,
        SpoilersRightPercent = double.NaN, FlapsPercent = double.NaN, BrakeLeftPercent = double.NaN, BrakeRightPercent = double.NaN,
        LongitudinalLoadG = double.NaN, LateralLoadG = double.NaN, BodyRateXDegreesPerSecond = double.NaN,
        BodyRateYDegreesPerSecond = double.NaN, BodyRateZDegreesPerSecond = double.NaN, WindSpeedKnots = double.NaN,
        WindDirectionDegrees = double.NaN, LateralAccelerationFps2 = double.NaN, LongitudinalAccelerationFps2 = double.NaN,
        ElevatorDeflectionPercent = double.NaN, AileronLeftDeflectionPercent = double.NaN, AileronRightDeflectionPercent = double.NaN,
        RudderDeflectionPercent = double.NaN, AxisElevatorSetPercent = double.NaN, AxisElevatorSetAgeSeconds = double.NaN,
        RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>(),
    };

    // Never add members here for a new feature: use a separately versioned block.
    [DataContract]
    private sealed class CoreV1
    {
        [DataMember(Name = "version", Order = 1, IsRequired = true)] public int Version = 1;
        [DataMember(Name = "sourceId", Order = 2, IsRequired = true)] public string SourceId = "";
        [DataMember(Name = "episodeId", Order = 3, IsRequired = true)] public string EpisodeId = "";
        [DataMember(Name = "timestampUtc", Order = 4, IsRequired = true)] public string TimestampUtc = "";
        [DataMember(Name = "simulator", Order = 5, IsRequired = true)] public string Simulator = "";
        [DataMember(Name = "aircraftTitle", Order = 6, IsRequired = true)] public string AircraftTitle = "";
        [DataMember(Name = "aircraftType", Order = 7, IsRequired = true)] public string AircraftType = "";
        [DataMember(Name = "airport", Order = 8, IsRequired = true)] public string Airport = "";
        [DataMember(Name = "runway", Order = 9, IsRequired = true)] public string Runway = "";
        [DataMember(Name = "contactNumber", Order = 10, IsRequired = true)] public int ContactNumber;
        [DataMember(Name = "contactCount", Order = 11, IsRequired = true)] public int ContactCount;
        [DataMember(Name = "inertialFpm", Order = 12, EmitDefaultValue = false)] public double? InertialFpm;
        [DataMember(Name = "surfaceFpm", Order = 13, EmitDefaultValue = false)] public double? SurfaceFpm;
        [DataMember(Name = "surfaceDeltaFpm", Order = 14, EmitDefaultValue = false)] public double? SurfaceDeltaFpm;
        [DataMember(Name = "terrainFpm", Order = 15, EmitDefaultValue = false)] public double? TerrainFpm;
        [DataMember(Name = "unresolvedFpm", Order = 16, EmitDefaultValue = false)] public double? UnresolvedFpm;
        [DataMember(Name = "peakG150ms", Order = 17, EmitDefaultValue = false)] public double? PeakG150Milliseconds;
        [DataMember(Name = "peakG2s", Order = 18, EmitDefaultValue = false)] public double? PeakG2Seconds;
        [DataMember(Name = "pitchDeg", Order = 19, EmitDefaultValue = false)] public double? PitchDegrees;
        [DataMember(Name = "bankDeg", Order = 20, EmitDefaultValue = false)] public double? BankDegrees;
        [DataMember(Name = "airspeedKt", Order = 21, EmitDefaultValue = false)] public double? AirspeedKnots;

        internal static CoreV1 From(LandingRecord value) => new CoreV1
        {
            SourceId = value.Id, EpisodeId = value.RecordingEpisodeId!, TimestampUtc = value.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Simulator = value.Simulator, AircraftTitle = value.AircraftTitle, AircraftType = value.AircraftType, Airport = value.Airport, Runway = value.Runway,
            ContactNumber = value.ContactNumber, ContactCount = value.ContactCount, InertialFpm = Finite(value.InertialFpm), SurfaceFpm = Finite(value.SurfaceFpm),
            SurfaceDeltaFpm = Finite(value.SurfaceDeltaFpm), TerrainFpm = Finite(value.TerrainFpm), UnresolvedFpm = Finite(value.UnresolvedFpm),
            PeakG150Milliseconds = Finite(value.PeakG150Milliseconds), PeakG2Seconds = Finite(value.PeakG2Seconds),
            PitchDegrees = Finite(value.PitchDegrees), BankDegrees = Finite(value.BankDegrees), AirspeedKnots = Finite(value.AirspeedKnots),
        };
        internal void ApplyTo(LandingRecord record)
        {
            if (Version != 1 || !Identity(SourceId) || !Identity(EpisodeId) || ContactNumber < 1 || ContactCount < ContactNumber ||
                !DateTime.TryParseExact(TimestampUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
                timestamp == default || timestamp.Kind != DateTimeKind.Utc || Simulator == null || AircraftTitle == null || AircraftType == null || Airport == null || Runway == null)
                throw new InvalidDataException("The landing share core is invalid or unsupported.");
            record.Id = SourceId; record.RecordingEpisodeId = EpisodeId; record.TimestampUtc = timestamp;
            record.Simulator = Simulator; record.AircraftTitle = AircraftTitle; record.AircraftType = AircraftType; record.Airport = Airport; record.Runway = Runway;
            record.ContactNumber = ContactNumber; record.ContactCount = ContactCount; record.InertialFpm = Metric(InertialFpm); record.SurfaceFpm = Metric(SurfaceFpm);
            record.SurfaceDeltaFpm = Metric(SurfaceDeltaFpm); record.TerrainFpm = Metric(TerrainFpm); record.UnresolvedFpm = Metric(UnresolvedFpm);
            record.PeakG150Milliseconds = Metric(PeakG150Milliseconds); record.PeakG2Seconds = Metric(PeakG2Seconds);
            record.PitchDegrees = Metric(PitchDegrees); record.BankDegrees = Metric(BankDegrees); record.AirspeedKnots = Metric(AirspeedKnots);
        }
        private static bool Identity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
    }
    [DataContract] private sealed class SeriesV1
    {
        [DataMember(Name = "columns", Order = 1, IsRequired = true)] public LandingSeriesColumns Columns = null!;
        [DataMember(Name = "sourceIndices", Order = 2, IsRequired = true)] public int[] SourceIndices = Array.Empty<int>();
    }
    [DataContract] private sealed class EnginesV1
    { [DataMember(Name = "columns", Order = 1, IsRequired = true)] public LandingEngineColumns[] Columns = Array.Empty<LandingEngineColumns>(); }
    [DataContract] private sealed class ContactsV1
    { [DataMember(Name = "columns", Order = 1, IsRequired = true)] public LandingContactColumns[] Columns = Array.Empty<LandingContactColumns>(); }
    [DataContract] private sealed class PoseV1
    {
        [DataMember(Name = "time", Order = 1, IsRequired = true)] public double[] Time = Array.Empty<double>();
        [DataMember(Name = "columns", Order = 2, IsRequired = true)] public LandingPoseColumns Columns = null!;
        [DataMember(Name = "pitch", Order = 3, IsRequired = true)] public double?[] Pitch = Array.Empty<double?>();
        [DataMember(Name = "bank", Order = 4, IsRequired = true)] public double?[] Bank = Array.Empty<double?>();
        [DataMember(Name = "onGround", Order = 5, IsRequired = true)] public bool[] OnGround = Array.Empty<bool>();
    }
    [DataContract] private sealed class InputsV1
    {
        [DataMember(Name = "time", Order = 1, IsRequired = true)] public double[] Time = Array.Empty<double>();
        [DataMember(Name = "columns", Order = 2, IsRequired = true)] public LandingPhysicalInputColumns Columns = null!;
        [DataMember(Name = "sourceIndices", Order = 3, IsRequired = true)] public int[] SourceIndices = Array.Empty<int>();
        [DataMember(Name = "axisCount", Order = 4, IsRequired = true)] public int AxisCount;
    }
    [DataContract] private sealed class ControllersV1
    {
        [DataMember(Name = "time", Order = 1, IsRequired = true)] public double[] Time = Array.Empty<double>();
        [DataMember(Name = "timeline", Order = 2, IsRequired = true)] public ControllerCatalogTimeline Timeline = null!;
    }
    [DataContract] private sealed class SourceStateV1
    {
        [DataMember(Name = "time", Order = 1, IsRequired = true)] public double[] Time = Array.Empty<double>();
        [DataMember(Name = "data", Order = 2, IsRequired = true)] public string Data = "";
    }
}
