using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;

namespace LandingStats.App.Runtime;

/// <summary>Private, persisted fake Drive used to verify lost HTTP replies across
/// real worker exits. Its state lives only beside that fixture's job directories.</summary>
internal sealed class CloudSyncFixture : IGoogleDriveApi
{
    private readonly string _path, _lost;
    private readonly bool _loseReply;
    public CloudSyncFixture(GoogleOAuthTokenStore tokens, bool loseReply)
    {
        var job = Path.GetDirectoryName(tokens.Path)!; var root = Path.GetDirectoryName(Path.GetDirectoryName(job)!)!;
        _path = Path.Combine(root, "fixture-server.json"); _lost = Path.Combine(root, "fixture-lost-reply"); _loseReply = loseReply;
    }
    [DataContract]
    private sealed class State
    { [DataMember(Order = 1)] public List<Entry> Files { get; set; } = new List<Entry>(); }
    [DataContract]
    private sealed class Entry
    {
        [DataMember(Order = 1)] public CloudFileData File { get; set; } = new CloudFileData();
        [DataMember(Order = 2)] public byte[] Body { get; set; } = Array.Empty<byte>();
    }
    private State Read() => File.Exists(_path) ? DurableFile.ReadJson<State>(_path, CloudApiRequest.MaximumBodyBytes) : new State();
    private GoogleDriveFile Write(string? id, string name, string mime, string? parent, IReadOnlyDictionary<string, string> properties, byte[] content)
    {
        var state = Read(); var item = id == null ? null : state.Files.Single(entry => entry.File.Id == id);
        if (item == null) { item = new Entry { File = new CloudFileData { Id = "fixture-" + Guid.NewGuid().ToString("N"), Parents = parent == null ? Array.Empty<string>() : new[] { parent } } }; state.Files.Add(item); }
        item.File.Name = name; item.File.Mime = mime; item.File.Properties = properties.ToDictionary(pair => pair.Key, pair => pair.Value); item.Body = content;
        DurableFile.WriteJson(_path, state); return item.File.Restore();
    }
    public Task<string> GetAccountPermissionIdAsync(CancellationToken token) => Task.FromResult("fixture-account");
    public Task<IReadOnlyList<GoogleDriveFile>> ListApplicationFilesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<GoogleDriveFile>>(Read().Files.Select(entry => entry.File.Restore()).ToArray());
    public Task<GoogleDriveFile> CreateFolderAsync(string name, string? parent, IReadOnlyDictionary<string, string> properties, CancellationToken token) =>
        Task.FromResult(Write(null, name, "application/vnd.google-apps.folder", parent, properties, Array.Empty<byte>()));
    public async Task<GoogleDriveFile> UploadFileAsync(string name, string mime, string parent, IReadOnlyDictionary<string, string> properties, byte[] content, CancellationToken token)
    {
        var result = Write(null, name, mime, parent, properties, content);
        if (_loseReply && !File.Exists(_lost))
        {
            DurableFile.Write(_lost, output => output.WriteByte(1));
            await new TaskCompletionSource<bool>().Task.ConfigureAwait(false); // Server committed; its reply never reaches the parent.
        }
        return result;
    }
    public Task<GoogleDriveFile> UpdateFileAsync(string id, string name, string mime, IReadOnlyDictionary<string, string> properties, byte[] content, CancellationToken token) =>
        Task.FromResult(Write(id, name, mime, null, properties, content));
    public Task<GoogleDriveFile> UpdateAppPropertiesAsync(string id, IReadOnlyDictionary<string, string> properties, CancellationToken token)
    { var item = Read().Files.Single(entry => entry.File.Id == id); return Task.FromResult(Write(id, item.File.Name, item.File.Mime, null, properties, item.Body)); }
    public Task<byte[]> DownloadFileAsync(string id, CancellationToken token) => Task.FromResult(Read().Files.Single(entry => entry.File.Id == id).Body);
    public Task TrashFileAsync(string id, CancellationToken token)
    { var state = Read(); state.Files.Single(entry => entry.File.Id == id).File.Trashed = true; DurableFile.WriteJson(_path, state); return Task.CompletedTask; }
}
