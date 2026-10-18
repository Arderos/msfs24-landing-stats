using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectSource
{
    private sealed class LocalSceneryQueryPort : ISceneryQueryPort
    {
        private readonly SimConnectSource _owner;
        public LocalSceneryQueryPort(SimConnectSource owner) { _owner = owner; }
        public bool CanQuery => _owner._simConnect != null && !_owner._disposed;
        public IReadOnlyList<AirportFacility> CachedAirports => new List<AirportFacility>(_owner._airportFacilities.Values);
        public SceneryQuerySnapshot Snapshot => new SceneryQuerySnapshot(_owner.CompletedBaseRunways(),
            _owner._stageAirports, _owner._runwayFreshAirportsReceived, _owner.BaseRunwayCandidatesComplete(),
            _owner._runwayBatchKeys.Length, _owner._runwayBatchKeys.Count(key => _owner._runways.ContainsKey(key)));
        public void Request(double latitude, double longitude) => _owner.RequestRunwaysAt(latitude, longitude);
        public void Reset() => _owner.ResetNativeQuery();
        public void Cancel(string reason) => _owner.CancelNativeQuery(reason);
    }
}
