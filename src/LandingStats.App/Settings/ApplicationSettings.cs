using System.Runtime.Serialization;

namespace LandingStats.App.Settings;

[DataContract]
internal sealed class ApplicationSettings : IExtensibleDataObject
{
    internal const int CurrentSchemaVersion = 1;

    [DataMember(Name = "schemaVersion", Order = 1)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [DataMember(Name = "language", Order = 2)]
    public string Language { get; set; } = LocalizationManager.AutomaticLanguage;

    public ExtensionDataObject? ExtensionData { get; set; }

    public void Normalize()
    {
        if (SchemaVersion <= 0)
        {
            SchemaVersion = CurrentSchemaVersion;
        }

        Language = LocalizationManager.NormalizePreference(Language);
    }
}
