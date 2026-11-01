using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using Microsoft.Win32;

namespace LandingStats.App;

public partial class MainWindow
{
    private Task _sharingStorageTail = Task.CompletedTask;
    private readonly object _sharingAdmissionGate = new object();
    private bool _sharingAccepting = true;
    private int _sharingPendingOperations;
    private string? _sharingStatusKey;
    private object[] _sharingStatusArguments = Array.Empty<object>();
    private string? _sharingErrorDetail;

    private async void OnShareLandingClick(object sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (SharingStopped || _sharingPendingOperations != 0 ||
            sender is not FrameworkElement { Tag: LandingRecord record }) return;
        try
        {
            var dialog = CreateLandingExportDialog(record);
            if (dialog.ShowDialog(this) != true) return;
            await ExportLandingAsync(record.Id, dialog.FileName);
        }
        catch (OperationCanceledException) when (SharingStopped) { }
        catch (Exception error) { SetSharingStatus("Sharing.ExportFailed", error.Message); }
    }

    private async void OnImportLandingsClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SharingStopped || _sharingPendingOperations != 0) return;
        try
        {
            var dialog = CreateLandingImportDialog();
            if (dialog.ShowDialog(this) == true) await QueueLandingImports(dialog.FileNames);
        }
        catch (OperationCanceledException) when (SharingStopped) { }
        catch (Exception error) { SetSharingStatus("Sharing.ImportFailed", error.Message); }
    }

    internal static SaveFileDialog CreateLandingExportDialog(LandingRecord record) => new SaveFileDialog
    {
        Title = LocalizationManager.Text("Sharing.Share"),
        Filter = LandingFileFilter,
        DefaultExt = LandingShareArchive.Extension,
        AddExtension = true,
        OverwritePrompt = true,
        FileName = "Landing-" + record.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture) + LandingShareArchive.Extension,
    };

    internal static OpenFileDialog CreateLandingImportDialog() => new OpenFileDialog
    {
        Title = LocalizationManager.Text("Sharing.ImportTitle"),
        Filter = LandingFileFilter,
        DefaultExt = LandingShareArchive.Extension,
        Multiselect = true,
        CheckFileExists = true,
    };

    private static string LandingFileFilter => LocalizationManager.Text("Sharing.FileType") +
        " (*" + LandingShareArchive.Extension + ")|*" + LandingShareArchive.Extension;

    internal async Task ExportLandingAsync(string id, string destination)
    {
        Dispatcher.VerifyAccess();
        ThrowIfSharingStopped();
        _sharingPendingOperations++;
        SetSharingStatus("Sharing.Exporting");
        try
        {
            await ScheduleSharingStorage(() => { LandingShareArchive.Export(_repository, id, destination); return true; });
            ThrowIfSharingStopped();
            SetSharingStatus("Sharing.ExportedFormat", null, Path.GetFileName(destination));
        }
        catch (OperationCanceledException) when (SharingStopped) { throw; }
        catch (Exception error)
        {
            if (SharingStopped) throw new OperationCanceledException("The landing window is closing.", error);
            SetSharingStatus("Sharing.ExportFailed", error.Message);
            throw;
        }
        finally { _sharingPendingOperations--; RefreshSharingPresentation(); }
    }

    // File-open activation awaits this batch before acknowledging its durable
    // request. Storage can start before the first history read: both use the
    // repository gate. No runtime/simulator startup is needed to import a file.
    internal async Task QueueLandingImports(IEnumerable<string> paths)
    {
        Dispatcher.VerifyAccess();
        ThrowIfSharingStopped();
        var files = paths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? throw new ArgumentNullException(nameof(paths));
        if (files.Length == 0) return;
        _sharingPendingOperations++;
        SetSharingStatus("Sharing.ImportingFormat", null, files.Length);
        try
        {
            var result = await ScheduleSharingStorage(() => LandingShareArchive.Import(_repository, files));
            ThrowIfSharingStopped();
            await LoadHistoryAsync();
            ThrowIfSharingStopped();
            if (!string.IsNullOrEmpty(result.PreferredRecordId) && _landings.Any(record => record.Id == result.PreferredRecordId))
            {
                // An explicit file-open must reveal its landing even when an
                // earlier search excludes that aircraft or it is already saved.
                SessionFilterBox.Clear();
                ApplySessionFilter(result.PreferredRecordId);
                LandingHistoryList.ScrollIntoView(LandingHistoryList.SelectedItem);
            }
            var details = result.Failures.Count == 0 ? null : string.Join("\n\n", result.Failures.Select(failure =>
                failure.Path + "\n" + failure.Message));
            SetSharingStatus("Sharing.ImportedFormat", details, result.Imported.Count, result.DuplicateCount, result.Failures.Count);
        }
        catch (OperationCanceledException) when (SharingStopped) { throw; }
        catch (Exception error)
        {
            if (SharingStopped) throw new OperationCanceledException("The landing window is closing.", error);
            SetSharingStatus("Sharing.ImportFailed", error.Message);
            throw;
        }
        finally { _sharingPendingOperations--; RefreshSharingPresentation(); }
    }

    private Task<T> ScheduleSharingStorage<T>(Func<T> work)
    {
        // Preserve accepted batch order without blocking the dispatcher. Closing
        // drains these storage tasks; cancellation must not drop an accepted file.
        lock (_sharingAdmissionGate)
        {
            ThrowIfSharingStopped();
            var operation = _sharingStorageTail.ContinueWith(_ =>
            {
                lock (_landingRepositoryGate) return work();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _sharingStorageTail = operation;
            TrackHistoryOperation(operation);
            return operation;
        }
    }

    private bool SharingStopped => Volatile.Read(ref _isClosed) || Volatile.Read(ref _historyStopped) || !Volatile.Read(ref _sharingAccepting);

    private bool SharingStorageIdle
    {
        get { lock (_sharingAdmissionGate) return _sharingStorageTail.IsCompleted; }
    }

    private void StopAcceptingLandingSharing()
    {
        lock (_sharingAdmissionGate) _sharingAccepting = false;
    }

    private bool TrySealSharingForRestart()
    {
        lock (_sharingAdmissionGate)
        {
            if (SharingStopped || !_sharingStorageTail.IsCompleted) return false;
            _sharingAccepting = false;
            return true;
        }
    }

    private void ResumeSharingAfterFailedRestart()
    {
        lock (_sharingAdmissionGate)
            if (!Volatile.Read(ref _isClosed) && !Volatile.Read(ref _historyStopped)) _sharingAccepting = true;
    }

    private async Task WaitForSharingStorageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task pending;
            lock (_sharingAdmissionGate) pending = _sharingStorageTail;
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // A failed file is reported by its UI operation. Its completed task
            // still releases storage ownership and cannot block an update forever.
            _ = pending.Exception;
            lock (_sharingAdmissionGate)
                if (ReferenceEquals(pending, _sharingStorageTail)) return;
        }
    }

    internal static string[] LandingFilesFromDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths)
            return Array.Empty<string>();
        return paths.Any(path => path != null && path.EndsWith(LandingShareArchive.Extension, StringComparison.OrdinalIgnoreCase))
            ? paths : Array.Empty<string>();
    }

    private void OnLandingFilesDragOver(object sender, DragEventArgs eventArgs)
    {
        if (!eventArgs.Data.GetDataPresent(DataFormats.FileDrop)) return;
        eventArgs.Handled = true;
        eventArgs.Effects = !SharingStopped && _sharingPendingOperations == 0 &&
            LandingFilesFromDrop(eventArgs.Data).Length != 0 && (eventArgs.AllowedEffects & DragDropEffects.Copy) != 0
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnLandingFilesDrop(object sender, DragEventArgs eventArgs)
    {
        if (!eventArgs.Data.GetDataPresent(DataFormats.FileDrop)) return;
        eventArgs.Handled = true;
        eventArgs.Effects = DragDropEffects.None;
        if (SharingStopped || _sharingPendingOperations != 0) return;
        var paths = LandingFilesFromDrop(eventArgs.Data);
        if (paths.Length == 0) return;
        eventArgs.Effects = DragDropEffects.Copy;
        try { await QueueLandingImports(paths); }
        catch (OperationCanceledException) when (SharingStopped) { }
        catch (Exception error) { SetSharingStatus("Sharing.ImportFailed", error.Message); }
    }

    internal void ShowLandingSharingError(string detail)
    {
        Dispatcher.VerifyAccess();
        SetSharingStatus("Sharing.OperationFailed", detail);
    }

    private void ThrowIfSharingStopped()
    {
        if (SharingStopped) throw new OperationCanceledException("The landing window is closing.");
    }

    private void SetSharingStatus(string key, string? detail = null, params object[] arguments)
    {
        if (_isClosed || _historyStopped) return;
        _sharingStatusKey = key; _sharingStatusArguments = arguments; _sharingErrorDetail = detail;
        RefreshSharingPresentation();
    }

    private void RefreshSharingPresentation()
    {
        if (ImportLandingsButton == null || _isClosed || _historyStopped) return;
        ImportLandingsButton.IsEnabled = !SharingStopped && _sharingPendingOperations == 0;
        DismissSharingStatusButton.IsEnabled = _sharingPendingOperations == 0;
        SharingStatusPanel.Visibility = _sharingStatusKey == null ? Visibility.Collapsed : Visibility.Visible;
        SharingStatusText.Text = _sharingStatusKey == null ? string.Empty : LocalizationManager.Format(_sharingStatusKey, _sharingStatusArguments);
        SharingErrorText.Text = _sharingErrorDetail ?? string.Empty;
        SharingErrorDetails.Visibility = string.IsNullOrEmpty(_sharingErrorDetail) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDismissSharingStatusClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_sharingPendingOperations != 0) return;
        _sharingStatusKey = null; _sharingErrorDetail = null;
        RefreshSharingPresentation();
    }
}
