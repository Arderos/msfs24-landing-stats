using System.Runtime.Serialization;

namespace LandingStats.App.Settings;

[DataContract]
internal sealed class ApplicationSettings : IExtensibleDataObject
{
    internal const int CurrentSchemaVersion = 3;

    [DataMember(Name = "schemaVersion", Order = 1)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [DataMember(Name = "language", Order = 2)]
    public string Language { get; set; } = LocalizationManager.AutomaticLanguage;

    [DataMember(Name = "startWithSimulator", Order = 3, EmitDefaultValue = false)]
    public bool? StartWithSimulator { get; set; }

    [DataMember(Name = "googleDrivePromptAnswered", Order = 4, EmitDefaultValue = false)]
    public bool GoogleDrivePromptAnswered { get; set; }

    public ExtensionDataObject? ExtensionData { get; set; }

    public void Normalize()
    {
        if (SchemaVersion < CurrentSchemaVersion)
        {
            SchemaVersion = CurrentSchemaVersion;
        }

        Language = LocalizationManager.NormalizePreference(Language);
    }
}
