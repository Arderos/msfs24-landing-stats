using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace LandingStats.App.Settings;

internal sealed class SimulatorAutoStartManager
{
    internal const string EntryName = "MSFS Landing Stats";
    internal const string LauncherPathEnvironmentVariable = "MSFS_LANDING_STATS_LAUNCHER_PATH";

    private const string BeginMarker = "<!-- MSFS Landing Stats: managed autostart begin -->";
    private const string EndMarker = "<!-- MSFS Landing Stats: managed autostart end -->";
    private const string RootElementName = "SimBase.Document";
    private const int MaximumConfigurationBytes = 4 * 1024 * 1024;

    private readonly IReadOnlyList<SimulatorProfile> _profiles;
    private readonly Func<string?> _applicationPathProvider;

    public SimulatorAutoStartManager()
        : this(DefaultProfiles(), ResolveApplicationPath)
    {
    }

    internal SimulatorAutoStartManager(
        IReadOnlyList<SimulatorProfile> profiles,
        Func<string?> applicationPathProvider)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _applicationPathProvider = applicationPathProvider ?? throw new ArgumentNullException(nameof(applicationPathProvider));
    }

    public AutoStartOperationResult SetEnabled(bool enabled)
    {
        var detected = _profiles
            .Where(profile => profile.IsDetected())
            .GroupBy(profile => Path.GetFullPath(profile.ExeXmlPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (enabled && detected.Length == 0)
        {
            throw new InvalidOperationException(
                "No Microsoft Flight Simulator 2020 or 2024 profile was found for this Windows user.");
        }

        var applicationPath = enabled ? ValidateApplicationPath(_applicationPathProvider()) : null;
        using var mutex = new Mutex(false, "Local\\MSFSLandingStatsExeXml");
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                throw new TimeoutException("Timed out while waiting to update the simulator auto-start configuration.");
            }

            var mutations = detected
                .Select(profile => PrepareMutation(profile, enabled, applicationPath))
                .ToArray();
            var committed = new List<ConfigurationMutation>();
            try
            {
                foreach (var mutation in mutations.Where(candidate => candidate.Changed))
                {
                    Commit(mutation);
                    committed.Add(mutation);
                }
            }
            catch (Exception commitException)
            {
                var rollbackFailures = new List<Exception>();
                for (var index = committed.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        RollBack(committed[index]);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackFailures.Add(rollbackException);
                    }
                }

                if (rollbackFailures.Count > 0)
                {
                    throw new AggregateException(
                        "Updating simulator auto-start failed and at least one configuration could not be rolled back safely.",
                        new[] { commitException }.Concat(rollbackFailures));
                }

                throw;
            }

            return new AutoStartOperationResult(
                enabled,
                mutations.Select(mutation => mutation.Path).ToArray(),
                mutations.Where(mutation => mutation.Changed).Select(mutation => mutation.Path).ToArray());
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    internal static IReadOnlyList<SimulatorProfile> DefaultProfiles(
        string? roamingApplicationData = null,
        string? localApplicationData = null)
    {
        var roaming = roamingApplicationData ??
                      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = localApplicationData ??
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            new SimulatorProfile("MSFS 2024 Steam", Path.Combine(roaming, "Microsoft Flight Simulator 2024")),
            new SimulatorProfile("MSFS 2024 Microsoft Store", Path.Combine(
                local,
                "Packages",
                "Microsoft.Limitless_8wekyb3d8bbwe",
                "LocalCache")),
            new SimulatorProfile("MSFS 2020 Steam", Path.Combine(roaming, "Microsoft Flight Simulator")),
            new SimulatorProfile("MSFS 2020 Microsoft Store", Path.Combine(
                local,
                "Packages",
                "Microsoft.FlightSimulator_8wekyb3d8bbwe",
                "LocalCache")),
        };
    }

    internal static string? ResolveApplicationPath()
    {
        var launcherPath = Environment.GetEnvironmentVariable(LauncherPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            return launcherPath;
        }

        return Process.GetCurrentProcess().MainModule?.FileName;
    }

    private static ConfigurationMutation PrepareMutation(
        SimulatorProfile profile,
        bool enabled,
        string? applicationPath)
    {
        var path = Path.GetFullPath(profile.ExeXmlPath);
        byte[]? originalBytes = null;
        EncodedXml encoded;
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Refusing to modify a reparse-point simulator configuration: {path}");
            }

            var file = new FileInfo(path);
            if (file.Length <= 0 || file.Length > MaximumConfigurationBytes)
            {
                throw new InvalidDataException($"Simulator auto-start configuration has an unsafe size: {path}");
            }

            originalBytes = File.ReadAllBytes(path);
            encoded = Decode(originalBytes, path);
        }
        else
        {
            if (!enabled)
            {
                return ConfigurationMutation.Unchanged(path, null);
            }

            encoded = EncodedXml.NewDocument();
        }

        var originalDocument = Parse(encoded.Text, path);
        ValidateDocumentRoot(originalDocument, path);
        var beforeFingerprint = ForeignFingerprint(originalDocument);
        var mutatedText = MutateText(encoded.Text, originalDocument, enabled, applicationPath, encoded.Encoding);
        if (string.Equals(encoded.Text, mutatedText, StringComparison.Ordinal))
        {
            return ConfigurationMutation.Unchanged(path, originalBytes);
        }

        var updatedDocument = Parse(mutatedText, path);
        ValidateDocumentRoot(updatedDocument, path);
        if (!string.Equals(beforeFingerprint, ForeignFingerprint(updatedDocument), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Refusing to update {path} because content owned by another add-on would change.");
        }

        VerifyDesiredState(updatedDocument, mutatedText, enabled, applicationPath, path);
        var updatedBytes = encoded.Encode(mutatedText);
        return new ConfigurationMutation(path, originalBytes, updatedBytes, true);
    }

    private static string MutateText(
        string text,
        XDocument document,
        bool enabled,
        string? applicationPath,
        Encoding encoding)
    {
        var markerRange = FindMarkerRange(text);
        var ownedEntries = OwnedEntries(document).ToArray();
        if (enabled && markerRange == null && ownedEntries.Length > 0)
        {
            throw new InvalidDataException(
                $"An unmanaged '{EntryName}' entry already exists. Remove or rename that entry before using this setting.");
        }

        if (markerRange != null && ownedEntries.Length != 1)
        {
            throw new InvalidDataException("The managed simulator auto-start block is incomplete or ambiguous.");
        }

        if (!enabled)
        {
            return markerRange == null
                ? text
                : text.Remove(markerRange.Value.Start, markerRange.Value.Length);
        }

        var fragment = BuildManagedFragment(applicationPath!, encoding);
        if (markerRange != null)
        {
            var current = text.Substring(markerRange.Value.Start, markerRange.Value.Length);
            return string.Equals(current, fragment, StringComparison.Ordinal)
                ? text
                : text.Remove(markerRange.Value.Start, markerRange.Value.Length)
                    .Insert(markerRange.Value.Start, fragment);
        }

        var closeTag = "</" + RootElementName;
        var insertionIndex = text.LastIndexOf(closeTag, StringComparison.Ordinal);
        if (insertionIndex < 0)
        {
            throw new InvalidDataException("The simulator auto-start document has no supported closing root element.");
        }

        return text.Insert(insertionIndex, fragment);
    }

    private static string BuildManagedFragment(string applicationPath, Encoding encoding)
    {
        return BeginMarker +
               "<Launch.Addon>" +
               "<Name>" + EscapeXmlText(EntryName, encoding) + "</Name>" +
               "<Disabled>False</Disabled>" +
               "<ManualLoad>False</ManualLoad>" +
               "<Path>" + EscapeXmlText(applicationPath, encoding) + "</Path>" +
               "</Launch.Addon>" +
               EndMarker;
    }

    private static string EscapeXmlText(string value, Encoding encoding)
    {
        var result = new StringBuilder(value.Length + 16);
        var strictEncoding = Encoding.GetEncoding(
            encoding.CodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '&')
            {
                result.Append("&amp;");
                continue;
            }

            if (character == '<')
            {
                result.Append("&lt;");
                continue;
            }

            if (character == '>')
            {
                result.Append("&gt;");
                continue;
            }

            var text = character.ToString();
            var codePoint = (int)character;
            if (char.IsHighSurrogate(character) &&
                index + 1 < value.Length &&
                char.IsLowSurrogate(value[index + 1]))
            {
                text = new string(new[] { character, value[++index] });
                codePoint = char.ConvertToUtf32(text, 0);
            }

            try
            {
                strictEncoding.GetBytes(text);
                result.Append(text);
            }
            catch (EncoderFallbackException)
            {
                result.Append("&#x").Append(codePoint.ToString("X")).Append(';');
            }
        }

        return result.ToString();
    }

    private static MarkerRange? FindMarkerRange(string text)
    {
        var begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin < 0 && end < 0)
        {
            return null;
        }

        if (begin < 0 || end < begin ||
            text.IndexOf(BeginMarker, begin + BeginMarker.Length, StringComparison.Ordinal) >= 0 ||
            text.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("The managed simulator auto-start markers are incomplete or duplicated.");
        }

        return new MarkerRange(begin, end + EndMarker.Length - begin);
    }

    private static IEnumerable<XElement> OwnedEntries(XDocument document)
    {
        return document.Root!
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Launch.Addon", StringComparison.Ordinal))
            .Where(element => string.Equals(
                ChildValue(element, "Name")?.Trim(),
                EntryName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? ChildValue(XElement parent, string localName)
    {
        return parent.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value;
    }

    private static void VerifyDesiredState(
        XDocument document,
        string text,
        bool enabled,
        string? applicationPath,
        string path)
    {
        var markers = FindMarkerRange(text);
        var entries = OwnedEntries(document).ToArray();
        if (!enabled)
        {
            if (markers != null || entries.Length != 0)
            {
                throw new InvalidDataException($"The managed auto-start entry was not removed from {path}.");
            }

            return;
        }

        if (markers == null || entries.Length != 1 ||
            !string.Equals(ChildValue(entries[0], "Path"), applicationPath, StringComparison.OrdinalIgnoreCase) ||
            !IsFalse(ChildValue(entries[0], "Disabled")) ||
            !IsFalse(ChildValue(entries[0], "ManualLoad")))
        {
            throw new InvalidDataException($"The managed auto-start entry failed verification in {path}.");
        }
    }

    private static bool IsFalse(string? value)
    {
        return string.Equals(value?.Trim(), "False", StringComparison.OrdinalIgnoreCase);
    }

    private static string ForeignFingerprint(XDocument source)
    {
        var result = new StringBuilder();
        foreach (var node in source.Nodes())
        {
            AppendForeignNodeFingerprint(node, result);
        }

        return result.ToString();
    }

    private static void AppendForeignNodeFingerprint(XNode node, StringBuilder result)
    {
        if (node is XElement element)
        {
            if (string.Equals(element.Name.LocalName, "Launch.Addon", StringComparison.Ordinal) &&
                string.Equals(ChildValue(element, "Name")?.Trim(), EntryName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AppendFingerprintValue(result, "element", element.Name.ToString());
            foreach (var attribute in element.Attributes())
            {
                AppendFingerprintValue(result, "attribute-name", attribute.Name.ToString());
                AppendFingerprintValue(result, "attribute-value", attribute.Value);
            }

            foreach (var child in element.Nodes())
            {
                AppendForeignNodeFingerprint(child, result);
            }

            result.Append("/element;");
            return;
        }

        if (node is XComment comment)
        {
            var value = comment.Value.Trim();
            if (string.Equals(value, "MSFS Landing Stats: managed autostart begin", StringComparison.Ordinal) ||
                string.Equals(value, "MSFS Landing Stats: managed autostart end", StringComparison.Ordinal))
            {
                return;
            }

            AppendFingerprintValue(result, "comment", comment.Value);
            return;
        }

        if (node is XText text)
        {
            if (!string.IsNullOrWhiteSpace(text.Value))
            {
                AppendFingerprintValue(result, node is XCData ? "cdata" : "text", text.Value);
            }

            return;
        }

        if (node is XProcessingInstruction instruction)
        {
            AppendFingerprintValue(result, "pi-target", instruction.Target);
            AppendFingerprintValue(result, "pi-data", instruction.Data);
            return;
        }

        if (node is XDocumentType documentType)
        {
            AppendFingerprintValue(result, "doctype", documentType.ToString(SaveOptions.DisableFormatting));
        }
    }

    private static void AppendFingerprintValue(StringBuilder result, string label, string value)
    {
        result.Append(label).Append(':').Append(value.Length).Append(':').Append(value).Append(';');
    }

    private static XDocument Parse(string text, string path)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumConfigurationBytes,
                IgnoreWhitespace = false,
            };
            using var stringReader = new StringReader(text);
            using var reader = XmlReader.Create(stringReader, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Simulator auto-start configuration is not valid XML: {path}", exception);
        }
    }

    private static void ValidateDocumentRoot(XDocument document, string path)
    {
        if (document.Root == null ||
            !string.Equals(document.Root.Name.LocalName, RootElementName, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(document.Root.Name.NamespaceName))
        {
            throw new InvalidDataException($"Simulator auto-start configuration has an unsupported root element: {path}");
        }
    }

    private static EncodedXml Decode(byte[] bytes, string path)
    {
        var encoding = DetectEncoding(bytes);
        var preamble = encoding.GetPreamble();
        var preambleLength = StartsWith(bytes, preamble) ? preamble.Length : 0;
        try
        {
            var strict = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            return new EncodedXml(
                strict.GetString(bytes, preambleLength, bytes.Length - preambleLength),
                strict,
                preambleLength == 0 ? Array.Empty<byte>() : bytes.Take(preambleLength).ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Simulator auto-start configuration uses invalid text encoding: {path}", exception);
        }
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (StartsWith(bytes, Encoding.UTF8.GetPreamble()))
        {
            return new UTF8Encoding(true);
        }

        if (StartsWith(bytes, Encoding.Unicode.GetPreamble()))
        {
            return Encoding.Unicode;
        }

        if (StartsWith(bytes, Encoding.BigEndianUnicode.GetPreamble()))
        {
            return Encoding.BigEndianUnicode;
        }

        if (bytes.Length >= 4 && bytes[0] == '<' && bytes[1] == 0 && bytes[2] == '?' && bytes[3] == 0)
        {
            return Encoding.Unicode;
        }

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == '<' && bytes[2] == 0 && bytes[3] == '?')
        {
            return Encoding.BigEndianUnicode;
        }

        var prefixLength = Math.Min(bytes.Length, 512);
        var prefix = Encoding.ASCII.GetString(bytes, 0, prefixLength);
        var marker = "encoding=";
        var markerIndex = prefix.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var quoteIndex = markerIndex + marker.Length;
            while (quoteIndex < prefix.Length && char.IsWhiteSpace(prefix[quoteIndex]))
            {
                quoteIndex++;
            }

            if (quoteIndex < prefix.Length && (prefix[quoteIndex] == '\'' || prefix[quoteIndex] == '"'))
            {
                var quote = prefix[quoteIndex];
                var end = prefix.IndexOf(quote, quoteIndex + 1);
                if (end > quoteIndex + 1)
                {
                    try
                    {
                        return Encoding.GetEncoding(prefix.Substring(quoteIndex + 1, end - quoteIndex - 1));
                    }
                    catch (ArgumentException)
                    {
                        throw new InvalidDataException("Simulator auto-start configuration declares an unsupported encoding.");
                    }
                }
            }
        }

        return new UTF8Encoding(false);
    }

    private static bool StartsWith(byte[] value, byte[] prefix)
    {
        return prefix.Length > 0 &&
               value.Length >= prefix.Length &&
               prefix.Select((candidate, index) => value[index] == candidate).All(matches => matches);
    }

    private static string ValidateApplicationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("The portable application executable path is unavailable.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) ||
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The portable application executable was not found.", fullPath);
        }

        var privateRuntimeSegment = Path.DirectorySeparatorChar +
                                    Path.Combine("MSFS Landing Stats", "Runtime", "App") +
                                    Path.DirectorySeparatorChar;
        if (fullPath.IndexOf(privateRuntimeSegment, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidOperationException(
                "Start the downloaded single-file application before enabling simulator auto-start.");
        }

        try
        {
            var assemblyName = AssemblyName.GetAssemblyName(fullPath).Name;
            if (!string.Equals(assemblyName, "MSFS-Landing-Stats", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The selected auto-start executable is not MSFS Landing Stats.");
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException("The selected auto-start executable is not a valid application.", exception);
        }

        return fullPath;
    }

    private static void Commit(ConfigurationMutation mutation)
    {
        var directory = Path.GetDirectoryName(mutation.Path)
                        ?? throw new InvalidOperationException("Simulator configuration directory is unavailable.");
        Directory.CreateDirectory(directory);
        VerifyUnchanged(mutation.Path, mutation.OriginalBytes);
        if (mutation.OriginalBytes != null)
        {
            EnsureOriginalBackup(mutation.Path, mutation.OriginalBytes);
        }

        try
        {
            AtomicReplace(mutation.Path, mutation.OriginalBytes, mutation.UpdatedBytes!);
            var committed = File.ReadAllBytes(mutation.Path);
            if (!committed.SequenceEqual(mutation.UpdatedBytes!))
            {
                throw new IOException($"Simulator configuration verification failed after writing {mutation.Path}.");
            }
        }
        catch (Exception commitException)
        {
            try
            {
                var current = File.Exists(mutation.Path) ? File.ReadAllBytes(mutation.Path) : null;
                if ((current == null && mutation.OriginalBytes == null) ||
                    (current != null && mutation.OriginalBytes != null && current.SequenceEqual(mutation.OriginalBytes)))
                {
                    throw;
                }

                RollBack(mutation);
            }
            catch (Exception rollbackException) when (!ReferenceEquals(rollbackException, commitException))
            {
                throw new AggregateException(
                    $"Updating {mutation.Path} failed and its previous state could not be restored safely.",
                    commitException,
                    rollbackException);
            }

            throw;
        }
    }

    private static void RollBack(ConfigurationMutation mutation)
    {
        if (mutation.OriginalBytes == null)
        {
            if (File.Exists(mutation.Path))
            {
                var current = File.ReadAllBytes(mutation.Path);
                if (!current.SequenceEqual(mutation.UpdatedBytes!))
                {
                    throw new IOException($"Refusing to remove concurrently changed configuration {mutation.Path}.");
                }

                File.Delete(mutation.Path);
            }

            return;
        }

        AtomicReplace(mutation.Path, mutation.UpdatedBytes, mutation.OriginalBytes);
    }

    private static void EnsureOriginalBackup(string path, byte[] originalBytes)
    {
        var backupPath = path + ".msfs-landing-stats.bak";
        if (File.Exists(backupPath))
        {
            return;
        }

        var temporaryPath = backupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(originalBytes, 0, originalBytes.Length);
                output.Flush(true);
            }

            File.Move(temporaryPath, backupPath);
        }
        catch (IOException) when (File.Exists(backupPath))
        {
            // Another instance won the create-only backup race.
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void AtomicReplace(string path, byte[]? expectedBytes, byte[] newBytes)
    {
        VerifyUnchanged(path, expectedBytes);
        var temporaryPath = path + ".msfs-landing-stats." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(newBytes, 0, newBytes.Length);
                output.Flush(true);
            }

            VerifyUnchanged(path, expectedBytes);
            if (expectedBytes == null)
            {
                File.Move(temporaryPath, path);
            }
            else
            {
                File.Replace(temporaryPath, path, null, true);
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

    private static void VerifyUnchanged(string path, byte[]? expectedBytes)
    {
        if (expectedBytes == null)
        {
            if (File.Exists(path))
            {
                throw new IOException($"Simulator configuration appeared while it was being updated: {path}");
            }

            return;
        }

        if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(expectedBytes))
        {
            throw new IOException($"Simulator configuration changed concurrently; no overwrite was performed: {path}");
        }
    }

    internal sealed class SimulatorProfile
    {
        public SimulatorProfile(string name, string directoryPath)
        {
            Name = name;
            DirectoryPath = Path.GetFullPath(directoryPath);
            ExeXmlPath = Path.Combine(DirectoryPath, "exe.xml");
            UserConfigPath = Path.Combine(DirectoryPath, "UserCfg.opt");
        }

        public string Name { get; }
        public string DirectoryPath { get; }
        public string ExeXmlPath { get; }
        public string UserConfigPath { get; }

        public bool IsDetected()
        {
            return File.Exists(ExeXmlPath) || File.Exists(UserConfigPath);
        }
    }

    private readonly struct MarkerRange
    {
        public MarkerRange(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int Start { get; }
        public int Length { get; }
    }

    private sealed class EncodedXml
    {
        public EncodedXml(string text, Encoding encoding, byte[] preamble)
        {
            Text = text;
            Encoding = encoding;
            Preamble = preamble;
        }

        public string Text { get; }
        public Encoding Encoding { get; }
        public byte[] Preamble { get; }

        public byte[] Encode(string value)
        {
            var body = Encoding.GetBytes(value);
            return Preamble.Concat(body).ToArray();
        }

        public static EncodedXml NewDocument()
        {
            return new EncodedXml(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<SimBase.Document Type=\"Launch\" version=\"1,0\">" +
                "<Descr>Launch</Descr>" +
                "<Filename>EXE.xml</Filename>" +
                "<Disabled>False</Disabled>" +
                "<Launch.ManualLoad>False</Launch.ManualLoad>" +
                "</SimBase.Document>",
                new UTF8Encoding(false),
                Array.Empty<byte>());
        }
    }

    private sealed class ConfigurationMutation
    {
        public ConfigurationMutation(string path, byte[]? originalBytes, byte[]? updatedBytes, bool changed)
        {
            Path = path;
            OriginalBytes = originalBytes;
            UpdatedBytes = updatedBytes;
            Changed = changed;
        }

        public string Path { get; }
        public byte[]? OriginalBytes { get; }
        public byte[]? UpdatedBytes { get; }
        public bool Changed { get; }

        public static ConfigurationMutation Unchanged(string path, byte[]? originalBytes)
        {
            return new ConfigurationMutation(path, originalBytes, null, false);
        }
    }
}

internal sealed class AutoStartOperationResult
{
    public AutoStartOperationResult(bool enabled, IReadOnlyList<string> configurationPaths, IReadOnlyList<string> changedPaths)
    {
        Enabled = enabled;
        ConfigurationPaths = configurationPaths;
        ChangedPaths = changedPaths;
    }

    public bool Enabled { get; }
    public IReadOnlyList<string> ConfigurationPaths { get; }
    public IReadOnlyList<string> ChangedPaths { get; }
}
