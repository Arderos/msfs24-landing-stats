using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

internal sealed class FlightModelGearPoint
{
    public FlightModelGearPoint(int contactPointIndex, double longitudinalFeet)
    {
        ContactPointIndex = contactPointIndex;
        LongitudinalFeet = longitudinalFeet;
    }

    public int ContactPointIndex { get; }

    public double LongitudinalFeet { get; }
}

internal sealed class FlightModelGearGeometry
{
    public FlightModelGearGeometry(
        double mainGearLongitudinalFeet,
        IReadOnlyList<FlightModelGearPoint> mainGearPoints,
        IReadOnlyList<int> noseGearContactPointIndices,
        string configPath,
        double referenceDatumLongitudinalFeet = 0)
    {
        MainGearLongitudinalFeet = mainGearLongitudinalFeet;
        MainGearPoints = mainGearPoints.ToArray();
        NoseGearContactPointIndices = noseGearContactPointIndices.ToArray();
        ConfigPath = configPath;
        ReferenceDatumLongitudinalFeet = referenceDatumLongitudinalFeet;
    }

    public double MainGearLongitudinalFeet { get; }

    public IReadOnlyList<FlightModelGearPoint> MainGearPoints { get; }

    public IReadOnlyList<int> NoseGearContactPointIndices { get; }

    public string ConfigPath { get; }

    public double ReferenceDatumLongitudinalFeet { get; }
}

internal sealed class FlightModelGeometryMatch
{
    public FlightModelGeometryMatch(
        string aircraftTitle,
        string aircraftModel,
        FlightModelGearGeometry geometry)
    {
        AircraftTitle = aircraftTitle;
        AircraftModel = aircraftModel;
        MainGearLongitudinalFeet = geometry.MainGearLongitudinalFeet;
        MainGearPoints = geometry.MainGearPoints;
        NoseGearContactPointIndices = geometry.NoseGearContactPointIndices;
        ConfigPath = geometry.ConfigPath;
        ReferenceDatumLongitudinalFeet = geometry.ReferenceDatumLongitudinalFeet;
    }

    public string AircraftTitle { get; }

    public string AircraftModel { get; }

    public double MainGearLongitudinalFeet { get; }

    public IReadOnlyList<FlightModelGearPoint> MainGearPoints { get; }

    public IReadOnlyList<int> NoseGearContactPointIndices { get; }

    public string ConfigPath { get; }

    public double ReferenceDatumLongitudinalFeet { get; }
}

/// <summary>
/// Resolves readable aircraft geometry from the user's installed packages.
/// Marketplace-encrypted or ambiguous configurations fail closed so the core
/// analyzer can use its telemetry calibration instead.
/// </summary>
internal sealed class FlightModelAnalysisResolution
{
    public FlightModelAnalysisResolution(TouchdownAnalysisOptions? options, string outcome) { Options = options; Outcome = outcome; }
    public TouchdownAnalysisOptions? Options { get; }
    public string Outcome { get; }
}

internal sealed class FlightModelGeometryResolver
{
    private sealed class Catalog
    {
        public List<FlightModelGeometryMatch> Matches { get; } = new List<FlightModelGeometryMatch>();
        public Dictionary<string, string> Failures { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
    private const double DuplicateGeometryToleranceFeet = 0.10;
    private const double MaximumAbsoluteArmFeet = 500.0;
    private static readonly TimeSpan MissRefreshThrottle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExactMatchRefreshInterval = TimeSpan.FromMinutes(5);
    private readonly Func<IReadOnlyList<string>>? _installedPackagesPathProvider;
    private string[] _installedPackagesPaths;
    private readonly object _catalogGate = new object();
    private Task<Catalog> _catalogTask;
    private DateTime _lastCatalogBuildUtc;

    public FlightModelGeometryResolver()
    {
        _installedPackagesPathProvider = FindInstalledPackagesPaths;
        _installedPackagesPaths = Array.Empty<string>();
        _lastCatalogBuildUtc = DateTime.UtcNow;
        _catalogTask = Task.Run(() =>
        {
            var paths = FindInstalledPackagesPaths().ToArray();
            lock (_catalogGate) _installedPackagesPaths = paths;
            return BuildCatalog(paths);
        });
    }

    internal FlightModelGeometryResolver(IEnumerable<string> installedPackagesPaths, bool buildAsync)
        : this(installedPackagesPaths, buildAsync, null)
    {
    }

    internal FlightModelGeometryResolver(
        IEnumerable<string> installedPackagesPaths,
        bool buildAsync,
        Func<IReadOnlyList<string>>? installedPackagesPathProvider)
    {
        _installedPackagesPathProvider = installedPackagesPathProvider;
        _installedPackagesPaths = (installedPackagesPaths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeFullPath)
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _catalogTask = buildAsync
            ? Task.Run(() => BuildCatalog(_installedPackagesPaths))
            : Task.FromResult(BuildCatalog(_installedPackagesPaths));
        _lastCatalogBuildUtc = buildAsync ? DateTime.UtcNow : DateTime.MinValue;
    }

    public async Task<TouchdownAnalysisOptions?> CreateAnalysisOptionsAsync(
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<TelemetrySample> samples)
        => (await ResolveAnalysisAsync(aircraftTitle, aircraftType, aircraftModel, samples).ConfigureAwait(false)).Options;

    public async Task<FlightModelAnalysisResolution> ResolveAnalysisAsync(
        string aircraftTitle, string aircraftType, string aircraftModel,
        IReadOnlyList<TelemetrySample> samples, TelemetryDecisionJournal? journal = null)
    {
        var outcome = "resolver_failed";
        TouchdownAnalysisOptions? selected = null;
        FlightModelGeometryMatch? geometry = null;
        try
        {
            geometry = await ResolveAsync(aircraftTitle, code => outcome = code).ConfigureAwait(false);
            if (geometry != null)
            {
                selected = await Task.Run(() => TryCreateAnalysisOptions(geometry, samples, out var options, code => outcome = code)
                    ? options : null).ConfigureAwait(false);
            }
        }
        catch
        {
            // Installed-package geometry is an optional enhancement. Any I/O,
            // parse, refresh, or calibration failure must preserve the landing
            // and let TouchdownAnalysis use telemetry geometry instead.
            outcome = "resolver_io_or_parse_failed";
        }
        journal?.Record(samples.Count == 0 ? 0 : TelemetryAnalysisFrames.TimeOf(samples[samples.Count - 1]),
            "geometry.cfg", outcome, "Configuration resolution outcome; no matching readable geometry does not establish whether a file is absent or encrypted.",
            new Dictionary<string, double>
            {
                ["geometry_read"] = geometry == null ? 0 : 1,
                ["options_ready"] = selected == null ? 0 : 1,
                ["reference_datum_longitudinal_feet"] = geometry?.ReferenceDatumLongitudinalFeet ?? double.NaN,
            });
        return new FlightModelAnalysisResolution(selected, outcome);
    }

    public bool TryCreateAnalysisOptions(
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<TelemetrySample> samples,
        out TouchdownAnalysisOptions options)
    {
        options = null!;
        if (!TryResolve(aircraftTitle, aircraftType, aircraftModel, out var geometry))
        {
            return false;
        }

        return TryCreateAnalysisOptions(geometry, samples, out options);
    }

    private static bool TryCreateAnalysisOptions(
        FlightModelGeometryMatch geometry,
        IReadOnlyList<TelemetrySample> samples,
        out TouchdownAnalysisOptions options,
        Action<string>? diagnostic = null)
    {
        options = null!;
        if (!TelemetryGeometryCalibration.TryRecoverDatumOffset(samples, out var datum))
        {
            diagnostic?.Invoke("telemetry_datum_unavailable");
            return false;
        }

        var armFeet = geometry.MainGearLongitudinalFeet + datum.DatumOffsetFeet;
        if (!IsFinite(armFeet) || Math.Abs(armFeet) > MaximumAbsoluteArmFeet)
        {
            diagnostic?.Invoke("configured_arm_out_of_bounds");
            return false;
        }

        options = new TouchdownAnalysisOptions
        {
            LongitudinalMainGearArmFeet = armFeet,
            LongitudinalMainGearArmSource = TouchdownGeometrySource.FlightModelConfig,
            LongitudinalMainGearArmQuality = datum.Quality,
            RecoverLongitudinalMainGearArmFromTelemetry = true,
        };

        var configuredPointsFitCapture = geometry.MainGearPoints.Count >= 2 &&
            geometry.NoseGearContactPointIndices.Count >= 1 &&
            geometry.MainGearPoints.All(point =>
                point.ContactPointIndex >= 0 &&
                point.ContactPointIndex < TelemetrySample.CapturedContactPointCount &&
                IsFinite(point.LongitudinalFeet + datum.DatumOffsetFeet) &&
                Math.Abs(point.LongitudinalFeet + datum.DatumOffsetFeet) <= MaximumAbsoluteArmFeet) &&
            geometry.NoseGearContactPointIndices.All(point =>
                point >= 0 && point < TelemetrySample.CapturedContactPointCount);
        if (configuredPointsFitCapture)
        {
            options.MainGearContactPoints = geometry.MainGearPoints
                .Select(point => new TouchdownMainGearContactPoint(
                    point.ContactPointIndex,
                    point.LongitudinalFeet + datum.DatumOffsetFeet))
                .ToArray();
            options.NoseGearContactPointIndices = geometry.NoseGearContactPointIndices.ToArray();
        }
        diagnostic?.Invoke(configuredPointsFitCapture ? "cfg_options_ready" : "cfg_arm_ready_without_point_roles");
        return true;
    }

    internal bool TryResolve(
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        out FlightModelGeometryMatch geometry)
    {
        try
        {
            geometry = ResolveAsync(aircraftTitle).GetAwaiter().GetResult()!;
            return geometry != null;
        }
        catch
        {
            geometry = null!;
            return false;
        }
    }

    private async Task<FlightModelGeometryMatch?> ResolveAsync(string aircraftTitle, Action<string>? diagnostic = null)
    {
        var expectedTitle = ValueOrEmpty(aircraftTitle);
        if (expectedTitle.Length == 0)
        {
            diagnostic?.Invoke("aircraft_title_missing");
            return null;
        }

        var catalog = await CurrentCatalogTask().ConfigureAwait(false);
        var titleMatches = Match(catalog.Matches, entry => entry.AircraftTitle, expectedTitle).ToArray();
        if (titleMatches.Length > 0)
        {
            // StreamedPackages can add or replace an aircraft after the initial
            // catalog scan. Periodically refresh even a successful exact-title
            // lookup so the process does not keep stale geometry indefinitely.
            catalog = await RefreshCatalogAsync(ExactMatchRefreshInterval).ConfigureAwait(false);
            titleMatches = Match(catalog.Matches, entry => entry.AircraftTitle, expectedTitle).ToArray();
            var success = TrySelectUnambiguous(titleMatches, out var exact);
            diagnostic?.Invoke(success ? "cfg_geometry_ready" : titleMatches.Length > 0 ? "ambiguous_aircraft_geometry" : FailureFor(catalog, expectedTitle));
            return success ? exact : null;
        }

        catalog = await RefreshCatalogAsync(MissRefreshThrottle).ConfigureAwait(false);
        titleMatches = Match(catalog.Matches, entry => entry.AircraftTitle, expectedTitle).ToArray();
        var found = TrySelectUnambiguous(titleMatches, out var refreshed);
        diagnostic?.Invoke(found ? "cfg_geometry_ready" : titleMatches.Length > 0 ? "ambiguous_aircraft_geometry" : FailureFor(catalog, expectedTitle));
        return found ? refreshed : null;
    }

    private static string FailureFor(Catalog catalog, string title) =>
        catalog.Failures.TryGetValue(title, out var reason) ? reason : "no_readable_matching_geometry";

    internal IReadOnlyList<FlightModelGeometryMatch> CatalogForTesting() =>
        CurrentCatalogTask().GetAwaiter().GetResult().Matches;

    private Task<Catalog> CurrentCatalogTask()
    {
        lock (_catalogGate)
        {
            return _catalogTask;
        }
    }

    private Task<Catalog> RefreshCatalogAsync(TimeSpan minimumAge)
    {
        lock (_catalogGate)
        {
            var now = DateTime.UtcNow;
            if (!_catalogTask.IsCompleted || now - _lastCatalogBuildUtc < minimumAge)
            {
                return _catalogTask;
            }

            _lastCatalogBuildUtc = now;
            if (_installedPackagesPathProvider != null)
            {
                _installedPackagesPaths = _installedPackagesPaths
                    .Concat(_installedPackagesPathProvider())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeFullPath)
                    .Where(path => path != null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            var refreshPaths = _installedPackagesPaths.ToArray();
            _catalogTask = Task.Run(() => BuildCatalog(refreshPaths));
            return _catalogTask;
        }
    }

    internal static IReadOnlyList<string> FindInstalledPackagesPaths()
    {
        var candidates = new List<string>();
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddUserConfig(candidates, Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"));
        AddUserConfig(candidates, Path.Combine(roaming, "Microsoft Flight Simulator", "UserCfg.opt"));
        AddUserConfig(candidates, Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"));
        AddUserConfig(candidates, Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"));

        candidates.Add(Path.Combine(roaming, "Microsoft Flight Simulator 2024", "Packages"));
        candidates.Add(Path.Combine(roaming, "Microsoft Flight Simulator 2024", "StreamedPackages"));
        candidates.Add(Path.Combine(roaming, "Microsoft Flight Simulator", "Packages"));
        candidates.Add(Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "Packages"));
        candidates.Add(Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalState", "StreamedPackages"));
        candidates.Add(Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "Packages"));
        return candidates
            .Select(NormalizeFullPath)
            // Keep standard and configured roots even when they do not exist
            // yet. MSFS 2024 can create StreamedPackages only after the app
            // has started; refresh-on-miss must still be able to discover it.
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Catalog BuildCatalog(IEnumerable<string> installedPackagesPaths)
    {
        var result = new Catalog();
        var visitedAircraftConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installedPackagesPath in installedPackagesPaths)
        {
            foreach (var aircraftConfig in SafeEnumerateFiles(installedPackagesPath, "aircraft.cfg"))
            {
                if (!visitedAircraftConfigs.Add(aircraftConfig))
                {
                    continue;
                }
            }
        }

        var simObjectsByName = BuildSimObjectDirectoryIndex(visitedAircraftConfigs);
        var packageRoots = BuildPackageRootIndex(installedPackagesPaths, visitedAircraftConfigs);
        var attachmentRootsByVfsPath = BuildAttachmentRootIndex(packageRoots);
        foreach (var aircraftConfig in visitedAircraftConfigs)
        {
            TryAddAircraftConfig(result, aircraftConfig, simObjectsByName, attachmentRootsByVfsPath);
        }

        return result;
    }

    private static void TryAddAircraftConfig(
        Catalog result,
        string aircraftConfig,
        IReadOnlyDictionary<string, IReadOnlyList<string>> simObjectsByName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attachmentRootsByVfsPath)
    {
        var titles = Array.Empty<string>();
        try
        {
            var values = IniValues.Read(aircraftConfig);
            var selectable = values.GetAll("isUserSelectable").ToArray();
            if (selectable.Length > 0 && selectable.All(value =>
                    !int.TryParse(Unquote(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var enabled) ||
                    enabled == 0))
            {
                return;
            }

            titles = values.GetAll("title")
                .Select(Unquote)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (titles.Length == 0)
            {
                return;
            }

            var packageRoot = FindPackageRoot(aircraftConfig);
            if (packageRoot == null)
            {
                foreach (var title in titles) result.Failures[title] = "package_root_unresolved";
                return;
            }

            var configPaths = FindMergedFlightModelPaths(
                aircraftConfig,
                packageRoot,
                values,
                simObjectsByName,
                attachmentRootsByVfsPath);
            if (!FlightModelConfigParser.TryReadGearGeometry(
                    configPaths,
                    out var geometry, out var failure))
            {
                foreach (var title in titles) result.Failures[title] = failure;
                return;
            }

            var model = Unquote(values.GetLast("atc_model"));
            foreach (var title in titles)
            {
                result.Matches.Add(new FlightModelGeometryMatch(
                    title,
                    model,
                    geometry));
            }
        }
        catch
        {
            foreach (var title in titles) result.Failures[title] = "cfg_read_or_parse_failed";
            // One encrypted, damaged, or partially installed aircraft must not
            // prevent readable aircraft from entering the catalog.
        }
    }

    private static IReadOnlyList<string> FindMergedFlightModelPaths(
        string aircraftConfig,
        string packageRoot,
        IniValues aircraftValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> simObjectsByName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attachmentRootsByVfsPath)
    {
        var paths = new List<string>();
        var configDirectory = Path.GetDirectoryName(aircraftConfig)!;
        var modularRoot = FindModularSimObjectRoot(aircraftConfig);
        var localFlightModel = Path.Combine(configDirectory, "flight_model.cfg");
        if (modularRoot != null)
        {
            AddIfFile(paths, Path.Combine(modularRoot, "common", "config", "flight_model.cfg"));
            var visitedAttachments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!AddAttachmentFlightModels(
                paths,
                packageRoot,
                attachmentRootsByVfsPath,
                Path.Combine(configDirectory, "attached_objects.cfg"),
                visitedAttachments))
            {
                return Array.Empty<string>();
            }

            AddIfFile(paths, localFlightModel);
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // A non-modular variation that supplies its own flight model replaces
        // the base-container file; it is not an auto-merge layer.
        if (File.Exists(localFlightModel))
        {
            return new[] { localFlightModel };
        }

        var resolvedContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseContainer in aircraftValues.GetAll("base_container"))
        {
            var relative = Unquote(baseContainer).Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0)
            {
                continue;
            }

            var exactContainers = new[]
                {
                    SafeCombine(configDirectory, relative),
                    SafeCombine(packageRoot, relative),
                }
                .Where(container => container != null && Directory.Exists(container))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (exactContainers.Length == 1)
            {
                resolvedContainers.Add(exactContainers[0]);
                continue;
            }

            if (exactContainers.Length > 1)
            {
                return Array.Empty<string>();
            }

            var containerName = Path.GetFileName(relative.TrimEnd(Path.DirectorySeparatorChar));
            if (containerName.Length > 0 && simObjectsByName.TryGetValue(containerName, out var containers))
            {
                var distinctContainers = containers
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (distinctContainers.Length != 1)
                {
                    return Array.Empty<string>();
                }

                resolvedContainers.Add(distinctContainers[0]);
            }
        }

        if (resolvedContainers.Count != 1)
        {
            return Array.Empty<string>();
        }

        AddContainerFlightModels(paths, resolvedContainers.Single());
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool AddAttachmentFlightModels(
        List<string> paths,
        string packageRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attachmentRootsByVfsPath,
        string attachedObjectsPath,
        HashSet<string> visitedAttachments)
    {
        if (!File.Exists(attachedObjectsPath))
        {
            return true;
        }

        foreach (var attachment in ReadAttachmentReferences(attachedObjectsPath))
        {
            if (attachment.Alias.Length == 0)
            {
                // Without an alias the object can be attached visually, but it
                // is not a CFG overrider and contributes no merge layer.
                continue;
            }

            var relativeRoot = attachment.AttachmentRoot.Length > 0
                ? attachment.AttachmentRoot
                : AttachmentRootFromAssetPath(attachment.AttachmentPath);
            if (!TryResolveVfsDirectory(
                    packageRoot,
                    attachmentRootsByVfsPath,
                    relativeRoot,
                    out var root))
            {
                if (relativeRoot.Length > 0 && !IsAircraftAttachmentPath(relativeRoot))
                {
                    // Cabin/passenger/ground-vehicle attachments do not merge
                    // aircraft flight-model contact geometry. ToLiss A340, for
                    // example, references an external passenger-seat object.
                    continue;
                }

                // A VFS-only, GUID-only, missing, or ambiguous attachment may
                // contain contact points. Omitting it would make partial
                // geometry look complete, so use telemetry fallback instead.
                return false;
            }

            if (!AddAttachmentRoot(
                    paths,
                    packageRoot,
                    attachmentRootsByVfsPath,
                    root,
                    visitedAttachments))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAircraftAttachmentPath(string relativePath)
    {
        var normalized = Unquote(relativePath).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var prefix = Path.Combine("SimObjects", "Airplanes") + Path.DirectorySeparatorChar;
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AddAttachmentRoot(
        List<string> paths,
        string packageRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attachmentRootsByVfsPath,
        string attachmentRoot,
        HashSet<string> visitedAttachments)
    {
        var normalized = NormalizeFullPath(attachmentRoot);
        if (normalized == null)
        {
            return false;
        }

        if (!visitedAttachments.Add(normalized))
        {
            return true;
        }

        var configRoot = Path.Combine(normalized, "config");
        var inheritedBase = ReadSectionValue(
            Path.Combine(normalized, "attachment.cfg"),
            "Inherit",
            "base");
        if (inheritedBase.Length > 0)
        {
            if (!TryResolveVfsDirectory(
                    packageRoot,
                    attachmentRootsByVfsPath,
                    inheritedBase,
                    out var inheritedRoot) ||
                !AddAttachmentRoot(
                    paths,
                    packageRoot,
                    attachmentRootsByVfsPath,
                    inheritedRoot,
                    visitedAttachments))
            {
                return false;
            }
        }

        // A nested SimAttachment is merged before its parent.
        if (!AddAttachmentFlightModels(
                paths,
                packageRoot,
                attachmentRootsByVfsPath,
                Path.Combine(configRoot, "attached_objects.cfg"),
                visitedAttachments))
        {
            return false;
        }

        AddIfFile(paths, Path.Combine(configRoot, "flight_model.cfg"));
        return true;
    }

    private static bool TryResolveVfsDirectory(
        string currentPackageRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attachmentRootsByVfsPath,
        string relativePath,
        out string directory)
    {
        directory = string.Empty;
        var relative = Unquote(relativePath).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return false;
        }

        var local = SafeCombine(currentPackageRoot, relative);
        if (local != null && Directory.Exists(local))
        {
            directory = local;
            return true;
        }

        if (!attachmentRootsByVfsPath.TryGetValue(relative, out var indexedCandidates))
        {
            return false;
        }

        var candidates = indexedCandidates
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        directory = candidates[0];
        return true;
    }

    private static string AttachmentRootFromAssetPath(string attachmentPath)
    {
        var relative = Unquote(attachmentPath).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return string.Empty;
        }

        var parts = relative.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 2 < parts.Length; index++)
        {
            if (!string.Equals(parts[index], "attachments", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return string.Join(Path.DirectorySeparatorChar.ToString(), parts.Take(index + 3));
        }

        return string.Empty;
    }

    private static IReadOnlyList<AttachmentReference> ReadAttachmentReferences(string path)
    {
        var result = new List<AttachmentReference>();
        var inAttachment = false;
        var alias = string.Empty;
        var root = string.Empty;
        var asset = string.Empty;
        var guid = string.Empty;

        Action flush = () =>
        {
            if (inAttachment)
            {
                result.Add(new AttachmentReference(alias, root, asset, guid));
            }

            alias = string.Empty;
            root = string.Empty;
            asset = string.Empty;
            guid = string.Empty;
        };

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = IniValues.WithoutComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[' && line[line.Length - 1] == ']')
            {
                flush();
                var section = line.Substring(1, line.Length - 2).Trim();
                inAttachment = section.StartsWith("SIM_ATTACHMENT.", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inAttachment)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line.Substring(0, equals).Trim();
            var value = Unquote(line.Substring(equals + 1));
            if (string.Equals(key, "alias", StringComparison.OrdinalIgnoreCase))
            {
                alias = value;
            }
            else if (string.Equals(key, "attachment_root", StringComparison.OrdinalIgnoreCase))
            {
                root = value;
            }
            else if (string.Equals(key, "attachment", StringComparison.OrdinalIgnoreCase))
            {
                asset = value;
            }
            else if (string.Equals(key, "attachment_guid", StringComparison.OrdinalIgnoreCase))
            {
                guid = value;
            }
        }

        flush();
        return result;
    }

    private static string ReadSectionValue(string path, string expectedSection, string expectedKey)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var section = string.Empty;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = IniValues.WithoutComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[' && line[line.Length - 1] == ']')
            {
                section = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            if (!string.Equals(section, expectedSection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals > 0 &&
                string.Equals(line.Substring(0, equals).Trim(), expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                return Unquote(line.Substring(equals + 1));
            }
        }

        return string.Empty;
    }

    private readonly struct AttachmentReference
    {
        public AttachmentReference(
            string alias,
            string attachmentRoot,
            string attachmentPath,
            string attachmentGuid)
        {
            Alias = alias;
            AttachmentRoot = attachmentRoot;
            AttachmentPath = attachmentPath;
            AttachmentGuid = attachmentGuid;
        }

        public string Alias { get; }

        public string AttachmentRoot { get; }

        public string AttachmentPath { get; }

        public string AttachmentGuid { get; }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildSimObjectDirectoryIndex(
        IEnumerable<string> aircraftConfigs)
    {
        var mutable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var aircraftConfig in aircraftConfigs)
        {
            var simObject = FindSimObjectRoot(aircraftConfig);
            if (simObject == null)
            {
                continue;
            }

            var name = new DirectoryInfo(simObject).Name;
            if (!mutable.TryGetValue(name, out var directories))
            {
                directories = new List<string>();
                mutable[name] = directories;
            }

            if (!directories.Contains(simObject, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(simObject);
            }
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildPackageRootIndex(
        IEnumerable<string> installedPackagesPaths,
        IEnumerable<string> aircraftConfigs)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var aircraftConfig in aircraftConfigs)
        {
            var packageRoot = FindPackageRoot(aircraftConfig);
            if (packageRoot != null)
            {
                roots.Add(packageRoot);
            }
        }

        // Included-attachment packages need not own an aircraft.cfg. Their
        // manifest still gives us the physical package root behind the VFS.
        foreach (var installedPackagesPath in installedPackagesPaths)
        {
            foreach (var packageRoot in EnumeratePackageRoots(installedPackagesPath))
            {
                roots.Add(packageRoot);
            }
        }

        return roots.ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildAttachmentRootIndex(
        IEnumerable<string> packageRoots)
    {
        var mutable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageRoot in packageRoots)
        {
            var categoryRoot = Path.Combine(packageRoot, "SimObjects", "Airplanes");
            foreach (var simObjectRoot in SafeEnumerateDirectories(categoryRoot))
            {
                var attachmentsRoot = Path.Combine(simObjectRoot, "attachments");
                foreach (var vendorRoot in SafeEnumerateDirectories(attachmentsRoot))
                {
                    foreach (var attachmentRoot in SafeEnumerateDirectories(vendorRoot))
                    {
                        var relative = Path.Combine(
                            "SimObjects",
                            "Airplanes",
                            new DirectoryInfo(simObjectRoot).Name,
                            "attachments",
                            new DirectoryInfo(vendorRoot).Name,
                            new DirectoryInfo(attachmentRoot).Name);
                        if (!mutable.TryGetValue(relative, out var candidates))
                        {
                            candidates = new List<string>();
                            mutable[relative] = candidates;
                        }

                        if (!candidates.Contains(attachmentRoot, StringComparer.OrdinalIgnoreCase))
                        {
                            candidates.Add(attachmentRoot);
                        }
                    }
                }
            }
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.GetDirectories(path)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumeratePackageRoots(string installedPackagesPath)
    {
        if (!Directory.Exists(installedPackagesPath))
        {
            yield break;
        }

        const int maximumContainerDepth = 3;
        var pending = new Queue<KeyValuePair<string, int>>();
        pending.Enqueue(new KeyValuePair<string, int>(installedPackagesPath, 0));
        while (pending.Count > 0)
        {
            var item = pending.Dequeue();
            var directory = item.Key;
            if (item.Value > 0 &&
                (File.Exists(Path.Combine(directory, "manifest.json")) ||
                 File.Exists(Path.Combine(directory, "layout.json")) ||
                 Directory.Exists(Path.Combine(directory, "SimObjects"))))
            {
                yield return directory;
                continue;
            }

            if (item.Value >= maximumContainerDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Enqueue(new KeyValuePair<string, int>(child, item.Value + 1));
            }
        }
    }

    private static string? FindSimObjectRoot(string path)
    {
        var marker = Path.DirectorySeparatorChar + "SimObjects" + Path.DirectorySeparatorChar +
                     "Airplanes" + Path.DirectorySeparatorChar;
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var nameStart = markerIndex + marker.Length;
        var nameEnd = path.IndexOf(Path.DirectorySeparatorChar, nameStart);
        return nameEnd <= nameStart ? null : path.Substring(0, nameEnd);
    }

    private static void AddContainerFlightModels(List<string> paths, string? container)
    {
        if (container == null || !Directory.Exists(container))
        {
            return;
        }

        AddIfFile(paths, Path.Combine(container, "flight_model.cfg"));
        AddIfFile(paths, Path.Combine(container, "config", "flight_model.cfg"));
        AddIfFile(paths, Path.Combine(container, "common", "config", "flight_model.cfg"));
    }

    private static string? FindModularSimObjectRoot(string path)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (directory != null)
        {
            if (string.Equals(directory.Name, "presets", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directory.Name, "attachments", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directory.Name, "common", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent?.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindPackageRoot(string path)
    {
        var marker = Path.DirectorySeparatorChar + "SimObjects" + Path.DirectorySeparatorChar;
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index <= 0 ? null : path.Substring(0, index);
    }

    private static IEnumerable<FlightModelGeometryMatch> Match(
        IEnumerable<FlightModelGeometryMatch> catalog,
        Func<FlightModelGeometryMatch, string> selector,
        string value)
    {
        var expected = ValueOrEmpty(value);
        return expected.Length == 0
            ? Array.Empty<FlightModelGeometryMatch>()
            : catalog.Where(entry => string.Equals(
                ValueOrEmpty(selector(entry)),
                expected,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool TrySelectUnambiguous(
        IEnumerable<FlightModelGeometryMatch> candidates,
        out FlightModelGeometryMatch geometry)
    {
        geometry = null!;
        var matches = candidates.ToArray();
        if (matches.Length == 0)
        {
            return false;
        }

        var first = matches[0];
        if (matches.Any(match => !EquivalentGeometry(first, match)))
        {
            return false;
        }

        geometry = first;
        return true;
    }

    private static bool EquivalentGeometry(
        FlightModelGeometryMatch left,
        FlightModelGeometryMatch right)
    {
        if (Math.Abs(left.MainGearLongitudinalFeet - right.MainGearLongitudinalFeet) >
                DuplicateGeometryToleranceFeet ||
            left.MainGearPoints.Count != right.MainGearPoints.Count ||
            left.NoseGearContactPointIndices.Count != right.NoseGearContactPointIndices.Count)
        {
            return false;
        }

        for (var index = 0; index < left.MainGearPoints.Count; index++)
        {
            var leftPoint = left.MainGearPoints[index];
            var rightPoint = right.MainGearPoints[index];
            if (leftPoint.ContactPointIndex != rightPoint.ContactPointIndex ||
                Math.Abs(leftPoint.LongitudinalFeet - rightPoint.LongitudinalFeet) >
                    DuplicateGeometryToleranceFeet)
            {
                return false;
            }
        }

        for (var index = 0; index < left.NoseGearContactPointIndices.Count; index++)
        {
            if (left.NoseGearContactPointIndices[index] != right.NoseGearContactPointIndices[index])
            {
                return false;
            }
        }

        return true;
    }

    private static void AddUserConfig(List<string> paths, string userConfigPath)
    {
        try
        {
            if (!File.Exists(userConfigPath))
            {
                return;
            }

            foreach (var line in File.ReadLines(userConfigPath))
            {
                const string key = "InstalledPackagesPath";
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed.Substring(key.Length).Trim();
                value = Unquote(value);
                if (value.Length > 0)
                {
                    paths.Add(value);
                }
            }
        }
        catch
        {
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(
        string root,
        string fileName,
        bool pruneKnownNonAircraftPackages = true)
    {
        var result = new List<string>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        const int maximumDirectoryDepth = 32;
        var pending = new Stack<KeyValuePair<string, int>>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(new KeyValuePair<string, int>(root, 0));
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            var directory = item.Key;
            var depth = item.Value;
            string fullDirectory;
            try
            {
                fullDirectory = Path.GetFullPath(directory);
            }
            catch
            {
                continue;
            }

            if (!visited.Add(fullDirectory))
            {
                continue;
            }

            if (pruneKnownNonAircraftPackages && depth > 0 && IsKnownNonAircraftPackage(fullDirectory))
            {
                continue;
            }

            try
            {
                result.AddRange(Directory.EnumerateFiles(fullDirectory, fileName, SearchOption.TopDirectoryOnly));
            }
            catch
            {
            }

            try
            {
                if (depth >= maximumDirectoryDepth)
                {
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(fullDirectory))
                {
                    pending.Push(new KeyValuePair<string, int>(child, depth + 1));
                }
            }
            catch
            {
            }
        }

        return result;
    }

    private static bool IsKnownNonAircraftPackage(string directory)
    {
        var manifest = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifest))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(manifest);
            var key = text.IndexOf("\"content_type\"", StringComparison.OrdinalIgnoreCase);
            if (key < 0)
            {
                return false;
            }

            var colon = text.IndexOf(':', key + 14);
            var openingQuote = colon < 0 ? -1 : text.IndexOf('"', colon + 1);
            var closingQuote = openingQuote < 0 ? -1 : text.IndexOf('"', openingQuote + 1);
            if (openingQuote < 0 || closingQuote <= openingQuote)
            {
                return false;
            }

            var contentType = text.Substring(openingQuote + 1, closingQuote - openingQuote - 1).Trim();
            // A livery package still owns the aircraft.cfg whose TITLE SimConnect
            // reports. Its base_container can point at geometry in a different
            // package, so livery packages must participate in the catalog even
            // though they do not carry their own flight_model.cfg.
            return !string.Equals(contentType, "AIRCRAFT", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(contentType, "LIVERY", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void AddIfFile(List<string> paths, string? path)
    {
        if (path != null && File.Exists(path))
        {
            paths.Add(path);
        }
    }

    private static string? SafeCombine(params string[] parts)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(parts));
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(Unquote(path)));
        }
        catch
        {
            return null;
        }
    }

    private static string ValueOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value!.Trim();

    private static string Unquote(string? value)
    {
        var result = ValueOrEmpty(value);
        return result.Length >= 2 && result[0] == '"' && result[result.Length - 1] == '"'
            ? result.Substring(1, result.Length - 2).Trim()
            : result;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

internal static class FlightModelConfigParser
{
    private const double MinimumNoseMainSeparationFeet = 4.0;
    private const double MinimumMainLateralSpanFeet = 1.0;

    public static bool TryReadMainGearLongitudinal(
        IEnumerable<string> configPaths,
        out double mainGearLongitudinalFeet,
        out string geometryConfigPath)
    {
        mainGearLongitudinalFeet = double.NaN;
        geometryConfigPath = string.Empty;
        if (!TryReadGearGeometry(configPaths, out var geometry))
        {
            return false;
        }

        mainGearLongitudinalFeet = geometry.MainGearLongitudinalFeet;
        geometryConfigPath = geometry.ConfigPath;
        return true;
    }

    public static bool TryReadGearGeometry(
        IEnumerable<string> configPaths,
        out FlightModelGearGeometry geometry)
        => TryReadGearGeometry(configPaths, out geometry, out _);

    public static bool TryReadGearGeometry(
        IEnumerable<string> configPaths,
        out FlightModelGearGeometry geometry,
        out string failure)
    {
        geometry = null!;
        failure = "unsupported_cfg_topology";
        var geometryConfigPath = string.Empty;
        var points = new List<WheelPoint>();
        double[]? referenceDatum = null;
        var paths = (configPaths ?? Array.Empty<string>()).ToArray();
        if (paths.Length == 0) { failure = "flight_model_unavailable"; return false; }
        foreach (var path in paths)
        {
            try
            {
                if (HasManualModularMerge(path))
                {
                    failure = "manual_modular_merge_unsupported";
                    return false;
                }

                var values = IniValues.Read(path);
                foreach (var value in values.GetAll("reference_datum_position"))
                {
                    var coordinates = value.Split(',');
                    var datum = new double[3];
                    if (coordinates.Length != 3) { failure = "invalid_reference_datum"; return false; }
                    for (var axis = 0; axis < datum.Length; axis++)
                    {
                        if (!TryDouble(coordinates[axis], out datum[axis]) || !IsFinite(datum[axis])) { failure = "invalid_reference_datum"; return false; }
                        // The datum is shared by the entire merged aircraft.
                        // Conflicting/static-unresolvable layers are not guessed.
                        if (referenceDatum != null && Math.Abs(referenceDatum[axis] - datum[axis]) > 1e-9) { failure = "conflicting_reference_datum"; return false; }
                    }
                    referenceDatum = datum;
                }

                var layer = new SortedDictionary<int, WheelPoint>();
                foreach (var pair in values.Pairs)
                {
                    if (!pair.Key.StartsWith("point.", StringComparison.OrdinalIgnoreCase) ||
                        !int.TryParse(pair.Key.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    {
                        continue;
                    }

                    if (TryParseWheelPoint(pair.Value, out var point))
                    {
                        layer[index] = point;
                    }
                    else
                    {
                        // Dynamic/manual values that cannot be resolved from the
                        // static package files must fail closed. Omitting a point
                        // could silently move the inferred gear axis.
                        failure = "unresolved_contact_point";
                        return false;
                    }
                }

                if (layer.Count > 0)
                {
                    // MSFS auto-merge appends indexed parameters from each
                    // attachment/preset and assigns new final indices. Original
                    // point.N values are only ordering keys within their layer.
                    foreach (var point in layer.Values)
                    {
                        points.Add(point.WithContactPointIndex(points.Count));
                    }
                    geometryConfigPath = path;
                }
            }
            catch
            {
                failure = "cfg_read_or_parse_failed";
                return false;
            }
        }

        // CFG contact positions are relative to reference_datum_position;
        // telemetry's recovered datum offset starts at the model origin.
        // Translate once after merging, before the telemetry offset is added.
        var referenceDatumLongitudinal = referenceDatum?[0] ?? 0;
        var wheels = points
            .Where(point => point.Type == 1)
            .Select(point => new WheelPoint(point.ContactPointIndex, point.Type,
                point.LongitudinalFeet + referenceDatumLongitudinal, point.LateralFeet))
            .OrderByDescending(point => point.LongitudinalFeet)
            .ToArray();
        if (wheels.Length < 3)
        {
            return false;
        }

        var bestSplit = -1;
        var bestGap = double.NegativeInfinity;
        for (var split = 1; split <= Math.Min(2, wheels.Length - 2); split++)
        {
            var gap = wheels[split - 1].LongitudinalFeet - wheels[split].LongitudinalFeet;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestSplit = split;
            }
        }

        if (bestSplit < 1 || bestGap < MinimumNoseMainSeparationFeet)
        {
            return false;
        }

        var mains = wheels.Skip(bestSplit).ToArray();
        var lateralMinimum = mains.Min(point => point.LateralFeet);
        var lateralMaximum = mains.Max(point => point.LateralFeet);
        if (lateralMaximum - lateralMinimum < MinimumMainLateralSpanFeet)
        {
            return false;
        }

        var longitudinal = mains
            .Select(point => point.LongitudinalFeet)
            .OrderBy(value => value)
            .ToArray();
        var mainGearLongitudinalFeet = longitudinal.Length % 2 == 1
            ? longitudinal[longitudinal.Length / 2]
            : 0.5 * (longitudinal[longitudinal.Length / 2 - 1] + longitudinal[longitudinal.Length / 2]);
        if (!IsFinite(mainGearLongitudinalFeet) || Math.Abs(mainGearLongitudinalFeet) > 1000.0)
        {
            return false;
        }

        geometry = new FlightModelGearGeometry(
            mainGearLongitudinalFeet,
            mains
                .OrderBy(point => point.ContactPointIndex)
                .Select(point => new FlightModelGearPoint(
                    point.ContactPointIndex,
                    point.LongitudinalFeet))
                .ToArray(),
            wheels
                .Take(bestSplit)
                .OrderBy(point => point.ContactPointIndex)
                .Select(point => point.ContactPointIndex)
                .ToArray(),
            geometryConfigPath,
            referenceDatumLongitudinal);
        failure = "cfg_geometry_ready";
        return true;
    }

    private static bool HasManualModularMerge(string path)
    {
        var section = string.Empty;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = IniValues.WithoutComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[' && line[line.Length - 1] == ']')
            {
                section = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            if (!string.Equals(section, "MODULAR_MERGE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0 ||
                !string.Equals(line.Substring(0, equals).Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line.Substring(equals + 1).Trim();
            return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0";
        }

        return false;
    }

    private static bool TryParseWheelPoint(string value, out WheelPoint point)
    {
        point = default;
        var properties = value.IndexOf("#Properties:", StringComparison.OrdinalIgnoreCase);
        if (properties >= 0)
        {
            value = value.Substring(properties + "#Properties:".Length);
        }

        var columns = value.Split(',');
        if (columns.Length < 4 ||
            !TryDouble(columns[0], out var typeValue) ||
            !TryDouble(columns[1], out var longitudinal) ||
            !TryDouble(columns[2], out var lateral) ||
            !TryDouble(columns[3], out _))
        {
            return false;
        }

        var type = (int)Math.Round(typeValue);
        if (Math.Abs(typeValue - type) > 0.000001)
        {
            return false;
        }

        point = new WheelPoint(-1, type, longitudinal, lateral);
        return IsFinite(longitudinal) && IsFinite(lateral);
    }

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private readonly struct WheelPoint
    {
        public WheelPoint(int contactPointIndex, int type, double longitudinalFeet, double lateralFeet)
        {
            ContactPointIndex = contactPointIndex;
            Type = type;
            LongitudinalFeet = longitudinalFeet;
            LateralFeet = lateralFeet;
        }

        public int ContactPointIndex { get; }

        public int Type { get; }

        public double LongitudinalFeet { get; }

        public double LateralFeet { get; }

        public WheelPoint WithContactPointIndex(int contactPointIndex) =>
            new WheelPoint(contactPointIndex, Type, LongitudinalFeet, LateralFeet);
    }
}

internal sealed class IniValues
{
    private readonly List<KeyValuePair<string, string>> _pairs = new List<KeyValuePair<string, string>>();

    public IReadOnlyList<KeyValuePair<string, string>> Pairs => _pairs;

    public static IniValues Read(string path)
    {
        var result = new IniValues();
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = WithoutComment(rawLine).Trim();
            if (line.Length == 0 || line[0] == '[')
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line.Substring(0, equals).Trim();
            var value = line.Substring(equals + 1).Trim();
            if (key.Length > 0)
            {
                result._pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return result;
    }

    public IEnumerable<string> GetAll(string key) =>
        _pairs.Where(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value);

    public string GetLast(string key) => GetAll(key).LastOrDefault() ?? string.Empty;

    internal static string WithoutComment(string line)
    {
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                quoted = !quoted;
            }
            else if (line[index] == ';' && !quoted)
            {
                return line.Substring(0, index);
            }
        }

        return line;
    }
}
