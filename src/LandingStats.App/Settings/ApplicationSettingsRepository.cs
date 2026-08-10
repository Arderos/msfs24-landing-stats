using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace LandingStats.App.Settings;

internal sealed class ApplicationSettingsRepository
{
    private readonly DataContractJsonSerializer _serializer =
        new DataContractJsonSerializer(typeof(ApplicationSettings));

    public ApplicationSettingsRepository(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "settings.json");
    }

    public string Path { get; }

    public ApplicationSettings Load()
    {
        if (!File.Exists(Path))
        {
            return new ApplicationSettings();
        }

        try
        {
            using var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var settings = _serializer.ReadObject(input) as ApplicationSettings ?? new ApplicationSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is SerializationException)
        {
            return new ApplicationSettings();
        }
    }

    public void Save(ApplicationSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        settings.Normalize();
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = Path + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                _serializer.WriteObject(output, settings);
                output.Flush(true);
            }

            if (File.Exists(Path))
            {
                File.Replace(temporaryPath, Path, null);
            }
            else
            {
                File.Move(temporaryPath, Path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
