using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace LandingStats.App.GoogleDrive;

internal sealed class GoogleOAuthClient : IDisposable
{
    private const string UserKey = "msfs-landing-stats-user";
    private const string ClosePage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Google Drive connected</title></head>" +
        "<body style=\"font-family:Segoe UI,sans-serif;background:#111;color:#eee;padding:40px\">" +
        "<h1>Google Drive connected</h1><p>You can close this tab and return to MSFS Landing Stats.</p>" +
        "</body></html>";
    private readonly GoogleOAuthTokenStore _tokenStore;
    private readonly SemaphoreSlim _credentialGate = new SemaphoreSlim(1, 1);
    private UserCredential? _credential;
    private bool _disposed;

    public GoogleOAuthClient(GoogleOAuthTokenStore tokenStore)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    }

    public bool IsSignedIn => _tokenStore.HasRefreshToken();

    public string? AccountEmail => _tokenStore.Load()?.AccountEmail;

    public async Task<string?> SignInAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _credentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsSignedIn && _credential != null)
            {
                _credential.Flow.Dispose();
                _credential = null;
            }
            _credential ??= await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
            await _credential.GetAccessTokenForRequestAsync(null, cancellationToken).ConfigureAwait(false);
            return AccountEmail;
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    public async Task<string> GetAccessTokenAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _credentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_credential == null)
            {
                if (!IsSignedIn)
                {
                    throw new GoogleDriveNotSignedInException();
                }
                _credential = await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (forceRefresh && !await _credential.RefreshTokenAsync(cancellationToken).ConfigureAwait(false))
                {
                    SignOutCore();
                    throw new GoogleDriveNotSignedInException();
                }

                return await _credential.GetAccessTokenForRequestAsync(null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TokenResponseException exception) when (
                string.Equals(exception.Error?.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                SignOutCore();
                throw new GoogleDriveNotSignedInException();
            }
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    public void SignOut()
    {
        ThrowIfDisposed();
        SignOutCore();
    }

    internal static Uri BuildAuthorizationUri(
        string clientId,
        string clientSecret,
        string redirectUri,
        out string codeVerifier)
    {
        using var flow = new PkceGoogleAuthorizationCodeFlow(
            CreateInitializer(clientId, clientSecret, null));
        return flow.CreateAuthorizationCodeRequest(redirectUri, out codeVerifier).Build();
    }

    private async Task<UserCredential> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var receiver = new LocalServerCodeReceiver(
            ClosePage,
            LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp);
        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
                CreateInitializer(
                    GoogleDriveOAuthConfiguration.ClientId,
                    GoogleDriveOAuthConfiguration.ClientSecret,
                    _tokenStore),
                new[] { GoogleDriveOAuthConfiguration.Scope },
                UserKey,
                true,
                cancellationToken,
                _tokenStore,
                receiver)
            .ConfigureAwait(false);
    }

    private static GoogleAuthorizationCodeFlow.Initializer CreateInitializer(
        string clientId,
        string clientSecret,
        GoogleOAuthTokenStore? tokenStore)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("A Google OAuth client id is required.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new ArgumentException("A Google desktop OAuth client value is required.", nameof(clientSecret));
        }

        return new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
            },
            DataStore = tokenStore,
            Prompt = "consent",
            Scopes = new[] { GoogleDriveOAuthConfiguration.Scope },
        };
    }

    private void SignOutCore()
    {
        _credential?.Flow.Dispose();
        _credential = null;
        _tokenStore.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GoogleOAuthClient));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _credential?.Flow.Dispose();
        _credential = null;
        _credentialGate.Dispose();
    }
}

internal sealed class GoogleDriveNotSignedInException : InvalidOperationException
{
    public GoogleDriveNotSignedInException()
        : base("Google Drive is not connected.")
    {
    }
}
