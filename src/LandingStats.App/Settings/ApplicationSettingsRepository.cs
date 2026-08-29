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

    public string GoogleDriveRestoreMarkerPath => Path + ".google-drive-restore-pending";

    public bool GoogleDriveRestorePending => File.Exists(GoogleDriveRestoreMarkerPath);

    public ApplicationSettings Load()
    {
        TryLoad(out var settings);
        return settings;
    }

    public bool TryLoad(out ApplicationSettings settings)
    {
        if (!File.Exists(Path))
        {
            settings = new ApplicationSettings();
            return false;
        }

        try
        {
            using var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            settings = _serializer.ReadObject(input) as ApplicationSettings ?? new ApplicationSettings();
            settings.Normalize();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is SerializationException)
        {
            settings = new ApplicationSettings();
            return false;
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

    public void MarkGoogleDriveRestorePending()
    {
        var directory = System.IO.Path.GetDirectoryName(GoogleDriveRestoreMarkerPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var output = new FileStream(
            GoogleDriveRestoreMarkerPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read);
        output.SetLength(0);
        output.WriteByte(1);
        output.Flush(true);
    }

    public bool TryMarkGoogleDriveRestorePending()
    {
        try
        {
            MarkGoogleDriveRestorePending();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException)
        {
            // An existing marker may be temporarily locked by antivirus or a
            // second reader. Its presence is enough to preserve recovery state.
            return File.Exists(GoogleDriveRestoreMarkerPath);
        }
    }

    public void ClearGoogleDriveRestorePending()
    {
        if (File.Exists(GoogleDriveRestoreMarkerPath))
        {
            File.Delete(GoogleDriveRestoreMarkerPath);
        }
    }

    public void CompleteGoogleDriveRestore(ApplicationSettings settings)
    {
        Save(settings);
        ClearGoogleDriveRestorePending();
    }
}
