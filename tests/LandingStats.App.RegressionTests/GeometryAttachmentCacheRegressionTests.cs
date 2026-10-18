using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class GeometryAttachmentCacheRegressionTests
{
    internal static void RunAll()
    {
        foreach (var inheritance in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "ls-cfg-" + Guid.NewGuid().ToString("N"));
            try
            {
                var packages = Path.Combine(root, "p");
                var plane = Path.Combine(packages, "pkg", "SimObjects", "Airplanes", "test");
                var preset = Path.Combine(plane, "presets", "default", "config");
                var attachment = Path.Combine(preset, "attached_objects.cfg");
                var inherited = Path.Combine(plane, "attachments", "vendor", "gear", "attachment.cfg");
                var cache = Path.Combine(root, "c");
                Write(Path.Combine(preset, "aircraft.cfg"), "[FLTSIM.0]\ntitle=Cache test\nisUserSelectable=1\n");
                Write(attachment, Attached(inheritance ? "gear" : "gearA"));
                if (inheritance) Write(inherited, Inherits("gearA"));
                Write(Path.Combine(plane, "attachments", "vendor", "gearA", "config", "flight_model.cfg"), Gear(-10));
                Write(Path.Combine(plane, "attachments", "vendor", "gearB", "config", "flight_model.cfg"), Gear(-30));
                Check(Resolve(packages, cache) == -10, "Initial attachment geometry was wrong.");
                Check(Resolve(packages, cache) == -10, "Warm persistent cache changed geometry.");
                var dependency = inheritance ? inherited : attachment;
                var parent = Path.GetDirectoryName(dependency)!;
                var directoryStamp = Directory.GetLastWriteTimeUtc(parent);
                var fileStamp = File.GetLastWriteTimeUtc(dependency);
                Write(dependency, inheritance ? Inherits("gearB") : Attached("gearB"));
                // Keep directory evidence unchanged; the input file is the dependency.
                File.SetLastWriteTimeUtc(dependency, fileStamp.AddSeconds(2));
                Directory.SetLastWriteTimeUtc(parent, directoryStamp);
                Check(Resolve(packages, cache) == -30 && Resolve(packages, null) == -30,
                    "Persistent cache missed " + (inheritance ? "inheritance" : "attachment root") + " update.");
                var cacheFile = Directory.GetFiles(cache, "*.json").Single();
                var json = File.ReadAllText(cacheFile);
                Check(json.Contains("\"Version\":3"), "Cache does not invalidate earlier dependency schemas.");
                File.WriteAllText(cacheFile, json.Replace("\"Version\":3", "\"Version\":2").Replace("\"Arm\":-30", "\"Arm\":-999"));
                Check(Resolve(packages, cache) == -30 && File.ReadAllText(cacheFile).Contains("\"Version\":3"),
                    "Legacy cache lacking attachment dependencies was reused.");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        DanglingJunctionDoesNotDisableCache();
    }

    private static void DanglingJunctionDoesNotDisableCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-cfg-" + Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(root, "p");
        var junction = Path.Combine(packages, "late-package");
        var target = Path.Combine(root, "late-target");
        var replacement = Path.Combine(root, "replacement-target");
        var cache = Path.Combine(root, "c");
        try
        {
            WriteAircraft(Path.Combine(packages, "known"), "Known aircraft", -10);
            // Exercise the bounded parallel dependency validation as well as
            // the small sequential catalogs covered by the attachment fixtures.
            for (var index = 0; index < 160; index++) Directory.CreateDirectory(Path.Combine(packages, "padding-" + index));
            CreateJunction(junction, target);
            var cold = Catalog(packages, cache);
            Check(cold == Catalog(packages, null), "An unavailable package changed the readable aircraft catalog.");
            Check(Directory.Exists(cache) && Directory.GetFiles(cache, "*.json").Length == 1,
                "A dangling package junction disabled the whole geometry cache.");
            var cacheFile = Directory.GetFiles(cache, "*.json").Single();
            var cacheStamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(cacheFile, cacheStamp);
            Check(Catalog(packages, cache) == cold && File.GetLastWriteTimeUtc(cacheFile) == cacheStamp,
                "The unchanged unavailable directory prevented reuse of the persistent catalog.");

            WriteAircraft(target, "Late aircraft", -20);
            var appeared = Catalog(packages, cache);
            Check(appeared == Catalog(packages, null) && appeared.Contains("Late aircraft=-20"),
                "The geometry cache missed the previously unavailable junction target appearing.");

            WriteAircraft(replacement, "Late aircraft", -30);
            var parentStamp = Directory.GetLastWriteTimeUtc(packages);
            Directory.Delete(junction);
            CreateJunction(junction, replacement);
            Directory.SetLastWriteTimeUtc(packages, parentStamp);
            var retargeted = Catalog(packages, cache);
            Check(retargeted == Catalog(packages, null) && retargeted.Contains("Late aircraft=-30"),
                "The geometry cache missed a junction being retargeted to different readable geometry.");
        }
        finally
        {
            // Remove the fixture link itself before recursively deleting only
            // this fixture's own directory and targets.
            if (Directory.Exists(junction)) Directory.Delete(junction);
            var fullRoot = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fullRoot).StartsWith("ls-cfg-", StringComparison.Ordinal),
                "Refusing to remove a geometry fixture outside its temporary root.");
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
        }
    }

    private static string Catalog(string root, string? cache) => string.Join("\n",
        new FlightModelGeometryResolver(new[] { root }, false, null, cache).CatalogForTesting()
            .OrderBy(item => item.AircraftTitle, StringComparer.Ordinal)
            .Select(item => item.AircraftTitle + "=" + item.MainGearLongitudinalFeet.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static void WriteAircraft(string package, string title, int arm)
    {
        var plane = Path.Combine(package, "SimObjects", "Airplanes", "test");
        Write(Path.Combine(plane, "aircraft.cfg"), "[FLTSIM.0]\ntitle=" + title + "\nisUserSelectable=1\n");
        Write(Path.Combine(plane, "flight_model.cfg"), Gear(arm));
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Arguments = "/d /c mklink /J \"" + junction + "\" \"" + target + "\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start the junction fixture helper.");
        if (!process.WaitForExit(10000))
        {
            process.Kill();
            throw new InvalidOperationException("Creating the fixture junction timed out.");
        }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Check(process.ExitCode == 0, "Could not create fixture junction: " + output + error);
    }

    private static double Resolve(string root, string? cache) =>
        new FlightModelGeometryResolver(new[] { root }, false, null, cache).CatalogForTesting().Single().MainGearLongitudinalFeet;
    private static string Attached(string gear) => "[SIM_ATTACHMENT.0]\nalias=gear\nattachment_root=SimObjects/Airplanes/test/attachments/vendor/" + gear + "\n";
    private static string Inherits(string gear) => "[Inherit]\nbase=SimObjects/Airplanes/test/attachments/vendor/" + gear + "\n";
    private static string Gear(int arm) => "[CONTACT_POINTS]\npoint.0=1,20,0,-3\npoint.1=1," + arm + ",-5,-3\npoint.2=1," + arm + ",5,-3\n";
    private static void Write(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
