using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Storage;

namespace LandingStats.App.GoogleDrive;

internal sealed class GoogleDriveBackupService : IDisposable
{
    private const string KindProperty = "kind";
    private const string LandingIdProperty = "landingId";
    private const string HashProperty = "sha256";
    private const string BaseHashProperty = "baseSha256";
    private const string DeletedByApplicationProperty = "deletedByApplication";
    private const string RootKind = "root";
    private const string LandingsFolderKind = "landings-folder";
    private const string LandingKind = "landing";
    private const string SettingsKind = "settings";
    private readonly IGoogleDriveApi _drive;
    private readonly LandingRepository _landings;
    private readonly object _landingGate;
    private readonly GoogleDriveSyncStateRepository _stateRepository;
    private readonly Func<Task<GoogleDriveLocalSettings>> _readSettingsAsync;
    private readonly Func<GoogleDriveCloudSettings, string, Task<bool>> _applySettingsAsync;
    private readonly SemaphoreSlim _syncGate = new SemaphoreSlim(1, 1);
    private readonly object _pendingDeleteGate = new object();
    private readonly ConcurrentDictionary<string, byte> _sessionPendingDeletes =
        new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _durableLocalDeletesAwaitingQueue =
        new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
    private bool _disposed;

    public GoogleDriveBackupService(
        IGoogleDriveApi drive,
        LandingRepository landings,
        object landingGate,
        GoogleDriveSyncStateRepository stateRepository,
        Func<Task<GoogleDriveLocalSettings>> readSettingsAsync,
        Func<GoogleDriveCloudSettings, string, Task<bool>> applySettingsAsync)
    {
        _drive = drive ?? throw new ArgumentNullException(nameof(drive));
        _landings = landings ?? throw new ArgumentNullException(nameof(landings));
        _landingGate = landingGate ?? throw new ArgumentNullException(nameof(landingGate));
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        _readSettingsAsync = readSettingsAsync ?? throw new ArgumentNullException(nameof(readSettingsAsync));
        _applySettingsAsync = applySettingsAsync ?? throw new ArgumentNullException(nameof(applySettingsAsync));
    }

    public async Task DeleteLandingAsync(string landingId, CancellationToken cancellationToken)
    {
        DeleteLandingLocally(landingId);
        await QueueLandingDeletionAsync(landingId, cancellationToken).ConfigureAwait(false);
    }

    public bool DeleteLandingLocally(string landingId)
    {
        ValidateLandingId(landingId);
        ThrowIfDisposed();
        lock (_pendingDeleteGate)
        {
            _sessionPendingDeletes.TryAdd(landingId, 0);
            var stateQueued = false;
            try
            {
                try
                {
                    _stateRepository.AddPendingDelete(landingId);
                    stateQueued = true;
                }
                catch (Exception exception) when (IsStateStorageFailure(exception))
                {
                    // Local history deletion must remain available without Drive state
                    // storage. QueueLandingDeletionAsync will report/retry persistence.
                }
                lock (_landingGate)
                {
                    var deleted = _landings.Delete(landingId);
                    if (stateQueued)
                    {
                        _durableLocalDeletesAwaitingQueue.TryAdd(landingId, 0);
                    }
                    return deleted;
                }
            }
            catch
            {
                _sessionPendingDeletes.TryRemove(landingId, out _);
                if (stateQueued)
                {
                    try
                    {
                        _stateRepository.RemovePendingDelete(landingId);
                    }
                    catch (Exception exception) when (IsStateStorageFailure(exception))
                    {
                    }
                }
                throw;
            }
        }
    }

    internal Task QueueLandingDeletionAsync(string landingId, CancellationToken cancellationToken)
    {
        ValidateLandingId(landingId);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_pendingDeleteGate)
        {
            if (_durableLocalDeletesAwaitingQueue.TryRemove(landingId, out _))
            {
                return Task.CompletedTask;
            }
            _sessionPendingDeletes.TryAdd(landingId, 0);
            _stateRepository.AddPendingDelete(landingId);
        }
        return Task.CompletedTask;
    }

    internal Task CancelLandingDeletionAsync(string landingId, CancellationToken cancellationToken)
    {
        ValidateLandingId(landingId);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _stateRepository.RemovePendingDelete(landingId);
        lock (_pendingDeleteGate)
        {
            _sessionPendingDeletes.TryRemove(landingId, out _);
            _durableLocalDeletesAwaitingQueue.TryRemove(landingId, out _);
        }
        return Task.CompletedTask;
    }

    public async Task<GoogleDriveSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new GoogleDriveSyncResult();
            var state = _stateRepository.Load();
            var accountPermissionId = await _drive.GetAccountPermissionIdAsync(cancellationToken)
                .ConfigureAwait(false);
            var files = (await _drive.ListApplicationFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();

            var root = Active(files, RootKind)
                .OrderBy(file => file.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (root == null)
            {
                await _drive.CreateFolderAsync(
                    "MSFS Landing Stats",
                    null,
                    Properties(RootKind),
                    cancellationToken).ConfigureAwait(false);
                files = (await _drive.ListApplicationFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
                root = Active(files, RootKind)
                    .OrderBy(file => file.Id, StringComparer.Ordinal)
                    .FirstOrDefault() ?? throw new InvalidDataException(
                        "Google Drive did not return the newly created backup folder.");
            }
            var rootIds = ActiveRootIds(files);
            var accountChanged = !string.IsNullOrWhiteSpace(state.AccountPermissionId) &&
                                 !string.Equals(
                                     state.AccountPermissionId,
                                     accountPermissionId,
                                     StringComparison.Ordinal);
            if (accountChanged)
            {
                var previousAccountPermissionId = state.AccountPermissionId;
                var previousRootFolderId = state.RootFolderId;
                SwitchAccountState(
                    state,
                    accountPermissionId,
                    root.Id,
                    rootIds,
                    previousAccountPermissionId,
                    previousRootFolderId);
            }
            else if (!string.Equals(state.RootFolderId, root.Id, StringComparison.Ordinal))
            {
                var visibleRootIds = files
                    .Where(file => string.Equals(
                        file.Property(KindProperty),
                        RootKind,
                        StringComparison.Ordinal))
                    .Select(file => file.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var legacyStateBelongsToCurrentAccount = string.IsNullOrWhiteSpace(state.AccountPermissionId) &&
                    !string.IsNullOrWhiteSpace(state.RootFolderId) &&
                    (visibleRootIds.Contains(state.RootFolderId!) ||
                     state.KnownRootIds.Any(visibleRootIds.Contains));
                if (!string.IsNullOrWhiteSpace(state.AccountPermissionId) ||
                    legacyStateBelongsToCurrentAccount)
                {
                    state.AccountPermissionId = accountPermissionId;
                    state.RebaseWithinActiveRoots(root.Id, rootIds);
                    _stateRepository.Save(state);
                }
                else
                {
                    // A legacy state without an account identity cannot safely carry an
                    // unscoped deletion into a Drive account it has never seen.
                    var previousAccountPermissionId = state.AccountPermissionId;
                    var previousRootFolderId = state.RootFolderId;
                    SwitchAccountState(
                        state,
                        accountPermissionId,
                        root.Id,
                        rootIds,
                        previousAccountPermissionId,
                        previousRootFolderId);
                }
            }
            else
            {
                state.AccountPermissionId = accountPermissionId;
                state.RememberActiveRoots(rootIds);
            }

            var landingsFolder = Active(files, LandingsFolderKind)
                .Where(file => file.Parents.Contains(root.Id, StringComparer.Ordinal))
                .OrderBy(file => file.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (landingsFolder == null)
            {
                await _drive.CreateFolderAsync(
                    "Landings",
                    root.Id,
                    Properties(LandingsFolderKind),
                    cancellationToken).ConfigureAwait(false);
                files = (await _drive.ListApplicationFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
                landingsFolder = Active(files, LandingsFolderKind)
                    .Where(file => file.Parents.Contains(root.Id, StringComparer.Ordinal))
                    .OrderBy(file => file.Id, StringComparer.Ordinal)
                    .FirstOrDefault() ?? throw new InvalidDataException(
                        "Google Drive did not return the newly created Landings folder.");
            }
            var hadPendingDeletes = state.PendingDeletes.Count > 0 || SessionPendingDeletes().Length > 0;
            var cloudDeletionChanged = await ProcessCloudDeletionMarkersAsync(
                    state,
                    files,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
            await ProcessPendingDeletesAsync(state, files, cancellationToken).ConfigureAwait(false);
            if (hadPendingDeletes || cloudDeletionChanged)
            {
                files = (await _drive.ListApplicationFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            }

            rootIds = ActiveRootIds(files);
            var landingsFolderIds = ActiveLandingsFolderIds(files, rootIds);

            var remoteLandings = await NormalizeLandingFilesAsync(
                    files,
                    landingsFolderIds,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
            await SyncLandingsAsync(state, remoteLandings, landingsFolder.Id, result, cancellationToken)
                .ConfigureAwait(false);

            // Re-list after creates/replacements. This deterministically collapses a
            // simultaneous first-sync duplicate and detects a concurrent edit fork.
            files = (await _drive.ListApplicationFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            rootIds = ActiveRootIds(files);
            landingsFolderIds = ActiveLandingsFolderIds(files, rootIds);
            remoteLandings = await NormalizeLandingFilesAsync(
                    files,
                    landingsFolderIds,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateFinalLandingState(state, remoteLandings, result.ConflictedLandingIds);

            var remoteSettings = await NormalizeSettingsFilesAsync(files, rootIds, result, cancellationToken)
                .ConfigureAwait(false);
            if (!result.SettingsConflict)
            {
                await SyncSettingsAsync(state, remoteSettings, root.Id, result, cancellationToken)
                    .ConfigureAwait(false);
            }

            files = (await _drive.ListApplicationFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            rootIds = ActiveRootIds(files);
            remoteSettings = await NormalizeSettingsFilesAsync(files, rootIds, result, cancellationToken)
                .ConfigureAwait(false);
            if (!result.SettingsConflict)
            {
                ValidateFinalSettingsState(state, remoteSettings, result);
            }

            _stateRepository.SavePreservingPendingDeletes(state);
            return result;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private void SwitchAccountState(
        GoogleDriveSyncState state,
        string accountPermissionId,
        string rootFolderId,
        IReadOnlyCollection<string> rootIds,
        string? previousAccountPermissionId,
        string? previousRootFolderId)
    {
        lock (_pendingDeleteGate)
        {
            state.SwitchAccount(accountPermissionId, rootFolderId, rootIds);
            _stateRepository.SaveAfterAccountSwitch(
                state,
                previousAccountPermissionId,
                previousRootFolderId);
            _sessionPendingDeletes.Clear();
        }
    }

    private static bool IsStateStorageFailure(Exception exception) =>
        exception is IOException ||
        exception is UnauthorizedAccessException ||
        exception is System.Runtime.Serialization.SerializationException;

    private async Task<bool> ProcessCloudDeletionMarkersAsync(
        GoogleDriveSyncState state,
        IReadOnlyList<GoogleDriveFile> files,
        GoogleDriveSyncResult result,
        CancellationToken cancellationToken)
    {
        var changedCloud = false;
        var deletedLandingIds = files
            .Where(file => string.Equals(file.Property(KindProperty), LandingKind, StringComparison.Ordinal))
            .Where(file => string.Equals(
                file.Property(DeletedByApplicationProperty),
                "true",
                StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Property(LandingIdProperty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var landingId in deletedLandingIds)
        {
            foreach (var active in files.Where(file =>
                         !file.Trashed &&
                         string.Equals(file.Property(KindProperty), LandingKind, StringComparison.Ordinal) &&
                         string.Equals(file.Property(LandingIdProperty), landingId, StringComparison.Ordinal)))
            {
                var deletionProperties = active.AppProperties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
                deletionProperties[DeletedByApplicationProperty] = "true";
                await _drive.UpdateAppPropertiesAsync(
                        active.Id,
                        deletionProperties,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _drive.TrashFileAsync(active.Id, cancellationToken).ConfigureAwait(false);
                changedCloud = true;
            }

            lock (_landingGate)
            {
                if (_landings.Delete(landingId!))
                {
                    result.LocalHistoryChanged = true;
                }
            }
            state.Landings.Remove(landingId!);
        }
        return changedCloud;
    }

    private async Task ProcessPendingDeletesAsync(
        GoogleDriveSyncState state,
        IReadOnlyList<GoogleDriveFile> files,
        CancellationToken cancellationToken)
    {
        foreach (var landingId in SessionPendingDeletes())
        {
            AddPendingDelete(state, landingId);
        }
        if (state.PendingDeletes.Count > 0)
        {
            _stateRepository.SavePreservingPendingDeletes(state);
        }

        foreach (var landingId in state.PendingDeletes.ToArray())
        {
            var matchingFiles = files
                .Where(file => string.Equals(
                    file.Property(KindProperty),
                    LandingKind,
                    StringComparison.Ordinal))
                .Where(file => string.Equals(
                    file.Property(LandingIdProperty),
                    landingId,
                    StringComparison.Ordinal))
                .ToArray();
            foreach (var file in matchingFiles)
            {
                var deletionProperties = file.AppProperties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
                deletionProperties[DeletedByApplicationProperty] = "true";
                await _drive.UpdateAppPropertiesAsync(
                        file.Id,
                        deletionProperties,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!file.Trashed)
                {
                    await _drive.TrashFileAsync(file.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            lock (_landingGate)
            {
                _landings.Delete(landingId);
            }

            state.Landings.Remove(landingId);
            RemovePendingDelete(state, landingId);
            _stateRepository.RemovePendingDelete(landingId);
            lock (_pendingDeleteGate)
            {
                _sessionPendingDeletes.TryRemove(landingId, out _);
            }
        }
    }

    private async Task<Dictionary<string, GoogleDriveFile>> NormalizeLandingFilesAsync(
        IReadOnlyList<GoogleDriveFile> files,
        IReadOnlyCollection<string> landingsFolderIds,
        GoogleDriveSyncResult syncResult,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, GoogleDriveFile>(StringComparer.Ordinal);
        foreach (var group in Active(files, LandingKind)
                     .Where(file => file.Parents.Any(parent => landingsFolderIds.Contains(parent)))
                     .Where(file => !string.IsNullOrWhiteSpace(file.Property(LandingIdProperty)))
                     .GroupBy(file => file.Property(LandingIdProperty)!, StringComparer.Ordinal))
        {
            GoogleDriveFile canonical;
            try
            {
                canonical = ResolveRevisionGroup(group, "landing " + group.Key);
            }
            catch (InvalidDataException)
            {
                if (!syncResult.ConflictedLandingIds.Contains(group.Key, StringComparer.Ordinal))
                {
                    syncResult.ConflictedLandingIds.Add(group.Key);
                }
                continue;
            }
            await TrashNonCanonicalAsync(group, canonical, cancellationToken).ConfigureAwait(false);
            result.Add(group.Key, canonical);
        }
        return result;
    }

    private async Task<GoogleDriveFile?> NormalizeSettingsFilesAsync(
        IReadOnlyList<GoogleDriveFile> files,
        IReadOnlyCollection<string> rootFolderIds,
        GoogleDriveSyncResult result,
        CancellationToken cancellationToken)
    {
        var values = Active(files, SettingsKind)
            .Where(file => file.Parents.Any(parent => rootFolderIds.Contains(parent)))
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }
        GoogleDriveFile canonical;
        try
        {
            canonical = ResolveRevisionGroup(values, "settings");
        }
        catch (InvalidDataException)
        {
            result.SettingsConflict = true;
            return null;
        }
        await TrashNonCanonicalAsync(values, canonical, cancellationToken).ConfigureAwait(false);
        return canonical;
    }

    private async Task TrashNonCanonicalAsync(
        IEnumerable<GoogleDriveFile> files,
        GoogleDriveFile canonical,
        CancellationToken cancellationToken)
    {
        foreach (var duplicate in files.Where(file =>
                     !string.Equals(file.Id, canonical.Id, StringComparison.Ordinal)))
        {
            await _drive.TrashFileAsync(duplicate.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SyncLandingsAsync(
        GoogleDriveSyncState state,
        IReadOnlyDictionary<string, GoogleDriveFile> remoteByLandingId,
        string landingsFolderId,
        GoogleDriveSyncResult result,
        CancellationToken cancellationToken)
    {
        Dictionary<string, LocalLandingBackup> local;
        lock (_landingGate)
        {
            var summaries = _landings.LoadAll();
            var fingerprints = _landings.GetBackupFingerprints(summaries);
            local = summaries.ToDictionary(
                record => record.Id,
                record => new LocalLandingBackup(
                    record.Id,
                    fingerprints[record.Id]),
                StringComparer.Ordinal);
        }

        foreach (var remotePair in remoteByLandingId)
        {
            if (IsDeletePending(remotePair.Key))
            {
                continue;
            }

            var remote = remotePair.Value;
            var remoteHash = remote.Property(HashProperty);
            byte[]? remoteBytes = null;
            if (string.IsNullOrWhiteSpace(remoteHash))
            {
                remoteBytes = await _drive.DownloadFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
                remoteHash = Hash(remoteBytes);
                remote = await _drive.UpdateFileAsync(
                    remote.Id,
                    remotePair.Key + ".landing.json.gz",
                    "application/gzip",
                    RevisionProperties(LandingKind, remotePair.Key, remoteHash, null),
                    remoteBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            var expectedRemoteHash = remoteHash!;

            if (local.TryGetValue(remotePair.Key, out var localBackup))
            {
                var linkedAndUnchanged = state.Landings.TryGetValue(remotePair.Key, out var cachedLink) &&
                                         string.Equals(
                                             cachedLink!.LocalFingerprint,
                                             localBackup.Fingerprint,
                                             StringComparison.Ordinal);
                if (linkedAndUnchanged)
                {
                    localBackup.Hash = cachedLink!.Sha256;
                }
                else
                {
                    LoadLocalContent(localBackup);
                }
                var localHash = localBackup.Hash!;
                if (!string.Equals(localHash, expectedRemoteHash, StringComparison.OrdinalIgnoreCase))
                {
                    var hasLink = state.Landings.TryGetValue(remotePair.Key, out var link);
                    var remoteUnchanged = hasLink &&
                                          string.Equals(expectedRemoteHash, link!.Sha256, StringComparison.OrdinalIgnoreCase);
                    var localUnchanged = hasLink &&
                                         string.Equals(localHash, link!.Sha256, StringComparison.OrdinalIgnoreCase);

                    if (remoteUnchanged && !localUnchanged)
                    {
                        var uploaded = await _drive.UploadFileAsync(
                            remotePair.Key + ".landing.json.gz",
                            "application/gzip",
                            landingsFolderId,
                            RevisionProperties(LandingKind, remotePair.Key, localHash, expectedRemoteHash),
                            localBackup.Bytes!,
                            cancellationToken).ConfigureAwait(false);
                        await _drive.TrashFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
                        state.Landings[remotePair.Key] = Link(
                            uploaded.Id,
                            localHash,
                            localBackup.Fingerprint);
                        result.UploadedLandings++;
                        continue;
                    }

                    if (localUnchanged && !remoteUnchanged)
                    {
                        remoteBytes ??= await _drive.DownloadFileAsync(remote.Id, cancellationToken)
                            .ConfigureAwait(false);
                        VerifyHash(remoteBytes, expectedRemoteHash, "landing");
                        lock (_landingGate)
                        {
                            if (IsDeletePending(remotePair.Key))
                            {
                                continue;
                            }
                            _landings.Import(remoteBytes, remotePair.Key, true);
                            localBackup.Fingerprint = _landings.GetBackupFingerprint(
                                _landings.LoadAll().Single(record =>
                                    string.Equals(record.Id, remotePair.Key, StringComparison.Ordinal)));
                        }
                        localBackup.Bytes = remoteBytes;
                        localBackup.Hash = expectedRemoteHash;
                        state.Landings[remotePair.Key] = Link(
                            remote.Id,
                            expectedRemoteHash,
                            localBackup.Fingerprint);
                        result.DownloadedLandings++;
                        continue;
                    }

                    if (await TryResolveLandingMetadataConflictAsync(
                            state,
                            remotePair.Key,
                            remote,
                            expectedRemoteHash,
                            remoteBytes,
                            localBackup,
                            landingsFolderId,
                            result,
                            cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    result.ConflictedLandingIds.Add(remotePair.Key);
                    continue;
                }

                state.Landings[remotePair.Key] = Link(
                    remote.Id,
                    localHash,
                    localBackup.Fingerprint);
                continue;
            }

            remoteBytes ??= await _drive.DownloadFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
            VerifyHash(remoteBytes, expectedRemoteHash, "landing");
            lock (_landingGate)
            {
                if (IsDeletePending(remotePair.Key))
                {
                    continue;
                }
                _landings.Import(remoteBytes, remotePair.Key);
                var summary = _landings.LoadAll().Single(record =>
                    string.Equals(record.Id, remotePair.Key, StringComparison.Ordinal));
                local[remotePair.Key] = new LocalLandingBackup(
                    remotePair.Key,
                    _landings.GetBackupFingerprint(summary),
                    remoteBytes,
                    expectedRemoteHash);
            }
            state.Landings[remotePair.Key] = Link(
                remote.Id,
                expectedRemoteHash,
                local[remotePair.Key].Fingerprint);
            result.DownloadedLandings++;
        }

        foreach (var pair in local.ToArray())
        {
            if (remoteByLandingId.ContainsKey(pair.Key))
            {
                continue;
            }

            if (IsDeletePending(pair.Key))
            {
                continue;
            }

            if (result.ConflictedLandingIds.Contains(pair.Key, StringComparer.Ordinal))
            {
                continue;
            }

            // Google Drive is a backup, not an authority for destructive changes.
            // A file or the Landings folder removed manually in Drive is recreated
            // from the intact local record. Only an explicit in-app delete enters
            // PendingDeletes and reaches the Drive trash.
            LoadLocalContent(pair.Value);
            var hash = pair.Value.Hash!;
            var uploaded = await _drive.UploadFileAsync(
                pair.Key + ".landing.json.gz",
                "application/gzip",
                landingsFolderId,
                RevisionProperties(LandingKind, pair.Key, hash, null),
                pair.Value.Bytes!,
                cancellationToken).ConfigureAwait(false);
            state.Landings[pair.Key] = Link(uploaded.Id, hash, pair.Value.Fingerprint);
            result.UploadedLandings++;
        }
    }

    private async Task<bool> TryResolveLandingMetadataConflictAsync(
        GoogleDriveSyncState state,
        string landingId,
        GoogleDriveFile remote,
        string remoteHash,
        byte[]? remoteBytes,
        LocalLandingBackup localBackup,
        string landingsFolderId,
        GoogleDriveSyncResult result,
        CancellationToken cancellationToken)
    {
        LoadLocalContent(localBackup);
        remoteBytes ??= await _drive.DownloadFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
        VerifyHash(remoteBytes, remoteHash, "landing");

        byte[] mergedBytes;
        string mergedHash;
        lock (_landingGate)
        {
            if (IsDeletePending(landingId))
            {
                return true;
            }
            var localRecord = _landings.DeserializeBackup(localBackup.Bytes!);
            var remoteRecord = _landings.DeserializeBackup(remoteBytes);
            if (!string.Equals(
                    MetadataNeutralHash(localRecord),
                    MetadataNeutralHash(remoteRecord),
                    StringComparison.OrdinalIgnoreCase) ||
                !TryMergeLandingMetadata(localRecord, remoteRecord))
            {
                return false;
            }

            mergedBytes = _landings.SerializeForBackup(localRecord);
            mergedHash = Hash(mergedBytes);
            if (!string.Equals(mergedHash, localBackup.Hash, StringComparison.OrdinalIgnoreCase))
            {
                _landings.Import(mergedBytes, landingId, true);
                result.LocalHistoryChanged = true;
                var summary = _landings.LoadAll().Single(record =>
                    string.Equals(record.Id, landingId, StringComparison.Ordinal));
                localBackup.Fingerprint = _landings.GetBackupFingerprint(summary);
                localBackup.Bytes = mergedBytes;
                localBackup.Hash = mergedHash;
            }
        }

        if (string.Equals(mergedHash, remoteHash, StringComparison.OrdinalIgnoreCase))
        {
            state.Landings[landingId] = Link(remote.Id, remoteHash, localBackup.Fingerprint);
            result.DownloadedLandings++;
            result.ResolvedLandingConflicts++;
            return true;
        }

        var uploaded = await _drive.UploadFileAsync(
            landingId + ".landing.json.gz",
            "application/gzip",
            landingsFolderId,
            RevisionProperties(LandingKind, landingId, mergedHash, remoteHash),
            mergedBytes,
            cancellationToken).ConfigureAwait(false);
        await _drive.TrashFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
        state.Landings[landingId] = Link(uploaded.Id, mergedHash, localBackup.Fingerprint);
        result.UploadedLandings++;
        result.ResolvedLandingConflicts++;
        return true;
    }

    private string MetadataNeutralHash(LandingRecord record)
    {
        var airport = record.Airport;
        var runway = record.Runway;
        var distance = record.AirportDistanceNauticalMiles;
        try
        {
            record.Airport = "Unknown airport";
            record.Runway = "—";
            record.AirportDistanceNauticalMiles = null;
            return Hash(_landings.SerializeForBackup(record));
        }
        finally
        {
            record.Airport = airport;
            record.Runway = runway;
            record.AirportDistanceNauticalMiles = distance;
        }
    }

    private static bool TryMergeLandingMetadata(LandingRecord local, LandingRecord remote)
    {
        if (!TryMergeText(local.Airport, remote.Airport, IsUnknownAirport, out var airport) ||
            !TryMergeText(local.Runway, remote.Runway, IsUnknownRunway, out var runway))
        {
            return false;
        }

        var localAirportWon = string.Equals(airport, local.Airport, StringComparison.OrdinalIgnoreCase) &&
                              !IsUnknownAirport(local.Airport);
        var remoteAirportWon = string.Equals(airport, remote.Airport, StringComparison.OrdinalIgnoreCase) &&
                               !IsUnknownAirport(remote.Airport);
        local.Airport = airport;
        local.Runway = runway;
        local.AirportDistanceNauticalMiles = localAirportWon && !remoteAirportWon
            ? local.AirportDistanceNauticalMiles
            : remoteAirportWon && !localAirportWon
                ? remote.AirportDistanceNauticalMiles
                : MinimumDistance(local.AirportDistanceNauticalMiles, remote.AirportDistanceNauticalMiles);
        return true;
    }

    private static bool TryMergeText(
        string? local,
        string? remote,
        Func<string?, bool> isUnknown,
        out string merged)
    {
        if (string.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
        {
            merged = local ?? string.Empty;
            return true;
        }
        if (isUnknown(local))
        {
            merged = remote ?? string.Empty;
            return true;
        }
        if (isUnknown(remote))
        {
            merged = local ?? string.Empty;
            return true;
        }
        merged = string.Empty;
        return false;
    }

    private static bool IsUnknownAirport(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, "Unknown airport", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknownRunway(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "—", StringComparison.Ordinal);

    private static double? MinimumDistance(double? first, double? second)
    {
        if (!first.HasValue)
        {
            return second;
        }
        if (!second.HasValue)
        {
            return first;
        }
        return Math.Min(first.Value, second.Value);
    }

    private async Task SyncSettingsAsync(
        GoogleDriveSyncState state,
        GoogleDriveFile? remote,
        string rootId,
        GoogleDriveSyncResult result,
        CancellationToken cancellationToken)
    {
        var local = await _readSettingsAsync().ConfigureAwait(false);
        var localBytes = GoogleDriveCloudSettings.Serialize(local.Settings.Clone());
        var localHash = Hash(localBytes);
        if (remote == null)
        {
            var uploaded = await _drive.UploadFileAsync(
                "settings.json",
                "application/json",
                rootId,
                RevisionProperties(SettingsKind, null, localHash, null),
                localBytes,
                cancellationToken).ConfigureAwait(false);
            state.SettingsFileId = uploaded.Id;
            state.LastSettingsHash = localHash;
            result.UploadedSettings = true;
            return;
        }

        var remoteHash = remote.Property(HashProperty);
        byte[]? remoteBytes = null;
        if (string.IsNullOrWhiteSpace(remoteHash))
        {
            remoteBytes = await _drive.DownloadFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
            remoteHash = Hash(remoteBytes);
            remote = await _drive.UpdateFileAsync(
                remote.Id,
                "settings.json",
                "application/json",
                RevisionProperties(SettingsKind, null, remoteHash, null),
                remoteBytes,
                cancellationToken).ConfigureAwait(false);
        }
        var expectedRemoteHash = remoteHash!;
        remoteBytes ??= await _drive.DownloadFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
        VerifyHash(remoteBytes, expectedRemoteHash, "settings");
        var remoteSettings = GoogleDriveCloudSettings.Deserialize(remoteBytes);
        if (!remoteSettings.IsSupported)
        {
            result.UnsupportedSettingsFormatVersion = remoteSettings.FormatVersion;
            return;
        }

        state.SettingsFileId = remote.Id;
        if (!local.PersistedAndReadable)
        {
            await DownloadAndApplySettingsAsync(
                    remote,
                    expectedRemoteHash,
                    remoteBytes,
                    remoteSettings,
                    localHash,
                    state,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(localHash, expectedRemoteHash, StringComparison.OrdinalIgnoreCase))
        {
            state.LastSettingsHash = localHash;
            return;
        }

        if (string.IsNullOrWhiteSpace(state.LastSettingsHash))
        {
            // First union sync preserves the established behaviour: an existing cloud
            // settings file initializes a newly connected device.
            await DownloadAndApplySettingsAsync(
                    remote,
                    expectedRemoteHash,
                    remoteBytes,
                    remoteSettings,
                    localHash,
                    state,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var remoteUnchanged = string.Equals(
            expectedRemoteHash,
            state.LastSettingsHash,
            StringComparison.OrdinalIgnoreCase);
        var localUnchanged = string.Equals(
            localHash,
            state.LastSettingsHash,
            StringComparison.OrdinalIgnoreCase);
        if (remoteUnchanged && !localUnchanged)
        {
            var uploaded = await _drive.UploadFileAsync(
                "settings.json",
                "application/json",
                rootId,
                RevisionProperties(SettingsKind, null, localHash, expectedRemoteHash),
                localBytes,
                cancellationToken).ConfigureAwait(false);
            await _drive.TrashFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
            state.SettingsFileId = uploaded.Id;
            state.LastSettingsHash = localHash;
            result.UploadedSettings = true;
            return;
        }

        if (localUnchanged && !remoteUnchanged)
        {
            await DownloadAndApplySettingsAsync(
                    remote,
                    expectedRemoteHash,
                    remoteBytes,
                    remoteSettings,
                    localHash,
                    state,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Settings are a tiny mutable preference set. If both devices changed
        // them since the shared base, the device performing this sync wins as
        // one atomic revision; the other device will download that revision on
        // its next sync. This converges instead of permanently blocking backup.
        var resolved = await _drive.UploadFileAsync(
            "settings.json",
            "application/json",
            rootId,
            RevisionProperties(SettingsKind, null, localHash, expectedRemoteHash),
            localBytes,
            cancellationToken).ConfigureAwait(false);
        await _drive.TrashFileAsync(remote.Id, cancellationToken).ConfigureAwait(false);
        state.SettingsFileId = resolved.Id;
        state.LastSettingsHash = localHash;
        result.UploadedSettings = true;
        result.ResolvedSettingsConflict = true;
    }

    private async Task<bool> DownloadAndApplySettingsAsync(
        GoogleDriveFile remote,
        string remoteHash,
        byte[] remoteBytes,
        GoogleDriveCloudSettings cloudSettings,
        string expectedLocalHash,
        GoogleDriveSyncState state,
        GoogleDriveSyncResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyHash(remoteBytes, remoteHash, "settings");
        if (!await _applySettingsAsync(cloudSettings, expectedLocalHash).ConfigureAwait(false))
        {
            result.SettingsChangedDuringSync = true;
            return false;
        }
        state.SettingsFileId = remote.Id;
        state.LastSettingsHash = remoteHash;
        result.DownloadedSettings = true;
        return true;
    }

    private void ValidateFinalLandingState(
        GoogleDriveSyncState state,
        IReadOnlyDictionary<string, GoogleDriveFile> remoteByLandingId,
        IReadOnlyCollection<string> conflictedLandingIds)
    {
        foreach (var pair in state.Landings.ToArray())
        {
            if (conflictedLandingIds.Contains(pair.Key, StringComparer.Ordinal) || IsDeletePending(pair.Key))
            {
                continue;
            }
            if (!remoteByLandingId.TryGetValue(pair.Key, out var remote))
            {
                lock (_landingGate)
                {
                    if (_landings.Contains(pair.Key))
                    {
                        throw new InvalidDataException(
                            "Cloud landing " + pair.Key + " changed while synchronization was running.");
                    }
                }
                continue;
            }

            var remoteHash = remote.Property(HashProperty);
            if (!string.Equals(remoteHash, pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Cloud landing " + pair.Key + " changed while synchronization was running.");
            }
            pair.Value.FileId = remote.Id;
        }
    }

    private static void ValidateFinalSettingsState(
        GoogleDriveSyncState state,
        GoogleDriveFile? remote,
        GoogleDriveSyncResult result)
    {
        if (result.SettingsChangedDuringSync || result.UnsupportedSettingsFormatVersion.HasValue)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(state.LastSettingsHash))
        {
            return;
        }
        if (remote == null ||
            !string.Equals(
                remote.Property(HashProperty),
                state.LastSettingsHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Cloud settings changed while synchronization was running.");
        }
        state.SettingsFileId = remote.Id;
    }

    private static GoogleDriveFile ResolveRevisionGroup(
        IEnumerable<GoogleDriveFile> candidates,
        string label)
    {
        var values = candidates.OrderBy(file => file.Id, StringComparer.Ordinal).ToArray();
        if (values.Length == 1)
        {
            return values[0];
        }
        if (values.Any(file => string.IsNullOrWhiteSpace(file.Property(HashProperty))))
        {
            throw new InvalidDataException("Google Drive contains conflicting " + label + " backup files.");
        }

        var byHash = values
            .GroupBy(file => file.Property(HashProperty)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        if (byHash.Count == 1)
        {
            return values[0];
        }

        var childrenByBase = byHash.Values
            .Where(file => !string.IsNullOrWhiteSpace(file.Property(BaseHashProperty)))
            .GroupBy(file => file.Property(BaseHashProperty)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (childrenByBase.Any(pair => pair.Value.Length != 1))
        {
            throw new InvalidDataException("Google Drive contains concurrent " + label + " edits.");
        }

        var leaves = byHash
            .Where(pair => !childrenByBase.ContainsKey(pair.Key))
            .Select(pair => pair.Key)
            .ToArray();
        if (leaves.Length != 1)
        {
            throw new InvalidDataException("Google Drive contains concurrent " + label + " edits.");
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = leaves[0];
        while (visited.Add(current))
        {
            var parent = byHash[current].Property(BaseHashProperty);
            if (string.IsNullOrWhiteSpace(parent) || !byHash.ContainsKey(parent!))
            {
                break;
            }
            current = parent!;
        }
        if (visited.Count != byHash.Count)
        {
            throw new InvalidDataException("Google Drive contains concurrent " + label + " edits.");
        }

        return byHash[leaves[0]];
    }

    private void LoadLocalContent(LocalLandingBackup local)
    {
        if (local.Bytes != null && !string.IsNullOrWhiteSpace(local.Hash))
        {
            return;
        }
        lock (_landingGate)
        {
            local.Bytes = _landings.ExportForBackup(local.Id);
        }
        local.Hash = Hash(local.Bytes);
    }

    private static GoogleDriveLandingLink Link(
        string fileId,
        string hash,
        string localFingerprint)
    {
        return new GoogleDriveLandingLink
        {
            FileId = fileId,
            Sha256 = hash,
            LocalFingerprint = localFingerprint,
        };
    }

    private static IReadOnlyDictionary<string, string> RevisionProperties(
        string kind,
        string? landingId,
        string hash,
        string? baseHash)
    {
        var additional = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>(HashProperty, hash),
        };
        if (!string.IsNullOrWhiteSpace(landingId))
        {
            additional.Add(new KeyValuePair<string, string>(LandingIdProperty, landingId!));
        }
        if (!string.IsNullOrWhiteSpace(baseHash))
        {
            additional.Add(new KeyValuePair<string, string>(BaseHashProperty, baseHash!));
        }
        return Properties(kind, additional.ToArray());
    }

    private static void VerifyHash(byte[] bytes, string expectedHash, string label)
    {
        if (!string.Equals(Hash(bytes), expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Google Drive " + label + " backup failed its SHA-256 check.");
        }
    }

    private static bool AddPendingDelete(GoogleDriveSyncState state, string landingId)
    {
        if (state.PendingDeletes.Contains(landingId, StringComparer.Ordinal))
        {
            return false;
        }
        state.PendingDeletes.Add(landingId);
        return true;
    }

    private static bool RemovePendingDelete(GoogleDriveSyncState state, string landingId)
    {
        return state.PendingDeletes.RemoveAll(value =>
            string.Equals(value, landingId, StringComparison.Ordinal)) > 0;
    }

    private bool IsDeletePending(string landingId)
    {
        return _sessionPendingDeletes.ContainsKey(landingId);
    }

    private string[] SessionPendingDeletes()
    {
        lock (_pendingDeleteGate)
        {
            return _sessionPendingDeletes.Keys.ToArray();
        }
    }

    private void ClearSessionPendingDeletes()
    {
        lock (_pendingDeleteGate)
        {
            _sessionPendingDeletes.Clear();
        }
    }

    private static void ValidateLandingId(string landingId)
    {
        if (string.IsNullOrWhiteSpace(landingId))
        {
            throw new ArgumentException("A landing id is required.", nameof(landingId));
        }
    }

    private static IEnumerable<GoogleDriveFile> Active(
        IEnumerable<GoogleDriveFile> files,
        string kind) => files.Where(file =>
        !file.Trashed && string.Equals(file.Property(KindProperty), kind, StringComparison.Ordinal));

    private static string[] ActiveRootIds(IReadOnlyList<GoogleDriveFile> files) =>
        Active(files, RootKind)
            .Select(file => file.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string[] ActiveLandingsFolderIds(
        IReadOnlyList<GoogleDriveFile> files,
        IReadOnlyCollection<string> rootIds) =>
        Active(files, LandingsFolderKind)
            .Where(file => file.Parents.Any(parent => rootIds.Contains(parent)))
            .Select(file => file.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static Dictionary<string, string> Properties(
        string kind,
        params KeyValuePair<string, string>[] additional)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KindProperty] = kind,
        };
        foreach (var pair in additional)
        {
            values[pair.Key] = pair.Value;
        }
        return values;
    }

    internal static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var builder = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            builder.Append(value.ToString("x2"));
        }
        return builder.ToString();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GoogleDriveBackupService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _syncGate.Dispose();
    }

    private sealed class LocalLandingBackup
    {
        public LocalLandingBackup(
            string id,
            string fingerprint,
            byte[]? bytes = null,
            string? hash = null)
        {
            Id = id;
            Fingerprint = fingerprint;
            Bytes = bytes;
            Hash = hash;
        }

        public string Id { get; }
        public string Fingerprint { get; set; }
        public byte[]? Bytes { get; set; }
        public string? Hash { get; set; }
    }
}

internal sealed class GoogleDriveSyncResult
{
    public int UploadedLandings { get; set; }
    public int DownloadedLandings { get; set; }
    public List<string> ConflictedLandingIds { get; } = new List<string>();
    public int ResolvedLandingConflicts { get; set; }
    public bool UploadedSettings { get; set; }
    public bool DownloadedSettings { get; set; }
    public bool ResolvedSettingsConflict { get; set; }
    public bool LocalHistoryChanged { get; set; }
    public bool SettingsChangedDuringSync { get; set; }
    public int? UnsupportedSettingsFormatVersion { get; set; }
    public bool SettingsConflict { get; set; }

    public bool HistoryChanged => DownloadedLandings > 0 || LocalHistoryChanged;
}
