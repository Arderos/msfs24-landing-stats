using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LandingStats.App.TelemetryUpload;

internal sealed class TelemetryUploadIdentityStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MSFS Landing Stats telemetry identity v1");
    private readonly object _gate = new object();
    private readonly string _path;
    private string? _boundInstallId;

    public TelemetryUploadIdentityStore(string rootPath)
    {
        _path = Path.Combine(rootPath, "identity.json");
    }

    // All processes use the same lock, including first creation and enrollment.
    // The last returned signing identity is a write fence, never a cached disk copy.
    private T Serialized<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            using var sha = SHA256.Create();
            var name = "Local\\LandingStats.TelemetryIdentity." + Convert.ToBase64String(
                sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant()))).Replace('/', '_');
            using var mutex = new Mutex(false, name);
            try
            {
                if (!cancellationToken.CanBeCanceled) mutex.WaitOne();
                else if (WaitHandle.WaitAny(new WaitHandle[] { mutex, cancellationToken.WaitHandle }) != 0)
                    cancellationToken.ThrowIfCancellationRequested();
            }
            catch (AbandonedMutexException) { /* Atomic file publication survives owner death. */ }
            try { cancellationToken.ThrowIfCancellationRequested(); return action(); } finally { mutex.ReleaseMutex(); }
        }
    }

    public TelemetryUploadIdentity Identity() => Serialized(() =>
    {
        var value = LoadOrCreate();
        var identity = ToIdentity(value);
        _boundInstallId = value.InstallId;
        return identity;
    });

    internal bool IsCurrentIdentityUsable(CancellationToken cancellationToken = default) => Serialized(() =>
    {
        if (!File.Exists(_path)) return false;
        try { _ = ToIdentity(Read()); return true; }
        catch (Exception error) when (IsUnavailableIdentity(error)) { return false; }
    }, cancellationToken);

    // Consent is plain metadata. Reading it neither creates keys nor decrypts DPAPI.
    public bool ConsentAccepted => Serialized(() => File.Exists(_path) && Read().ConsentVersion >= 1);

    public bool IsEnrolled(Uri endpoint) => Serialized(() =>
        string.Equals(CurrentForUpdate().EnrolledEndpoint, EndpointKey(endpoint), StringComparison.OrdinalIgnoreCase));

    public void AcceptConsent() => Serialized(() =>
    {
        var value = CurrentForUpdate(); value.ConsentVersion = 1; Save(value); return true;
    });

    public void MarkEnrolled(Uri endpoint, bool enrolled) => Serialized(() =>
    {
        var value = CurrentForUpdate(); value.EnrolledEndpoint = enrolled ? EndpointKey(endpoint) : null;
        Save(value); return true;
    });

    private IdentityFile CurrentForUpdate()
    {
        var expectedInstallId = _boundInstallId;
        var value = LoadOrCreate();
        if (expectedInstallId != null && expectedInstallId != value.InstallId)
            throw new CryptographicException("Telemetry identity changed; the previous sender must retire.");
        return value;
    }

    // Approval replaces an unavailable signing key; it never changes the user's
    // independent consent for unclassified legacy reports. Recheck under the same
    // mutex as publication so repeated recovery requests preserve a usable key.
    public TelemetryIdentityRecoveryResult RecoverUnavailableIdentity(bool replacementConfirmed, CancellationToken cancellationToken = default) => Serialized(() =>
    {
        if (!replacementConfirmed) throw new InvalidOperationException("Identity replacement requires explicit confirmation.");
        if (!File.Exists(_path))
        {
            // First use has no prior identity or legacy consent to recover.
            var created = Create(0); cancellationToken.ThrowIfCancellationRequested(); Save(created); _boundInstallId = created.InstallId;
            return new TelemetryIdentityRecoveryResult(false, null, created: true);
        }
        var consentVersion = 0;
        try
        {
            var current = ReadMetadata(); consentVersion = current.ConsentVersion;
            Validate(current); _ = ToIdentity(current);
            _boundInstallId = current.InstallId;
            return new TelemetryIdentityRecoveryResult(false, null);
        }
        catch (Exception error) when (IsUnavailableIdentity(error))
        {
            // Access/IO errors do not establish that the existing key is lost.
            // A later explicit request always rechecks DPAPI before replacing it.
        }
        var value = Create(consentVersion);
        _ = ToIdentity(value); // A still-unavailable DPAPI provider cannot commit an unusable replacement.
        var backup = _path + ".replaced-" + Guid.NewGuid().ToString("N");
        cancellationToken.ThrowIfCancellationRequested();
        Save(value, backup); // The atomic replacement is the recovery commit.
        _boundInstallId = value.InstallId;
        return new TelemetryIdentityRecoveryResult(true, backup);
    }, cancellationToken);

    private static bool IsUnavailableIdentity(Exception error) => error is CryptographicException || error is InvalidDataException ||
        error is SerializationException || error is FormatException || error is System.Xml.XmlException;

    private IdentityFile ReadMetadata()
    {
        using var input = File.OpenRead(_path);
        var serializer = new DataContractJsonSerializer(typeof(IdentityFile));
        if (serializer.ReadObject(input) is IdentityFile value) return value;
        throw new InvalidDataException("Telemetry upload identity is damaged");
    }
    private IdentityFile Read()
    { var value = ReadMetadata(); Validate(value); return value; }
    private static void Validate(IdentityFile value)
    {
        if (!Guid.TryParseExact(value.InstallId, "N", out _) || string.IsNullOrWhiteSpace(value.ProtectedPrivateKey))
            throw new InvalidDataException("Telemetry upload identity is damaged");
    }

    private IdentityFile LoadOrCreate()
    {
        var consentVersion = 0;
        if (File.Exists(_path))
        {
            try
            {
                var value = Read(); consentVersion = value.ConsentVersion;
                _ = ToIdentity(value);
                return value;
            }
            catch (Exception error) when (error is InvalidDataException || error is SerializationException ||
                error is FormatException || error is System.Xml.XmlException)
            {
                // DPAPI failure never establishes permanent key loss.
                File.Move(_path, _path + ".unreadable-" + Guid.NewGuid().ToString("N"));
                System.Diagnostics.Trace.TraceWarning("Telemetry identity recovered after {0}.", error.GetType().Name);
            }
        }
        var created = Create(consentVersion); Save(created); return created;
    }

    private static IdentityFile Create(int consentVersion)
    {
        using var rsa = new RSACryptoServiceProvider(3072) { PersistKeyInCsp = false };
        var privateXml = Encoding.UTF8.GetBytes(rsa.ToXmlString(true));
        try
        {
            return new IdentityFile
            {
                InstallId = Guid.NewGuid().ToString("N"), ConsentVersion = consentVersion,
                ProtectedPrivateKey = Convert.ToBase64String(ProtectedData.Protect(privateXml, Entropy, DataProtectionScope.CurrentUser)),
            };
        }
        finally { Array.Clear(privateXml, 0, privateXml.Length); }
    }

    private void Save(IdentityFile value, string? backup = null)
    {
        // Keep consent numeric for older readers. Missing or malformed consent
        // remains denied while the usable key and enrollment remain intact.
        value.ConsentMetadata = value.ConsentVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            new DataContractJsonSerializer(typeof(IdentityFile)).WriteObject(output, value); output.Flush(true);
        }
        if (File.Exists(_path)) File.Replace(temporary, _path, backup, true);
        else File.Move(temporary, _path);
    }

    private static TelemetryUploadIdentity ToIdentity(IdentityFile value)
    {
        var protectedBytes = Convert.FromBase64String(value.ProtectedPrivateKey);
        var privateXml = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var privateKeyXml = Encoding.UTF8.GetString(privateXml);
            using var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false };
            rsa.FromXmlString(privateKeyXml);
            var parameters = rsa.ExportParameters(false);
            return new TelemetryUploadIdentity(
                value.InstallId,
                Convert.ToBase64String(parameters.Modulus),
                Convert.ToBase64String(parameters.Exponent),
                message =>
                {
                    using var signer = new RSACryptoServiceProvider { PersistKeyInCsp = false };
                    signer.FromXmlString(privateKeyXml);
                    return signer.SignData(message, CryptoConfig.MapNameToOID("SHA256"));
                });
        }
        finally
        {
            Array.Clear(privateXml, 0, privateXml.Length);
            Array.Clear(protectedBytes, 0, protectedBytes.Length);
        }
    }

    private static string EndpointKey(Uri endpoint) => endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/');

    [DataContract]
    private sealed class IdentityFile
    {
        [DataMember(Name = "install_id", Order = 1)]
        public string InstallId { get; set; } = string.Empty;

        [DataMember(Name = "protected_private_key", Order = 2)]
        public string ProtectedPrivateKey { get; set; } = string.Empty;

        // Malformed consent is denied without damaging an otherwise usable key.
        // DataContract JSON reads integer primitives as Int32 when typed as object.
        [DataMember(Name = "consent_version", Order = 3)]
        public object? ConsentMetadata { get; set; }
        public int ConsentVersion
        {
            get => ConsentMetadata is int version && version >= 1 ? version : 0;
            set => ConsentMetadata = value;
        }

        [DataMember(Name = "enrolled_endpoint", Order = 4, EmitDefaultValue = false)]
        public string? EnrolledEndpoint { get; set; }
    }
}

internal sealed class TelemetryIdentityRecoveryResult
{
    public TelemetryIdentityRecoveryResult(bool replaced, string? backupPath, string? statusRefreshFailure = null, bool created = false)
    { Replaced = replaced; BackupPath = backupPath; StatusRefreshFailure = statusRefreshFailure; Created = created; }
    public bool Replaced { get; }
    public bool Created { get; }
    public string? BackupPath { get; }
    public string? StatusRefreshFailure { get; }
}

internal sealed class TelemetryUploadIdentity
{
    private readonly Func<byte[], byte[]> _sign;

    public TelemetryUploadIdentity(
        string installId,
        string publicModulus,
        string publicExponent,
        Func<byte[], byte[]> sign)
    {
        InstallId = installId;
        PublicModulus = publicModulus;
        PublicExponent = publicExponent;
        _sign = sign;
    }

    public string InstallId { get; }
    public string PublicModulus { get; }
    public string PublicExponent { get; }

    public string Sign(byte[] message) => Convert.ToBase64String(_sign(message));
}
