using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LandingStats.App.Launcher;

internal static class Program
{
    private const string VerifyArgument = "--verify-bundle";
    private const string ChildExecutable = "MSFS-Landing-Stats.exe";
    private const string LauncherPathEnvironmentVariable = "MSFS_LANDING_STATS_LAUNCHER_PATH";

    private static readonly byte[] BundleMagic = Encoding.ASCII.GetBytes("MSFSLSABUNDLE1");

    private static readonly string[] RequiredFiles =
    {
        ChildExecutable,
        "MSFS-Landing-Stats.exe.config",
        "LandingStats.Core.dll",
        "Microsoft.FlightSimulator.SimConnect.dll",
        "SimConnect.dll",
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var payload = ReadPayload();
            ValidateArchive(payload);
            if (args.Any(argument => string.Equals(argument, VerifyArgument, StringComparison.OrdinalIgnoreCase)))
            {
                return 0;
            }

            var runtimeDirectory = PrepareRuntime(payload);
            var childArguments = args
                .Where(argument => !string.Equals(argument, VerifyArgument, StringComparison.OrdinalIgnoreCase))
                .Select(QuoteArgument);
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(runtimeDirectory, ChildExecutable),
                WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory,
                Arguments = string.Join(" ", childArguments),
                UseShellExecute = false,
            };
            startInfo.EnvironmentVariables[LauncherPathEnvironmentVariable] = Application.ExecutablePath;
            if (Process.Start(startInfo) == null)
            {
                throw new InvalidOperationException("The application process could not be started.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "MSFS Landing Stats could not start.\r\n\r\n" + exception.Message,
                "MSFS Landing Stats",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string PrepareRuntime(byte[] payload)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version
                      ?? throw new InvalidDataException("Bootstrap version is unavailable.");
        var versionText = $"{version.Major}.{version.Minor}.{version.Build}";
        var runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Runtime",
            "App");
        var runtimeDirectory = Path.Combine(runtimeRoot, versionText);
        var markerPath = Path.Combine(runtimeDirectory, ".bootstrap");
        var mutexName = "Local\\MSFSLandingStatsBootstrap-" + versionText.Replace('.', '-');

        using (var mutex = new Mutex(false, mutexName))
        {
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(TimeSpan.FromMinutes(2));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    throw new TimeoutException("Timed out while preparing the application runtime.");
                }

                // The signed external updater replaces the private runtime in place. Once this
                // bootstrap has created it, never restore the embedded version over an update.
                if (File.Exists(markerPath) &&
                    string.Equals(File.ReadAllText(markerPath).Trim(), versionText, StringComparison.Ordinal) &&
                    RuntimeIsComplete(runtimeDirectory))
                {
                    return runtimeDirectory;
                }

                Directory.CreateDirectory(runtimeRoot);
                var stagingDirectory = Path.Combine(runtimeRoot, ".staging-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ExtractArchive(payload, stagingDirectory);
                    if (!RuntimeIsComplete(stagingDirectory))
                    {
                        throw new InvalidDataException("The application bundle is incomplete.");
                    }

                    File.WriteAllText(
                        Path.Combine(stagingDirectory, ".bootstrap"),
                        versionText,
                        new UTF8Encoding(false));
                    if (Directory.Exists(runtimeDirectory))
                    {
                        Directory.Delete(runtimeDirectory, true);
                    }

                    Directory.Move(stagingDirectory, runtimeDirectory);
                }
                finally
                {
                    if (Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory, true);
                    }
                }

                return runtimeDirectory;
            }
            finally
            {
                if (ownsMutex)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    private static byte[] ReadPayload()
    {
        using (var executable = File.OpenRead(Application.ExecutablePath))
        using (var reader = new BinaryReader(executable, Encoding.UTF8, true))
        {
            var trailerLength = sizeof(long) + BundleMagic.Length;
            if (executable.Length <= trailerLength)
            {
                throw new InvalidDataException("This file does not contain an application bundle.");
            }

            executable.Seek(-BundleMagic.Length, SeekOrigin.End);
            if (!reader.ReadBytes(BundleMagic.Length).SequenceEqual(BundleMagic))
            {
                throw new InvalidDataException("The application bundle signature is missing or damaged.");
            }

            executable.Seek(-trailerLength, SeekOrigin.End);
            var payloadLength = reader.ReadInt64();
            var payloadOffset = executable.Length - trailerLength - payloadLength;
            if (payloadLength <= 0 || payloadLength > int.MaxValue || payloadOffset <= 0)
            {
                throw new InvalidDataException("The application bundle length is invalid.");
            }

            executable.Seek(payloadOffset, SeekOrigin.Begin);
            var payload = reader.ReadBytes((int)payloadLength);
            if (payload.LongLength != payloadLength)
            {
                throw new EndOfStreamException("The application bundle is truncated.");
            }

            return payload;
        }
    }

    private static void ValidateArchive(byte[] payload)
    {
        using var memory = new MemoryStream(payload, false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
        if (archive.Entries.Count != RequiredFiles.Length)
        {
            throw new InvalidDataException("The application bundle contains an unexpected number of files.");
        }

        var expected = new HashSet<string>(RequiredFiles, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) ||
                !string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal) ||
                entry.Length <= 0 ||
                !expected.Contains(entry.Name) ||
                !seen.Add(entry.Name))
            {
                throw new InvalidDataException("The application bundle contains an unexpected or unsafe entry.");
            }
        }

        if (!expected.SetEquals(seen))
        {
            throw new InvalidDataException("The application bundle is incomplete.");
        }
    }

    private static void ExtractArchive(byte[] payload, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        using var memory = new MemoryStream(payload, false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.Combine(destinationDirectory, entry.Name);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            if (output.Length != entry.Length)
            {
                throw new InvalidDataException("The application bundle entry is truncated: " + entry.Name);
            }
        }
    }

    private static bool RuntimeIsComplete(string runtimeDirectory)
    {
        return RequiredFiles.All(file =>
        {
            var path = Path.Combine(runtimeDirectory, file);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        });
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return argument;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}
