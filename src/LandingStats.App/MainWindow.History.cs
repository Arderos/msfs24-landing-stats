using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using LandingStats.App.Controls;
using System.Windows;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;

namespace LandingStats.App;

public partial class MainWindow
{
    private readonly object _historyOperationsGate = new object();
    private readonly HashSet<Task> _historyOperations = new HashSet<Task>();
    private readonly HashSet<string> _locallyRemovedHistoryIds = new HashSet<string>(StringComparer.Ordinal);
    private long _historyLoadVersion;
    private long _detailLoadVersion;
    private CancellationTokenSource? _detailSelectionCancellation;
    private readonly SemaphoreSlim _detailReadQueue = new SemaphoreSlim(1, 1);
    private readonly LinkedList<string> _detailCacheRecency = new LinkedList<string>();
    private const int DetailCacheCapacity = 24;
    private bool _historyLoading = true;
    private bool _detailLoading;
    private bool _historyStopped;
    private bool _updatingHistorySelection;
    private readonly DelayedBusyIndicator _historyBusyIndicator = new DelayedBusyIndicator();
    private readonly DelayedBusyIndicator _detailBusyIndicator = new DelayedBusyIndicator();
    private string? _historyLoadError;
    private string? _detailLoadError;
    private IReadOnlyList<LandingRecord> _presentedReplayContacts = Array.Empty<LandingRecord>();
    private IReadOnlyList<RunwayFacility?> _presentedReplayRunways = Array.Empty<RunwayFacility?>();

    // Shutdown waits for the storage tasks only. Their completion never requires
    // the dispatcher, and includes optional legacy input-selection migrations.
    private Task PendingHistoryOperations
    {
        get
        {
            lock (_historyOperationsGate) return Task.WhenAll(_historyOperations.ToArray());
        }
    }

    private Task TrackHistoryOperation(Task operation)
    {
        lock (_historyOperationsGate) _historyOperations.Add(operation);
        _ = operation.ContinueWith(completed =>
        {
            // Observe failures even if a closing window no longer awaits a load.
            _ = completed.Exception;
            lock (_historyOperationsGate) _historyOperations.Remove(completed);
        }, TaskScheduler.Default);
        return operation;
    }

    private void StopHistoryOperations()
    {
        _detailSelectionCancellation?.Cancel();
        _historyStopped = true;
        _historyLoadVersion++;
        _detailLoadVersion++;
        _historyBusyIndicator.Cancel();
        _detailBusyIndicator.Cancel();
    }

    private void InvalidateLoadedHistoryDetails()
    {
        _detailSelectionCancellation?.Cancel();
        _detailLoadVersion++;
        _loadedDetails.Clear();
        _detailCacheRecency.Clear();
    }

    private async Task InitializeHistoryAsync()
    {
        if (_isClosed || _historyStopped) return;
        try
        {
            var loading = Task.Run(() =>
            {
                lock (_airportFacilityRepositoryGate)
                { _lifetimeCancellation.Token.ThrowIfCancellationRequested(); return _airportFacilityRepository.Load(); }
            }, _lifetimeCancellation.Token);
            await TrackHistoryOperation(loading);
            if (_isClosed || _historyStopped) return;
            // Live simulator facilities received during startup are newer.
            _airportFacilities = MergeAirportFacilities(loading.Result, _airportFacilities);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // The optional scenery cache must not prevent loading the logbook.
            System.Diagnostics.Debug.WriteLine(exception);
        }
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync(string? preferredSelectionId = null)
    {
        if (_isClosed || _historyStopped) return;
        var version = ++_historyLoadVersion;
        var selectionVersion = _detailLoadVersion;
        var initialSummaries = _landings.ToDictionary(record => record.Id, LandingRepository.CreateSummary, StringComparer.Ordinal);
        var initialIds = new HashSet<string>(initialSummaries.Keys, StringComparer.Ordinal);
        var facilities = _airportFacilities.ToArray();
        _historyLoading = true;
        _historyLoadError = null;
        RefreshHistoryLoadingPresentation();
        try
        {
            var loading = Task.Run(() => HistoryCommands.LoadSummaries(_repository, _landingRepositoryGate,
                facilities, _lifetimeCancellation.Token), _lifetimeCancellation.Token);
            await TrackHistoryOperation(loading);
            if (_isClosed || _historyStopped || version != _historyLoadVersion) return;

            var stored = loading.Result;
            var currentSummaries = _landings.ToDictionary(record => record.Id, StringComparer.Ordinal);
            foreach (var record in stored)
            {
                if (!initialSummaries.TryGetValue(record.Id, out var initial) ||
                    !currentSummaries.TryGetValue(record.Id, out var current)) continue;
                if (initial.Airport == current.Airport && initial.Runway == current.Runway &&
                    initial.AirportDistanceNauticalMiles == current.AirportDistanceNauticalMiles &&
                    ReferenceEquals(initial.RunwayGeometry, current.RunwayGeometry)) continue;
                // Metadata arriving after this read was scheduled is newer than
                // its snapshot (in particular a resolved runway from the simulator).
                record.Airport = current.Airport;
                record.Runway = current.Runway;
                record.AirportDistanceNauticalMiles = current.AirportDistanceNauticalMiles;
                record.RunwayGeometry = current.RunwayGeometry;
            }
            // An episode can finish while the index is being read. Do not drop
            // those new records or reintroduce a locally deleted one.
            _landings = _landings.Where(record => !initialIds.Contains(record.Id))
                .Concat(stored)
                .Where(record => !_locallyRemovedHistoryIds.Contains(record.Id))
                .GroupBy(record => record.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(record => record.TimestampUtc)
                .ThenByDescending(record => record.ContactNumber)
                .ToArray();
            _historyLoading = false;
            // A user's selection made during a slow refresh outranks the
            // selection captured by a cloud-sync caller before its network await.
            var currentSelectionId = (LandingHistoryList.SelectedItem as LandingRecord)?.Id;
            ApplySessionFilter(currentSelectionId ??
                (selectionVersion == _detailLoadVersion ? preferredSelectionId : null));
            StoragePathText.Text = "%LOCALAPPDATA%\\MSFS Landing Stats\\Landings";
            StoragePathText.ToolTip = _repository.RootPath;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosed && !_historyStopped && version == _historyLoadVersion)
                _historyLoadError = exception.Message;
        }
        finally
        {
            if (!_isClosed && !_historyStopped && version == _historyLoadVersion)
            {
                _historyLoading = false;
                RefreshHistoryLoadingPresentation();
            }
        }
    }

    private async Task SelectRecordAsync(LandingRecord selected)
    {
        if (_isClosed || _historyStopped || _locallyRemovedHistoryIds.Contains(selected.Id)) return;
        var version = ++_detailLoadVersion;
        _detailSelectionCancellation?.Cancel();
        using var selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _detailSelectionCancellation = selectionCancellation;
        var selectionToken = selectionCancellation.Token;
        var siblings = LandingReplayContacts.ForEpisode(selected, _landings)
            .Where(record => !_locallyRemovedHistoryIds.Contains(record.Id)).ToArray();
        var cached = new Dictionary<string, LandingRecord>(_loadedDetails, StringComparer.Ordinal);
        var wasPresented = DataContext is LandingRecord previous && previous.Id == selected.Id;
        _detailLoadError = null;
        _detailLoading = !wasPresented;
        // Keep the previous frame until replacement data arrives. Disable its
        // interaction immediately, but avoid a blank flash for a fast disk read.
        if (!wasPresented) ReplayView.PausePlayback();
        RefreshHistoryLoadingPresentation();
        try
        {
            var allRecords = new[] { selected }.Concat(siblings.Where(record => record.Id != selected.Id)).ToArray();
            var metadataAtSelection = allRecords.ToDictionary(record => record.Id,
                LandingRepository.CreateSummary, StringComparer.Ordinal);
            var loadedFromStorage = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<LandingRecord> contacts;
            if (allRecords.All(record => !record.IsSummaryOnly || cached.ContainsKey(record.Id)))
            {
                contacts = allRecords.Select(record => cached.TryGetValue(record.Id, out var detail) ? detail : record).ToArray();
            }
            else
            {
                var loading = Task.Run(async () =>
                {
                    await _detailReadQueue.WaitAsync(selectionToken).ConfigureAwait(false);
                    try
                    {
                        return HistoryCommands.LoadDetails(_repository, _landingRepositoryGate, allRecords, selected.Id,
                            cached, loadedFromStorage, selectionToken);
                    }
                    finally { _detailReadQueue.Release(); }
                }, selectionToken);
                await TrackHistoryOperation(loading);
                contacts = loading.Result;
            }

            if (!IsCurrentHistorySelection(version, selected.Id)) return;
            contacts = contacts.Where(record => !_locallyRemovedHistoryIds.Contains(record.Id) &&
                _landings.Any(summary => summary.Id == record.Id)).ToArray();
            var selectedDetail = contacts.FirstOrDefault(record => record.Id == selected.Id);
            if (selectedDetail == null) return;
            foreach (var detail in contacts)
            {
                var summary = _landings.First(record => record.Id == detail.Id);
                var initial = metadataAtSelection[detail.Id];
                if (!loadedFromStorage.Contains(detail.Id) ||
                    initial.Airport != summary.Airport || initial.Runway != summary.Runway ||
                    initial.AirportDistanceNauticalMiles != summary.AirportDistanceNauticalMiles ||
                    !ReferenceEquals(initial.RunwayGeometry, summary.RunwayGeometry))
                {
                    // Cached details follow the UI; scenery resolution that
                    // arrived during this read also outranks its disk snapshot.
                    detail.Airport = summary.Airport;
                    detail.Runway = summary.Runway;
                    detail.AirportDistanceNauticalMiles = summary.AirportDistanceNauticalMiles;
                    if (summary.RunwayGeometry != null) detail.RunwayGeometry = summary.RunwayGeometry;
                }
                else
                {
                    // An unchanged UI summary can be older than the detail
                    // loaded under the gate; publish its committed metadata.
                    CopyRunwayMetadata(detail, summary);
                }
                _loadedDetails[detail.Id] = detail;
                _detailCacheRecency.Remove(detail.Id);
                _detailCacheRecency.AddLast(detail.Id);
            }
            while (_detailCacheRecency.Count > DetailCacheCapacity)
            {
                var oldest = _detailCacheRecency.First!.Value;
                _detailCacheRecency.RemoveFirst(); _loadedDetails.Remove(oldest);
            }
            var preparation = Task.Run(() => ReplayPresentationData.Prepare(selectedDetail, contacts), selectionToken);
            await TrackHistoryOperation(preparation);
            if (!IsCurrentHistorySelection(version, selected.Id)) return;
            if (ReferenceEquals(DataContext, selectedDetail) &&
                _presentedReplayContacts.Select(record => record.Id).SequenceEqual(contacts.Select(record => record.Id)))
            {
                // Background metadata updates must not reset the replay clock,
                // chart zoom or the user's isolated series.
                DataContext = null;
                DataContext = selectedDetail;
                if (!_presentedReplayRunways.SequenceEqual(contacts.Select(record => record.RunwayGeometry)))
                {
                    var replayTime = ReplayView.CurrentTimeSeconds;
                    ReplayView.SetRecord(selectedDetail, contacts);
                    ReplayView.SetTime(replayTime);
                }
            }
            else
            {
                PresentRecord(selectedDetail, contacts);
                _presentedReplayContacts = contacts;
            }
            _presentedReplayRunways = contacts.Select(record => record.RunwayGeometry).ToArray();
            if (Guid.TryParse(selectedDetail.Id, out var presentedId))
                RecordPresentationPhase(presentedId, EpisodeTimelinePhase.UiDetailApplied, contacts.Count);
        }
        catch (OperationCanceledException) when (selectionToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentHistorySelection(version, selected.Id))
            {
                ClearRecordPresentation();
                _detailLoadError = exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_detailSelectionCancellation, selectionCancellation)) _detailSelectionCancellation = null;
            if (IsCurrentHistorySelection(version, selected.Id))
            {
                _detailLoading = false;
                RefreshHistoryLoadingPresentation();
            }
        }
    }

    private static void CopyRunwayMetadata(LandingRecord source, LandingRecord target)
    {
        target.RunwayGeometry = source.RunwayGeometry;
        target.Runway = source.Runway;
        target.Airport = source.Airport;
        target.AirportDistanceNauticalMiles = source.AirportDistanceNauticalMiles;
    }

    private bool IsCurrentHistorySelection(long version, string id) =>
        !_isClosed && !_historyStopped && version == _detailLoadVersion &&
        !_locallyRemovedHistoryIds.Contains(id) &&
        (LandingHistoryList.SelectedItem as LandingRecord)?.Id == id &&
        _landings.Any(record => record.Id == id);

    private async void OnRetryHistoryLoadClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_isClosed || _historyStopped) return;
        if (!_startupReady)
        {
            if (!_initializationTask.IsCompleted) return;
            _historyLoading = true;
            _historyLoadError = null;
            RefreshHistoryLoadingPresentation();
            _initializationTask = InitializeWindowAsync();
            await _initializationTask;
            return;
        }
        if (!ReferenceEquals(sender, RetryHistoryIndexLoadButton) &&
            _detailLoadError != null && LandingHistoryList.SelectedItem is LandingRecord selected)
            await SelectRecordAsync(selected);
        else
            await InitializeHistoryAsync();
    }

    private void RefreshHistoryLoadingPresentation()
    {
        if (HistoryLoadingPanel == null || DetailLoadingPanel == null) return;
        var initialLoad = _historyLoading && _landings.Count == 0;
        var showInitialLoad = _historyBusyIndicator.Update(initialLoad && !_historyStopped && !_isClosed,
            null, RefreshHistoryLoadingPresentation);
        var showDetailLoad = _detailBusyIndicator.Update(_detailLoading && !_historyStopped && !_isClosed,
            _detailLoadVersion, RefreshHistoryLoadingPresentation);
        var historyError = !string.IsNullOrEmpty(_historyLoadError);
        HistoryLoadingPanel.Visibility = showInitialLoad || historyError ? Visibility.Visible : Visibility.Collapsed;
        HistoryLoadingProgress.Visibility = showInitialLoad ? Visibility.Visible : Visibility.Collapsed;
        HistoryLoadingText.Text = LocalizationManager.Text(historyError ? "History.LoadFailed" : "History.Loading");
        HistoryLoadingText.ToolTip = _historyLoadError;
        RetryHistoryIndexLoadButton.Visibility = historyError ? Visibility.Visible : Visibility.Collapsed;

        var detailError = !string.IsNullOrEmpty(_detailLoadError);
        var showDetailStatus = showInitialLoad || showDetailLoad || detailError || (historyError && _landings.Count == 0);
        DetailLoadingPanel.Visibility = showDetailStatus ? Visibility.Visible : Visibility.Collapsed;
        DetailLoadingProgress.Visibility = showInitialLoad || showDetailLoad ? Visibility.Visible : Visibility.Collapsed;
        DetailLoadingText.Text = LocalizationManager.Text(detailError ? "History.DetailLoadFailed" :
            historyError ? "History.LoadFailed" : initialLoad ? "History.Loading" : "History.DetailLoading");
        DetailLoadingText.ToolTip = _detailLoadError ?? _historyLoadError;
        RetryHistoryLoadButton.Visibility = detailError || historyError ? Visibility.Visible : Visibility.Collapsed;
        LandingContent.Visibility = !showDetailStatus && DataContext is LandingRecord ? Visibility.Visible : Visibility.Collapsed;
        LandingContent.IsHitTestVisible = !_detailLoading && !_isClosed;
        LandingContent.IsEnabled = !_detailLoading && !_isClosed;
        EmptyState.Visibility = !initialLoad && !_detailLoading && !showDetailStatus && LandingHistoryList.Items.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
