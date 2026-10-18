using System;
using System.Runtime.Serialization;
namespace LandingStats.App.Runtime;

internal enum EpisodeWorkStage { Created, InputsCommitted, Analysed, HistorySaved, ReportReady, ArchivePublished, OutboxEnqueued, Completed }

[DataContract]
internal sealed class EpisodeWorkManifest
{
    [DataMember(Order = 1)] public int Version { get; set; } = 2;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public string Engine { get; set; } = "";
    [DataMember(Order = 4)] public long TimestampUtcTicks { get; set; }
    public DateTime TimestampUtc { get => new DateTime(TimestampUtcTicks, DateTimeKind.Utc); set => TimestampUtcTicks = value.ToUniversalTime().Ticks; }
    [DataMember(Order = 5)] public EpisodeWorkStage Stage { get; set; }
    [DataMember(Order = 6)] public string InputHash { get; set; } = "";
    [DataMember(Order = 7)] public string AnalysisHash { get; set; } = "";
    [DataMember(Order = 8)] public Guid ArchiveId { get; set; }
    [DataMember(Order = 9)] public bool AutomaticAllowedAtCapture { get; set; }
    [DataMember(Order = 10)] public long PolicyRevision { get; set; }
    [DataMember(Order = 11)] public string ArchivePath { get; set; } = "";
    [DataMember(Order = 12)] public string ArchiveHash { get; set; } = "";
    [DataMember(Order = 13)] public string ArchiveSkipReason { get; set; } = "";
    [DataMember(Order = 14)] public string LastFailure { get; set; } = "";
    [DataMember(Order = 15)] public int FailureCount { get; set; }
    [DataMember(Order = 16)] public string FacilityHash { get; set; } = "";
    [DataMember(Order = 17)] public string ReportHash { get; set; } = "";
    [DataMember(Order = 18)] public bool IsFailed { get; set; }
    [DataMember(Order = 19, EmitDefaultValue = false)] public string GeometryHash { get; set; } = "";

    public void CopyFrom(EpisodeWorkManifest value)
    {
        Version = value.Version; Id = value.Id; Engine = value.Engine; TimestampUtcTicks = value.TimestampUtcTicks; Stage = value.Stage;
        InputHash = value.InputHash; AnalysisHash = value.AnalysisHash; ArchiveId = value.ArchiveId;
        AutomaticAllowedAtCapture = value.AutomaticAllowedAtCapture; PolicyRevision = value.PolicyRevision;
        ArchivePath = value.ArchivePath; ArchiveHash = value.ArchiveHash; ArchiveSkipReason = value.ArchiveSkipReason;
        LastFailure = value.LastFailure; FailureCount = value.FailureCount; FacilityHash = value.FacilityHash; ReportHash = value.ReportHash;
        IsFailed = value.IsFailed;
        GeometryHash = value.GeometryHash;
    }
}
