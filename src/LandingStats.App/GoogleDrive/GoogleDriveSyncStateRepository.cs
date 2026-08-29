using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace LandingStats.App.GoogleDrive;

internal sealed class GoogleDriveSyncStateRepository
{
    private readonly object _gate = new object();
    private readonly string _path;

    public GoogleDriveSyncStateRepository(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "google-drive-sync.json");
    }

    public GoogleDriveSyncState Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    public void Save(GoogleDriveSyncState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        lock (_gate)
        {
            SaveCore(state);
        }
    }

    public void SaveAfterAccountSwitch(
        GoogleDriveSyncState state,
        string? previousAccountPermissionId,
        string? previousRootFolderId)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        lock (_gate)
        {
            var persisted = LoadCore();
            var samePreviousAccount = !string.IsNullOrWhiteSpace(previousAccountPermissionId) &&
                                      string.Equals(
                                          persisted.AccountPermissionId,
                                          previousAccountPermissionId,
                                          StringComparison.Ordinal);
            var samePreviousLegacyRoot = string.IsNullOrWhiteSpace(previousAccountPermissionId) &&
                                         string.IsNullOrWhiteSpace(persisted.AccountPermissionId) &&
                                         !string.IsNullOrWhiteSpace(previousRootFolderId) &&
                                         string.Equals(
                                             persisted.RootFolderId,
                                             previousRootFolderId,
                                             StringComparison.Ordinal);
            if (samePreviousAccount || samePreviousLegacyRoot)
            {
                var target = samePreviousAccount
                    ? PendingBucket(state.PendingDeletesByAccount, previousAccountPermissionId!)
                    : PendingBucket(state.PendingDeletesByRoot, previousRootFolderId!);
                foreach (var landingId in persisted.PendingDeletes)
                {
                    if (!target.Contains(landingId, StringComparer.Ordinal))
                    {
                        target.Add(landingId);
                    }
                }
            }
            SaveCore(state);
        }
    }

    public void AddPendingDelete(string landingId)
    {
        if (string.IsNullOrWhiteSpace(landingId))
        {
            throw new ArgumentException("A landing id is required.", nameof(landingId));
        }
        lock (_gate)
        {
            var state = LoadCore();
            if (!state.PendingDeletes.Contains(landingId, StringComparer.Ordinal))
            {
                state.PendingDeletes.Add(landingId);
                SaveCore(state);
            }
        }
    }

    public void RemovePendingDelete(string landingId)
    {
        lock (_gate)
        {
            var state = LoadCore();
            if (state.PendingDeletes.RemoveAll(value =>
                    string.Equals(value, landingId, StringComparison.Ordinal)) > 0)
            {
                SaveCore(state);
            }
        }
    }

    public void SavePreservingPendingDeletes(GoogleDriveSyncState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        lock (_gate)
        {
            var persisted = LoadCore();
            var sameAccount = !string.IsNullOrWhiteSpace(state.AccountPermissionId) &&
                              string.Equals(
                                  persisted.AccountPermissionId,
                                  state.AccountPermissionId,
                                  StringComparison.Ordinal);
            var sameLegacyRoot = string.IsNullOrWhiteSpace(persisted.AccountPermissionId) &&
                                 string.Equals(
                                     persisted.RootFolderId,
                                     state.RootFolderId,
                                     StringComparison.Ordinal);
            if (sameAccount || sameLegacyRoot)
            {
                foreach (var landingId in persisted.PendingDeletes)
                {
                    if (!state.PendingDeletes.Contains(landingId, StringComparer.Ordinal))
                    {
                        state.PendingDeletes.Add(landingId);
                    }
                }
            }
            SaveCore(state);
        }
    }

    private GoogleDriveSyncState LoadCore()
    {
        if (!File.Exists(_path))
        {
            return new GoogleDriveSyncState();
        }
        try
        {
            using var input = File.OpenRead(_path);
            var value = new DataContractJsonSerializer(typeof(GoogleDriveSyncState))
                .ReadObject(input) as GoogleDriveSyncState;
            value ??= new GoogleDriveSyncState();
            value.Normalize();
            return value;
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is SerializationException)
        {
            return new GoogleDriveSyncState();
        }
    }

    private static List<string> PendingBucket(
        Dictionary<string, List<string>> buckets,
        string key)
    {
        if (!buckets.TryGetValue(key, out var values))
        {
            values = new List<string>();
            buckets[key] = values;
        }
        return values;
    }

    private void SaveCore(GoogleDriveSyncState state)
    {
        state.Normalize();
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporary = _path + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(GoogleDriveSyncState)).WriteObject(output, state);
                output.Flush(true);
            }
            if (File.Exists(_path))
            {
                File.Replace(temporary, _path, null, true);
            }
            else
            {
                File.Move(temporary, _path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

[DataContract]
internal sealed class GoogleDriveSyncState
{
    [DataMember(Name = "formatVersion", Order = 1)]
    public int FormatVersion { get; set; } = 4;

    [DataMember(Name = "landings", Order = 2)]
    public Dictionary<string, GoogleDriveLandingLink> Landings { get; set; } =
        new Dictionary<string, GoogleDriveLandingLink>(StringComparer.Ordinal);

    [DataMember(Name = "pendingDeletes", Order = 3)]
    public List<string> PendingDeletes { get; set; } = new List<string>();

    [DataMember(Name = "settingsFileId", Order = 4, EmitDefaultValue = false)]
    public string? SettingsFileId { get; set; }

    [DataMember(Name = "lastSettingsHash", Order = 5, EmitDefaultValue = false)]
    public string? LastSettingsHash { get; set; }

    [DataMember(Name = "rootFolderId", Order = 6, EmitDefaultValue = false)]
    public string? RootFolderId { get; set; }

    [DataMember(Name = "pendingDeletesByRoot", Order = 7, EmitDefaultValue = false)]
    public Dictionary<string, List<string>> PendingDeletesByRoot { get; set; } =
        new Dictionary<string, List<string>>(StringComparer.Ordinal);

    [DataMember(Name = "knownRootIds", Order = 8, EmitDefaultValue = false)]
    public List<string> KnownRootIds { get; set; } = new List<string>();

    [DataMember(Name = "accountPermissionId", Order = 9, EmitDefaultValue = false)]
    public string? AccountPermissionId { get; set; }

    [DataMember(Name = "pendingDeletesByAccount", Order = 10, EmitDefaultValue = false)]
    public Dictionary<string, List<string>> PendingDeletesByAccount { get; set; } =
        new Dictionary<string, List<string>>(StringComparer.Ordinal);

    public void Normalize()
    {
        FormatVersion = 4;
        Landings ??= new Dictionary<string, GoogleDriveLandingLink>(StringComparer.Ordinal);
        if (Landings.Comparer != StringComparer.Ordinal)
        {
            Landings = new Dictionary<string, GoogleDriveLandingLink>(Landings, StringComparer.Ordinal);
        }
        PendingDeletes ??= new List<string>();
        PendingDeletes.RemoveAll(string.IsNullOrWhiteSpace);
        PendingDeletes = PendingDeletes.Distinct(StringComparer.Ordinal).ToList();
        PendingDeletesByRoot ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (PendingDeletesByRoot.Comparer != StringComparer.Ordinal)
        {
            PendingDeletesByRoot = new Dictionary<string, List<string>>(
                PendingDeletesByRoot,
                StringComparer.Ordinal);
        }
        foreach (var pair in PendingDeletesByRoot.ToArray())
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                PendingDeletesByRoot.Remove(pair.Key);
                continue;
            }
            pair.Value?.RemoveAll(string.IsNullOrWhiteSpace);
            PendingDeletesByRoot[pair.Key] = (pair.Value ?? new List<string>())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        if (!string.IsNullOrWhiteSpace(RootFolderId))
        {
            PendingDeletesByRoot[RootFolderId!] = PendingDeletes.ToList();
        }
        PendingDeletesByAccount ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (PendingDeletesByAccount.Comparer != StringComparer.Ordinal)
        {
            PendingDeletesByAccount = new Dictionary<string, List<string>>(
                PendingDeletesByAccount,
                StringComparer.Ordinal);
        }
        foreach (var pair in PendingDeletesByAccount.ToArray())
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                PendingDeletesByAccount.Remove(pair.Key);
                continue;
            }
            pair.Value?.RemoveAll(string.IsNullOrWhiteSpace);
            PendingDeletesByAccount[pair.Key] = (pair.Value ?? new List<string>())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        if (!string.IsNullOrWhiteSpace(AccountPermissionId))
        {
            PendingDeletesByAccount[AccountPermissionId!] = PendingDeletes.ToList();
        }
        KnownRootIds ??= new List<string>();
        KnownRootIds.RemoveAll(string.IsNullOrWhiteSpace);
        KnownRootIds = KnownRootIds.Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(RootFolderId) &&
            !KnownRootIds.Contains(RootFolderId!, StringComparer.Ordinal))
        {
            KnownRootIds.Add(RootFolderId!);
        }
    }

    public void ResetForRoot(string rootFolderId, IReadOnlyCollection<string>? activeRootIds = null)
    {
        if (string.IsNullOrWhiteSpace(rootFolderId))
        {
            throw new ArgumentException("A Google Drive root folder id is required.", nameof(rootFolderId));
        }

        if (!string.IsNullOrWhiteSpace(RootFolderId))
        {
            PendingDeletesByRoot[RootFolderId!] = PendingDeletes.ToList();
        }

        RootFolderId = rootFolderId;
        Landings.Clear();
        PendingDeletes = PendingDeletesByRoot.TryGetValue(rootFolderId, out var pendingForRoot)
            ? pendingForRoot.ToList()
            : new List<string>();
        KnownRootIds = (activeRootIds ?? new[] { rootFolderId })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        SettingsFileId = null;
        LastSettingsHash = null;
    }

    public void SwitchAccount(
        string accountPermissionId,
        string rootFolderId,
        IReadOnlyCollection<string> activeRootIds)
    {
        if (string.IsNullOrWhiteSpace(accountPermissionId))
        {
            throw new ArgumentException("A Google Drive account identity is required.", nameof(accountPermissionId));
        }
        if (activeRootIds == null)
        {
            throw new ArgumentNullException(nameof(activeRootIds));
        }

        if (!string.IsNullOrWhiteSpace(AccountPermissionId))
        {
            PendingDeletesByAccount[AccountPermissionId!] = PendingDeletes.ToList();
        }
        else if (!string.IsNullOrWhiteSpace(RootFolderId))
        {
            PendingDeletesByRoot[RootFolderId!] = PendingDeletes.ToList();
        }

        AccountPermissionId = accountPermissionId;
        RootFolderId = rootFolderId;
        Landings.Clear();
        if (PendingDeletesByAccount.TryGetValue(accountPermissionId, out var accountPending))
        {
            PendingDeletes = accountPending.ToList();
        }
        else if (PendingDeletesByRoot.TryGetValue(rootFolderId, out var legacyRootPending))
        {
            PendingDeletes = legacyRootPending.ToList();
        }
        else
        {
            PendingDeletes = new List<string>();
        }
        KnownRootIds = activeRootIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        SettingsFileId = null;
        LastSettingsHash = null;
    }

    public void RebaseWithinActiveRoots(
        string rootFolderId,
        IReadOnlyCollection<string> activeRootIds)
    {
        if (string.IsNullOrWhiteSpace(rootFolderId))
        {
            throw new ArgumentException("A Google Drive root folder id is required.", nameof(rootFolderId));
        }
        if (activeRootIds == null)
        {
            throw new ArgumentNullException(nameof(activeRootIds));
        }

        var merged = new HashSet<string>(PendingDeletes, StringComparer.Ordinal);
        foreach (var activeRootId in activeRootIds)
        {
            if (PendingDeletesByRoot.TryGetValue(activeRootId, out var pending))
            {
                merged.UnionWith(pending);
            }
        }

        RootFolderId = rootFolderId;
        PendingDeletes = merged.ToList();
        foreach (var activeRootId in activeRootIds)
        {
            PendingDeletesByRoot[activeRootId] = PendingDeletes.ToList();
        }
        KnownRootIds = activeRootIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public void RememberActiveRoots(IReadOnlyCollection<string> activeRootIds)
    {
        if (activeRootIds == null)
        {
            throw new ArgumentNullException(nameof(activeRootIds));
        }
        KnownRootIds = activeRootIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

[DataContract]
internal sealed class GoogleDriveLandingLink
{
    [DataMember(Name = "fileId", Order = 1)]
    public string FileId { get; set; } = string.Empty;

    [DataMember(Name = "sha256", Order = 2)]
    public string Sha256 { get; set; } = string.Empty;

    [DataMember(Name = "localFingerprint", Order = 3, EmitDefaultValue = false)]
    public string? LocalFingerprint { get; set; }
}
