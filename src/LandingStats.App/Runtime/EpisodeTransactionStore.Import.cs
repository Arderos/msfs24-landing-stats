using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LandingStats.App.Runtime;

internal sealed partial class EpisodeTransactionStore
{
    public EpisodeWorkManifest ImportInputs(EpisodeTransactionStore source, Guid id)
    {
        lock (Gate(id))
        {
            var incoming = source.Read(id);
            if (incoming.Stage != EpisodeWorkStage.InputsCommitted) throw new InvalidDataException("Recovery must supply immutable inputs, not another owner's side effects.");
            var incomingInputs = source.ReadInputs(incoming);
            var manifest = File.Exists(PathFor(id, "manifest.json")) ? Read(id) : new EpisodeWorkManifest
            {
                Id = incoming.Id, Engine = incoming.Engine, TimestampUtcTicks = incoming.TimestampUtcTicks, ArchiveId = incoming.ArchiveId,
                AutomaticAllowedAtCapture = incoming.AutomaticAllowedAtCapture, PolicyRevision = incoming.PolicyRevision
            };
            if (manifest.Engine != incoming.Engine || manifest.TimestampUtcTicks != incoming.TimestampUtcTicks || manifest.AutomaticAllowedAtCapture != incoming.AutomaticAllowedAtCapture ||
                (manifest.Stage >= EpisodeWorkStage.InputsCommitted && manifest.InputHash != incoming.InputHash &&
                    RecoveryContentHash(ReadInputs(manifest)) != RecoveryContentHash(incomingInputs)))
                throw new InvalidDataException("Recovered input differs from the already accepted episode; both sources retained.");
            if (manifest.Stage < EpisodeWorkStage.InputsCommitted)
            {
                Save(manifest);
                CopyImmutable(source.PathFor(id, "inputs.bin.gz"), PathFor(id, "inputs.bin.gz"), incoming.InputHash);
                ReadInputFile(manifest, PathFor(id, "inputs.bin.gz"));
                manifest.InputHash = incoming.InputHash; manifest.Stage = EpisodeWorkStage.InputsCommitted; Save(manifest);
            }
            else ReadInputs(manifest);
            if (ValidHash(incoming.FacilityHash) && !ValidHash(manifest.FacilityHash))
            {
                var facilities = source.ReadFacilities(incoming);
                var destination = PathFor(id, "facilities.bin.gz");
                if (manifest.InputHash == incoming.InputHash)
                {
                    CopyImmutable(source.PathFor(id, "facilities.bin.gz"), destination, incoming.FacilityHash);
                    ReadFacilityFile(manifest, destination); manifest.FacilityHash = incoming.FacilityHash; Save(manifest);
                }
                else
                {
                    // A replay can recreate the same flight with a different advisory
                    // airport cache. Rebind verified facilities to the accepted bytes.
                    // Retain a pre-manifest file left by an interrupted older import.
                    if (File.Exists(destination))
                    {
                        try { ReadFacilityFile(manifest, destination); }
                        catch (InvalidDataException)
                        {
                            Verify(destination, incoming.FacilityHash);
                            File.Move(destination, destination + ".recovery-" + Guid.NewGuid().ToString("N"));
                        }
                    }
                    CommitFacilities(facilities);
                    manifest = Read(id);
                }
            }
            return manifest;
        }
    }
    private static string RecoveryContentHash(StoredEpisodeInput input)
    {
        // Airport cache is advisory, host-local metadata and is not in the WAL.
        // All immutable flight data must still match. Keep the already accepted
        // input bytes/hash and import only missing final facilities on replay.
        var known = input.Metadata.KnownAirports;
        input.Metadata.KnownAirports = Array.Empty<Models.AirportFacility>();
        try
        {
            using var hash = SHA256.Create();
            using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
            using var writer = new BinaryWriter(sink, Encoding.UTF8, true);
            WriteJson(writer, input.Metadata);
            WriteFrames(writer, input.Episode.Samples); WriteFrames(writer, input.Episode.InitialFacilities.ApproachSamples);
            writer.Flush(); sink.FlushFinalBlock();
            return BitConverter.ToString(hash.Hash!);
        }
        finally { input.Metadata.KnownAirports = known; }
    }
    internal static void CopyImmutable(string source, string destination, string hash)
    {
        if (File.Exists(destination)) { Verify(destination, hash); return; }
        Verify(source, hash);
        DurableFile.Write(destination, output => { using var input = File.OpenRead(source); input.CopyTo(output); });
        Verify(destination, hash);
    }
}
