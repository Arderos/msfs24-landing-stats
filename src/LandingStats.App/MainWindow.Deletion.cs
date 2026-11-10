using System;
using LandingStats.App.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    private LandingRecord[] DeletionContacts(LandingRecord record) => LandingHistoryEpisodes.ForRecord(record,
        _landings.Where(candidate => !_locallyRemovedHistoryIds.Contains(candidate.Id)));

    private void OnDeleteLandingClick(object sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (_isClosed || _historyStopped || _deleteLandingInProgress ||
            sender is not Button button || button.Tag is not LandingRecord record) return;
        _pendingDeleteRecords = DeletionContacts(record);
        if (_pendingDeleteRecords.Length == 0) return;
        _pendingDeleteRecord = record;
        UpdateDeleteLandingDescription(record);
        DeleteLandingErrorText.Text = string.Empty;
        DeleteLandingErrorText.Visibility = Visibility.Collapsed;
        ConfirmDeleteLandingButton.IsEnabled = true;
        DeleteLandingOverlay.Visibility = Visibility.Visible;
        DeleteLandingOverlay.Focus();
        Keyboard.Focus(CancelDeleteLandingButton);
    }

    private void OnCancelDeleteLandingClick(object sender, RoutedEventArgs eventArgs) => HideDeleteLandingConfirmation();

    private void OnDeleteLandingOverlayKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape || _deleteLandingInProgress) return;
        eventArgs.Handled = true;
        HideDeleteLandingConfirmation();
    }

    private async void OnConfirmDeleteLandingClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_isClosed || _historyStopped || _deleteLandingInProgress) return;
        var record = _pendingDeleteRecord;
        if (record == null) { HideDeleteLandingConfirmation(); return; }
        var contacts = DeletionContacts(record);
        if (contacts.Length == 0) { HideDeleteLandingConfirmation(); return; }
        var ids = new HashSet<string>(contacts.Select(contact => contact.Id), StringComparer.Ordinal);
        if (!ids.SetEquals(_pendingDeleteRecords.Select(contact => contact.Id)))
        {
            // History may refresh while the dialog is open. Never delete a
            // larger set than the user reviewed without renewing confirmation.
            _pendingDeleteRecords = contacts;
            UpdateDeleteLandingDescription(record);
            DeleteLandingErrorText.Text = LocalizationManager.Text("Delete.ContactsChanged");
            DeleteLandingErrorText.Visibility = Visibility.Visible;
            return;
        }

        var visible = LandingHistoryList.Items.Cast<LandingRecord>().ToArray();
        var selectedId = (LandingHistoryList.SelectedItem as LandingRecord)?.Id;
        var remaining = visible.Where(candidate => !ids.Contains(candidate.Id)).ToArray();
        var deletedIndex = Array.FindIndex(visible, candidate => candidate.Id == selectedId);
        var preferredSelectionId = selectedId != null && !ids.Contains(selectedId) ? selectedId :
            remaining.Length == 0 ? null : remaining[Math.Min(Math.Max(0, deletedIndex), remaining.Length - 1)].Id;
        var signedIn = _googleDriveTokenStore.HasRefreshToken();
        var deletedIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            _deleteLandingInProgress = true;
            ConfirmDeleteLandingButton.IsEnabled = false;
            CancelDeleteLandingButton.IsEnabled = false;
            // Reserve every contact before yielding: older history and detail
            // reads cannot put a sibling back while storage is still busy.
            _locallyRemovedHistoryIds.UnionWith(ids);
            ApplySessionFilter(preferredSelectionId);
            var deletion = _services.DeleteLandingsAsync(ids);
            _localDeletionTask = deletion;
            var result = await deletion;
            deletedIds.UnionWith(result.DeletedIds);
            if (_isClosed || _historyStopped) return;
            ReconcileDeletedContacts(contacts, deletedIds);
            ApplySessionFilter(preferredSelectionId);

            if (result.QueueErrors.Count > 0)
            {
                _googleDriveErrorDetail = new AggregateException(result.QueueErrors).Message;
                _googleDriveErrorKey = null;
                RefreshGoogleDrivePresentation();
            }
            if (result.QueueErrors.Count == 0 && signedIn && deletedIds.Count > 0) QueueGoogleDriveSync();
            if (result.LocalErrors.Count > 0)
            {
                _pendingDeleteRecords = contacts.Where(contact => !deletedIds.Contains(contact.Id)).ToArray();
                _pendingDeleteRecord = _pendingDeleteRecords.First();
                UpdateDeleteLandingDescription(_pendingDeleteRecord);
                ShowDeletionError(new AggregateException(result.LocalErrors));
            }
            else HideDeleteLandingConfirmation(true);
        }
        catch (Exception exception)
        {
            if (_isClosed || _historyStopped) return;
            ReconcileDeletedContacts(contacts, deletedIds);
            ApplySessionFilter(preferredSelectionId);
            ShowDeletionError(exception);
        }
        finally
        {
            _deleteLandingInProgress = false;
            ConfirmDeleteLandingButton.IsEnabled = true;
            CancelDeleteLandingButton.IsEnabled = true;
        }
    }

    private void ReconcileDeletedContacts(LandingRecord[] requested, ISet<string> deleted)
    {
        foreach (var contact in requested)
        {
            if (!deleted.Contains(contact.Id)) _locallyRemovedHistoryIds.Remove(contact.Id);
            _loadedDetails.Remove(contact.Id);
            _detailCacheRecency.Remove(contact.Id);
        }
        // A refresh in flight may already have excluded the reserved IDs.
        // Restore only failed contacts; successful tombstones remain final.
        _landings = LandingHistoryOrder.Sort(_landings.Concat(requested.Where(contact => !deleted.Contains(contact.Id)))
            .Where(contact => !deleted.Contains(contact.Id)).GroupBy(contact => contact.Id, StringComparer.Ordinal)
            .Select(group => group.First()));
    }

    private void ShowDeletionError(Exception exception)
    {
        DeleteLandingErrorText.Text = LocalizationManager.Format("Delete.ErrorFormat", exception.Message);
        DeleteLandingErrorText.Visibility = Visibility.Visible;
        Keyboard.Focus(CancelDeleteLandingButton);
    }

    private void HideDeleteLandingConfirmation(bool force = false)
    {
        if (_deleteLandingInProgress && !force) return;
        _pendingDeleteRecord = null;
        _pendingDeleteRecords = Array.Empty<LandingRecord>();
        DeleteLandingOverlay.Visibility = Visibility.Collapsed;
        ConfirmDeleteLandingButton.IsEnabled = true;
        CancelDeleteLandingButton.IsEnabled = true;
        Keyboard.Focus(LandingHistoryList);
    }

    private void UpdateDeleteLandingDescription(LandingRecord record)
    {
        var presentation = new LandingPresentation(record);
        DeleteLandingDescription.Text = $"{record.AircraftTitle}\n{presentation.LocationDisplay} · {presentation.TimestampDisplay}\n" +
            LocalizationManager.Format("Delete.ContactCountFormat", _pendingDeleteRecords.Length);
    }
}
