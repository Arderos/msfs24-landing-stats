using System.Runtime.Serialization;

namespace LandingStats.App.Settings;

[DataContract]
internal sealed class ApplicationSettings : IExtensibleDataObject
{
    internal const int CurrentSchemaVersion = 5;

    [DataMember(Name = "schemaVersion", Order = 1)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [DataMember(Name = "language", Order = 2)]
    public string Language { get; set; } = LocalizationManager.AutomaticLanguage;

    [DataMember(Name = "startWithSimulator", Order = 3, EmitDefaultValue = false)]
    public bool? StartWithSimulator { get; set; }

    [DataMember(Name = "googleDrivePromptAnswered", Order = 4, EmitDefaultValue = false)]
    public bool GoogleDrivePromptAnswered { get; set; }

    [DataMember(Name = "automaticTelemetryUpload", Order = 5)]
    public bool AutomaticTelemetryUpload { get; set; } = true;

    // Access to local data is opt-in on each computer and is never cloud-synced.
    [DataMember(Name = "integrationApiEnabled", Order = 6)]
    public bool IntegrationApiEnabled { get; set; }

    // DataContractSerializer does not run property initializers for old files.
    [OnDeserializing]
    private void InitializeDefaults(StreamingContext context) => AutomaticTelemetryUpload = true;

    public ExtensionDataObject? ExtensionData { get; set; }

    public ApplicationSettings Clone() => new ApplicationSettings
    {
        SchemaVersion = SchemaVersion,
        Language = Language,
        StartWithSimulator = StartWithSimulator,
        GoogleDrivePromptAnswered = GoogleDrivePromptAnswered,
        AutomaticTelemetryUpload = AutomaticTelemetryUpload,
        IntegrationApiEnabled = IntegrationApiEnabled,
        // Extension data is opaque and never mutated by the application. Keep
        // it on detached save snapshots so future-version options survive.
        ExtensionData = ExtensionData,
    };

    public void CopyFrom(ApplicationSettings source)
    {
        SchemaVersion = source.SchemaVersion;
        Language = source.Language;
        StartWithSimulator = source.StartWithSimulator;
        GoogleDrivePromptAnswered = source.GoogleDrivePromptAnswered;
        AutomaticTelemetryUpload = source.AutomaticTelemetryUpload;
        IntegrationApiEnabled = source.IntegrationApiEnabled;
        ExtensionData = source.ExtensionData;
    }

    public void Normalize()
    {
        if (SchemaVersion < CurrentSchemaVersion)
        {
            SchemaVersion = CurrentSchemaVersion;
        }

        Language = LocalizationManager.NormalizePreference(Language);
    }
}
