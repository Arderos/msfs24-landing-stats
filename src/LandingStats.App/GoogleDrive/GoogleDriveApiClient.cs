using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;
using DriveFileResource = Google.Apis.Drive.v3.Data.File;

namespace LandingStats.App.GoogleDrive;

internal interface IGoogleDriveApi
{
    Task<string> GetAccountPermissionIdAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<GoogleDriveFile>> ListApplicationFilesAsync(CancellationToken cancellationToken);

    Task<GoogleDriveFile> CreateFolderAsync(
        string name,
        string? parentId,
        IReadOnlyDictionary<string, string> appProperties,
        CancellationToken cancellationToken);

    Task<GoogleDriveFile> UploadFileAsync(
        string name,
        string mimeType,
        string parentId,
        IReadOnlyDictionary<string, string> appProperties,
        byte[] content,
        CancellationToken cancellationToken);

    Task<GoogleDriveFile> UpdateFileAsync(
        string fileId,
        string name,
        string mimeType,
        IReadOnlyDictionary<string, string> appProperties,
        byte[] content,
        CancellationToken cancellationToken);

    Task<GoogleDriveFile> UpdateAppPropertiesAsync(
        string fileId,
        IReadOnlyDictionary<string, string> appProperties,
        CancellationToken cancellationToken);

    Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken);

    Task TrashFileAsync(string fileId, CancellationToken cancellationToken);
}

internal sealed class GoogleDriveApiClient : IGoogleDriveApi, IDisposable
{
    private const string ApplicationPropertyKey = "application";
    private const string ApplicationPropertyValue = "msfs-landing-stats";
    private const string FileFields =
        "id,name,mimeType,modifiedTime,trashed,appProperties,size,md5Checksum,parents";
    private readonly DriveService _driveService;
    private bool _disposed;

    public GoogleDriveApiClient(GoogleOAuthClient oauthClient)
    {
        if (oauthClient == null)
        {
            throw new ArgumentNullException(nameof(oauthClient));
        }

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "MSFS Landing Stats",
            HttpClientInitializer = new GoogleOAuthHttpInitializer(oauthClient),
        });
    }

    public async Task<string> GetAccountPermissionIdAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var request = _driveService.About.Get();
        request.Fields = "user(permissionId)";
        var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var permissionId = response.User?.PermissionId;
        if (string.IsNullOrWhiteSpace(permissionId))
        {
            throw new InvalidDataException("Google Drive did not return an account identity.");
        }
        return permissionId!;
    }

    public async Task<IReadOnlyList<GoogleDriveFile>> ListApplicationFilesAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var files = new List<GoogleDriveFile>();
        string? pageToken = null;
        do
        {
            var request = _driveService.Files.List();
            request.Spaces = "drive";
            request.PageSize = 1000;
            request.Q = "appProperties has { key='" + ApplicationPropertyKey +
                        "' and value='" + ApplicationPropertyValue + "' }";
            request.Fields = "nextPageToken,files(" + FileFields + ")";
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            files.AddRange((response.Files ?? new List<DriveFileResource>()).Select(ToFile));
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return files;
    }

    public async Task<GoogleDriveFile> CreateFolderAsync(
        string name,
        string? parentId,
        IReadOnlyDictionary<string, string> appProperties,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var metadata = Metadata(
            name,
            "application/vnd.google-apps.folder",
            string.IsNullOrWhiteSpace(parentId) ? null : new[] { parentId! },
            appProperties);
        var request = _driveService.Files.Create(metadata);
        request.Fields = FileFields;
        var result = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return ToValidatedFile(result);
    }

    public Task<GoogleDriveFile> UploadFileAsync(
        string name,
        string mimeType,
        string parentId,
        IReadOnlyDictionary<string, string> appProperties,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var metadata = Metadata(name, mimeType, new[] { parentId }, appProperties);
        return UploadCoreAsync(metadata, null, mimeType, content, cancellationToken);
    }

    public Task<GoogleDriveFile> UpdateFileAsync(
        string fileId,
        string name,
        string mimeType,
        IReadOnlyDictionary<string, string> appProperties,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException("A Google Drive file id is required.", nameof(fileId));
        }
        var metadata = Metadata(name, mimeType, null, appProperties);
        return UploadCoreAsync(metadata, fileId, mimeType, content, cancellationToken);
    }

    public async Task<GoogleDriveFile> UpdateAppPropertiesAsync(
        string fileId,
        IReadOnlyDictionary<string, string> appProperties,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException("A Google Drive file id is required.", nameof(fileId));
        }
        if (appProperties == null)
        {
            throw new ArgumentNullException(nameof(appProperties));
        }

        var properties = appProperties.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        properties[ApplicationPropertyKey] = ApplicationPropertyValue;
        var request = _driveService.Files.Update(
            new DriveFileResource { AppProperties = properties },
            fileId);
        request.Fields = FileFields;
        var result = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return ToValidatedFile(result);
    }

    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException("A Google Drive file id is required.", nameof(fileId));
        }

        using var output = new MemoryStream();
        var progress = await _driveService.Files.Get(fileId)
            .DownloadAsync(output, cancellationToken)
            .ConfigureAwait(false);
        EnsureDownloadCompleted(progress);
        return output.ToArray();
    }

    public async Task TrashFileAsync(string fileId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException("A Google Drive file id is required.", nameof(fileId));
        }

        var request = _driveService.Files.Update(
            new DriveFileResource { Trashed = true },
            fileId);
        request.Fields = "id,trashed";
        await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoogleDriveFile> UploadCoreAsync(
        DriveFileResource metadata,
        string? fileId,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (content == null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        using var input = new MemoryStream(content, false);
        IUploadProgress progress;
        DriveFileResource? response;
        if (string.IsNullOrWhiteSpace(fileId))
        {
            var request = _driveService.Files.Create(metadata, input, mimeType);
            request.Fields = FileFields;
            progress = await request.UploadAsync(cancellationToken).ConfigureAwait(false);
            response = request.ResponseBody;
        }
        else
        {
            var request = _driveService.Files.Update(metadata, fileId, input, mimeType);
            request.Fields = FileFields;
            progress = await request.UploadAsync(cancellationToken).ConfigureAwait(false);
            response = request.ResponseBody;
        }

        EnsureUploadCompleted(progress);
        return ToValidatedFile(response);
    }

    private static DriveFileResource Metadata(
        string name,
        string mimeType,
        IEnumerable<string>? parents,
        IReadOnlyDictionary<string, string> appProperties)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Google Drive file name is required.", nameof(name));
        }
        if (appProperties == null)
        {
            throw new ArgumentNullException(nameof(appProperties));
        }

        var properties = appProperties.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        properties[ApplicationPropertyKey] = ApplicationPropertyValue;
        return new DriveFileResource
        {
            Name = name,
            MimeType = mimeType,
            Parents = parents?.ToList(),
            AppProperties = properties,
        };
    }

    private static GoogleDriveFile ToValidatedFile(DriveFileResource? resource)
    {
        if (resource == null || string.IsNullOrWhiteSpace(resource.Id))
        {
            throw new InvalidDataException("Google Drive returned invalid file metadata.");
        }
        return ToFile(resource);
    }

    private static GoogleDriveFile ToFile(DriveFileResource resource) => new GoogleDriveFile(
        resource.Id ?? string.Empty,
        resource.Name ?? string.Empty,
        resource.MimeType ?? string.Empty,
        resource.Trashed ?? false,
        resource.AppProperties == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(resource.AppProperties, StringComparer.Ordinal),
        resource.Parents == null
            ? new List<string>()
            : resource.Parents.ToList());

    private static void EnsureUploadCompleted(IUploadProgress progress)
    {
        if (progress.Status == UploadStatus.Completed)
        {
            return;
        }
        throw new IOException(
            "Google Drive upload did not complete.",
            progress.Exception);
    }

    private static void EnsureDownloadCompleted(IDownloadProgress progress)
    {
        if (progress.Status == DownloadStatus.Completed)
        {
            return;
        }
        throw new IOException(
            "Google Drive download did not complete.",
            progress.Exception);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GoogleDriveApiClient));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _driveService.Dispose();
    }

    private sealed class GoogleOAuthHttpInitializer :
        IConfigurableHttpClientInitializer,
        IHttpExecuteInterceptor,
        IHttpUnsuccessfulResponseHandler
    {
        private const string RetriedProperty = "MSFSLandingStats.GoogleDriveAuthRetried";
        private readonly GoogleOAuthClient _oauthClient;

        public GoogleOAuthHttpInitializer(GoogleOAuthClient oauthClient)
        {
            _oauthClient = oauthClient;
        }

        public void Initialize(ConfigurableHttpClient httpClient)
        {
            httpClient.MessageHandler.AddExecuteInterceptor(this);
            httpClient.MessageHandler.AddUnsuccessfulResponseHandler(this);
        }

        public async Task InterceptAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var token = await _oauthClient.GetAccessTokenAsync(false, cancellationToken)
                .ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            if (args.Response.StatusCode != HttpStatusCode.Unauthorized ||
                args.Request.Properties.ContainsKey(RetriedProperty))
            {
                return false;
            }

            args.Request.Properties[RetriedProperty] = true;
            var token = await _oauthClient.GetAccessTokenAsync(true, args.CancellationToken)
                .ConfigureAwait(false);
            args.Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return true;
        }
    }
}

internal sealed class GoogleDriveFile
{
    public GoogleDriveFile(
        string id,
        string name,
        string mimeType,
        bool trashed,
        IReadOnlyDictionary<string, string> appProperties,
        IReadOnlyList<string> parents)
    {
        Id = id;
        Name = name;
        MimeType = mimeType;
        Trashed = trashed;
        AppProperties = appProperties;
        Parents = parents;
    }

    public string Id { get; }
    public string Name { get; }
    public string MimeType { get; }
    public bool Trashed { get; }
    public IReadOnlyDictionary<string, string> AppProperties { get; }
    public IReadOnlyList<string> Parents { get; }

    public string? Property(string key) =>
        AppProperties.TryGetValue(key, out var value) ? value : null;
}
