using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.RegressionTests;

internal static class FollowupStorageRegressionTests
{
    internal static void RunAll()
    {
        UnpublishedReplacementPreservesMetadata(false);
        UnpublishedReplacementPreservesMetadata(true);
        RetiredEmptyRecoveryAllowsImport();
        DetailProvenanceFollowsPublishedBytes();
        foreach (var kind in new[] { "bad-json", "bad-identity", "bad-name", "future-detail", "null-contact", "null-engine" })
        {
            var root = Path.Combine(Path.GetTempPath(), "followup-pending-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new LandingRepository(root);
                var ordinary = new LandingRecord { Id = "ordinary", AircraftTitle = "Ordinary" };
                var imported = new LandingRecord { Id = "imported", Airport = "ORIGINAL" };
                repository.Save(ordinary); repository.Save(imported);
                ordinary.Airport = "INDEX-ONLY";
                repository.UpdateSummary(ordinary);
                imported.Airport = "IMPORTED";
                var supportedBytes = repository.SerializeForBackup(imported);
                repository.BeginBatch(); // Model exit before publishing the index.
                repository.Import(supportedBytes, imported.Id, true);
                var pending = Path.Combine(root, ".pending-imports");
                var marker = Directory.GetFiles(pending, "*.json").Single();
                var commit = JObject.Parse(File.ReadAllText(marker));
                var detail = Path.Combine(root, (string)commit["DetailName"]!);
                if (kind == "bad-json") File.WriteAllText(marker, "{broken");
                else if (kind == "bad-identity") { commit["DetailName"] = "../outside"; File.WriteAllText(marker, commit.ToString()); }
                else if (kind == "bad-name") { commit["DetailName"] = "invalid\0name"; File.WriteAllText(marker, commit.ToString()); }
                else
                {
                    JObject payload;
                    using (var stream = new GZipStream(new MemoryStream(supportedBytes), CompressionMode.Decompress))
                    using (var reader = new StreamReader(stream)) payload = JObject.Parse(reader.ReadToEnd());
                    if (kind == "future-detail") payload["summary"]!["FormatVersion"] = LandingRecord.CurrentFormatVersion + 1;
                    else payload[kind == "null-contact" ? "contacts" : "engines"] = new JArray(JValue.CreateNull());
                    using (var file = File.Create(detail))
                    using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                    using (var writer = new StreamWriter(gzip, new UTF8Encoding(false))) writer.Write(payload.ToString());
                    using var sha = SHA256.Create();
                    commit["DetailHash"] = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(detail)));
                    File.WriteAllText(marker, commit.ToString());
                }
                var retainedDetail = File.ReadAllBytes(detail);
                var retainedMarker = File.ReadAllBytes(marker);
                var retainedIndex = File.ReadAllBytes(repository.IndexPath);
                // Windows permits reading this index but refuses its atomic
                // replacement. Recovery must leave the intent active until a
                // corrected index can actually be published.
                using (var lockedIndex = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var failed = false;
                    try { new LandingRepository(root).LoadAll(); }
                    catch (IOException) { failed = true; }
                    Check(failed, "Index publication failure was not exercised: " + kind);
                    Check(File.Exists(marker),
                        "Recovery retired its intent before publishing the corrected index: " + kind);
                    var preserved = Directory.GetFiles(Path.Combine(pending, "quarantine"), "*.json").Single();
                    Check(File.ReadAllBytes(preserved).SequenceEqual(retainedMarker),
                        "Preparing recovery lost the original invalid intent: " + kind);
                    Check(File.ReadAllBytes(repository.IndexPath).SequenceEqual(retainedIndex), "Failed publication modified the index.");
                }
                var reopened = new LandingRepository(root);
                // The recovery intent is now valid but cannot be deleted while
                // this read handle is open. Index publication must still succeed,
                // and its newer generation must fence this intent on restart.
                using (var heldIntent = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var recovered = reopened.LoadAll();
                    Check(recovered.Single(r => r.Id == ordinary.Id).Airport == "INDEX-ONLY", "Recovery changed unrelated index metadata: " + kind);
                    if (kind.StartsWith("bad-", StringComparison.Ordinal))
                    {
                        var affected = recovered.Single(r => r.Id == imported.Id);
                        Check(affected.Airport == "IMPORTED", "Broken intent left stale replacement metadata: " + kind);
                        affected.Airport = "AFTER-RECOVERY";
                        reopened.UpdateSummary(affected);
                    }
                    else
                        Check(recovered.All(r => r.Id != imported.Id), "Unsupported detail retained a stale summary: " + kind);
                    Check(File.Exists(marker), "Fixture did not retain the undeletable recovery intent: " + kind);
                }
                var restarted = new LandingRepository(root);
                var afterRestart = restarted.LoadAll();
                Check(afterRestart.Single(r => r.Id == ordinary.Id).Airport == "INDEX-ONLY", "Cleanup retry changed unrelated index metadata: " + kind);
                if (kind.StartsWith("bad-", StringComparison.Ordinal))
                    Check(afterRestart.Single(r => r.Id == imported.Id).Airport == "AFTER-RECOVERY",
                        "A stale recovery intent overwrote metadata committed after recovery: " + kind);
                else
                    Check(afterRestart.All(r => r.Id != imported.Id), "Cleanup retry restored an unreadable detail summary: " + kind);
                Check(File.ReadAllBytes(detail).SequenceEqual(retainedDetail), "Recovery changed unsupported immutable detail.");
                var quarantined = Directory.GetFiles(Path.Combine(pending, "quarantine"), "*.json").Single();
                Check(File.ReadAllBytes(quarantined).SequenceEqual(retainedMarker), "Recovery did not preserve the original import intent.");
                if (kind.StartsWith("bad-", StringComparison.Ordinal))
                {
                    imported.Airport = "NEXT-IMPORT";
                    restarted.Import(restarted.SerializeForBackup(imported), imported.Id, true);
                    Check(restarted.LoadAll().Single(r => r.Id == imported.Id).Airport == "NEXT-IMPORT",
                        "An old invalid intent blocked replacement after its cleanup obstruction cleared: " + kind);
                }
                reopened = restarted;
                reopened.Save(new LandingRecord { Id = "later" });
                Check(reopened.Delete("later"), "Unrelated history mutations remain blocked.");
                Check(new LandingRepository(root).LoadAll().Any(r => r.Id == ordinary.Id), "Reopening history reintroduced the poison marker.");
                if (kind == "future-detail")
                {
                    // Once the detail becomes readable (e.g. upgrading again),
                    // reconciliation must use its imported metadata, not the old row.
                    File.WriteAllBytes(detail, supportedBytes);
                    Check(new LandingRepository(root).LoadAll().Single(r => r.Id == imported.Id).Airport == "IMPORTED",
                        "Readable retained detail failed to recover after unrelated history commits.");
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
    private static void DetailProvenanceFollowsPublishedBytes()
    {
        foreach (var version in new[] { 6, 7 })
        {
            var root = Path.Combine(Path.GetTempPath(), "detail-provenance-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new LandingRepository(root);
                var record = new LandingRecord { Id = "provenance", FormatVersion = version, Airport = "ORIGINAL" };
                var detail = repository.Save(record);
                void MatchesPublishedBytes()
                {
                    JArray index;
                    using (var gzip = new GZipStream(File.OpenRead(repository.IndexPath), CompressionMode.Decompress))
                    using (var reader = new StreamReader(gzip)) index = JArray.Parse(reader.ReadToEnd());
                    using var sha = SHA256.Create();
                    Check((string?)index.Single()!["_IndexDetailHash"] == Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(detail))),
                        "Index provenance differs from complete published compressed bytes, format " + version);
                }
                MatchesPublishedBytes();
                var before = File.ReadAllBytes(detail); var indexBefore = File.ReadAllBytes(repository.IndexPath);
                using (var held = new FileStream(detail, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    record.Airport = "REPLACED";
                    var failed = false;
                    try { repository.Save(record); } catch (IOException) { failed = true; }
                    Check(failed && File.ReadAllBytes(detail).SequenceEqual(before) && File.ReadAllBytes(repository.IndexPath).SequenceEqual(indexBefore),
                        "Failed publication changed committed bytes or provenance.");
                }
                repository.Save(record); MatchesPublishedBytes();
                File.Delete(repository.IndexPath);
                repository = new LandingRepository(root);
                Check(repository.LoadAll().Single().Airport == "REPLACED", "Index reconstruction lost readable detail metadata.");
                MatchesPublishedBytes();
                // A temporarily locked orphan must not stop recovery of other
                // history; once readable it is adopted with its own fingerprint.
                File.Delete(repository.IndexPath);
                using (var held = new FileStream(detail, FileMode.Open, FileAccess.Read, FileShare.None))
                    Check(new LandingRepository(root).LoadAll().Count == 0, "A locked orphan blocked history loading.");
                repository = new LandingRepository(root);
                Check(repository.LoadAll().Single().Airport == "REPLACED", "An unlocked orphan did not recover.");
                MatchesPublishedBytes();
                var saved = repository.LoadAll().Single(); saved.Airport = "INDEX-ONLY"; repository.UpdateSummary(saved);
                repository.SaveRecovered(record);
                Check(repository.LoadAll().Single().Airport == "REPLACED", "Recovered canonical detail was not restored.");
                MatchesPublishedBytes();
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    private static void UnpublishedReplacementPreservesMetadata(bool legacyIndex)
    {
        var root = Path.Combine(Path.GetTempPath(), "unpublished-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(root);
            var record = new LandingRecord { Id = "affected", Airport = "DETAIL-OLD", Runway = "01", AirportDistanceNauticalMiles = 1 };
            var detail = repository.Save(record);
            var originalDetail = File.ReadAllBytes(detail);
            record.Airport = "INDEX-NEW"; record.Runway = "02"; record.AirportDistanceNauticalMiles = 2;
            repository.UpdateSummary(record);
            if (legacyIndex)
            {
                JArray index;
                using (var gzip = new GZipStream(File.OpenRead(repository.IndexPath), CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip))
                using (var json = new Newtonsoft.Json.JsonTextReader(reader) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                    index = JArray.Load(json);
                foreach (var item in index.OfType<JObject>()) item.Remove("_IndexDetailHash");
                using (var gzip = new GZipStream(File.Create(repository.IndexPath), CompressionLevel.Optimal))
                using (var writer = new StreamWriter(gzip)) writer.Write(index.ToString());
                repository = new LandingRepository(root);
                Check(repository.LoadAll().Single().Airport == "INDEX-NEW", "Legacy fixture must retain its readable index metadata.");
            }
            record.Airport = "REMOTE"; record.Runway = "03"; record.AirportDistanceNauticalMiles = 3;
            var remote = repository.SerializeForBackup(record);
            using (var gzip = new GZipStream(new MemoryStream(remote), CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
                Check(!reader.ReadToEnd().Contains("_IndexDetailHash"), "Index provenance leaked into a portable detail.");
            if (legacyIndex)
            {
                repository.BeginBatch(); // Retry must establish a durable baseline even within the same batch.
                using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var failed = false;
                    try { repository.Import(remote, record.Id, true); } catch (IOException) { failed = true; }
                    Check(failed, "Fixture did not prevent legacy provenance publication.");
                }
            }
            using (var held = new FileStream(detail, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try { repository.Import(remote, record.Id, true); } catch (IOException) { failed = true; }
                Check(failed, "Fixture did not prevent detail replacement.");
            }
            Check(File.ReadAllBytes(detail).SequenceEqual(originalDetail), "Unpublished replacement altered detail bytes.");
            var marker = Directory.GetFiles(Path.Combine(root, ".pending-imports"), "*.json").Single();
            File.WriteAllText(marker, "{broken");
            using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try { new LandingRepository(root).LoadAll(); } catch (IOException) { failed = true; }
                Check(failed, "Fixture did not prevent recovery index publication.");
            }
            using (var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var recovered = new LandingRepository(root).LoadAll().Single();
                Check(recovered.Airport == "INDEX-NEW" && recovered.Runway == "02" && recovered.AirportDistanceNauticalMiles == 2,
                    "Corrupt intent rolled back index-only metadata although detail never changed; legacy=" + legacyIndex);
                Check(File.Exists(marker), "Fixture did not prevent receipt cleanup.");
            }
            Check(new LandingRepository(root).LoadAll().Single().Airport == "INDEX-NEW", "Retired receipt rolled back metadata.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void RetiredEmptyRecoveryAllowsImport()
    {
        var root = Path.Combine(Path.GetTempPath(), "empty-import-receipt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(Path.Combine(root, "target"));
            var source = new LandingRepository(Path.Combine(root, "source"));
            repository.Save(new LandingRecord { Id = "ordinary" });
            var incoming = new LandingRecord { Id = "new-import", Airport = "INCOMING" };
            var sourcePath = source.Save(incoming);
            var bytes = source.Export(incoming.Id);
            var blocked = Path.Combine(repository.RootPath, Path.GetFileName(sourcePath));
            Directory.CreateDirectory(blocked);
            var failed = false;
            try { repository.Import(bytes, incoming.Id); } catch (IOException) { failed = true; }
            Check(failed, "Fixture did not prevent new detail publication.");
            Directory.Delete(blocked);
            var marker = Directory.GetFiles(Path.Combine(repository.RootPath, ".pending-imports"), "*.json").Single();
            File.WriteAllText(marker, "{broken");
            using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                failed = false;
                try { new LandingRepository(repository.RootPath).LoadAll(); } catch (IOException) { failed = true; }
                Check(failed, "Fixture did not prevent index publication.");
            }
            var receipt = JObject.Parse(File.ReadAllText(marker));
            Check((string?)receipt["Id"] == "" && (bool)receipt["ReconcileDetail"]!, "Expected an empty recovery receipt.");
            using (var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                repository = new LandingRepository(repository.RootPath);
                repository.LoadAll();
                Check(File.Exists(marker), "Fixture did not retain receipt after index publication.");
            }
            repository.Import(bytes, incoming.Id);
            Check(new LandingRepository(repository.RootPath).LoadAll().Single(item => item.Id == incoming.Id).Airport == "INCOMING",
                "Retired empty receipt blocked import of the same landing.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
