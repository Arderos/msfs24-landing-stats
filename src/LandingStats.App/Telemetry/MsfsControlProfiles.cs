using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace LandingStats.App.Telemetry;

internal enum MsfsControlKind { Pitch, Roll, Yaw, Throttle }
internal enum MsfsControlProfileLayer { General, Category, Aircraft }

internal sealed class MsfsControlProfileBinding
{
    internal MsfsControlProfileBinding(Guid device, int axis, MsfsControlKind control, int engine,
        bool inverted, string action, string file, string name, MsfsControlProfileLayer layer)
    {
        DeviceInstanceId = device; AxisIndex = axis; Control = control; EngineIndex = engine;
        Inverted = inverted; ActionName = action; ProfileFile = file; ProfileName = name; Layer = layer;
    }
    public Guid DeviceInstanceId { get; }
    // DirectInput semantic order: X, Y, Z, Rx, Ry, Rz, slider 0, slider 1.
    public int AxisIndex { get; }
    public MsfsControlKind Control { get; }
    public int EngineIndex { get; }
    public bool Inverted { get; }
    public string ActionName { get; }
    public string ProfileFile { get; }
    public string ProfileName { get; }
    public MsfsControlProfileLayer Layer { get; }
}

internal sealed class MsfsControlProfileResolution
{
    internal MsfsControlProfileResolution(string aircraftPath, bool matchedAircraft,
        IEnumerable<MsfsControlProfileBinding> bindings, IEnumerable<string> diagnostics)
    {
        AircraftPath = aircraftPath; MatchedAircraft = matchedAircraft;
        Bindings = Array.AsReadOnly(bindings.ToArray()); Diagnostics = Array.AsReadOnly(diagnostics.Distinct().ToArray());
    }
    public string AircraftPath { get; }
    public bool MatchedAircraft { get; }
    public IReadOnlyList<MsfsControlProfileBinding> Bindings { get; }
    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>Immutable read-only snapshot of saved MSFS selections and their referenced XML.
/// Display names never identify aircraft. Unsupported data leaves a diagnostic and correlation available.</summary>
internal sealed class MsfsControlProfileStore
{
    private readonly Selection[] _general, _category, _specific;
    private readonly AircraftSelection[] _aircraft;
    private readonly Dictionary<string, Profile> _profiles;
    private readonly string[] _diagnostics;
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private MsfsControlProfileStore(Selection[] general, Selection[] category, Selection[] specific,
        AircraftSelection[] aircraft, Dictionary<string, Profile> profiles, IEnumerable<string> diagnostics, string directory = "")
    {
        _general = general; _category = category; _specific = specific; _aircraft = aircraft;
        _profiles = profiles; _diagnostics = diagnostics.ToArray(); DirectoryPath = directory;
    }
    public IReadOnlyList<string> Diagnostics => Array.AsReadOnly(_diagnostics);
    public string ProfileEvidence { get; private set; } = "";
    public string DirectoryPath { get; private set; }

    public static MsfsControlProfileStore Load(string directory)
    {
        var selections = Array.Empty<byte>();
        var readProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MsfsControlProfileStore store;
        try
        {
            selections = ReadBounded(Path.Combine(directory, "profile_00"));
            store = Parse(selections, name =>
            {
                var xml = Encoding.UTF8.GetString(ReadBounded(Path.Combine(directory, name)));
                readProfiles[name] = xml; return xml;
            });
            store.DirectoryPath = directory;
        }
        catch (Exception error) when (IsReadError(error))
        { store = Unavailable("MSFS saved input selections are unavailable: " + error.Message, directory); }
        store.ProfileEvidence = MsfsControlProfileEvidence.Capture(directory, selections, readProfiles, store.Diagnostics, store.DescribeSelections());
        return store;
    }

    private MsfsControlProfileEvidence.SelectionInfo[] DescribeSelections()
    {
        var result = new List<MsfsControlProfileEvidence.SelectionInfo>();
        void Add(Selection? selection, string layer, string aircraft = "", Guid? device = null)
        {
            if (selection == null) return;
            result.Add(new MsfsControlProfileEvidence.SelectionInfo { Scope = aircraft.Length == 0 ? "global" : "aircraft",
                AircraftPath = aircraft, DeviceGuid = (device ?? selection.Device).ToString("D"), Layer = layer,
                Kind = selection.Kind, ProfileFile = selection.File, ProfileName = selection.Name,
                Category = selection.Category, Package = selection.Package });
        }
        foreach (var selection in _general) Add(selection, "General");
        foreach (var selection in _category) Add(selection, "Category");
        foreach (var selection in _specific) Add(selection, "Aircraft");
        foreach (var aircraft in _aircraft)
        { Add(aircraft.General, "General", aircraft.Path, aircraft.Device); Add(aircraft.Category, "Category", aircraft.Path, aircraft.Device); Add(aircraft.Specific, "Aircraft", aircraft.Path, aircraft.Device); }
        return result.ToArray();
    }

    /// <summary>Locates the active Steam account. Ambiguous accounts are never resolved by timestamp.</summary>
    public static MsfsControlProfileStore Discover(string? simulator = null)
    {
        var diagnostics = new List<string>(); var candidates = new List<string>();
        var is2020 = simulator?.IndexOf("2020", StringComparison.OrdinalIgnoreCase) >= 0;
        var is2024 = simulator?.IndexOf("2024", StringComparison.OrdinalIgnoreCase) >= 0;
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = steam?.GetValue("SteamPath") as string;
            using var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var userValue = active?.GetValue("ActiveUser");
            var user = userValue is int number && number != 0 ? unchecked((uint)number).ToString(CultureInfo.InvariantCulture) : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var userdata = Path.Combine(path!, "userdata");
                if (Directory.Exists(userdata))
                {
                    var accounts = user == null ? Directory.GetDirectories(userdata) : new[] { Path.Combine(userdata, user) };
                    foreach (var account in accounts)
                    {
                        if (!is2020) Add(Path.Combine(account, "2537590", "remote"));
                        if (!is2024) Add(Path.Combine(account, "1250410", "remote"));
                    }
                }
            }
        }
        catch (Exception error) when (IsReadError(error)) { diagnostics.Add("Steam input-profile discovery failed: " + error.Message); }
        if (candidates.Count == 1) return Load(candidates[0]);
        if (candidates.Count > 1) diagnostics.Add("Multiple MSFS input-profile stores are present; the active simulator/account is ambiguous: " + string.Join("; ", candidates) + ".");
        else diagnostics.Add("No readable MSFS input-profile store was found. Store/WGS input selections require a supported container reader.");
        return Unavailable(string.Join(" ", diagnostics));
        void Add(string folder) { if (File.Exists(Path.Combine(folder, "profile_00"))) candidates.Add(folder); }
    }

    public MsfsControlProfileResolution Resolve(string? aircraftConfigPath, string? aircraftCategory = "AIRPLANE")
    {
        var diagnostics = new List<string>(_diagnostics);
        var path = NormalizeAircraftPath(aircraftConfigPath);
        var category = (aircraftCategory ?? "").Trim().ToUpperInvariant();
        var matches = path.Length == 0 ? Array.Empty<AircraftSelection>() : _aircraft.Where(a => a.Path == path).ToArray();
        var devices = _general.Concat(_category.Where(s => s.Category == category)).Select(s => s.Device)
            .Concat(matches.Select(m => m.Device)).Where(id => id != Guid.Empty).Distinct().ToArray();
        var bindings = new List<MsfsControlProfileBinding>();
        if (path.Length == 0) diagnostics.Add("The loaded aircraft configuration path is unavailable; only saved category/general selections can be resolved.");
        foreach (var device in devices)
        {
            var selected = matches.Where(m => m.Device == device).ToArray();
            if (selected.Length > 1 && selected.Select(s => s.Identity).Distinct().Count() > 1)
            { diagnostics.Add("Conflicting saved aircraft profile selections for device " + device + "."); continue; }
            var layers = new Selection?[3];
            if (selected.Length > 0)
            { layers[0] = selected[0].General; layers[1] = selected[0].Category; layers[2] = selected[0].Specific; }
            // Missing per-aircraft layers inherit saved category/general selections.
            // Explicit None remains None and never silently selects a different profile.
            layers[0] ??= Unique(_general.Where(s => s.Device == device), diagnostics, "general", device);
            layers[1] ??= Unique(_category.Where(s => s.Device == device && s.Category == category), diagnostics, "category", device);
            if (selected.Length > 0 && layers[2] == null)
                layers[2] = Unique(_specific.Where(s => s.Device == device && s.Package == selected[0].Package), diagnostics, "aircraft", device);
            var resolved = new Dictionary<string, List<MsfsControlProfileBinding>>(StringComparer.Ordinal);
            for (var layer = 0; layer < layers.Length; layer++)
            {
                var selection = layers[layer];
                if (selection == null || selection.Kind == 0) continue;
                if (selection.Kind != 3 || selection.File.Length == 0)
                { diagnostics.Add("Built-in or unsupported " + (MsfsControlProfileLayer)layer + " profile for device " + device + "; no explicit mapping was read from that layer."); continue; }
                if (!_profiles.TryGetValue(selection.File, out var profile))
                { diagnostics.Add("Selected MSFS profile could not be read: " + selection.File + "."); continue; }
                // Instance identity is authoritative; product IDs cannot distinguish identical controllers.
                if (profile.Device != device)
                { diagnostics.Add("Selected MSFS profile has a different device instance: " + selection.File + "."); continue; }
                foreach (var diagnostic in profile.Diagnostics) diagnostics.Add(selection.File + ": " + diagnostic);
                foreach (var group in profile.Bindings.GroupBy(b => ControlKey(b.Control, b.EngineIndex)))
                    resolved[group.Key] = group.Select(b => new MsfsControlProfileBinding(device, b.AxisIndex, b.Control,
                        b.EngineIndex, b.Inverted, b.ActionName, selection.File, profile.Name, (MsfsControlProfileLayer)layer)).ToList();
            }
            foreach (var group in resolved.Values)
                bindings.AddRange(group.GroupBy(b => b.AxisIndex.ToString(CultureInfo.InvariantCulture) + ":" + b.Inverted).Select(g => g.First()));
        }
        return new MsfsControlProfileResolution(path, matches.Length > 0, bindings, diagnostics);
    }

    internal static MsfsControlProfileStore Parse(byte[] selectionBytes, Func<string, string> readProfile)
    {
        var root = SavedNode.Read(selectionBytes, 0, selectionBytes.Length);
        var controls = root.Children().SingleOrDefault(n => n.Key == "a532ec6925941962");
        if (controls == null) throw new InvalidDataException("Unsupported MSFS saved input-selection structure.");
        var groups = controls.Children().ToDictionary(n => n.Key, StringComparer.Ordinal);
        Selection[] ReadSelections(string key) => groups.TryGetValue(key, out var list)
            ? list.Children().Select(Selection.Read).ToArray() : Array.Empty<Selection>();
        var general = ReadSelections("a46c8bf66a7db8ce");
        var category = ReadSelections("4f1fd58aaa70b44e");
        var specific = ReadSelections("7af6b80fdb235d3f");
        var aircraft = groups.TryGetValue("a9281cff465213e7", out var aircraftList)
            ? aircraftList.Children().Select(AircraftSelection.Read).ToArray() : Array.Empty<AircraftSelection>();
        var profiles = new Dictionary<string, Profile>(StringComparer.Ordinal); var diagnostics = new List<string>();
        var names = general.Concat(category).Concat(specific)
            .Concat(aircraft.SelectMany(a => new[] { a.General, a.Category, a.Specific }).Where(s => s != null).Cast<Selection>())
            .Where(s => s.Kind == 3).Select(s => s.File).Where(s => s.Length > 0).Distinct();
        foreach (var name in names)
        {
            if (!IsProfileFileName(name)) { diagnostics.Add("Invalid MSFS input-profile file reference: " + name + "."); continue; }
            try { profiles.Add(name, Profile.Parse(readProfile(name))); }
            catch (Exception error) when (IsReadError(error)) { diagnostics.Add("Could not read " + name + ": " + error.Message); }
        }
        return new MsfsControlProfileStore(general, category, specific, aircraft, profiles, diagnostics);
    }

    internal static string NormalizeAircraftPath(string? path)
    {
        var value = (path ?? "").Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var index = value.IndexOf("simobjects\\", StringComparison.Ordinal);
        if (index >= 0 && (index == 0 || value[index - 1] == '\\')) value = value.Substring(index);
        if (value.EndsWith("\\aircraft.cfg", StringComparison.Ordinal) || value.EndsWith(".air", StringComparison.Ordinal))
        { var separator = value.LastIndexOf('\\'); value = separator < 0 ? "" : value.Substring(0, separator); }
        return value;
    }
    private static Selection? Unique(IEnumerable<Selection> values, List<string> diagnostics, string layer, Guid device)
    {
        var distinct = values.GroupBy(s => s.Identity).Select(g => g.First()).ToArray();
        if (distinct.Length > 1) diagnostics.Add("Conflicting saved " + layer + " selections for device " + device + ".");
        return distinct.Length == 1 ? distinct[0] : null;
    }
    private static string ControlKey(MsfsControlKind control, int engine) => control + ":" + engine;
    private static bool IsProfileFileName(string value) => value.StartsWith("inputprofile_", StringComparison.Ordinal) &&
        value.Length > "inputprofile_".Length && value.Substring("inputprofile_".Length).All(c => c >= '0' && c <= '9');
    private static bool IsReadError(Exception error) => error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
        error is SecurityException || error is XmlException || error is ArgumentException || error is InvalidOperationException;
    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("MSFS input-profile file exceeds its size bound.");
        var bytes = new byte[checked((int)stream.Length)]; var offset = 0;
        while (offset < bytes.Length)
        { var read = stream.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
        return bytes;
    }
    private static MsfsControlProfileStore Unavailable(string diagnostic, string directory = "") => new MsfsControlProfileStore(
        Array.Empty<Selection>(), Array.Empty<Selection>(), Array.Empty<Selection>(), Array.Empty<AircraftSelection>(),
        new Dictionary<string, Profile>(StringComparer.Ordinal), new[] { diagnostic }, directory);

    private sealed class Selection
    {
        internal Guid Device;
        internal int Kind;
        internal string File = "", Name = "", Category = "", Package = "";
        internal string Identity => Kind + ":" + File;
        internal static Selection Read(SavedNode node)
        {
            var fields = node.Children().ToDictionary(n => n.Key, StringComparer.Ordinal);
            Guid.TryParse(Text(fields, "e494a07827809546"), out var guid);
            return new Selection { Device = guid, Kind = fields.TryGetValue("00d10becc027d54f", out var kind) ? kind.Int32() : -1,
                File = Text(fields, "e5374a443ee0986e"), Name = Text(fields, "877ddf5aa523fd04"),
                Category = Text(fields, "dd0e07ae6ad0a4fc").ToUpperInvariant(), Package = Text(fields, "6ef48b11820c125d") };
        }
    }
    private sealed class AircraftSelection
    {
        internal Guid Device;
        internal string Path = "", Package = "";
        internal Selection? General, Category, Specific;
        internal string Identity => Package + ":" + General?.Identity + ":" + Category?.Identity + ":" + Specific?.Identity;
        internal static AircraftSelection Read(SavedNode node)
        {
            var fields = node.Children().ToDictionary(n => n.Key, StringComparer.Ordinal);
            Guid.TryParse(Text(fields, "e494a07827809546"), out var device);
            return new AircraftSelection { Device = device, Path = NormalizeAircraftPath(Text(fields, "a8df1050ac9f14e5")),
                Package = Text(fields, "6ef48b11820c125d"), General = ReadLayer("ae7f5b37a56a781c"),
                Category = ReadLayer("8b57821791389404"), Specific = ReadLayer("6bc4c835749736a7") };
            Selection? ReadLayer(string key) => fields.TryGetValue(key, out var layer) ? Selection.Read(layer) : null;
        }
    }
    private static string Text(Dictionary<string, SavedNode> fields, string key) => fields.TryGetValue(key, out var node) ? node.Text() : "";

    // Saved-settings tags are not an SDK contract. Validate parent/child lengths and field types;
    // a changed format must leave correlation available, never invent an explicit mapping.
    private sealed class SavedNode
    {
        private readonly byte[] _bytes; private readonly int _start, _end;
        private SavedNode(byte[] bytes, int start, int end) { _bytes = bytes; _start = start; _end = end; }
        internal string Key => BitConverter.ToString(_bytes, _start, 8).Replace("-", "").ToLowerInvariant();
        internal static SavedNode Read(byte[] bytes, int start, int end)
        {
            if (start < 0 || end > bytes.Length || start > end - 12) throw new InvalidDataException("Truncated MSFS selection node.");
            var size = BitConverter.ToUInt32(bytes, start + 8);
            if (size < 12 || size > end - start) throw new InvalidDataException("Invalid MSFS selection node length.");
            return new SavedNode(bytes, start, start + (int)size);
        }
        internal SavedNode[] Children()
        {
            if (_end - _start < 16) throw new InvalidDataException("Truncated MSFS selection container.");
            var count = BitConverter.ToUInt32(_bytes, _start + 12);
            if (count > (_end - _start - 16) / 12) throw new InvalidDataException("Invalid MSFS selection child count.");
            var children = new SavedNode[(int)count]; var offset = _start + 16;
            for (var i = 0; i < children.Length; i++) { children[i] = Read(_bytes, offset, _end); offset = children[i]._end; }
            if (offset != _end) throw new InvalidDataException("Unexpected MSFS selection container suffix.");
            return children;
        }
        internal int Int32()
        { if (_end - _start != 16) throw new InvalidDataException("Invalid MSFS selection integer."); return BitConverter.ToInt32(_bytes, _start + 12); }
        internal string Text()
        {
            var length = _end - _start - 12;
            if (length == 4 && BitConverter.ToUInt32(_bytes, _start + 12) == 0) return "";
            if (length == 0 || _bytes[_end - 1] != 0) throw new InvalidDataException("Invalid MSFS selection string.");
            var text = new UTF8Encoding(false, true).GetString(_bytes, _start + 12, length - 1);
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid embedded terminator in MSFS selection string.");
            return text;
        }
    }
    private sealed class Profile
    {
        internal Guid Device; internal string Name = "";
        internal readonly List<MsfsControlProfileBinding> Bindings = new List<MsfsControlProfileBinding>();
        internal readonly List<string> Diagnostics = new List<string>();
        internal static Profile Parse(string xml)
        {
            xml = xml.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (xml.StartsWith("<?xml", StringComparison.Ordinal))
            { var end = xml.IndexOf("?>", StringComparison.Ordinal); if (end < 0) throw new XmlException("Incomplete XML declaration."); xml = xml.Substring(end + 2); }
            using var text = new StringReader("<Profiles>" + xml + "</Profiles>");
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader).Root!; var device = root.Descendants("Device").Single();
            var identity = (string?)device.Attribute("GUID");
            if (!Guid.TryParse(identity, out var guid) && identity != "{0}") throw new InvalidDataException("Invalid profile device GUID.");
            var result = new Profile { Device = guid, Name = root.Descendants("FriendlyName").FirstOrDefault()?.Value ?? "" };
            foreach (var context in device.Elements("Context"))
            foreach (var action in context.Elements("Action"))
            {
                var actionName = (string?)action.Attribute("ActionName") ?? "";
                if (!TryControl(actionName, out var control, out var engines)) continue;
                var assignments = action.Elements().Where(e => e.Name == "Primary" || e.Name == "Secondary").Where(e => e.Elements("KEY").Any()).ToArray();
                if (assignments.Length == 0) continue;
                if (!int.TryParse((string?)action.Attribute("Flag"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags) ||
                    (flags & 4) == 0 || (flags & (16 | 32 | 64 | 256 | 512 | 8192 | 16384)) != 0)
                { result.Diagnostics.Add("Conditional or non-axis assignment cannot identify a physical axis: " + actionName + "."); continue; }
                foreach (var assignment in assignments)
                {
                    var keys = assignment.Elements("KEY").ToArray();
                    if (keys.Length != 1 || !TryAxis(keys[0], out var axis))
                    { result.Diagnostics.Add("Unsupported or conditional physical input: " + actionName + "."); continue; }
                    foreach (var engine in engines)
                        result.Bindings.Add(new MsfsControlProfileBinding(guid, axis, control, engine, (flags & 128) != 0,
                            actionName, "", result.Name, MsfsControlProfileLayer.General));
                }
            }
            return result;
        }
    }
    private static bool TryControl(string name, out MsfsControlKind control, out int[] engines)
    {
        control = MsfsControlKind.Pitch; engines = new[] { -1 };
        switch (name)
        {
            case "KEY_AXIS_ELEVATOR_SET": case "KEY_ELEVATOR_SET": return true;
            case "KEY_AXIS_AILERONS_SET": case "KEY_AILERON_SET": case "KEY_AILERONS_SET": control = MsfsControlKind.Roll; return true;
            case "KEY_AXIS_RUDDER_SET": case "KEY_RUDDER_SET": control = MsfsControlKind.Yaw; return true;
            case "KEY_AXIS_THROTTLE_SET": case "KEY_THROTTLE_AXIS_SET_EX1": case "KEY_THROTTLE_SET":
                control = MsfsControlKind.Throttle; engines = new[] { 0, 1, 2, 3 }; return true;
        }
        for (var engine = 1; engine <= 4; engine++)
            if (name == "KEY_AXIS_THROTTLE" + engine + "_SET" || name == "KEY_THROTTLE" + engine + "_AXIS_SET_EX1" || name == "KEY_THROTTLE" + engine + "_SET")
            { control = MsfsControlKind.Throttle; engines = new[] { engine - 1 }; return true; }
        return false;
    }
    private static bool TryAxis(XElement key, out int axis)
    {
        // Information names the physical input. Numeric KEY is a simulator identifier,
        // not a DirectInput object ID (rY is 786 in MSFS, not DirectInput's 1026).
        var information = ((string?)key.Attribute("Information") ?? "").Trim();
        var names = new[] { "Joystick L-Axis X", "Joystick L-Axis Y", "Joystick L-Axis Z", "Joystick R-Axis X",
            "Joystick R-Axis Y", "Joystick R-Axis Z", "Joystick Slider X", "Joystick Slider Y" };
        axis = Array.FindIndex(names, n => string.Equals(n, information, StringComparison.OrdinalIgnoreCase));
        return axis >= 0;
    }
}
