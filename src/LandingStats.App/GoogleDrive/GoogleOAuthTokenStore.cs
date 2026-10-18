using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using LandingStats.App.Runtime;

namespace LandingStats.App.GoogleDrive;

internal sealed class GoogleOAuthTokenStore : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "MSFS Landing Stats Google Drive OAuth token v1");
    private readonly object _gate = new object();
    private readonly string _path;
    private int _hasRefreshToken = -1;

    public GoogleOAuthTokenStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "google-drive-token.json");
    }

    public string Path => _path;
    internal string ExportSnapshot(string destination)
    {
        lock (_gate)
        {
            if (Load() == null) throw new GoogleDriveNotSignedInException();
            var bytes = ReadBounded(_path);
            DurableFile.Write(destination, output => output.Write(bytes, 0, bytes.Length));
            return EpisodeTransactionStore.Hash(destination);
        }
    }
    internal string ExportAuthorizationSnapshot(string destination)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return "";
            var bytes = ReadBounded(_path);
            DurableFile.Write(destination, output => output.Write(bytes, 0, bytes.Length));
            return EpisodeTransactionStore.Hash(destination);
        }
    }
    internal bool ImportSnapshot(string source, string expectedOriginalHash, string resultHash)
    {
        lock (_gate)
        {
            if (expectedOriginalHash == "" ? File.Exists(_path) : !File.Exists(_path) || EpisodeTransactionStore.Hash(_path) != expectedOriginalHash) return false;
            if (resultHash.Length == 0) { Clear(); return true; }
            if (resultHash == expectedOriginalHash) return true;
            var bytes = ReadBounded(source); EpisodeTransactionStore.Verify(source, resultHash);
            if (new GoogleOAuthTokenStore(source).Load() == null) throw new InvalidDataException("Worker credential result is invalid.");
            DurableFile.Write(_path, output => output.Write(bytes, 0, bytes.Length)); Volatile.Write(ref _hasRefreshToken, 1); return true;
        }
    }
    private static byte[] ReadBounded(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length <= 0 || input.Length > 65536) throw new InvalidDataException("Credential snapshot exceeds its bound.");
        using var copy = new MemoryStream(); input.CopyTo(copy); return copy.ToArray();
    }

    public bool HasRefreshToken()
    {
        // Presentation only needs the last committed token state. A background
        // DPAPI/write/flush operation must not make the dispatcher wait on _gate.
        var cached = Volatile.Read(ref _hasRefreshToken);
        if (cached >= 0) return cached == 1;
        lock (_gate)
        {
            if (_hasRefreshToken < 0)
                Volatile.Write(ref _hasRefreshToken, LoadCore() != null ? 1 : 0);
            return _hasRefreshToken == 1;
        }
    }

    public GoogleOAuthStoredToken? Load()
    {
        lock (_gate)
        {
            var value = LoadCore();
            Volatile.Write(ref _hasRefreshToken, value != null ? 1 : 0);
            return value;
        }
    }

    public void Save(string refreshToken, string? accountEmail)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("A refresh token is required.", nameof(refreshToken));
        }

        lock (_gate)
        {
            var existing = LoadCore();
            if (existing?.RefreshToken == refreshToken && existing.AccountEmail == accountEmail) return;
            var clearBytes = Encoding.UTF8.GetBytes(refreshToken);
            try
            {
                var value = new TokenFile
                {
                    FormatVersion = 1,
                    ProtectedRefreshToken = Convert.ToBase64String(
                        ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser)),
                    AccountEmail = string.IsNullOrWhiteSpace(accountEmail) ? null : accountEmail,
                };
                WriteAtomic(value);
                Volatile.Write(ref _hasRefreshToken, 1);
            }
            finally
            {
                Array.Clear(clearBytes, 0, clearBytes.Length);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            Volatile.Write(ref _hasRefreshToken, 0);
            RemoveAbandonedCopies();
        }
    }

    private void RemoveAbandonedCopies()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        var name = System.IO.Path.GetFileName(_path);
        foreach (var candidate in Directory.EnumerateFiles(directory, name + "*.tmp"))
        {
            var suffix = System.IO.Path.GetFileName(candidate).Substring(name.Length);
            if (suffix != ".tmp" && !(suffix.Length == 38 && suffix.StartsWith(".w", StringComparison.Ordinal) &&
                suffix.EndsWith(".tmp", StringComparison.Ordinal) && Guid.TryParseExact(suffix.Substring(2, 32), "N", out _))) continue;
            try
            {
                using var held = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                File.Delete(candidate);
            }
            catch (IOException) { } // An active writer still owns this temporary file.
            catch (UnauthorizedAccessException) { }
        }
    }

    public Task StoreAsync<T>(string key, T value)
    {
        if (value is not TokenResponse token || string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new InvalidDataException("Google OAuth did not provide a refresh token.");
        }
        Save(token.RefreshToken, Load()?.AccountEmail);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        // Google.Apis calls this for every non-5xx refresh failure, including
        // transient rate limiting. Only the explicit invalid_grant handler may
        // revoke the durable refresh credential.
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var stored = Load();
        if (stored == null || typeof(T) != typeof(TokenResponse))
        {
            return Task.FromResult(default(T)!);
        }

        var token = new TokenResponse
        {
            RefreshToken = stored.RefreshToken,
        };
        return Task.FromResult((T)(object)token);
    }

    public Task ClearAsync()
    {
        Clear();
        return Task.CompletedTask;
    }

    private GoogleOAuthStoredToken? LoadCore()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            using var input = File.OpenRead(_path);
            var serializer = new DataContractJsonSerializer(typeof(TokenFile));
            if (serializer.ReadObject(input) is not TokenFile value ||
                value.FormatVersion != 1 ||
                string.IsNullOrWhiteSpace(value.ProtectedRefreshToken))
            {
                return null;
            }

            var protectedBytes = Convert.FromBase64String(value.ProtectedRefreshToken);
            var clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                var refreshToken = Encoding.UTF8.GetString(clearBytes);
                return string.IsNullOrWhiteSpace(refreshToken)
                    ? null
                    : new GoogleOAuthStoredToken(refreshToken, value.AccountEmail);
            }
            finally
            {
                Array.Clear(clearBytes, 0, clearBytes.Length);
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is SerializationException ||
            exception is CryptographicException ||
            exception is FormatException)
        {
            return null;
        }
    }

    private void WriteAtomic(TokenFile value)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                using var bounded = LandingStats.App.Runtime.RuntimeStorageQuota.Protect(output, _path, true);
                new DataContractJsonSerializer(typeof(TokenFile)).WriteObject(bounded, value);
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

    [DataContract]
    private sealed class TokenFile
    {
        [DataMember(Name = "formatVersion", Order = 1)]
        public int FormatVersion { get; set; }

        [DataMember(Name = "protectedRefreshToken", Order = 2)]
        public string ProtectedRefreshToken { get; set; } = string.Empty;

        [DataMember(Name = "accountEmail", Order = 3, EmitDefaultValue = false)]
        public string? AccountEmail { get; set; }
    }
}

internal sealed class GoogleOAuthStoredToken
{
    public GoogleOAuthStoredToken(string refreshToken, string? accountEmail)
    {
        RefreshToken = refreshToken;
        AccountEmail = accountEmail;
    }

    public string RefreshToken { get; }

    public string? AccountEmail { get; }
}
