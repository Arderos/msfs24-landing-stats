using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LandingStats.App.Models;
using LandingStats.App.Storage;

namespace LandingStats.App;

public partial class MainWindow
{
    private readonly Dictionary<string, int> _runwayPendingIds = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly DelayedBusyIndicator _runwayBusyIndicator = new DelayedBusyIndicator();

    private async Task<IReadOnlyList<LandingRecord>> PersistRunwaysAsync(
        IReadOnlyList<LandingRecord> records, IReadOnlyList<RunwayFacility> facilities)
    {
        if (_isClosed || facilities.Count == 0) return Array.Empty<LandingRecord>();
        var ids = records.Where(record => record.RunwayGeometry == null).Select(record => record.Id).Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<LandingRecord>();
        foreach (var id in ids)
            _runwayPendingIds[id] = _runwayPendingIds.TryGetValue(id, out var pending) ? pending + 1 : 1;
        RefreshRunwayLoadingPresentation();
        var task = RunwayMetadataPersistence.SaveAfterAsync(_runwayPersistenceTask, _repository,
            _landingRepositoryGate, ids, facilities);
        _runwayPersistenceTask = task;
        try { return await task; }
        catch (Exception exception)
        {
            // The base landing is durable already. Optional scenery failures
            // must not interrupt recording or mutate displayed metadata.
            System.Diagnostics.Debug.WriteLine(exception);
            return Array.Empty<LandingRecord>();
        }
        finally
        {
            foreach (var id in ids)
                if (--_runwayPendingIds[id] == 0) _runwayPendingIds.Remove(id);
            if (!_isClosed) RefreshRunwayLoadingPresentation();
        }
    }

    private static void CopyRunwayMetadata(LandingRecord source, LandingRecord target)
    {
        target.RunwayGeometry = source.RunwayGeometry;
        target.Runway = source.Runway;
        target.Airport = source.Airport;
        target.AirportDistanceNauticalMiles = source.AirportDistanceNauticalMiles;
    }

    private void RefreshRunwayLoadingPresentation()
    {
        if (RunwayLoadingText == null) return;
        var recordId = (DataContext as LandingRecord)?.Id;
        RunwayLoadingText.Visibility = _runwayBusyIndicator.Update(
            !_isClosed && recordId != null && _runwayPendingIds.ContainsKey(recordId), recordId, RefreshRunwayLoadingPresentation)
                ? Visibility.Visible : Visibility.Collapsed;
    }
}
