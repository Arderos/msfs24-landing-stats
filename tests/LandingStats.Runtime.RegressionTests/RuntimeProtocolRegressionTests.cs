using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.RegressionTests;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

internal static class RuntimeProtocolRegressionTests
{
    private static readonly Guid Session = Guid.Parse("e8c6a40a-5148-4f6a-98cb-cd23ba9515d4");
    private static readonly Guid Instance = Guid.Parse("e8c6a40a-5148-4f6a-98cb-cd23ba9515d5");
    private static readonly Guid Epoch = Guid.Parse("e8c6a40a-5148-4f6a-98cb-cd23ba9515d6");
    private static readonly string Token = new string('a', 64);
    private static WorkerIdentity Worker(long generation = 1) => new WorkerIdentity(Session, Instance, generation, ComponentRole.Simulator);
    private static RuntimeEnvelope Message(long sequence, RuntimePayload payload, long generation = 1, long connection = 1)
    {
        var role = payload is ControllerCatalogMessage || payload is ControllerSnapshotMessage ? ComponentRole.Controllers :
            payload is AnalysisJobMessage || payload is AnalysisResultMessage ? ComponentRole.Analysis :
            payload is UploadJobMessage || payload is UploadResultMessage ? ComponentRole.Upload :
            payload is CloudJobMessage || payload is CloudResultMessage ? ComponentRole.CloudSync : ComponentRole.Simulator;
        return new RuntimeEnvelope(new WorkerIdentity(Session, Instance, generation, role),
            role == ComponentRole.Simulator ? connection : 0, sequence, 100 + sequence, payload,
            payload is RuntimeCommand || payload is CommandResultMessage ? Instance : Guid.Empty,
            payload is RuntimeCommand || payload is CommandResultMessage ? 1 : 0,
            payload is SceneryChunkMessage ? Session : Guid.Empty, payload is SceneryChunkMessage ? Instance : Guid.Empty,
            payload is SceneryChunkMessage ? LookupStage.Final : LookupStage.None);
    }

    public static void RunAll()
    {
        RoundTripAndRejectMalformed();
        HandshakeAndClocks();
    }

    private static void RoundTripAndRejectMalformed()
    {
        RuntimePayload[] payloads =
        {
            new WorkerHello("build-0.9.4-test", Token, 3, Epoch, 10000000),
            new TelemetryFrameMessage(BinaryTelemetryRegressionTests.Vector(4)),
            new GuardFrameMessage(1.2, -0.0, false, double.NaN, true),
            new LifecycleMessage(SourceLifecycle.PauseFlags, 5),
            new LifecycleMessage(SourceLifecycle.PhysicsReady),
            new AircraftMessage("MSFS 2024", "Самолёт — 測試", "A320", "Fenix"),
            new ComponentHealthMessage(ComponentPhase.Waiting, DependencyWait.Storage, 4, 2, 100, 90, 1000, Instance),
            new SourceGapMessage(SourceGapReason.BufferOverflow, 2, 20, true),
            new WorkerCommandMessage(WorkerCommand.RawCapture, true),
            new CommandResultMessage(CommandOutcome.Failed, "temporary dependency failure"),
            new DurableAckMessage(123), new ControlAckMessage(456),
            new ControllerCatalogMessage(1, new[] { new ControllerDescriptor(0, 2, "Ручка — 測試", "product", 3, new uint[6], Enumerable.Repeat(65535u, 6).ToArray()) }),
            new ControllerSnapshotMessage(1, new long[8], new double[48], new bool[48]),
            new SceneryCommandMessage(1, Session, 10, Instance, LookupStage.Final, 42.5, 23.5),
            new SceneryCommandMessage(1, Session, 10, Instance, LookupStage.Final, 42.5, 23.5, witnessQpc: 123456, resumeOnly: true),
            new SceneryChunkMessage(Instance, 0, 3, false, new byte[32], new byte[] { 1, 2, 3 }),
            new SourceDiagnosticMessage(12.5, "source", "observed", "Точное событие", new System.Collections.Generic.Dictionary<string, double> { ["value"] = 1.25 }),
            new SourceStatusMessage(3, "source.error", new[] { "test", "42" }),
            new SourceObservationMessage(ReplaySourceCoreRegressionTests.Evidence().Observation),
            new SourceObservationMessage(ReplaySourceCoreRegressionTests.Evidence(availability: SourceObservationAvailability.Unavailable).Observation),
            new AnalysisJobMessage(Instance, Session, Token, "D:\\Inputs", "D:\\Result"),
            new AnalysisJobMessage(Instance, Session, Token, "D:\\Inputs", "D:\\Result", finalizeReport: true),
            new AnalysisJobMessage(Instance, Session, Token, "D:\\Session", "D:\\Result", AnalysisOperation.RecoverRecording),
            new AnalysisJobMessage(Instance, Session, Token, "D:\\Inputs", "D:\\Result", AnalysisOperation.RefreshGeometryCatalog, "SunRise"),
            new AnalysisJobMessage(Instance, Session, Token, "D:\\Inputs", "D:\\Result", AnalysisOperation.ResolvePreparedGeometry),
            new AnalysisResultMessage(Instance, Token, 1024),
            new AnalysisResultMessage(Instance, "", 0, "InvalidDataException"),
            new UploadJobMessage(Session, Instance, Token, "D:\\Queue\\test.zip", "D:\\Identity", 123, true),
            new UploadResultMessage(Session, Instance, Token, 123, UploadResultKind.Uploaded, 200, ""),
            new UploadResultMessage(Session, Instance, Token, 123, UploadResultKind.Uploaded, 200, "", Token, true),
            new UploadResultMessage(Session, Instance, Token, 123, UploadResultKind.Deferred, 0, "", Token, false),
            new UploadResultMessage(Session, Instance, Token, 123, UploadResultKind.Deferred, 503, "temporary"),
            new UploadResultMessage(Session, Instance, Token, 123, UploadResultKind.Rejected, 422, "invalid_capture"),
            new CloudJobMessage(Instance, 3, "D:\\CloudJob", Token, Token),
            new CloudResultMessage(Instance, 3, Token, 256),
            new CloudResultMessage(Instance, 3, "", 0, "InvalidDataException"),
        };
        foreach (var payload in payloads)
        {
            var original = Message(1, payload);
            var bytes = RuntimeWire.Encode(original);
            var roundtrip = RuntimeWire.Decode(bytes);
            Assert(RuntimeWire.Encode(roundtrip).SequenceEqual(bytes), "wire roundtrip changed bytes: " + payload.Kind);
            Assert(roundtrip.Worker.Equals(original.Worker) && roundtrip.CaptureQpc == original.CaptureQpc, "identity/time changed");
            for (var cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 19))
            {
                var truncated = bytes.Take(cut).ToArray();
                Reject(() => RuntimeWire.Decode(truncated));
            }
            Reject(() => RuntimeWire.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
            foreach (var offset in new[] { 0, 4, 6 })
            {
                var invalid = (byte[])bytes.Clone(); invalid[offset] = 0xff; invalid[offset + 1] = 0xff;
                Reject(() => RuntimeWire.Decode(invalid));
            }
            foreach (var length in new[] { -1, int.MaxValue, RuntimeWire.MaximumPayloadLength + 1, 0 })
            {
                var invalid = (byte[])bytes.Clone(); Array.Copy(BitConverter.GetBytes(length), 0, invalid, 8, 4);
                Reject(() => RuntimeWire.Decode(invalid));
                Reject(() => RuntimeWire.ReadAsync(new FragmentedStream(invalid), CancellationToken.None).GetAwaiter().GetResult());
            }
            using var fragmented = new FragmentedStream(bytes.Concat(bytes).ToArray());
            for (var i = 0; i < 2; i++)
                Assert(RuntimeWire.Encode(RuntimeWire.ReadAsync(fragmented, CancellationToken.None).GetAwaiter().GetResult()!).SequenceEqual(bytes), "partial reads changed a message");
            Assert(RuntimeWire.ReadAsync(fragmented, CancellationToken.None).GetAwaiter().GetResult() == null, "EOF fabricated message");
            Reject(() => RuntimeWire.ReadAsync(new FragmentedStream(bytes.Take(bytes.Length - 1).ToArray()), CancellationToken.None).GetAwaiter().GetResult());
        }
        var sample = BinaryTelemetryRegressionTests.Vector(2);
        var immutable = new TelemetryFrameMessage(sample);
        var before = RuntimeWire.Encode(Message(1, immutable));
        sample.ContactPointCompression[0] = 123;
        immutable.CopySample().ContactPointCompression[1] = 456;
        Assert(before.SequenceEqual(RuntimeWire.Encode(Message(1, immutable))), "published payload shared mutable sample storage");

        var invalidBool = RuntimeWire.Encode(Message(1, new GuardFrameMessage(1, 2, true, 3, true)));
        invalidBool[RuntimeWire.HeaderLength + 16] = 2;
        Reject(() => RuntimeWire.Decode(invalidBool));
        var invalidString = RuntimeWire.Encode(Message(1, new AircraftMessage("MSFS", "test", "type", "model")));
        invalidString[RuntimeWire.HeaderLength + 4] = 0xff;
        Reject(() => RuntimeWire.Decode(invalidString));
        RejectArgument(() => new RuntimeEnvelope(new WorkerIdentity(Session, Instance, 1, ComponentRole.Controllers), 0, 1, 1, new LifecycleMessage(SourceLifecycle.SimulatorQuit)));
        RejectArgument(() => new RuntimeEnvelope(Worker(), 0, 1, 1, new WorkerCommandMessage(WorkerCommand.Stop)));
    }

    private static void HandshakeAndClocks()
    {
        var clock = new RuntimeClockEpoch(Epoch, 100, 1000);
        var handshake = new WorkerHandshake(Worker(), "build", Token, 3, clock);
        Reject(() => handshake.Accept(Message(1, new WorkerHello("build", new string('b', 64), 3, Epoch, 1000), connection: 0)));
        Reject(() => handshake.Accept(Message(1, new WorkerHello("build", Token, 1, Epoch, 1000), connection: 0)));
        Reject(() => handshake.Accept(Message(1, new WorkerHello("old", Token, 3, Epoch, 1000), connection: 0)));
        Reject(() => handshake.Accept(Message(1, new WorkerHello("build", Token, 3, Instance, 1000), connection: 0)));
        Reject(() => handshake.Accept(Message(1, new WorkerHello("build", Token, 3, Epoch, 1000), generation: 2, connection: 0)));
        var valid = Message(1, new WorkerHello("build", Token, 3, Epoch, 1000), connection: 0);
        handshake.Accept(valid);
        Reject(() => handshake.Accept(valid));
        Assert(clock.ElapsedSeconds(Epoch, 1600, 1000) == 1.5, "QPC origin changed");
        Reject(() => clock.ElapsedSeconds(Instance, 1600, 1000));
        Reject(() => clock.ElapsedSeconds(Epoch, 1600, 10000000));
        var episode = StableRuntimeIdentity.Episode(Session, 100);
        // Independent SHA-256 vectors: .NET GUID bytes, BinaryWriter UTF-8
        // string prefix, little-endian Int64/Int32. Persisted IDs must not drift.
        Assert(episode == Guid.Parse("cabc0629-927d-a0c7-48a7-95251eba44dd"), "episode identity changed persisted v1 bytes");
        Assert(episode == StableRuntimeIdentity.Episode(Session, 100) && episode != StableRuntimeIdentity.Episode(Session, 101), "unstable episode identity");
        var segment = StableRuntimeIdentity.Segment(episode, 100, 0);
        Assert(segment == Guid.Parse("e202601b-3741-8298-3551-e022d9b80d1f"), "segment identity changed persisted v1 bytes");
        Assert(StableRuntimeIdentity.Contact(segment, 140, 0) == Guid.Parse("1e6e501d-c1e6-a9b1-58b9-0f03a5441d67"), "contact identity changed persisted v1 bytes");
        Assert(StableRuntimeIdentity.Lookup(episode, 123, 2) == Guid.Parse("a52a258f-7b9a-7db2-25e7-b214a4e86bd4"), "lookup identity changed persisted v1 bytes");
        Assert(StableRuntimeIdentity.Command(episode, 3) == Guid.Parse("207acd68-1327-6984-024c-15cdf1188b6b"), "command identity changed persisted v1 bytes");
        Assert(segment != episode && StableRuntimeIdentity.Contact(segment, 140, 0) != StableRuntimeIdentity.Contact(segment, 140, 1), "identity scope collision");
        Console.WriteLine("episode=" + episode + ";segment=" + segment + ";contact=" + StableRuntimeIdentity.Contact(segment, 140, 0));
    }

    private sealed class FragmentedStream : MemoryStream
    {
        public FragmentedStream(byte[] bytes) : base(bytes, false) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(3, count), cancellationToken);
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("malformed protocol input accepted"); }
    private static void RejectArgument(Action action) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("invalid contract accepted"); }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
