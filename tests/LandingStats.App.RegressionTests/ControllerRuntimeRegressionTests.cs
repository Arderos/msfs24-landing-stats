using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ControllerRuntimeRegressionTests
{
    public static void RunJoiner()
    {
        var session = Guid.NewGuid(); var epoch = new RuntimeClockEpoch(Guid.NewGuid(), 100, 1000);
        var worker = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Controllers);
        var joiner = new ControllerJoiner(epoch); var sample = new TelemetrySample();
        joiner.Join(100).Apply(sample); Check(sample.RawControllerAxesValid.All(value => !value), "Missing source fabricated axes.");
        joiner.Activate(worker);
        RuntimeEnvelope Wrap(long sequence, long time, RuntimePayload payload) => new RuntimeEnvelope(worker, 0, sequence, time, payload);
        joiner.Accept(Wrap(1, 100, new WorkerHello("test", new string('a', 64), 1, epoch.Id, epoch.Frequency)));
        var catalog = Catalog(1, "Stick A");
        joiner.Accept(Wrap(2, 101, catalog));
        var first = Wrap(3, 110, Snapshot(1, 109, -25)); joiner.Accept(first);
        var future = Wrap(4, 150, Snapshot(1, 149, 75)); joiner.Accept(future);
        var decision = joiner.Join(125); decision.Apply(sample);
        Check(sample.RawControllerAxesPercent[1] == -25 && sample.RawControllerAxesValid[1] && sample.RawControllerAxesAgeSeconds[1] == 0.016,
            "Join selected a future measurement or fabricated its age.");
        Check(sample.RawControllerYAxisPercent[0] == -25 && sample.RawControllerYAxisValid[0] && sample.RawControllerYAxisAgeSeconds[0] == 0.016,
            "Legacy Y projection diverged from the joined axes.");
        var persisted = decision.Encode();
        joiner.Accept(Wrap(5, 200, Catalog(2, "Pedals B")));
        var afterHotplug = joiner.Join(205); afterHotplug.Apply(sample);
        Check(afterHotplug.Availability == ControllerJoinAvailability.NoSnapshot && sample.RawControllerAxesValid.All(value => !value), "New catalog reused axes from the old device.");
        joiner.Accept(Wrap(6, 210, Snapshot(2, 209, 60)));
        var current = joiner.Join(220); current.Apply(sample);
        Check(sample.RawControllerAxesPercent[1] == 60 && ((ControllerCatalogMessage)current.Catalog!.Payload).Devices[0].Name == "Pedals B", "Hotplug provenance was not retained.");
        ControllerJoinDecision.Decode(persisted).Apply(sample);
        Check(sample.RawControllerAxesPercent[1] == -25 && sample.RawControllerAxesAgeSeconds[1] == 0.016, "Recovery recomputed the join using later input.");
        var stale = joiner.Join(500); stale.Apply(sample);
        Check(stale.Availability == ControllerJoinAvailability.Stale && !sample.RawControllerAxesValid[1] && sample.RawControllerAxesAgeSeconds[1] == 0.291,
            "Stale values masqueraded as fresh or lost their measured age.");
        joiner.Retire(worker); joiner.Join(500).Apply(sample);
        Check(sample.RawControllerDeviceId.All(value => value == -1) && sample.RawControllerAxesValid.All(value => !value), "Retired owner still supplied axes.");
        var old = worker; worker = new WorkerIdentity(session, Guid.NewGuid(), 2, ComponentRole.Controllers); joiner.Activate(worker);
        Check(!joiner.Accept(first), "Old generation was admitted after recovery.");
        joiner.Accept(Wrap(1, 501, new WorkerHello("test", new string('a', 64), 1, epoch.Id, epoch.Frequency)));
        var ambiguous = new ControllerCatalogMessage(1, new[] { Descriptor(0, "Same", "same-product"), Descriptor(1, "Same", "same-product") });
        Check(ambiguous.IsAmbiguous(ambiguous.Devices[0]), "Identical controllers received a fabricated stable identity.");
        joiner.Accept(Wrap(2, 502, ambiguous));
        joiner.Accept(Wrap(3, 510, Snapshot(1, 509, 55)));
        joiner.Join(520).Apply(sample); Check(sample.RawControllerYAxisValid[0], "Fresh axes did not resume after replacement.");
        for (var i = 0; i < 2000; i++) joiner.Accept(Wrap(i + 4, 600 + i, Snapshot(1, 599 + i, 10)));
        Check(joiner.SnapshotCount == ControllerJoiner.MaximumSnapshots && joiner.CatalogCount == 1, "Join history exceeded its memory bound.");
        var savedJoiner = joiner.CaptureCheckpoint();
        joiner = ControllerJoiner.RestoreCheckpoint(savedJoiner, epoch);
        Check(joiner.CaptureCheckpoint().SequenceEqual(savedJoiner), "Joiner checkpoint changed its retained catalog or sample order.");
        Check(joiner.Join(2600).Encode().SequenceEqual(ControllerJoiner.RestoreCheckpoint(savedJoiner, epoch).Join(2600).Encode()), "Restored joiner selected a different measurement.");
        Reject(() => ControllerJoiner.RestoreCheckpoint(savedJoiner, new RuntimeClockEpoch(Guid.NewGuid(), 100, 1000)));
        Reject(() => ControllerJoiner.RestoreCheckpoint(savedJoiner.Concat(new byte[] { 0 }).ToArray(), epoch));
        Reject(() => joiner.Accept(Wrap(2004, 3000, Snapshot(2, 2999, 10))));
        Reject(() => joiner.Accept(Wrap(2004, 3000, Snapshot(1, 3001, 10))));
        Reject(() => ControllerJoinDecision.Decode(persisted.Concat(new byte[] { 0 }).ToArray()));
        for (var cut = 0; cut < persisted.Length; cut += 31) Reject(() => ControllerJoinDecision.Decode(persisted.Take(cut).ToArray()));
        joiner.Retire(worker); joiner.Activate(new WorkerIdentity(session, Guid.NewGuid(), 3, ComponentRole.Controllers));
        joiner.Retire(old); Check(joiner.Join(4000).Availability == ControllerJoinAvailability.NoSnapshot, "Stale retirement killed the new owner.");
    }
    public static void RunWorker() => Task.Run(Worker).GetAwaiter().GetResult();
    private static async Task Worker()
    {
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
        var epoch = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        using var cancel = new CancellationTokenSource();
        var transport = new TestTransport(worker); var api = new TestApi();
        var role = new ControllersWorkerRole(() => new WindowsJoystickReader(api), TimeSpan.FromMilliseconds(20));
        var run = role.RunAsync(new WorkerExecutionContext(worker, epoch, transport, cancel.Token));
        await Until(() => transport.Messages.Count(message => message.Payload is ControllerSnapshotMessage) >= 5, "Owner did not poll controllers.");
        api.Product = 2;
        await Until(() => transport.Messages.Any(message => message.Payload is ControllerCatalogMessage catalog && catalog.Revision == 2), "Hotplug did not publish a new catalog.");
        api.Available = false;
        await Until(() => transport.Messages.Any(message => message.Payload is ControllerCatalogMessage catalog && catalog.Revision == 3 && catalog.Devices.Count == 0), "Device disappearance left a stale catalog.");
        await Until(() => transport.Messages.Any(message => message.Payload is ComponentHealthMessage health && health.WaitingFor == DependencyWait.Device && health.UsefulProgress > 0), "No devices was mistaken for failed work.");
        transport.Commands.Enqueue(new RuntimeEnvelope(worker, 0, 1, Stopwatch.GetTimestamp(), new WorkerCommandMessage(WorkerCommand.Stop), Guid.NewGuid(), 1));
        await run;
        Check(api.ThreadIds.Distinct().Count() == 1 && api.MaximumConcurrentCalls == 1, "Enumeration and polling used multiple owners.");
        Check(transport.Messages.Any(message => message.Payload is CommandResultMessage result && result.Outcome == CommandOutcome.Completed), "Stop was not acknowledged by the owner.");
        var joiner = new ControllerJoiner(epoch); joiner.Activate(worker);
        var hello = new RuntimeEnvelope(worker, 0, 1, epoch.OriginQpc, new WorkerHello("fixture", new string('a', 64), 1, epoch.Id, epoch.Frequency));
        joiner.Accept(hello);
        long sequence = 1;
        foreach (var message in transport.Messages.Where(value => value.Payload is ControllerCatalogMessage || value.Payload is ControllerSnapshotMessage))
            joiner.Accept(new RuntimeEnvelope(worker, 0, ++sequence, message.CaptureQpc, message.Payload));
        Check(sequence >= 10, "Insufficient real owner output was checked.");
        using var failedCancellation = new CancellationTokenSource();
        var full = new TestTransport(worker) { FailOutput = true };
        var never = new TestApi();
        try
        {
            await new ControllersWorkerRole(() => new WindowsJoystickReader(never)).RunAsync(new WorkerExecutionContext(worker, epoch, full, failedCancellation.Token));
            throw new InvalidOperationException("Owner ignored output overflow.");
        }
        catch (IOException) { Check(never.ThreadIds.Count == 0, "Native initialization continued after transport failure."); }
    }
    public static void RunNativeProcess() => Task.Run(NativeProcess).GetAwaiter().GetResult();
    private static async Task NativeProcess()
    {
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
        var epoch = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var messages = new ConcurrentQueue<RuntimeEnvelope>(); ProcessWorkerHost? host = null;
        using (host = new ProcessWorkerHost(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), worker, epoch, async (message, _) =>
        { messages.Enqueue(message); await host!.AcknowledgeAsync(message.Sequence).ConfigureAwait(false); }))
        {
            await host.StartAsync(TimeSpan.FromSeconds(8), "controllers-local");
            await Until(() => messages.Count(value => value.Payload is ControllerSnapshotMessage) >= 30, "Native process did not produce controller snapshots.");
            Check(host.LastHealth != null && host.LastHealth.UsefulProgress > 0, "Native owner did not report useful work.");
            using (var process = Process.GetProcessById(host.ProcessId))
                Check(process.Modules.Cast<ProcessModule>().All(module => !module.ModuleName.Equals("SimConnect.dll", StringComparison.OrdinalIgnoreCase)), "Controller role loaded SimConnect.");
            await host.StopAsync(TimeSpan.FromSeconds(3));
            await host.SourceCompletion;
            var joiner = new ControllerJoiner(epoch); joiner.Activate(worker);
            foreach (var message in messages) joiner.Accept(message);
            var snapshots = messages.Where(value => value.Payload is ControllerSnapshotMessage).ToArray();
            var intervals = snapshots.Skip(1).Select((value, index) => (value.CaptureQpc - snapshots[index].CaptureQpc) / (double)Stopwatch.Frequency * 1000).OrderBy(value => value).ToArray();
            var devices = ((ControllerCatalogMessage)messages.First(value => value.Payload is ControllerCatalogMessage).Payload).Devices.Count;
            Console.WriteLine("controller-poll: devices=" + devices + ", samples=" + snapshots.Length + ", median_ms=" + intervals[intervals.Length / 2].ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ", p95_ms=" + intervals[(int)(intervals.Length * 0.95)].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
    private static ControllerDescriptor Descriptor(int slot, string name, string product) => new ControllerDescriptor(slot, slot, name, product, 3, new uint[6], Enumerable.Repeat(65535u, 6).ToArray());
    private static ControllerCatalogMessage Catalog(long revision, string name) => new ControllerCatalogMessage(revision, new[] { Descriptor(0, name, name) });
    private static ControllerSnapshotMessage Snapshot(long revision, long time, double value)
    { var times = new long[8]; times[0] = time; var values = new double[48]; values[1] = value; var valid = new bool[48]; valid[1] = true; return new ControllerSnapshotMessage(revision, times, values, valid); }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message); await Task.Delay(5); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Malformed controller data was accepted."); }
    private sealed class TestTransport : IWorkerTransport
    {
        private readonly WorkerIdentity _worker;
        private long _sequence;
        public TestTransport(WorkerIdentity worker) { _worker = worker; }
        public readonly ConcurrentQueue<RuntimeEnvelope> Messages = new ConcurrentQueue<RuntimeEnvelope>();
        public readonly ConcurrentQueue<RuntimeEnvelope> Commands = new ConcurrentQueue<RuntimeEnvelope>();
        public bool FailOutput;
        public int PendingCount => 0;
        public bool TryTakeCommand(out RuntimeEnvelope? command) => Commands.TryDequeue(out command);
        public bool TryPublish(RuntimePayload payload, long connectionGeneration, long captureQpc, Guid correlationId = default, long revision = 0,
            Guid episodeId = default, Guid lookupId = default, LookupStage stage = LookupStage.None)
        { if (FailOutput) return false; Messages.Enqueue(new RuntimeEnvelope(_worker, connectionGeneration, ++_sequence, captureQpc, payload, correlationId, revision, episodeId, lookupId, stage)); return true; }
    }
    private sealed class TestApi : WindowsJoystickReader.IJoystickApi
    {
        public volatile int Product = 1;
        public volatile bool Available = true;
        public ConcurrentBag<int> ThreadIds { get; } = new ConcurrentBag<int>();
        private int _active;
        public int MaximumConcurrentCalls;
        private void Begin() { ThreadIds.Add(Thread.CurrentThread.ManagedThreadId); MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, Interlocked.Increment(ref _active)); }
        private void End() { Interlocked.Decrement(ref _active); }
        public uint DeviceCount { get { Begin(); try { return 1; } finally { End(); } } }
        public bool TryGetState(uint joystickId, out WindowsJoystickReader.JoyInfoEx state)
        { Begin(); try { state = new WindowsJoystickReader.JoyInfoEx { XPosition = 200, YPosition = 300 }; return Available; } finally { End(); } }
        public bool TryGetCapabilities(uint joystickId, out WindowsJoystickReader.JoyCaps capabilities)
        {
            Begin(); try { capabilities = new WindowsJoystickReader.JoyCaps { ProductId = (ushort)Product, ProductName = "Fixture " + Product, AxisCount = 2, XMaximum = 1000, YMaximum = 1000, RegistryKey = "", OemDriver = "" }; return Available; }
            finally { End(); }
        }
    }
}
