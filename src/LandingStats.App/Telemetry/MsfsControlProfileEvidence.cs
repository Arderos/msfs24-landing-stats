using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LandingStats.App.Telemetry;

/// <summary>One diagnostic snapshot per profile read, retained with recordings for independent re-analysis.</summary>
internal static class MsfsControlProfileEvidence
{
    internal static string Capture(string directory, byte[] selections, IReadOnlyDictionary<string, string> readProfiles,
        IReadOnlyList<string> diagnostics, SelectionInfo[] selectionsRead)
    {
        var files = new List<ProfileFile>();
        var errors = diagnostics.ToList();
        string[] paths;
        try { paths = Directory.GetFiles(directory, "inputprofile_*"); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
        { paths = Array.Empty<string>(); errors.Add("Profile inventory unavailable: " + error.Message); }
        foreach (var path in paths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path); var selected = readProfiles.TryGetValue(name, out var xml);
            var file = new ProfileFile { File = name, ReadForSelection = selected };
            try
            {
                xml ??= ReadText(path);
                var bytes = Encoding.UTF8.GetBytes(xml);
                file.Sha256 = Hash(bytes); file.SourceDeflateBase64 = Compress(bytes);
                var content = xml.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                if (content.StartsWith("<?xml", StringComparison.Ordinal))
                { var end = content.IndexOf("?>", StringComparison.Ordinal); if (end < 0) throw new XmlException("Incomplete XML declaration."); content = content.Substring(end + 2); }
                using var text = new StringReader("<Root>" + content + "</Root>");
                using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var root = XDocument.Load(reader).Root!;
                var device = root.Element("Device");
                file.Name = root.Element("FriendlyName")?.Value ?? "";
                file.DeviceName = (string?)device?.Attribute("DeviceName") ?? "";
                file.DeviceGuid = (string?)device?.Attribute("GUID") ?? "";
                file.ProductId = (string?)device?.Attribute("ProductID") ?? "";
                file.CompositeId = (string?)device?.Attribute("CompositeID") ?? "";
                file.AircraftInfo = device?.Element("AircraftInfo")?.ToString(SaveOptions.DisableFormatting) ?? "";
                file.AxisSettings = device?.Element("Axes")?.ToString(SaveOptions.DisableFormatting) ?? "";
                file.Assignments = device?.Descendants("Action").Where(action => action.Descendants("KEY").Any() &&
                    ((int.TryParse((string?)action.Attribute("Flag"), out var flags) && (flags & 4) != 0) ||
                     action.Descendants("KEY").Any(key => ((string?)key.Attribute("Information") ?? "").IndexOf("Axis", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         ((string?)key.Attribute("Information") ?? "").IndexOf("Slider", StringComparison.OrdinalIgnoreCase) >= 0)))
                    .Select(action => new Assignment { Context = (string?)action.Parent?.Attribute("ContextName") ?? "",
                        Action = (string?)action.Attribute("ActionName") ?? "", Xml = action.ToString(SaveOptions.DisableFormatting) }).ToArray() ?? Array.Empty<Assignment>();
                file.Status = "read";
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is XmlException || error is ArgumentException)
            { file.Status = "failed"; file.Error = error.GetType().Name + ": " + error.Message; }
            files.Add(file);
        }
        return Serialize(new Snapshot { CapturedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Store = directory, SelectionSha256 = Hash(selections), SelectionDeflateBase64 = Compress(selections),
            Profiles = files.ToArray(), Selections = selectionsRead, Diagnostics = errors.ToArray() });
    }

    internal static string WithResolution(string snapshot, MsfsControlProfileResolution resolution)
    {
        var detail = Serialize(new Resolution { AircraftPath = resolution.AircraftPath, MatchedAircraft = resolution.MatchedAircraft,
            Diagnostics = resolution.Diagnostics.ToArray(), Bindings = resolution.Bindings.Select(binding => new ResolvedBinding
            { DeviceGuid = binding.DeviceInstanceId.ToString("D"), AxisIndex = binding.AxisIndex, Control = binding.Control.ToString(),
                EngineIndex = binding.EngineIndex, Inverted = binding.Inverted, ProfileFile = binding.ProfileFile,
                ProfileName = binding.ProfileName, Layer = binding.Layer.ToString(), Action = binding.ActionName }).ToArray() });
        // Large saved-aircraft inventories repeat paths and profile names thousands of times.
        // Compress the complete snapshot once; the resolution remains directly readable JSON.
        var bytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(snapshot) ? "null" : snapshot);
        return "{\"version\":1,\"storeEncoding\":\"deflate-base64\",\"storeBytes\":" + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            ",\"storeSha256\":\"" + Hash(bytes) + "\",\"storeDeflateBase64\":\"" + Compress(bytes) + "\",\"resolution\":" + detail + "}";
    }

    internal static string Failure(string reason, string evidence) => Serialize(new FailureSnapshot
    { Reason = reason, EvidenceBytes = Encoding.UTF8.GetByteCount(evidence), EvidenceSha256 = Hash(Encoding.UTF8.GetBytes(evidence)) });

    internal static string Hash(byte[] bytes)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static string Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true)) deflate.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }
    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Input profile exceeds the reader's file byte budget.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true); return reader.ReadToEnd();
    }
    private static string Serialize<T>(T value)
    { using var stream = new MemoryStream(); new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); }

    [DataContract] private sealed class FailureSnapshot
    {
        [DataMember] public string Reason = "";
        [DataMember] public int EvidenceBytes;
        [DataMember] public string EvidenceSha256 = "";
    }
    [DataContract] private sealed class Snapshot
    {
        [DataMember] public string CapturedUtc = "";
        [DataMember] public string Store = "";
        [DataMember] public string SelectionSha256 = "";
        [DataMember] public string SelectionDeflateBase64 = "";
        [DataMember] public SelectionInfo[] Selections = Array.Empty<SelectionInfo>();
        [DataMember] public ProfileFile[] Profiles = Array.Empty<ProfileFile>();
        [DataMember] public string[] Diagnostics = Array.Empty<string>();
    }
    [DataContract] internal sealed class SelectionInfo
    {
        [DataMember] public string Scope = "";
        [DataMember] public string AircraftPath = "";
        [DataMember] public string DeviceGuid = "";
        [DataMember] public string Layer = "";
        [DataMember] public int Kind;
        [DataMember] public string ProfileFile = "";
        [DataMember] public string ProfileName = "";
        [DataMember] public string Category = "";
        [DataMember] public string Package = "";
    }
    [DataContract] private sealed class ProfileFile
    {
        [DataMember] public string File = "";
        [DataMember] public bool ReadForSelection;
        [DataMember] public string Status = "";
        [DataMember] public string Error = "";
        [DataMember] public string Name = "";
        [DataMember] public string DeviceName = "";
        [DataMember] public string DeviceGuid = "";
        [DataMember] public string ProductId = "";
        [DataMember] public string CompositeId = "";
        [DataMember] public string AircraftInfo = "";
        [DataMember] public string AxisSettings = "";
        [DataMember] public Assignment[] Assignments = Array.Empty<Assignment>();
        [DataMember] public string Sha256 = "";
        [DataMember] public string SourceDeflateBase64 = "";
    }
    [DataContract] private sealed class Assignment
    {
        [DataMember] public string Context = "";
        [DataMember] public string Action = "";
        [DataMember] public string Xml = "";
    }
    [DataContract] private sealed class Resolution
    {
        [DataMember] public string AircraftPath = "";
        [DataMember] public bool MatchedAircraft;
        [DataMember] public ResolvedBinding[] Bindings = Array.Empty<ResolvedBinding>();
        [DataMember] public string[] Diagnostics = Array.Empty<string>();
    }
    [DataContract] private sealed class ResolvedBinding
    {
        [DataMember] public string DeviceGuid = "";
        [DataMember] public int AxisIndex;
        [DataMember] public string Control = "";
        [DataMember] public int EngineIndex;
        [DataMember] public bool Inverted;
        [DataMember] public string ProfileFile = "";
        [DataMember] public string ProfileName = "";
        [DataMember] public string Layer = "";
        [DataMember] public string Action = "";
    }
}
