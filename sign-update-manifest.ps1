param([string]$ArtifactsDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:RELEASE_SIGNING_KEY_PKCS8_B64)) {
    throw 'Release manifest signing key is required.'
}
$key = [Convert]::FromBase64String($env:RELEASE_SIGNING_KEY_PKCS8_B64)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $read = 0
    $rsa.ImportPkcs8PrivateKey($key, [ref]$read)
    if ($read -ne $key.Length) { throw 'Unexpected trailing data in signing key.' }
    $bytes = [IO.File]::ReadAllBytes((Join-Path $ArtifactsDirectory 'update-channel.txt'))
    $signature = $rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [IO.File]::WriteAllText((Join-Path $ArtifactsDirectory 'update-channel.sig'),
        [Convert]::ToBase64String($signature) + "`n", [Text.UTF8Encoding]::new($false))
} finally {
    $rsa.Dispose()
    [Array]::Clear($key, 0, $key.Length)
}
