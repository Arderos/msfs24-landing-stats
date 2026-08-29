using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace LandingStats.App.GoogleDrive;

internal sealed class GoogleOAuthTokenStore : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "MSFS Landing Stats Google Drive OAuth token v1");
    private readonly object _gate = new object();
    private readonly string _path;
    private bool? _hasRefreshToken;

    public GoogleOAuthTokenStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "google-drive-token.json");
    }

    public string Path => _path;

    public bool HasRefreshToken()
    {
        lock (_gate)
        {
            _hasRefreshToken ??= LoadCore() != null;
            return _hasRefreshToken.Value;
        }
    }

    public GoogleOAuthStoredToken? Load()
    {
        lock (_gate)
        {
            var value = LoadCore();
            _hasRefreshToken = value != null;
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
                _hasRefreshToken = true;
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
            _hasRefreshToken = false;
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
        Clear();
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
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(TokenFile)).WriteObject(output, value);
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
