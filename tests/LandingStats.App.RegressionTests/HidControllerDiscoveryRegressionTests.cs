using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class HidControllerDiscoveryRegressionTests
{
    internal static void RunAll()
    {
        var messages = new List<string>();
        var endpoints = new List<HidControllerEndpoint>();
        var sources = new Dictionary<Guid, Probe>();
        // Consumer-control TLC of a composite device. Reading its identity
        // represents the product-string call that hung in the field dump.
        var monitor = new Probe(1, @"\\?\hid#vid_1234&pid_5678&mi_03#monitor") { FailIfIdentityRead = true };
        sources.Add(monitor.Identity.InstanceId, monitor);
        endpoints.Add(new HidControllerEndpoint(monitor.Path, 0x0c, 1));
        for (var i = 0; i < 25; i++)
        {
            // Same VID/PID, distinct interfaces/instances. Identity must never
            // be inferred from product identity or enumeration order.
            var probe = new Probe(i + 2, @"\\?\hid#vid_1234&pid_5678&mi_00#controller-" + i);
            sources.Add(probe.Identity.InstanceId, probe);
            endpoints.Add(new HidControllerEndpoint(probe.Path, 1, i % 2 == 0 ? (ushort)4 : (ushort)5));
        }
        var ids = sources.Keys.ToArray();
        var first = HidControllerDiscovery.Discover(endpoints, ids.Concat(ids), id => sources[id].Open(), messages.Add);
        Check(first.Count == 25, "HID discovery truncated the controller list.");
        Check(monitor.IdentityReads == 0, "Non-game HID product information was requested.");
        Check(sources.Values.All(value => value.Opens == value.Disposals), "Discovery leaked a native interface.");
        Check(first.Select(value => value.InstanceId).Distinct().Count() == 25, "Identical VID/PID devices lost their individual identities.");
        var reordered = HidControllerDiscovery.Discover(endpoints.AsEnumerable().Reverse(), ids.AsEnumerable().Reverse(), id => sources[id].Open(), messages.Add);
        Check(first.OrderBy(value => value.InstanceId).SequenceEqual(reordered.OrderBy(value => value.InstanceId)), "Enumeration order changed identity.");

        var gone = sources[ids[1]];
        gone.Disappeared = true;
        var partial = HidControllerDiscovery.Discover(endpoints, ids, id => sources[id].Open(), messages.Add);
        Check(partial.Count == 24 && !partial.Any(value => value.InstanceId == gone.Identity.InstanceId), "Unplug prevented discovery of healthy devices.");
        Check(messages.Any(message => message.Contains("controller_unresolved") && message.Contains(gone.Path)), "Unavailable controller was silently suppressed.");
        gone.Disappeared = false;
        Check(HidControllerDiscovery.Discover(endpoints, ids, id => sources[id].Open(), messages.Add).Count == 25, "Reconnected controller did not return.");
        Check(sources.Values.All(value => value.Opens == value.Disposals), "Failed probe leaked its interface.");

        var removed = endpoints.Where(item => item.Path != gone.Path);
        Check(HidControllerDiscovery.Discover(removed, ids, id => sources[id].Open(), messages.Add).Count == 24, "A persisted GUID resurrected an unplugged controller.");
        Check(HidControllerDiscovery.Discover(Array.Empty<HidControllerEndpoint>(), ids, _ => throw new Exception("Unnecessary probe"), messages.Add).Count == 0,
            "An empty HID list was not handled.");
        Check(new HidControllerEndpoint("flight", 2, 0x21).IsGameController, "Flight controller collection excluded.");
        Check(new HidControllerEndpoint("wheel", 2, 2).IsGameController, "Driving controller collection excluded.");
        Check(new HidControllerEndpoint("multi", 1, 8).IsGameController, "Multi-axis controller collection excluded.");
        Check(new HidControllerEndpoint("game", 5, 1).IsGameController, "3D game controller collection excluded.");
        Check(!new HidControllerEndpoint("mouse", 1, 2).IsGameController && !new HidControllerEndpoint("keyboard", 1, 6).IsGameController &&
            !new HidControllerEndpoint("vendor", 0xff00, 4).IsGameController, "Non-game usage was accepted.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Probe
    {
        internal Probe(int id, string path)
        { Path = path; Identity = new DirectInputIdentity(new Guid(id, 0, 0, new byte[8]), new Guid("56781234-0000-0000-0000-504944564944"), "Same model"); }
        internal readonly string Path;
        internal readonly DirectInputIdentity Identity;
        internal int Opens, Disposals, IdentityReads;
        internal bool Disappeared, FailIfIdentityRead;
        internal IDirectInputDiscoveryDevice Open() { Opens++; return new Handle(this); }
        private sealed class Handle : IDirectInputDiscoveryDevice
        {
            private readonly Probe _owner;
            internal Handle(Probe owner) { _owner = owner; }
            public string GetInterfacePath()
            {
                if (_owner.Disappeared) throw new COMException("Fixture unplugged", unchecked((int)0x80040209));
                // Windows interface paths are case-insensitive.
                return _owner.Path.ToUpperInvariant();
            }
            public DirectInputIdentity GetIdentity()
            {
                _owner.IdentityReads++;
                if (_owner.FailIfIdentityRead) throw new Exception("Non-game HID product query would hang.");
                return _owner.Identity;
            }
            public void Dispose() { _owner.Disposals++; }
        }
    }
}
