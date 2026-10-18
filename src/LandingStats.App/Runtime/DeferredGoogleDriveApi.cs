using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;

namespace LandingStats.App.Runtime;

/// <summary>Allows local logbook/delete services to exist before the runtime is
/// ready, without creating an unsupervised HTTP client or a transport fallback.</summary>
internal sealed class DeferredGoogleDriveApi : IGoogleDriveApi
{
    private IGoogleDriveApi? _target;
    public void Bind(IGoogleDriveApi target)
    { if (Interlocked.CompareExchange(ref _target, target, null) != null) throw new InvalidOperationException("Cloud transport is already owned by a runtime."); }
    private IGoogleDriveApi Target => Volatile.Read(ref _target) ?? throw new GoogleDriveNotSignedInException();
    public Task<string> GetAccountPermissionIdAsync(CancellationToken token) => Target.GetAccountPermissionIdAsync(token);
    public Task<IReadOnlyList<GoogleDriveFile>> ListApplicationFilesAsync(CancellationToken token) => Target.ListApplicationFilesAsync(token);
    public Task<GoogleDriveFile> CreateFolderAsync(string name, string? parent, IReadOnlyDictionary<string, string> properties, CancellationToken token) => Target.CreateFolderAsync(name, parent, properties, token);
    public Task<GoogleDriveFile> UploadFileAsync(string name, string mime, string parent, IReadOnlyDictionary<string, string> properties, byte[] content, CancellationToken token) => Target.UploadFileAsync(name, mime, parent, properties, content, token);
    public Task<GoogleDriveFile> UpdateFileAsync(string id, string name, string mime, IReadOnlyDictionary<string, string> properties, byte[] content, CancellationToken token) => Target.UpdateFileAsync(id, name, mime, properties, content, token);
    public Task<GoogleDriveFile> UpdateAppPropertiesAsync(string id, IReadOnlyDictionary<string, string> properties, CancellationToken token) => Target.UpdateAppPropertiesAsync(id, properties, token);
    public Task<byte[]> DownloadFileAsync(string id, CancellationToken token) => Target.DownloadFileAsync(id, token);
    public Task TrashFileAsync(string id, CancellationToken token) => Target.TrashFileAsync(id, token);
}
