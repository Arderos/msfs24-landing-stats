using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Diagnostics;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.Runtime;

/// <summary>Read-only public projections. Internal storage paths, authentication,
/// worker launch tokens and diagnostics messages are deliberately not serialized.</summary>
internal sealed class IntegrationApiData
{
    internal const int MaximumResponseBytes = 480 * 1024;
    private readonly LandingRepository _repository;
    private readonly object _repositoryGate;
    private readonly EpisodeTransactionStore _store;
    private readonly Func<RuntimePresentationSnapshot> _snapshot;
    private readonly Func<IEnumerable<SupervisionSnapshot>> _health;
    private readonly Func<JObject>? _telemetry;
    private readonly IntegrationEventStore _events;
    private readonly object _reconcileGate = new object();
    // All projection reads/appends observe one deletion epoch. Never acquire
    // this gate while holding the repository gate; disk append occurs after it is released.
    private readonly object _projectionGate = new object();
    private readonly Dictionary<Guid, string> _observed = new Dictionary<Guid, string>();
    private IEnumerator<Guid>? _scan;
    private DateTime _nextScan;
    private LandingRecord? _cachedDetail;
    private string? _cachedFingerprint;

    public IntegrationApiData(string journalRoot, LandingRepository repository, object repositoryGate,
        EpisodeTransactionStore store, Func<RuntimePresentationSnapshot> snapshot,
        Func<IEnumerable<SupervisionSnapshot>> health, Func<JObject>? telemetry = null)
    { _events = new IntegrationEventStore(journalRoot); _repository = repository; _repositoryGate = repositoryGate;
        _store = store; _snapshot = snapshot; _health = health; _telemetry = telemetry; }

    public string Handle(string requestJson, CancellationToken cancellationToken = default)
    {
        JToken? id = null;
        try
        {
            using var reader = new JsonTextReader(new StringReader(requestJson)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
            var request = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new IntegrationApiException("InvalidRequest", "A request must contain exactly one JSON object.");
            id = request["id"];
            if (id == null || id.Type != JTokenType.String || id.ToString().Length == 0 || id.ToString().Length > 128)
                throw new IntegrationApiException("InvalidRequest", "id must be a non-empty string of at most 128 characters.");
            if (request["protocolVersion"]?.Type != JTokenType.Integer || (long?)request["protocolVersion"] != 1)
                throw new IntegrationApiException("UnsupportedProtocol", "Supported protocolVersion is 1.");
            if (request["method"]?.Type != JTokenType.String) throw new IntegrationApiException("InvalidRequest", "method is required.");
            var method = (string)request["method"]!;
            if (request["params"] != null && !(request["params"] is JObject)) throw new IntegrationApiException("InvalidParams", "params must be an object.");
            var parameters = (JObject?)request["params"] ?? new JObject();
            using var projection = EnterGate(_projectionGate, cancellationToken);
            JToken result;
            switch (method)
            {
                case "hello": CheckDeletions(cancellationToken); result = Hello(); break;
                case "schema.get": result = Schema(); break;
                case "status.get": result = Status(); break;
                case "telemetry.get": result = _telemetry?.Invoke() ?? new JObject { ["available"] = false, ["reason"] = "NoSimulatorSample" }; break;
                case "landings.list": CheckDeletions(cancellationToken); result = List(parameters, cancellationToken); break;
                case "landings.get": result = Get(parameters, cancellationToken); break;
                case "episodes.get": result = Episode(parameters, cancellationToken); break;
                case "landings.series": result = Series(parameters, cancellationToken); break;
                case "landings.engines": result = AuxiliarySeries(parameters, true, cancellationToken); break;
                case "landings.contactPoints": result = AuxiliarySeries(parameters, false, cancellationToken); break;
                case "landings.provenance": result = Provenance(parameters, cancellationToken); break;
                case "events.read":
                    CheckDeletions(cancellationToken);
                    result = _events.Read(OptionalString(parameters, "streamId", 64), OptionalLong(parameters, "afterSequence", 0, long.MaxValue),
                        Integer(parameters, "limit", 16, 1, 32)); break;
                default: throw new IntegrationApiException("MethodNotFound", "Unknown method. Use hello for the supported method list.");
            }
            var response = new JObject { ["id"] = id.DeepClone(), ["result"] = result }.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(response) > MaximumResponseBytes)
                throw new IntegrationApiException("ResponseTooLarge", "Reduce the page limit. For provenance use section=metadata, sourceEvidenceTimeline, controllerCatalogs or controllerTransitions.");
            return response;
        }
        catch (IntegrationApiException error) { return Error(id, error.Code, error.Message); }
        catch (JsonException) { return Error(id, "InvalidRequest", "Malformed JSON request."); }
        catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException)
        { return Error(id, "InvalidParams", "A request parameter has an invalid type or value."); }
        catch (Exception) { return Error(id, "TemporarilyUnavailable", "The requested data is temporarily unavailable. Retry later."); }
    }
    private static string Error(JToken? id, string code, string message) => new JObject
    { ["id"] = id?.DeepClone(), ["error"] = new JObject { ["code"] = code, ["message"] = message } }.ToString(Formatting.None);

    private JObject Hello() => new JObject
    {
        ["application"] = "Landing Stats", ["protocolVersion"] = 1, ["schemaVersion"] = 1, ["readOnly"] = true,
        ["capabilities"] = new JArray("status", "latestTelemetry", "history", "episodeResults", "series", "engineSeries", "contactPointSeries", "provenance", "eventReplay", "eventSubscription"),
        ["methods"] = new JArray("hello", "schema.get", "status.get", "telemetry.get", "landings.list", "landings.get", "episodes.get", "landings.series", "landings.engines", "landings.contactPoints", "landings.provenance", "events.read", "events.subscribe"),
        ["limits"] = new JObject { ["landingPage"] = 200, ["seriesPage"] = 100, ["eventPage"] = 32, ["responseBytes"] = MaximumResponseBytes,
            ["provenanceTextPage"] = 65536, ["provenanceCatalogPage"] = 16, ["provenanceTransitionPage"] = 200,
            ["retainedEvents"] = IntegrationEventStore.MaximumEvents, ["journalBytes"] = IntegrationEventStore.MaximumBytes },
        ["events"] = new JObject { ["streamId"] = _events.StreamId, ["latestSequence"] = _events.LatestSequence,
            ["delivery"] = "atLeastOnce", ["deletionPolicy"] = "streamResetAndResync", ["resultsReady"] = "afterDurableAnalysisBeforeHistoryCompression" }
    };
    private JObject Status()
    {
        var snapshot = _snapshot();
        return new JObject
        {
            ["observedUtc"] = Utc(DateTime.UtcNow), ["revision"] = snapshot.Revision,
            ["recorder"] = new JObject { ["state"] = snapshot.Status?.State.ToString() ?? "Unavailable", ["messageKey"] = snapshot.Status?.MessageKey,
                ["airborne"] = snapshot.Airborne, ["activeEpisodeId"] = snapshot.ActiveEpisode == Guid.Empty ? null : snapshot.ActiveEpisode.ToString("N"), ["simulatorExited"] = snapshot.SimulatorExited },
            ["components"] = new JArray(_health().Select(item => new JObject { ["name"] = item.Component, ["state"] = item.State.ToString(),
                ["generation"] = item.Generation, ["restartCount"] = item.Failures })),
            ["recentEpisodes"] = new JArray(snapshot.Episodes.Select(item => new JObject { ["episodeId"] = item.Episode.ToString("N"), ["timestampUtc"] = Utc(item.TimestampUtc),
                ["metadataReady"] = item.ReportReady, ["contactCount"] = item.RecordCount, ["replayLike"] = item.ReplayLike, ["diagnosticOnly"] = item.DiagnosticOnly }))
        };
    }
    private JObject List(JObject parameters, CancellationToken cancellationToken)
    {
        var offset = Integer(parameters, "offset", 0, 0, 10000000); var limit = Integer(parameters, "limit", 50, 1, 200);
        var from = OptionalUtc(parameters, "fromUtc"); var to = OptionalUtc(parameters, "toUtc"); var aircraft = OptionalString(parameters, "aircraft", 256);
        var airport = OptionalString(parameters, "airport", 64);
        var cursor = OptionalString(parameters, "cursor", 512);
        DateTime? beforeUtc = null; string? beforeId = null;
        if (cursor != null)
        {
            if (offset != 0) throw new IntegrationApiException("InvalidParams", "cursor and a nonzero offset cannot be combined.");
            try
            {
                using var cursorReader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)))) { DateParseHandling = DateParseHandling.None, MaxDepth = 4 };
                var decoded = JObject.Load(cursorReader);
                beforeUtc = DateTime.Parse((string)decoded["timestampUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                beforeId = (string?)decoded["id"];
                if (string.IsNullOrEmpty(beforeId)) throw new FormatException();
            }
            catch (Exception error) when (error is JsonException || error is FormatException || error is ArgumentException)
            { throw new IntegrationApiException("InvalidParams", "Invalid history cursor."); }
        }
        if (from.HasValue && to.HasValue && from > to) throw new IntegrationApiException("InvalidParams", "fromUtc must not follow toUtc.");
        HistoryItem[] selected; int count;
        using (EnterGate(_repositoryGate, cancellationToken))
        {
            var history = _repository.LoadAll(); var ids = new HashSet<string>(history.Select(record => record.Id), StringComparer.Ordinal);
            var pending = _events.MissingHistoryContacts(ids);
            var candidates = history.Select(record => new HistoryItem(record)).Concat(pending.Select(contact => new HistoryItem(contact)));
            var matches = candidates.Where(record => !_repository.IsDeleted(record.Id) && (!from.HasValue || record.TimestampUtc >= from) &&
                (!to.HasValue || record.TimestampUtc <= to) && (aircraft == null || record.AircraftTitle.IndexOf(aircraft, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (airport == null || string.Equals(record.Airport, airport, StringComparison.OrdinalIgnoreCase))).ToArray();
            var ordered = matches.OrderByDescending(record => record.TimestampUtc).ThenByDescending(record => record.Id, StringComparer.Ordinal)
                .Where(record => !beforeUtc.HasValue || record.TimestampUtc < beforeUtc || (record.TimestampUtc == beforeUtc && string.CompareOrdinal(record.Id, beforeId) < 0)).ToArray();
            count = ordered.Length; selected = ordered.Skip(offset).Take(limit).ToArray();
        }
        var last = selected.LastOrDefault();
        var nextCursor = last == null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(new JObject { ["timestampUtc"] = Utc(last.TimestampUtc), ["id"] = last.Id }.ToString(Formatting.None)));
        return new JObject { ["items"] = new JArray(selected.Select(record => record.Projection())), ["total"] = count,
            ["offset"] = offset, ["nextOffset"] = offset + selected.Length, ["hasMore"] = offset + selected.Length < count,
            ["nextCursor"] = nextCursor, ["streamId"] = _events.StreamId, ["consistency"] = "keysetTimestampAndId" };
    }
    private sealed class HistoryItem
    {
        private readonly LandingRecord? _record; private readonly JObject? _projected;
        public HistoryItem(LandingRecord record) { _record = record; Id = record.Id; TimestampUtc = record.TimestampUtc; AircraftTitle = record.AircraftTitle; Airport = record.Airport; }
        public HistoryItem(JObject projected)
        { _projected = projected; Id = (string)projected["id"]!; TimestampUtc = DateTime.Parse((string)projected["timestampUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            AircraftTitle = (string?)projected["aircraft"]?["title"] ?? ""; Airport = (string?)projected["site"]?["airport"] ?? ""; }
        public string Id { get; } public DateTime TimestampUtc { get; } public string AircraftTitle { get; } public string Airport { get; }
        public JObject Projection() => _projected ?? ProjectContact(_record!, false);
    }
    private JObject Get(JObject parameters, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "id", 128); CheckDeletions(cancellationToken);
        LandingRecord? detail;
        using (EnterGate(_repositoryGate, cancellationToken))
        {
            if (_repository.IsDeleted(id)) throw new IntegrationApiException("NotFound", "Landing does not exist.");
            var summary = _repository.LoadAll().FirstOrDefault(item => item.Id == id);
            detail = summary == null ? null : LoadCachedDetail(summary);
        }
        if (detail != null) return ProjectContact(detail, true);
        var episode = _events.FindEpisodeForContact(id); var payload = episode == null ? null : _events.FindEpisode(episode);
        return (JObject?)((JArray?)payload?["contacts"])?.FirstOrDefault(item => (string?)item["id"] == id)?.DeepClone()
            ?? throw new IntegrationApiException("NotFound", "Landing does not exist.");
    }
    private JObject Episode(JObject parameters, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "id", 128); CheckDeletions(cancellationToken);
        var projected = _events.FindEpisode(id); if (projected != null) return projected;
        LandingRecord[] records;
        using (EnterGate(_repositoryGate, cancellationToken)) records = _repository.LoadAll().Where(record => !_repository.IsDeleted(record.Id) && EpisodeId(record) == id)
            .Select(record => _repository.LoadDetail(record)).Where(record => record != null).Cast<LandingRecord>().OrderBy(record => record.ContactNumber).ToArray();
        if (records.Length == 0) throw new IntegrationApiException("NotFound", "Episode does not exist.");
        return new JObject { ["episodeId"] = id, ["revision"] = null, ["timestampUtc"] = Utc(records[0].TimestampUtc), ["metricsReady"] = true,
            ["metadataReady"] = null, ["source"] = "history", ["contacts"] = new JArray(records.Select(record => ProjectContact(record, true))) };
    }
    private LandingRecord Detail(JObject parameters, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "id", 128);
        using (EnterGate(_repositoryGate, cancellationToken))
        {
            if (_repository.IsDeleted(id)) throw new IntegrationApiException("NotFound", "Landing does not exist.");
            var summary = _repository.LoadAll().FirstOrDefault(item => item.Id == id);
            var record = summary == null ? null : LoadCachedDetail(summary);
            if (record != null) return record;
        }
        var episodeId = _events.FindEpisodeForContact(id);
        var payload = episodeId == null ? null : _events.FindEpisode(episodeId);
        if (Guid.TryParseExact((string?)payload?["captureId"], "N", out var episode))
        {
            var manifest = _store.Read(episode);
            var result = manifest.Stage >= EpisodeWorkStage.ReportReady ? _store.ReadReport(manifest) : _store.ReadAnalysis(manifest);
            var record = result.Records.FirstOrDefault(item => item.Id == id);
            if (record != null) return record;
        }
        throw new IntegrationApiException("NotFound", "Landing does not exist.");
    }
    // Called under the repository gate. A single bounded immutable detail avoids
    // decompressing the entire recording for every small series page. Metadata
    // changes and imported replacement bytes invalidate the fingerprint.
    private LandingRecord? LoadCachedDetail(LandingRecord summary)
    {
        var fingerprint = _repository.GetBackupFingerprint(summary);
        if (_cachedDetail?.Id == summary.Id && _cachedFingerprint == fingerprint) return _cachedDetail;
        _cachedDetail = null; _cachedFingerprint = null;
        var record = _repository.LoadDetail(summary); if (record == null) return null;
        var bytes = record.Series.Count * (656L + 17L * (record.RawControllerSourceIndices.Count + record.RawControllerAxesSourceIndices.Count)) +
            record.Engines.Sum(engine => 64L * engine.Points.Count) + record.ContactPoints.Sum(contact => 48L * contact.Points.Count) +
            (record.SourceEvidenceTimeline?.Length ?? 0) * 2L + (record.ControllerTimeline?.Catalogs.Sum(catalog => catalog.Length * 2L) ?? 0) +
            (record.ControllerTimeline?.Transitions.Length ?? 0) * 32L;
        if (bytes <= 32L * 1024 * 1024) { _cachedDetail = record; _cachedFingerprint = fingerprint; }
        return record;
    }
    private JObject Series(JObject parameters, CancellationToken cancellationToken)
    {
        var record = Detail(parameters, cancellationToken); var offset = Integer(parameters, "offset", 0, 0, 1000000); var limit = Integer(parameters, "limit", 50, 1, 100);
        var from = OptionalNumber(parameters, "fromSeconds"); var to = OptionalNumber(parameters, "toSeconds");
        if (from.HasValue && to.HasValue && from > to) throw new IntegrationApiException("InvalidParams", "fromSeconds must not follow toSeconds.");
        var points = record.Series.Where(point => (!from.HasValue || point.TimeSeconds >= from) && (!to.HasValue || point.TimeSeconds <= to)).ToArray();
        var page = points.Skip(offset).Take(limit).Select(point => ProjectSeriesPoint(point)).ToArray();
        return Page(record.Id, offset, points.Length, page);
    }
    private JObject AuxiliarySeries(JObject parameters, bool engines, CancellationToken cancellationToken)
    {
        var record = Detail(parameters, cancellationToken); var offset = Integer(parameters, "offset", 0, 0, 1000000); var limit = Integer(parameters, "limit", 100, 1, 200);
        var index = Integer(parameters, engines ? "engineNumber" : "contactPointIndex", engines ? 1 : 0, 0, 1024);
        object[] points = engines ? record.Engines.Where(item => item.EngineNumber == index).SelectMany(item => item.Points).Cast<object>().ToArray()
            : record.ContactPoints.Where(item => item.ContactPointIndex == index).SelectMany(item => item.Points).Cast<object>().ToArray();
        var page = Page(record.Id, offset, points.Length, points.Skip(offset).Take(limit).Select(DataMembers)); page[engines ? "engineNumber" : "contactPointIndex"] = index; return page;
    }
    private JObject Provenance(JObject parameters, CancellationToken cancellationToken)
    {
        var record = Detail(parameters, cancellationToken);
        var section = OptionalString(parameters, "section", 32);
        if (section == "sourceEvidenceTimeline")
        {
            var text = record.SourceEvidenceTimeline ?? "";
            var offset = Integer(parameters, "offset", 0, 0, int.MaxValue);
            var limit = Integer(parameters, "limit", 65536, 1, 65536);
            var chunk = offset >= text.Length ? "" : text.Substring(offset, Math.Min(limit, text.Length - offset));
            return new JObject { ["id"] = record.Id, ["section"] = section, ["encoding"] = "base64",
                ["available"] = record.SourceEvidenceTimeline != null, ["data"] = chunk, ["offset"] = offset,
                ["nextOffset"] = (long)offset + chunk.Length, ["total"] = text.Length, ["hasMore"] = (long)offset + chunk.Length < text.Length };
        }
        if (section == "controllerCatalogs" || section == "controllerTransitions")
        {
            var catalogs = section == "controllerCatalogs";
            var offset = Integer(parameters, "offset", 0, 0, int.MaxValue);
            var limit = Integer(parameters, "limit", catalogs ? 8 : 100, 1, catalogs ? 16 : 200);
            var timeline = record.ControllerTimeline;
            var values = catalogs ? (timeline?.Catalogs ?? Array.Empty<string>()).Select(value => (JToken)new JValue(value)).ToArray()
                : (timeline?.Transitions ?? Array.Empty<int[]>()).Select(value => (JToken)new JArray(value)).ToArray();
            var items = new JArray(values.Skip(offset).Take(limit));
            return new JObject { ["id"] = record.Id, ["section"] = section, ["version"] = timeline?.Version,
                ["samples"] = timeline?.SampleCount, ["items"] = items, ["offset"] = offset,
                ["nextOffset"] = (long)offset + items.Count, ["total"] = values.Length, ["hasMore"] = (long)offset + items.Count < values.Length };
        }
        if (section != null && section != "metadata") throw new IntegrationApiException("InvalidParams", "Unknown provenance section.");
        return new JObject { ["id"] = record.Id, ["controlInputSources"] = new JArray(record.ControlInputSources),
            ["rawControllerSourceIndices"] = new JArray(record.RawControllerSourceIndices), ["rawControllerAxesSourceIndices"] = new JArray(record.RawControllerAxesSourceIndices),
            ["rawPitchInput"] = new JObject { ["available"] = record.HasRawPitchInput, ["sourceIndex"] = record.RawPitchInputSourceIndex,
                ["correlation"] = Number(record.RawPitchInputCorrelation), ["lagSeconds"] = Number(record.RawPitchInputLagSeconds) },
            ["controllerTimeline"] = section == "metadata" ? JValue.CreateNull() : SafeModel(record.ControllerTimeline), ["analysisGeometry"] = SafeModel(record.AnalysisGeometry),
            ["sourceEvidenceTimeline"] = section == "metadata" ? null : record.SourceEvidenceTimeline,
            ["sections"] = new JObject { ["sourceEvidenceTimeline"] = record.SourceEvidenceTimeline?.Length ?? 0,
                ["controllerCatalogs"] = record.ControllerTimeline?.Catalogs?.Length ?? 0, ["controllerTransitions"] = record.ControllerTimeline?.Transitions?.Length ?? 0 },
            ["availableEngineNumbers"] = new JArray(record.Engines.Select(item => item.EngineNumber)), ["availableContactPointIndices"] = new JArray(record.ContactPoints.Select(item => item.ContactPointIndex)) };
    }
    private static JObject Page(string id, int offset, int total, IEnumerable<JToken> values)
    {
        var page = new JArray(values); return new JObject { ["id"] = id, ["points"] = page, ["offset"] = offset,
            ["nextOffset"] = offset + page.Count, ["total"] = total, ["hasMore"] = offset + page.Count < total,
            ["timeOrigin"] = "contact", ["units"] = Schema()["seriesUnits"]!.DeepClone() };
    }
    public void Publish(Guid episodeId, ProcessedEpisode result, bool metadataReady, CancellationToken cancellationToken = default)
    {
        if (result.ReplayLike || result.Records.Count == 0) return;
        using var projection = EnterGate(_projectionGate, cancellationToken);
        CheckDeletions(cancellationToken);
        LandingRecord[] records;
        using (EnterGate(_repositoryGate, cancellationToken)) records = result.Records.Where(record => !_repository.IsDeleted(record.Id)).OrderBy(record => record.ContactNumber).ToArray();
        if (records.Length == 0) return;
        var revision = metadataReady ? 2 : 1;
        var physicalEpisodes = records.GroupBy(EpisodeId).ToArray();
        foreach (var physical in physicalEpisodes)
        {
            var payload = new JObject { ["episodeId"] = physical.Key, ["captureId"] = episodeId.ToString("N"), ["revision"] = revision,
                ["captureEpisodeCount"] = physicalEpisodes.Length,
                ["timestampUtc"] = Utc(physical.First().TimestampUtc), ["metricsReady"] = true, ["metadataReady"] = metadataReady,
                ["contacts"] = new JArray(physical.Select(record => ProjectContact(record, true))) };
            // A single event must fit in one protocol response; never expose an
            // incomplete multi-contact episode as if it were a coherent result.
            if (Encoding.UTF8.GetByteCount(payload.ToString(Formatting.None)) > MaximumResponseBytes / 2)
                throw new InvalidDataException("Episode result exceeds the integration event bound.");
            _events.Publish(physical.Key, physical.First().TimestampUtc, revision, payload);
        }
    }
    public void ReconcileCommitted(CancellationToken cancellationToken = default)
    {
        // Called only by the API owner's background loop. The scan has bounded
        // work per tick, and skips already observed immutable analysis hashes.
        using (EnterGate(_reconcileGate, cancellationToken))
        using (EnterGate(_projectionGate, cancellationToken))
        {
            CheckDeletions(cancellationToken);
            if (System.Threading.Interlocked.Exchange(ref _resetObserved, 0) != 0) { _observed.Clear(); _nextScan = DateTime.MinValue; }
            if (_scan == null)
            {
                if (DateTime.UtcNow < _nextScan) return;
                _scan = _store.Enumerate().GetEnumerator();
            }
            var budget = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 16 && budget.ElapsedMilliseconds < 50; i++)
            {
                if (!_scan.MoveNext()) { _scan.Dispose(); _scan = null; _nextScan = DateTime.UtcNow.AddSeconds(30); return; }
                var id = _scan.Current;
                try
                {
                    var manifest = _store.Read(id); if (manifest.Stage < EpisodeWorkStage.Analysed) continue;
                    var signature = manifest.AnalysisHash + ":" + manifest.ReportHash;
                    if (_observed.TryGetValue(id, out var known) && known == signature) continue;
                    if (_events.CoversCapture(id, manifest.TimestampUtc, manifest.Stage >= EpisodeWorkStage.ReportReady ? 2 : 1))
                    { Remember(id, signature); continue; }
                    // Append the initial revision as well as a ready report when
                    // repairing a crash between durable analysis and publication.
                    Publish(id, _store.ReadAnalysis(manifest), false, cancellationToken);
                    if (manifest.Stage >= EpisodeWorkStage.ReportReady) Publish(id, _store.ReadReport(manifest), true, cancellationToken);
                    Remember(id, signature);
                    return; // At most one full analysis/report read per owner tick.
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SerializationException || error is InvalidOperationException || error is ArgumentException)
                { /* Canonical episode is retained; a later bounded scan retries it. */ }
            }
        }
    }
    private void Remember(Guid id, string signature)
    { if (_observed.Count >= 16384) _observed.Clear(); _observed[id] = signature; }
    private void CheckDeletions(CancellationToken cancellationToken)
    {
        string fingerprint;
        using (EnterGate(_repositoryGate, cancellationToken))
        {
            var root = Path.Combine(_repository.RootPath, ".deleted");
            var manifest = Directory.Exists(root) ? string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) == ".del" || Path.GetFileName(path) == "entries.bin")
                .OrderBy(path => path, StringComparer.Ordinal).Select(path => { var file = new FileInfo(path); return path.Substring(root.Length) + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks; })) : "";
            using var sha = SHA256.Create();
            fingerprint = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(manifest)));
        }
        var reset = _events.RefreshDeletionFingerprint(fingerprint);
        if (reset)
        {
            // Do not take the reconcile lock here: a request must not wait on a
            // background disk scan. Clearing its cache is done at the next scan.
            System.Threading.Interlocked.Exchange(ref _resetObserved, 1);
        }
    }
    private int _resetObserved;
    private static IDisposable EnterGate(object gate, CancellationToken cancellationToken)
    {
        var waiting = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Monitor.TryEnter(gate, 25)) return new GateLease(gate);
        } while (waiting.ElapsedMilliseconds < 250);
        throw new IntegrationApiException("TemporarilyUnavailable", "History is busy. Retry this optional query later.");
    }
    private sealed class GateLease : IDisposable
    {
        private readonly object _gate;
        public GateLease(object gate) { _gate = gate; }
        public void Dispose() => Monitor.Exit(_gate);
    }

    internal static JObject ProjectContact(LandingRecord record, bool detail)
    {
        var metrics = new JObject
        {
            ["inertialVerticalRate"] = Metric(record.InertialFpm, "ft/min"), ["surfaceClosureRate"] = Metric(record.SurfaceFpm, "ft/min"),
            ["surfaceDelta"] = Metric(record.SurfaceDeltaFpm, "ft/min"), ["terrainRate"] = Metric(record.TerrainFpm, "ft/min"), ["unresolvedRate"] = Metric(record.UnresolvedFpm, "ft/min"),
            ["peakG150Milliseconds"] = Metric(record.PeakG150Milliseconds, "g"), ["peakG2Seconds"] = Metric(record.PeakG2Seconds, "g"),
            ["impactPeak"] = Metric(record.ImpactPeakG, "g"), ["impactWindow"] = Metric(record.ImpactWindowSeconds, "s"),
            ["pitch"] = Metric(record.PitchDegrees, "deg"), ["bank"] = Metric(record.BankDegrees, "deg"), ["angleOfAttack"] = Metric(record.AngleOfAttackDegrees, "deg"),
            ["indicatedAirspeed"] = Metric(record.AirspeedKnots, "kt"), ["groundSpeed"] = Metric(record.GroundSpeedKnots, "kt"),
            ["weight"] = Metric(record.WeightPounds, "lb"), ["cg"] = Metric(record.CgPercent, "%"), ["windSpeed"] = Metric(record.WindSpeedKnotsAtContact, "kt"),
            ["windDirection"] = Metric(record.WindDirectionDegreesAtContact, "deg")
        };
        var projected = new JObject
        {
            ["id"] = record.Id, ["episodeId"] = EpisodeId(record), ["timestampUtc"] = Utc(record.TimestampUtc), ["sourceFormatVersion"] = record.FormatVersion,
            ["aircraft"] = new JObject { ["title"] = record.AircraftTitle, ["type"] = record.AircraftType, ["simulator"] = record.Simulator },
            ["site"] = new JObject { ["airport"] = record.Airport, ["runway"] = record.Runway, ["latitudeDegrees"] = Number(record.TouchdownLatitudeDegrees),
                ["longitudeDegrees"] = Number(record.TouchdownLongitudeDegrees), ["airportDistanceNauticalMiles"] = Number(record.AirportDistanceNauticalMiles) },
            ["contactNumber"] = record.ContactNumber, ["contactCount"] = record.ContactCount, ["metrics"] = metrics,
            ["quality"] = new JObject { ["surfaceLatchAvailable"] = record.HasSurfaceLatchData, ["inertialExtrapolated"] = record.InertialExtrapolated,
                ["inertialFitDurationSeconds"] = Number(record.InertialFitDurationSeconds), ["latchUpdateDetected"] = record.LatchUpdateDetected,
                ["latchUpdateOffsetSeconds"] = Number(record.LatchUpdateOffsetSeconds), ["contactTimeEstimatedFromCompression"] = record.ContactTimeEstimatedFromCompression,
                ["impactStatus"] = record.ImpactStatus, ["impactAnalysisVersion"] = record.ImpactAnalysisVersion },
            ["detailAvailable"] = detail, ["availabilityConvention"] = "nonFiniteOrMissingIsNull"
        };
        if (detail)
        {
            projected["series"] = new JObject { ["samples"] = record.Series.Count, ["fromSeconds"] = Number(record.Series.FirstOrDefault()?.TimeSeconds),
                ["toSeconds"] = Number(record.Series.LastOrDefault()?.TimeSeconds), ["engineNumbers"] = new JArray(record.Engines.Select(item => item.EngineNumber)),
                ["contactPointIndices"] = new JArray(record.ContactPoints.Select(item => item.ContactPointIndex)) };
            projected["runwayGeometry"] = SafeModel(record.RunwayGeometry);
            projected["approachGateSeconds"] = Number(record.ApproachGateSeconds); projected["flareStartSeconds"] = Number(record.FlareStartSeconds);
            projected["estimatedContactTimeSeconds"] = Number(record.EstimatedContactTimeSeconds);
            projected["reconstruction"] = new JObject { ["available"] = record.HasClosureReconstruction, ["experimental"] = true, ["model"] = record.ClosureReconstructionModel,
                ["closure"] = Metric(record.ReconstructedClosureFpm, "ft/min"), ["inertial"] = Metric(record.ReconstructedInertialFpm, "ft/min"),
                ["terrain"] = Metric(record.ReconstructedTerrainFpm, "ft/min"), ["pitch"] = Metric(record.ReconstructedPitchFpm, "ft/min"),
                ["rotation"] = Metric(record.ReconstructedRotationFpm, "ft/min"), ["residual"] = Metric(record.ClosureReconstructionResidualFpm, "ft/min"),
                ["uncertainty"] = Metric(record.ClosureReconstructionUncertaintyFpm, "ft/min"), ["fitPointCount"] = record.ClosureReconstructionFitPointCount,
                ["longitudinalArm"] = Metric(record.ClosureReconstructionLongitudinalArmFeet, "ft"), ["geometryQuality"] = Number(record.ClosureReconstructionGeometryQuality),
                ["geometrySource"] = record.ClosureReconstructionGeometrySource, ["armRecoveredFromTelemetry"] = record.ClosureReconstructionArmRecoveredFromTelemetry };
        }
        return projected;
    }
    private static string EpisodeId(LandingRecord record) => string.IsNullOrWhiteSpace(record.RecordingEpisodeId) ? "legacy:" + record.Id : record.RecordingEpisodeId!;
    private static JObject Metric(double? value, string unit) => new JObject { ["value"] = Number(value), ["unit"] = unit, ["available"] = Finite(value) };
    private static bool Finite(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value != LandingRecord.NonFiniteStorageSentinel;
    private static JToken Number(double? value) => Finite(value) ? new JValue(value!.Value) : JValue.CreateNull();
    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static JObject ProjectSeriesPoint(LandingSeriesPoint point)
    {
        var value = DataMembers(point);
        if (!point.AxisElevatorSetValid) value["axisElevatorSetPercent"] = JValue.CreateNull();
        value["rawControllerYAxisPercent"] = new JArray(point.RawControllerYAxisPercent.Select((v, i) => i < point.RawControllerYAxisValid.Length && point.RawControllerYAxisValid[i] ? Number(v) : JValue.CreateNull()));
        value["rawControllerAxesPercent"] = new JArray(point.RawControllerAxesPercent.Select((v, i) => i < point.RawControllerAxesValid.Length && point.RawControllerAxesValid[i] ? Number(v) : JValue.CreateNull()));
        return value;
    }
    // Only these model types' explicitly persisted data members are projected;
    // this is not a serializer for arbitrary runtime objects.
    private static JObject DataMembers(object model)
    {
        var result = new JObject();
        foreach (var property in model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetCustomAttribute<DataMemberAttribute>() != null))
            result[char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1)] = SafeModel(property.GetValue(model));
        return result;
    }
    private static JToken SafeModel(object? value)
    {
        if (value == null) return JValue.CreateNull();
        var token = JToken.FromObject(value, JsonSerializer.Create(new JsonSerializerSettings { FloatFormatHandling = FloatFormatHandling.Symbol, TypeNameHandling = TypeNameHandling.None }));
        foreach (var number in (token is JContainer container ? container.DescendantsAndSelf() : new[] { token }).OfType<JValue>().ToArray())
            if (number.Type == JTokenType.Float && !Finite(number.Value<double>()))
            { if (ReferenceEquals(number, token)) return JValue.CreateNull(); number.Replace(JValue.CreateNull()); }
        return token;
    }
    internal static JObject Schema()
    {
        var units = new JObject();
        foreach (var property in typeof(LandingSeriesPoint).GetProperties().Where(p => p.GetCustomAttribute<DataMemberAttribute>() != null))
        {
            var name = property.Name; var unit = name.EndsWith("DegreesPerSecond", StringComparison.Ordinal) ? "deg/s" : name.EndsWith("Fps2", StringComparison.Ordinal) ? "ft/s^2" :
                name.EndsWith("Seconds", StringComparison.Ordinal) ? "s" : name.EndsWith("Percent", StringComparison.Ordinal) ? "%" : name.EndsWith("Degrees", StringComparison.Ordinal) ? "deg" :
                name.EndsWith("Feet", StringComparison.Ordinal) ? "ft" : name.EndsWith("Knots", StringComparison.Ordinal) ? "kt" : name.EndsWith("Fpm", StringComparison.Ordinal) ? "ft/min" :
                name.EndsWith("G", StringComparison.Ordinal) || name == "GForce" ? "g" : property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool[]) ? "boolean" : "dimensionless";
            units[char.ToLowerInvariant(name[0]) + name.Substring(1)] = unit;
        }
        units["compressionPercent"] = "%"; units["positionPercent"] = "%"; units["throttlePercent"] = "%"; units["n1Percent"] = "%"; units["rpm"] = "rpm"; units["reversePercent"] = "%";
        return new JObject { ["schemaVersion"] = 1, ["seriesUnits"] = units, ["timestamps"] = "ISO8601 UTC", ["seriesTimeOrigin"] = "contact",
            ["unavailableNumericValue"] = null, ["legacyEpisodeIdentity"] = "legacy:<landingId> when the recording has no episode identifier",
            ["legacyAvailability"] = "Fields absent in old recordings follow their existing reader defaults; explicit optional or non-finite fields are null. Zero is a measured value, never a generic missing marker.",
            ["provenancePaging"] = new JObject { ["parameter"] = "section", ["sections"] = new JArray("metadata", "sourceEvidenceTimeline", "controllerCatalogs", "controllerTransitions"),
                ["offset"] = "Zero-based character offset for sourceEvidenceTimeline; item offset for controller sections.",
                ["sourceEvidenceTimeline"] = "Concatenate data chunks before base64 decoding. metadata includes section totals." },
            ["eventTypes"] = new JArray("LandingResultAvailable", "LandingResultUpdated"), ["eventRevision"] = "1=analysis committed,2=final metadata committed",
            ["resync"] = "On ResyncRequired, capture hello.events cursor, rebuild history with nextCursor, verify streamId unchanged, then replay after the captured cursor. Deletion resets streamId." };
    }
    private static string RequiredString(JObject parameters, string name, int max) => OptionalString(parameters, name, max) ?? throw new IntegrationApiException("InvalidParams", name + " is required.");
    private static string? OptionalString(JObject parameters, string name, int max)
    {
        var token = parameters[name]; if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type != JTokenType.String || token.Value<string>()!.Length > max || token.Value<string>()!.Length == 0) throw new IntegrationApiException("InvalidParams", name + " must be a non-empty bounded string.");
        return token.Value<string>();
    }
    private static long? OptionalLong(JObject parameters, string name, long min, long max)
    {
        var token = parameters[name]; if (token == null) return null;
        if (token.Type != JTokenType.Integer) throw new IntegrationApiException("InvalidParams", name + " must be an integer.");
        var value = token.Value<long>(); if (value < min || value > max) throw new IntegrationApiException("InvalidParams", name + " is outside the supported range."); return value;
    }
    private static int Integer(JObject parameters, string name, int fallback, int min, int max) => (int)(OptionalLong(parameters, name, min, max) ?? fallback);
    private static double? OptionalNumber(JObject parameters, string name)
    {
        var token = parameters[name]; if (token == null) return null;
        if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) throw new IntegrationApiException("InvalidParams", name + " must be a finite number.");
        var value = token.Value<double>(); if (!Finite(value)) throw new IntegrationApiException("InvalidParams", name + " must be a finite number."); return value;
    }
    private static DateTime? OptionalUtc(JObject parameters, string name)
    {
        var text = OptionalString(parameters, name, 64); if (text == null) return null;
        if (!text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value))
            throw new IntegrationApiException("InvalidParams", name + " must be an ISO8601 UTC timestamp ending in Z."); return value;
    }
}
