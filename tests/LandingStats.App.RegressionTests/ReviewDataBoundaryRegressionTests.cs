using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ReviewDataBoundaryRegressionTests
{
    internal static void RunAll()
    {
        foreach (var mutate in new Action<TelemetrySample>[]
        {
            s => s.AnalysisTimeSeconds = double.PositiveInfinity,
            s => s.SimulatorState = (TelemetrySimulatorState)99,
            s => s.FlightSegment = -1, s => s.PauseFlags = -2,
        })
        {
            var sample = new TelemetrySample(); mutate(sample);
            Reject(() => TelemetryBinaryCodec.Encode(sample));
            Reject(() => TelemetryCsv.Format(sample));
        }
        var valid = new TelemetrySample { AnalysisTimeSeconds = double.NaN, PauseFlags = -1 };
        Check(double.IsNaN(TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(valid)).AnalysisTimeSeconds),
            "The supported unknown analysis clock did not round-trip.");
        var root = Path.Combine(Path.GetTempPath(), "review-data-boundaries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var airports = new AirportFacilityRepository(Path.Combine(root, "airports.gz"));
            airports.MergeAndSave(Enumerable.Range(0, 32768).Select(i => new AirportFacility { Ident = "A" + i.ToString("D5") }));
            airports.MergeAndSave(new[] { new AirportFacility { Ident = "YSSY" }, new AirportFacility { Ident = "A32767", AltitudeMeters = 123 } });
            var loaded = airports.Load();
            Check(loaded.Count == 32768 && loaded.Any(a => a.Ident == "YSSY") && loaded.Single(a => a.Ident == "A32767").AltitudeMeters == 123,
                "A full airport cache discarded a newly observed or refreshed facility.");

            var identityRoot = Path.Combine(root, "identity");
            var identity = new TelemetryUploadIdentityStore(identityRoot);
            identity.AcceptConsent(); var before = identity.Identity().InstallId;
            var path = Path.Combine(identityRoot, "identity.json");
            var original = File.ReadAllText(path);
            File.WriteAllText(path, Regex.Replace(original, "\"protected_private_key\":\"[^\"]*\"", "\"protected_private_key\":\"not-base64\""));
            var recovered = new TelemetryUploadIdentityStore(identityRoot);
            Check(recovered.ConsentAccepted && recovered.Identity().InstallId != before && recovered.Identity().Sign(new byte[] { 1 }).Length > 0,
                "An unreadable private key did not recover with preserved consent and a usable new identity.");
            Check(Directory.GetFiles(identityRoot, "identity.json.unreadable-*").Length == 1, "Unreadable identity evidence was lost.");
            var recoveredBytes = File.ReadAllBytes(path);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { new TelemetryUploadIdentityStore(identityRoot).Identity(); throw new Exception("A locked identity was accepted."); }
                catch (IOException) { }
            }
            Check(File.ReadAllBytes(path).SequenceEqual(recoveredBytes), "A transient lock rotated a valid identity.");
            var originalRecoveredId = recovered.Identity().InstallId;
            var unavailableKey = Convert.ToBase64String(ProtectedData.Protect(new byte[] { 1, 2, 3 },
                Encoding.UTF8.GetBytes("different protection context"), DataProtectionScope.CurrentUser));
            File.WriteAllText(path, Regex.Replace(Encoding.UTF8.GetString(recoveredBytes),
                "\"protected_private_key\":\"[^\"]*\"", "\"protected_private_key\":\"" + unavailableKey + "\""));
            var unavailableBytes = File.ReadAllBytes(path);
            var retrying = new TelemetryUploadIdentityStore(identityRoot);
            try { retrying.Identity(); throw new Exception("An unavailable DPAPI key was silently replaced."); }
            catch (CryptographicException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(unavailableBytes) &&
                Directory.GetFiles(identityRoot, "identity.json.unreadable-*").Length == 1,
                "DPAPI failure rotated enrollment or moved the original identity.");
            File.WriteAllBytes(path, recoveredBytes);
            Check(retrying.Identity().InstallId == originalRecoveredId && retrying.ConsentAccepted,
                "Recovery of access to the protected key did not reuse the original identity.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Reject(Action action)
    {
        try { action(); throw new Exception("The writer accepted a lifecycle value rejected by its reader."); }
        catch (InvalidDataException) { }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
