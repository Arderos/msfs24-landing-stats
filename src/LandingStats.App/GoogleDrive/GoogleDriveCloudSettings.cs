using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using LandingStats.App.Settings;

namespace LandingStats.App.GoogleDrive;

[DataContract]
internal sealed class GoogleDriveCloudSettings
{
    public const int CurrentFormatVersion = 1;

    [DataMember(Name = "formatVersion", Order = 1)]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [DataMember(Name = "language", Order = 2)]
    public string Language { get; set; } = LocalizationManager.AutomaticLanguage;

    [DataMember(Name = "startWithSimulator", Order = 3, EmitDefaultValue = false)]
    public bool? StartWithSimulator { get; set; }

    public void Normalize()
    {
        if (FormatVersion <= 0)
        {
            FormatVersion = CurrentFormatVersion;
        }
        Language = LocalizationManager.NormalizePreference(Language);
    }

    public bool IsSupported => FormatVersion <= CurrentFormatVersion;

    public GoogleDriveCloudSettings Clone()
    {
        return new GoogleDriveCloudSettings
        {
            FormatVersion = FormatVersion,
            Language = Language,
            StartWithSimulator = StartWithSimulator,
        };
    }

    public static byte[] Serialize(GoogleDriveCloudSettings settings)
    {
        settings.Normalize();
        if (!settings.IsSupported)
        {
            throw new InvalidDataException(
                "Unsupported Google Drive settings format " + settings.FormatVersion + ".");
        }
        using var output = new MemoryStream();
        new DataContractJsonSerializer(typeof(GoogleDriveCloudSettings)).WriteObject(output, settings);
        return output.ToArray();
    }

    public static GoogleDriveCloudSettings Deserialize(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var settings = new DataContractJsonSerializer(typeof(GoogleDriveCloudSettings))
            .ReadObject(input) as GoogleDriveCloudSettings ?? new GoogleDriveCloudSettings();
        settings.Normalize();
        return settings;
    }
}

internal sealed class GoogleDriveLocalSettings
{
    public GoogleDriveLocalSettings(GoogleDriveCloudSettings settings, bool persistedAndReadable)
    {
        Settings = settings ?? throw new System.ArgumentNullException(nameof(settings));
        PersistedAndReadable = persistedAndReadable;
    }

    public GoogleDriveCloudSettings Settings { get; }

    public bool PersistedAndReadable { get; }
}
