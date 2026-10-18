using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;

namespace LandingStats.App.Runtime;

/// <summary>Deterministic peer for process-boundary regression tests only.</summary>
internal sealed class CloudApiFixture : IGoogleDriveApi
{
    private readonly string _mode;
    private readonly GoogleOAuthTokenStore _tokens;
    public CloudApiFixture(string mode, GoogleOAuthTokenStore tokens) { _mode = mode; _tokens = tokens; }
    private async Task Before()
    {
        if (_mode == "cloud-unconfigured") throw new GoogleDriveConfigurationException();
        if (_mode == "cloud-hang") await new TaskCompletionSource<bool>().Task.ConfigureAwait(false);
        if (_mode == "cloud-late") await Task.Delay(600).ConfigureAwait(false);
        if (_mode == "cloud-invalid") { _tokens.Clear(); throw new GoogleDriveNotSignedInException(); }
        if (_mode == "cloud-refresh" || _mode == "cloud-late") _tokens.Save("fixture-refreshed-token", "fixture@example.invalid");
    }
    public async Task<string> GetAccountPermissionIdAsync(CancellationToken token) { await Before().ConfigureAwait(false); return "fixture-account"; }
    public async Task<IReadOnlyList<GoogleDriveFile>> ListApplicationFilesAsync(CancellationToken token)
    { await Before().ConfigureAwait(false); return new[] { File("fixture-file", "landing", "application/gzip", new Dictionary<string, string> { ["kind"] = "landing" }, new[] { "fixture-root" }) }; }
    public async Task<GoogleDriveFile> CreateFolderAsync(string name, string? parentId, IReadOnlyDictionary<string, string> properties, CancellationToken token)
    { await Before().ConfigureAwait(false); return File("fixture-folder", name, "application/vnd.google-apps.folder", properties, parentId == null ? Array.Empty<string>() : new[] { parentId }); }
    public async Task<GoogleDriveFile> UploadFileAsync(string name, string mime, string parent, IReadOnlyDictionary<string, string> properties, byte[] content, CancellationToken token)
    { await Before().ConfigureAwait(false); return File("fixture-upload-" + content.Length, name, mime, properties, new[] { parent }); }
    public async Task<GoogleDriveFile> UpdateFileAsync(string id, string name, string mime, IReadOnlyDictionary<string, string> properties, byte[] content, CancellationToken token)
    { await Before().ConfigureAwait(false); return File(id, name, mime, properties, Array.Empty<string>()); }
    public async Task<GoogleDriveFile> UpdateAppPropertiesAsync(string id, IReadOnlyDictionary<string, string> properties, CancellationToken token)
    { await Before().ConfigureAwait(false); return File(id, "landing", "application/gzip", properties, Array.Empty<string>()); }
    public async Task<byte[]> DownloadFileAsync(string id, CancellationToken token) { await Before().ConfigureAwait(false); return Encoding.UTF8.GetBytes("fixture-content:" + id); }
    public async Task TrashFileAsync(string id, CancellationToken token) { await Before().ConfigureAwait(false); }
    private static GoogleDriveFile File(string id, string name, string mime, IReadOnlyDictionary<string, string> properties, IReadOnlyList<string> parents) =>
        new GoogleDriveFile(id, name, mime, false, properties.ToDictionary(pair => pair.Key, pair => pair.Value), parents);
}
