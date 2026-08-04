using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace LandingStats.App.TelemetryUpload;

internal sealed class TelemetryUploadIdentityStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MSFS Landing Stats telemetry identity v1");
    private readonly object _gate = new object();
    private readonly string _path;
    private IdentityFile? _cached;

    public TelemetryUploadIdentityStore(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        _path = Path.Combine(rootPath, "identity.json");
    }

    public TelemetryUploadIdentity Identity()
    {
        lock (_gate)
        {
            _cached ??= LoadOrCreate();
            return ToIdentity(_cached);
        }
    }

    public bool ConsentAccepted
    {
        get
        {
            lock (_gate)
            {
                _cached ??= LoadOrCreate();
                return _cached.ConsentVersion >= 1;
            }
        }
    }

    public bool IsEnrolled(Uri endpoint)
    {
        lock (_gate)
        {
            _cached ??= LoadOrCreate();
            return string.Equals(_cached.EnrolledEndpoint, EndpointKey(endpoint), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void AcceptConsent()
    {
        lock (_gate)
        {
            _cached ??= LoadOrCreate();
            _cached.ConsentVersion = 1;
            Save(_cached);
        }
    }

    public void MarkEnrolled(Uri endpoint, bool enrolled)
    {
        lock (_gate)
        {
            _cached ??= LoadOrCreate();
            _cached.EnrolledEndpoint = enrolled ? EndpointKey(endpoint) : null;
            Save(_cached);
        }
    }

    private IdentityFile LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            using var input = File.OpenRead(_path);
            var serializer = new DataContractJsonSerializer(typeof(IdentityFile));
            if (serializer.ReadObject(input) is IdentityFile value &&
                Guid.TryParseExact(value.InstallId, "N", out _) &&
                !string.IsNullOrWhiteSpace(value.ProtectedPrivateKey))
            {
                return value;
            }
            throw new InvalidDataException("Telemetry upload identity is damaged");
        }

        using var rsa = new RSACryptoServiceProvider(3072) { PersistKeyInCsp = false };
        var privateXml = Encoding.UTF8.GetBytes(rsa.ToXmlString(true));
        try
        {
            var value = new IdentityFile
            {
                InstallId = Guid.NewGuid().ToString("N"),
                ProtectedPrivateKey = Convert.ToBase64String(
                    ProtectedData.Protect(privateXml, Entropy, DataProtectionScope.CurrentUser)),
            };
            Save(value);
            return value;
        }
        finally
        {
            Array.Clear(privateXml, 0, privateXml.Length);
        }
    }

    private void Save(IdentityFile value)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            new DataContractJsonSerializer(typeof(IdentityFile)).WriteObject(output, value);
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
        _cached = value;
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

        [DataMember(Name = "consent_version", Order = 3)]
        public int ConsentVersion { get; set; }

        [DataMember(Name = "enrolled_endpoint", Order = 4, EmitDefaultValue = false)]
        public string? EnrolledEndpoint { get; set; }
    }
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
