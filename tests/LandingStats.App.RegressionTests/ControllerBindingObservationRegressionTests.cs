using System;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ControllerBindingObservationRegressionTests
{
    internal static void RunAll()
    {
        var session = Guid.NewGuid(); var clock = new RuntimeClockEpoch(Guid.NewGuid(), 100, 1000);
        var controllers = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Controllers);
        var simulator = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Simulator);
        var joiner = new ControllerJoiner(clock); joiner.Activate(controllers);
        RuntimeEnvelope Input(long sequence, long capture, RuntimePayload payload) => new RuntimeEnvelope(controllers, 0, sequence, capture, payload);
        joiner.Accept(Input(1, 100, new WorkerHello("test", new string('a', 64), 1, clock.Id, clock.Frequency)));
        var descriptors = Enumerable.Range(0, 12).Select(index => new ControllerDescriptor(index, index, "Device " + index, "product", 255,
            new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), Guid.NewGuid(), Guid.NewGuid())).ToArray();
        joiner.Accept(Input(2, 101, new ControllerCatalogMessage(1, descriptors)));
        var values = new double[96]; var valid = new bool[96]; values[95] = 40; valid[95] = true;
        joiner.Accept(Input(3, 110, new ControllerSnapshotMessage(1, Enumerable.Repeat(109L, 12).ToArray(), values, valid, 8)));
        var binding = new ControllerBinding(ControllerControlKind.Throttle, 0, descriptors[11].InstanceId, 7, true, "saved profile");
        joiner.ObserveAircraftControls(new RuntimeEnvelope(simulator, 1, 2, 111, new AircraftControlsMessage("aircraft/config", "first profile", new[] { binding })));
        var saved = joiner.Join(120).Encode();
        var checkpoint = joiner.CaptureCheckpoint();
        var second = new ControllerBinding(ControllerControlKind.Roll, -1, descriptors[11].InstanceId, 7, false, "new profile");
        joiner.ObserveAircraftControls(new RuntimeEnvelope(simulator, 1, 3, 130, new AircraftControlsMessage("aircraft/config", "second profile", new[] { second })));
        Check(((ControllerCatalogMessage)joiner.Join(140).Catalog!.Payload).Bindings.Single().Control == ControllerControlKind.Roll, "A new frame did not use the newly observed assignment.");
        var sample = new TelemetrySample(); ControllerJoinDecision.Decode(saved).Apply(sample);
        var recorded = (ControllerCatalogMessage)ControllerProvenanceCodec.Decode(sample.ControllerCatalog!)!.Payload;
        Check(sample.ControllerCount == 12 && sample.ControllerAxisCount == 8 && sample.RawControllerAxesPercent[95] == 40 && sample.RawControllerAxesValid[95], "The recorded observation lost a device or axis.");
        Check(recorded.Bindings.Single().Control == ControllerControlKind.Throttle && recorded.Bindings.Single().Inverted, "Replay consulted the later profile instead of its recorded assignment.");
        var restored = ControllerJoiner.RestoreCheckpoint(checkpoint, clock);
        Check(((ControllerCatalogMessage)restored.Join(125).Catalog!.Payload).BindingContext == "first profile", "Recovery lost the selected profile observation.");
        joiner.ClearAircraftControls();
        Check(((ControllerCatalogMessage)joiner.Join(150).Catalog!.Payload).Bindings.Count == 0, "A retired simulator context retained aircraft assignments.");
        // A saved mapping remains observable even if no device is attached at application startup.
        var absent = new ControllerCatalogMessage(2, Array.Empty<ControllerDescriptor>(), new[] { binding }, "known configuration");
        var decoded = (ControllerCatalogMessage)RuntimeWire.Decode(RuntimeWire.Encode(Input(4, 160, absent))).Payload;
        Check(decoded.Devices.Count == 0 && decoded.Bindings.Single().DeviceInstanceId == binding.DeviceInstanceId, "An absent controller erased its saved assignment.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
