using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class MsfsControlProfileRegressionTests
{
    private static readonly Guid Stick = new Guid("11111111-1111-1111-1111-111111111111");
    private const string Aircraft = @"SimObjects\Airplanes\Test\presets\maker\variant\config";
    private const string GeneralKey = "ae7f5b37a56a781c", CategoryKey = "8b57821791389404", SpecificKey = "6bc4c835749736a7";

    internal static void RunAll()
    {
        LayersAndGeneralFallback();
        SharedThrottleAndAxes();
        LocalizedAxisIds();
        SimulatorVersionAndAccountDiscovery();
        AmbiguousAndConditionalInputs();
        IdentityAndMissingFiles();
        MalformedSelectionFails();
        AircraftIdentityIsExact();
        DiagnosticInventoryRetainsSelectedAndAvailableProfiles();
    }

    private static void DiagnosticInventoryRetainsSelectedAndAvailableProfiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "msfs-profile-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "profile_00"), Settings(new[] { Selection("inputprofile_1") }, Array.Empty<byte[]>(), Array.Empty<byte[]>()));
            File.WriteAllText(Path.Combine(directory, "inputprofile_1"), Xml(Action("KEY_AXIS_ELEVATOR_SET", "Joystick L-Axis Y")), Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "inputprofile_2"), Xml(Action("KEY_THROTTLE1_AXIS_SET_EX1", "Joystick R-Axis X", 132)), Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "inputprofile_3"), "<broken", Encoding.UTF8);
            var store = MsfsControlProfileStore.Load(directory);
            var evidence = store.ProfileEvidence;
            var packed = MsfsControlProfileEvidence.WithResolution(evidence, store.Resolve(Aircraft));
            using var json = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(packed), System.Xml.XmlDictionaryReaderQuotas.Max);
            var document = System.Xml.Linq.XDocument.Load(json).Root!;
            using var compressed = new MemoryStream(Convert.FromBase64String(document.Element("storeDeflateBase64")!.Value));
            using var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            using var decoded = new MemoryStream(); deflate.CopyTo(decoded);
            Require(Encoding.UTF8.GetString(decoded.ToArray()) == evidence && MsfsControlProfileEvidence.Hash(decoded.ToArray()) == document.Element("storeSha256")!.Value,
                "Compressed diagnostic evidence changed the complete source snapshot.");
            Require(evidence.Contains("inputprofile_1") && evidence.Contains("inputprofile_2") && evidence.Contains("inputprofile_3"), "Evidence omitted an available profile.");
            Require(evidence.Contains("KEY_AXIS_ELEVATOR_SET") && evidence.Contains("KEY_THROTTLE1_AXIS_SET_EX1") && evidence.Contains(Stick.ToString("D")), "Evidence lost device identity or assignments.");
            Require(evidence.Contains("ReadForSelection\":true") && evidence.Contains("ReadForSelection\":false") && evidence.Contains("failed"), "Selected, available and unreadable profiles were not distinguished.");
            Require(evidence.Contains("SelectionDeflateBase64") && evidence.Contains("SourceDeflateBase64") && evidence.Contains("SelectionSha256"), "Original source material was not retained for re-analysis.");
            File.WriteAllBytes(Path.Combine(directory, "profile_00"), new byte[] { 1, 2, 3 });
            var broken = MsfsControlProfileStore.Load(directory);
            Require(broken.ProfileEvidence.Contains("inputprofile_2") && broken.Diagnostics.Count > 0, "An unreadable selector hid the available profiles.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void LayersAndGeneralFallback()
    {
        var profiles = new Dictionary<string, string>
        {
            ["inputprofile_1"] = Xml(Action("KEY_AXIS_ELEVATOR_SET", "Joystick L-Axis Y") +
                Action("KEY_AXIS_AILERONS_SET", "Joystick L-Axis X")),
            ["inputprofile_2"] = Xml(Action("KEY_AXIS_ELEVATOR_SET", "Joystick R-Axis X")),
            ["inputprofile_3"] = Xml(Action("KEY_AXIS_ELEVATOR_SET", "Joystick Slider Y", 132))
        };
        var reads = 0;
        var store = MsfsControlProfileStore.Parse(Settings(
            new[] { Selection("inputprofile_1") }, new[] { Selection("inputprofile_2", category: "AIRPLANE") },
            new[] { Air(Selection("inputprofile_1", key: GeneralKey), Selection("inputprofile_2", key: CategoryKey),
                Selection("inputprofile_3", key: SpecificKey)) }), name => { reads++; return profiles[name]; });
        var result = store.Resolve(Aircraft, "Airplane");
        Require(result.MatchedAircraft, "The saved aircraft preset must match.");
        Require(result.Bindings.Single(b => b.Control == MsfsControlKind.Pitch).AxisIndex == 7, "Aircraft assignment must take precedence.");
        Require(result.Bindings.Single(b => b.Control == MsfsControlKind.Pitch).Inverted, "Axis inversion must be retained.");
        Require(result.Bindings.Single(b => b.Control == MsfsControlKind.Roll).Layer == MsfsControlProfileLayer.General, "An unassigned control must retain its general binding.");
        store.Resolve(Aircraft, "Airplane");
        Require(reads == 3, "Resolving an immutable snapshot must not reread profile files.");
        var fallback = store.Resolve(@"SimObjects\Airplanes\Different\config", "Airplane");
        Require(!fallback.MatchedAircraft && fallback.Bindings.Single(b => b.Control == MsfsControlKind.Pitch).AxisIndex == 3,
            "Unseen aircraft must use the selected category profile before general.");
        var general = store.Resolve("", "HELICOPTER");
        Require(general.Bindings.Single(b => b.Control == MsfsControlKind.Pitch).AxisIndex == 1, "General assignment must work without an aircraft/category assignment.");

        var disabled = MsfsControlProfileStore.Parse(Settings(new[] { Selection("inputprofile_1") },
            new[] { Selection("inputprofile_2", category: "AIRPLANE") },
            new[] { Air(Selection("inputprofile_1", key: GeneralKey), Selection("", kind: 0, key: CategoryKey)) }), name => profiles[name]);
        Require(disabled.Resolve(Aircraft).Bindings.Single(b => b.Control == MsfsControlKind.Pitch).AxisIndex == 1,
            "Explicit per-aircraft None must not resurrect the global category selection.");
    }

    private static void SharedThrottleAndAxes()
    {
        var actions = Action("KEY_THROTTLE1_AXIS_SET_EX1", "Joystick R-Axis X", 132) +
            Action("KEY_THROTTLE2_AXIS_SET_EX1", "Joystick R-Axis X", 132) +
            Action("KEY_THROTTLE3_AXIS_SET_EX1", "Joystick R-Axis Y", 132) +
            Action("KEY_THROTTLE4_AXIS_SET_EX1", "Joystick R-Axis Y", 132);
        var shared = Single(actions).Resolve(Aircraft).Bindings;
        Require(shared.Count == 4 && shared.Where(b => b.EngineIndex < 2).All(b => b.AxisIndex == 3) &&
            shared.Where(b => b.EngineIndex >= 2).All(b => b.AxisIndex == 4), "One lever may directly control several engines.");
        var all = Single(Action("KEY_AXIS_THROTTLE_SET", "Joystick Slider X")).Resolve(Aircraft).Bindings;
        Require(all.Count == 4 && all.All(b => b.AxisIndex == 6), "The all-engine throttle event must map every engine to its shared axis.");
        var sources = new[] { "Joystick L-Axis X", "Joystick L-Axis Y", "Joystick L-Axis Z", "Joystick R-Axis X",
            "Joystick R-Axis Y", "Joystick R-Axis Z", "Joystick Slider X", "Joystick Slider Y" };
        for (var i = 0; i < sources.Length; i++)
            Require(Single(Action("KEY_AXIS_ELEVATOR_SET", sources[i])).Resolve(Aircraft).Bindings.Single().AxisIndex == i,
                "Every semantic DirectInput axis must retain its identity.");
    }

    private static void AmbiguousAndConditionalInputs()
    {
        var ambiguous = Single("<Action ActionName=\"KEY_AXIS_ELEVATOR_SET\" Flag=\"4\"><Primary><KEY Information=\"Joystick L-Axis Y\">1042</KEY></Primary>" +
            "<Secondary><KEY Information=\"Joystick R-Axis X\">770</KEY></Secondary></Action>").Resolve(Aircraft);
        Require(ambiguous.Bindings.Count == 2, "Multiple assigned axes must remain ambiguous, not silently choose the primary.");
        var chord = Single("<Action ActionName=\"KEY_AXIS_ELEVATOR_SET\" Flag=\"4\"><Primary><KEY Information=\"Joystick L-Axis Y\">1042</KEY>" +
            "<KEY Information=\"Button 1\">0</KEY></Primary></Action>").Resolve(Aircraft);
        Require(chord.Bindings.Count == 0 && chord.Diagnostics.Any(d => d.Contains("conditional")), "Button-conditioned axes require fallback.");
        var modified = Single(Action("KEY_AXIS_ELEVATOR_SET", "Joystick L-Axis Y", 20)).Resolve(Aircraft);
        Require(modified.Bindings.Count == 0 && modified.Diagnostics.Any(d => d.Contains("Conditional")), "Modifier-conditioned axes require fallback.");
        var unknown = Single(Action("KEY_AXIS_ELEVATOR_SET", "Unknown device input")).Resolve(Aircraft);
        Require(unknown.Bindings.Count == 0 && unknown.Diagnostics.Any(d => d.Contains("Unsupported")), "Unknown physical inputs require fallback.");
    }

    private static void IdentityAndMissingFiles()
    {
        var mismatch = MsfsControlProfileStore.Parse(Settings(new[] { Selection("inputprofile_1") }),
            _ => Xml(Action("KEY_AXIS_ELEVATOR_SET", "Joystick L-Axis Y"), Guid.NewGuid())).Resolve(Aircraft);
        Require(mismatch.Bindings.Count == 0 && mismatch.Diagnostics.Any(d => d.Contains("different device instance")), "Identical product names must not bridge different instance GUIDs.");
        var missing = MsfsControlProfileStore.Parse(Settings(new[] { Selection("inputprofile_1") }),
            _ => throw new FileNotFoundException("missing")).Resolve(Aircraft);
        Require(missing.Bindings.Count == 0 && missing.Diagnostics.Any(d => d.Contains("Could not read")), "Missing selected profiles must report an explicit fallback reason.");
        var badXml = MsfsControlProfileStore.Parse(Settings(new[] { Selection("inputprofile_1") }), _ => "<Device").Resolve(Aircraft);
        Require(badXml.Bindings.Count == 0 && badXml.Diagnostics.Count > 0, "Malformed XML must not break recording.");
        var defaultProfile = MsfsControlProfileStore.Parse(Settings(new[] { Selection("", kind: 1) }), _ => throw new Exception("No custom file should be read")).Resolve(Aircraft);
        Require(defaultProfile.Bindings.Count == 0 && defaultProfile.Diagnostics.Any(d => d.Contains("Built-in")), "Unsupported default profile must retain its reason.");
    }

    private static void MalformedSelectionFails()
    {
        var bytes = Settings(new[] { Selection("inputprofile_1") }); bytes[8] = 1; bytes[9] = bytes[10] = bytes[11] = 0;
        try { MsfsControlProfileStore.Parse(bytes, _ => ""); throw new Exception("Malformed saved structure was accepted."); }
        catch (InvalidDataException) { }
        var path = Path.Combine(Path.GetTempPath(), "msfs-profile-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllBytes(Path.Combine(path, "profile_00"), bytes);
            var result = MsfsControlProfileStore.Load(path).Resolve(Aircraft);
            Require(result.Bindings.Count == 0 && result.Diagnostics.Any(d => d.Contains("unavailable")), "Load must contain a corrupt saved-selection failure.");
        }
        finally { Directory.Delete(path, true); }
    }

    private static void AircraftIdentityIsExact()
    {
        var store = MsfsControlProfileStore.Parse(Settings(Array.Empty<byte[]>(), aircraft: new[] {
            Air(category: Selection("inputprofile_1", key: CategoryKey)) }), _ => Xml(Action("KEY_AXIS_ELEVATOR_SET", "Joystick L-Axis Y")));
        Require(store.Resolve(@"D:\packages\test\" + Aircraft + @"\aircraft.cfg").MatchedAircraft, "Absolute loaded config paths must match the exact VFS preset.");
        Require(store.Resolve(Aircraft + @"\test.air").MatchedAircraft, "AircraftLoaded .AIR filename must resolve its parent configuration directory.");
        Require(!store.Resolve(@"SimObjects\Airplanes\Test\presets\maker\other\config").MatchedAircraft, "A similar aircraft name must never select a different preset.");
    }

    private static MsfsControlProfileStore Single(string actions) => MsfsControlProfileStore.Parse(
        Settings(new[] { Selection("inputprofile_1") }), _ => Xml(actions));
    internal static MsfsControlProfileStore StoreFixture(string directory, string pitchAxis)
    {
        File.WriteAllBytes(Path.Combine(directory, "profile_00"), Settings(new[] { Selection("inputprofile_1") }));
        File.WriteAllText(Path.Combine(directory, "inputprofile_1"), Xml(Action("KEY_AXIS_ELEVATOR_SET", pitchAxis)), Encoding.UTF8);
        return MsfsControlProfileStore.Load(directory);
    }
    private static string Action(string name, string axis, int flag = 4, int? key = null) =>
        "<Action ActionName=\"" + name + "\" Flag=\"" + flag + "\"><Primary><KEY Information=\"" + axis + "\">" + (key ?? AxisKey(axis)) + "</KEY></Primary></Action>";
    private static int AxisKey(string label) => label switch
    {
        "Joystick L-Axis X" => 1026, "Joystick L-Axis Y" => 1042, "Joystick L-Axis Z" => 1058,
        "Joystick R-Axis X" => 770, "Joystick R-Axis Y" => 786, "Joystick R-Axis Z" => 802,
        "Joystick Slider X" => 514, "Joystick Slider Y" => 530, _ => -1
    };

    internal static MsfsControlProfileResolution LocalizedThrottleFixture() => Single(
        Action("KEY_THROTTLE1_AXIS_SET_EX1", "Джойстик: ось вращения X", 132, 770) +
        Action("KEY_THROTTLE2_AXIS_SET_EX1", "Джойстик: ось вращения Y", 4, 786)).Resolve(Aircraft);

    private static void LocalizedAxisIds()
    {
        // Full-axis IDs from the MSFS Input Profile Editor and Asobo profiles.
        // Localized, absent and misleading labels must not change their meaning.
        var keys = new[] { 1026, 1042, 1058, 770, 786, 802, 514, 530 };
        foreach (var label in new[] { "Ось джойстика", "Axe de manette", "Joystick-Achse", "ジョイスティック軸", "Joystick L-Axis Y", "" })
        for (var axis = 0; axis < keys.Length; axis++)
        foreach (var inverted in new[] { false, true })
        {
            var binding = Single(Action("KEY_AXIS_ELEVATOR_SET", label, inverted ? 132 : 4, keys[axis])).Resolve(Aircraft).Bindings.Single();
            Require(binding.AxisIndex == axis && binding.DeviceInstanceId == Stick && binding.Inverted == inverted,
                "A display label changed the physical axis, inversion or device identity.");
        }
        foreach (var key in new[] { 0, 1, 768, 769, 771, 1024, 1025, 1027, 9999, -1 })
            Require(Single(Action("KEY_AXIS_ELEVATOR_SET", "Joystick L-Axis Y", key: key)).Resolve(Aircraft).Bindings.Count == 0,
                "An unsupported button, split axis or malformed ID was accepted using its display label.");
        foreach (var key in new[] { "", "1042.0", "not-a-number", "2147483648", "<KEY>1042</KEY>" })
            Require(Single(Action("KEY_AXIS_ELEVATOR_SET", "Joystick L-Axis Y").Replace(">1042</KEY>", ">" + key + "</KEY>")).Resolve(Aircraft).Bindings.Count == 0,
                "Malformed physical KEY was accepted.");
        var throttles = LocalizedThrottleFixture().Bindings;
        Require(throttles.Count == 2 && throttles.Single(b => b.EngineIndex == 0).AxisIndex == 3 &&
            throttles.Single(b => b.EngineIndex == 1).AxisIndex == 4, "Localized Rx and Ry throttle assignments were conflated.");
    }

    private static void SimulatorVersionAndAccountDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "msfs-profile-discovery-" + Guid.NewGuid().ToString("N"));
        string Folder(string account, string app) => Path.Combine(root, "userdata", account, app, "remote");
        void Add(string account, string app, string axis)
        { var folder = Folder(account, app); Directory.CreateDirectory(folder); StoreFixture(folder, axis); }
        try
        {
            Add("1", "2537590", "Joystick R-Axis X");
            Require(MsfsControlProfileStore.DiscoverSteam(12, root, 0).DirectoryPath == Folder("1", "2537590"), "A single installed version was not discovered.");
            Require(MsfsControlProfileStore.DiscoverSteam(11, root, 1).Resolve(Aircraft).Bindings.Count == 0, "Missing MSFS 2020 borrowed MSFS 2024 assignments.");
            Add("1", "1250410", "Joystick L-Axis Y");
            foreach (var account in new uint[] { 0, 1 })
            {
                Require(MsfsControlProfileStore.DiscoverSteam(11, root, account).DirectoryPath == Folder("1", "1250410"), "Application version 11 did not select MSFS 2020.");
                Require(MsfsControlProfileStore.DiscoverSteam(12, root, account).DirectoryPath == Folder("1", "2537590"), "Application version 12 did not select MSFS 2024.");
            }
            Add("2", "2537590", "Joystick R-Axis Y");
            var ambiguous = MsfsControlProfileStore.DiscoverSteam(12, root, 0);
            Require(ambiguous.Resolve(Aircraft).Bindings.Count == 0 && ambiguous.Diagnostics.Any(d => d.Contains("ambiguous")), "Unknown active account selected an arbitrary store.");
            Require(MsfsControlProfileStore.DiscoverSteam(12, root, 2).Resolve(Aircraft).Bindings.Single().AxisIndex == 4, "Active Steam account was ignored.");
            Require(MsfsControlProfileStore.DiscoverSteam(12, root, 3).Resolve(Aircraft).Bindings.Count == 0, "Missing active account borrowed another account's profile.");
            foreach (var version in new uint[] { 0, 10, 13, uint.MaxValue })
                Require(MsfsControlProfileStore.DiscoverSteam(version, root, 1).Resolve(Aircraft).Bindings.Count == 0, "Unknown application version selected an installed profile.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Xml(string actions, Guid? device = null) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Version Num=\"-1\"/><FriendlyName>Arbitrary display name</FriendlyName>" +
        "<Device GUID=\"" + (device ?? Stick) + "\" ProductID=\"123\"><Context ContextName=\"AIRCRAFT\">" + actions + "</Context></Device>";
    private static byte[] Settings(byte[][] general, byte[][]? category = null, byte[][]? aircraft = null) =>
        Container("94438d991bb0815f", Container("a532ec6925941962",
            Container("a46c8bf66a7db8ce", general), Container("4f1fd58aaa70b44e", category ?? Array.Empty<byte[]>()),
            Container("7af6b80fdb235d3f"), Container("a9281cff465213e7", aircraft ?? Array.Empty<byte[]>())));
    private static byte[] Selection(string file, int kind = 3, string category = "", string key = "0000000000000000") =>
        Container(key, String("e494a07827809546", Stick.ToString("B")), Field("00d10becc027d54f", BitConverter.GetBytes(kind)),
            String("e5374a443ee0986e", file), String("dd0e07ae6ad0a4fc", category));
    private static byte[] Air(byte[]? general = null, byte[]? category = null, byte[]? specific = null) =>
        Container("0000000000000000", new[] { String("e494a07827809546", Stick.ToString("B")), String("a8df1050ac9f14e5", Aircraft),
            String("6ef48b11820c125d", "test-package") }.Concat(new[] { general, category, specific }.Where(v => v != null).Cast<byte[]>()).ToArray());
    private static byte[] String(string key, string value) => Field(key, Encoding.UTF8.GetBytes(value + "\0"));
    private static byte[] Container(string key, params byte[][] children) => Field(key,
        BitConverter.GetBytes(children.Length).Concat(children.SelectMany(v => v)).ToArray());
    private static byte[] Field(string key, byte[] payload) => Enumerable.Range(0, 8).Select(i => Convert.ToByte(key.Substring(i * 2, 2), 16))
        .Concat(BitConverter.GetBytes(payload.Length + 12)).Concat(payload).ToArray();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
