using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LandingStats.App.Launcher;

internal static class Program
{
    private const string VerifyArgument = "--verify-bundle";
    private const string ChildExecutable = "LandingStats.App.exe";

    private static readonly byte[] BundleMagic = Encoding.ASCII.GetBytes("MSFSLSABUNDLE1");

    private static readonly string[] RequiredFiles =
    {
        ChildExecutable,
        "LandingStats.App.exe.config",
        "LandingStats.Core.dll",
        "Microsoft.FlightSimulator.SimConnect.dll",
        "SimConnect.dll",
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var runtimeDirectory = ExtractBundle();
            VerifyRuntime(runtimeDirectory);

            if (args.Any(argument => string.Equals(argument, VerifyArgument, StringComparison.OrdinalIgnoreCase)))
            {
                return 0;
            }

            var childArguments = args
                .Where(argument => !string.Equals(argument, VerifyArgument, StringComparison.OrdinalIgnoreCase))
                .Select(QuoteArgument);
            var launcherDirectory = Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(runtimeDirectory, ChildExecutable),
                WorkingDirectory = launcherDirectory,
                Arguments = string.Join(" ", childArguments),
                UseShellExecute = false,
            };
            startInfo.EnvironmentVariables["MSFS_LANDING_STATS_LAUNCHER_PATH"] = Application.ExecutablePath;
            Process.Start(startInfo);
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

    private static string ExtractBundle()
    {
        var payload = ReadPayload();
        string hash;
        using (var sha256 = SHA256.Create())
        {
            hash = ToHex(sha256.ComputeHash(payload));
        }

        var runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Runtime",
            "App",
            hash);
        var mutexName = "Local\\MSFSLandingStatsApp-" + hash.Substring(0, 24);

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
                    throw new TimeoutException("Timed out while another instance was preparing the application runtime.");
                }

                var markerPath = Path.Combine(runtimeDirectory, ".complete");
                if (File.Exists(markerPath) &&
                    string.Equals(File.ReadAllText(markerPath).Trim(), hash, StringComparison.OrdinalIgnoreCase) &&
                    RuntimeMatchesBundle(payload, runtimeDirectory))
                {
                    return runtimeDirectory;
                }

                Directory.CreateDirectory(runtimeDirectory);
                ExtractArchive(payload, runtimeDirectory);
                VerifyRuntime(runtimeDirectory);
                if (!RuntimeMatchesBundle(payload, runtimeDirectory))
                {
                    throw new InvalidDataException("The extracted application runtime failed its integrity check.");
                }

                File.WriteAllText(markerPath, hash, new UTF8Encoding(false));
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
            var actualMagic = reader.ReadBytes(BundleMagic.Length);
            if (!actualMagic.SequenceEqual(BundleMagic))
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
            if (payload.Length != payloadLength)
            {
                throw new EndOfStreamException("The application bundle is truncated.");
            }

            return payload;
        }
    }

    private static void ExtractArchive(byte[] payload, string runtimeDirectory)
    {
        var root = Path.GetFullPath(runtimeDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        using (var memory = new MemoryStream(payload, false))
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Read, false))
        {
            foreach (var entry in archive.Entries)
            {
                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The application bundle contains an unsafe path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                using (var input = entry.Open())
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
        }
    }

    private static void VerifyRuntime(string runtimeDirectory)
    {
        var missing = RequiredFiles
            .Where(file => !File.Exists(Path.Combine(runtimeDirectory, file)))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException("The application bundle is incomplete: " + string.Join(", ", missing));
        }
    }

    private static bool RuntimeMatchesBundle(byte[] payload, string runtimeDirectory)
    {
        using (var memory = new MemoryStream(payload, false))
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Read, false))
        using (var sha256 = SHA256.Create())
        {
            foreach (var requiredFile in RequiredFiles)
            {
                var entry = archive.GetEntry(requiredFile);
                var runtimePath = Path.Combine(runtimeDirectory, requiredFile);
                if (entry == null || !File.Exists(runtimePath))
                {
                    return false;
                }

                byte[] expected;
                using (var input = entry.Open())
                {
                    expected = sha256.ComputeHash(input);
                }

                byte[] actual;
                using (var input = File.OpenRead(runtimePath))
                {
                    actual = sha256.ComputeHash(input);
                }

                if (!expected.SequenceEqual(actual))
                {
                    return false;
                }
            }

            return true;
        }
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

    private static string ToHex(IEnumerable<byte> bytes)
    {
        var result = new StringBuilder();
        foreach (var value in bytes)
        {
            result.Append(value.ToString("x2"));
        }

        return result.ToString();
    }
}
