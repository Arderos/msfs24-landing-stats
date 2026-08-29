using System;
using System.Linq;
using System.Reflection;

namespace LandingStats.App.GoogleDrive;

internal static class GoogleDriveOAuthConfiguration
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";

    private const string ClientIdMetadataKey = "GoogleOAuthClientId";
    private const string ClientSecretMetadataKey = "GoogleOAuthClientSecret";

    private static readonly Lazy<string> ClientIdValue =
        new Lazy<string>(() => ReadRequiredMetadata(ClientIdMetadataKey));

    private static readonly Lazy<string> ClientSecretValue =
        new Lazy<string>(() => ReadRequiredMetadata(ClientSecretMetadataKey));

    public static string ClientId => ClientIdValue.Value;

    public static string ClientSecret => ClientSecretValue.Value;

    private static string ReadRequiredMetadata(string key)
    {
        var value = typeof(GoogleDriveOAuthConfiguration)
            .Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Google Drive support is not configured in this build. Please install an official release.");
        }

        return value!;
    }
}
