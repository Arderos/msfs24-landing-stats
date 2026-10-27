using System;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectSource
{
    private enum AircraftConfigurationEvents : uint { Loaded = 0x110 }
    private string _aircraftConfigurationPath = string.Empty;
    private string _aircraftCategory = string.Empty;
    public event Action<string, string>? AircraftConfigurationReceived;

    private void StartAircraftConfiguration()
    {
        _aircraftConfigurationPath = string.Empty; _aircraftCategory = string.Empty;
        TryOptionalRefresh("aircraft_profile_subscription_failed", sender =>
        {
            sender.SubscribeToSystemEvent(AircraftConfigurationEvents.Loaded, "AircraftLoaded");
            RememberOperation(sender, "Subscribe aircraft configuration", true, eventId: (uint)AircraftConfigurationEvents.Loaded);
        });
        RequestAircraftConfiguration();
    }

    private void RequestAircraftConfiguration() => TryOptionalRefresh("aircraft_profile_identity_failed", sender =>
    {
        sender.RequestSystemState(Requests.AircraftConfiguration, "AircraftLoaded");
        RememberOperation(sender, "Request aircraft configuration", true, (uint)Requests.AircraftConfiguration);
    });

    private void OnRecvAircraftFilename(SimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data)
    {
        if (!ReferenceEquals(sender, _simConnect) || data.uEventID != (uint)AircraftConfigurationEvents.Loaded) return;
        ObserveAircraftConfiguration(data.szFileName);
    }

    private void ObserveAircraftConfiguration(string path)
    {
        path = (path ?? string.Empty).Trim();
        if (path == _aircraftConfigurationPath) return;
        _aircraftConfigurationPath = path;
        AircraftConfigurationReceived?.Invoke(path, _aircraftCategory);
    }

    private void ObserveAircraftCategory(string? category)
    {
        category = (category ?? string.Empty).Trim();
        if (category == _aircraftCategory) return;
        _aircraftCategory = category;
        AircraftConfigurationReceived?.Invoke(_aircraftConfigurationPath, category);
    }
}
