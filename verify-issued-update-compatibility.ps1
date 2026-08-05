param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$UpdaterPath,
    [Parameter(Mandatory = $true)]
    [Version]$ExpectedVersion,
    [string]$SignaturePath
)

$ErrorActionPreference = "Stop"

$inputs = @($ManifestPath, $PackagePath, $UpdaterPath)
if (-not [string]::IsNullOrWhiteSpace($SignaturePath)) {
    $inputs += $SignaturePath
}
foreach ($path in $inputs) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Update compatibility input is missing: $path"
    }
}

$manifestBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ManifestPath))
$manifestText = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes)
$lines = @($manifestText.Split([char[]]@("`n"), [StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object { $_.TrimEnd("`r") })
if ($lines.Count -ne 8 -or $lines[0] -cne "format=3") {
    throw "Update manifest must use the exact format-3 contract."
}

function Read-ManifestValue([string]$Line, [string]$Key) {
    $prefix = "$Key="
    if (-not $Line.StartsWith($prefix, [StringComparison]::Ordinal) -or $Line.Length -eq $prefix.Length) {
        throw "Update manifest is missing $Key."
    }
    return $Line.Substring($prefix.Length)
}

$versionText = Read-ManifestValue $lines[1] "version"
$packageName = Read-ManifestValue $lines[2] "package"
$packageSizeText = Read-ManifestValue $lines[3] "package-size"
$packageHash = (Read-ManifestValue $lines[4] "package-sha256").ToLowerInvariant()
$updaterName = Read-ManifestValue $lines[5] "updater"
$updaterSizeText = Read-ManifestValue $lines[6] "updater-size"
$updaterHash = (Read-ManifestValue $lines[7] "updater-sha256").ToLowerInvariant()

try {
    $version = [Version]$versionText
}
catch {
    throw "Update manifest version is invalid."
}
$expected = [Version]::new($ExpectedVersion.Major, $ExpectedVersion.Minor, $ExpectedVersion.Build)
if ($version.Build -lt 0 -or $version.Revision -ge 0 -or $version -ne $expected) {
    throw "Update manifest version $versionText does not match required version $expected."
}

[long]$packageSize = 0
[long]$updaterSize = 0
$integerStyle = [Globalization.NumberStyles]::None
$invariant = [Globalization.CultureInfo]::InvariantCulture
if ($packageName -cne "MSFS-Landing-Stats.exe" -or
    -not [long]::TryParse($packageSizeText, $integerStyle, $invariant, [ref]$packageSize) -or
    $packageSize -le 0 -or $packageSize -gt 128MB -or
    $packageHash -cnotmatch '^[0-9a-f]{64}$' -or
    $updaterName -cne "MSFS-Landing-Stats.Updater.exe" -or
    -not [long]::TryParse($updaterSizeText, $integerStyle, $invariant, [ref]$updaterSize) -or
    $updaterSize -le 0 -or $updaterSize -gt 16MB -or
    $updaterHash -cnotmatch '^[0-9a-f]{64}$') {
    throw "Update manifest values violate the format-3 contract."
}

$package = Get-Item -LiteralPath $PackagePath
$updater = Get-Item -LiteralPath $UpdaterPath
if ($package.Name -cne $packageName -or
    $package.Length -ne $packageSize -or
    (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $packageHash) {
    throw "Application executable does not match the update manifest."
}
if ($updater.Name -cne $updaterName -or
    $updater.Length -ne $updaterSize -or
    (Get-FileHash -LiteralPath $updater.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $updaterHash) {
    throw "Updater executable does not match the update manifest."
}

foreach ($assemblyPath in @($PackagePath, $UpdaterPath)) {
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Resolve-Path -LiteralPath $assemblyPath)).Version
    $normalizedAssemblyVersion = [Version]::new($assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build)
    if ($normalizedAssemblyVersion -ne $expected) {
        throw "Release assembly $assemblyPath is v$normalizedAssemblyVersion, expected v$expected."
    }
}

if (-not [string]::IsNullOrWhiteSpace($SignaturePath)) {
    $publicModulus = "3UfZ8cUoPPA/C9ze+Yg2wPErrI/Cry1A12vhPXmebSaNqRPYHEDTiuWadXyHgFCIX/IZGEkMcCamVm6BSv8he+qI+98vU2NtgqKQ+P8YBxmirg7V/8RwbEi1AdcWWwmORZLHo8eOFuZMI9OOwdxhV+0tf89eo8VudLrxHtRjCQWHfB3d2VcoYpjdKse3btCfPxA4bmiVZYnC8M6lo5TqRXBIFjpmCC+oQpmehWodArLmZXT4vd9SaItN3Pfp1EWfLQxQerrmgpmHoySYSKw1yNPO6boelZ9aCWarhglvNlQsqMu5nLQpNCpkcs6jRbD/wY1s5BmmLNnljmNNgn78GxMl98CsVtr7tnmuk91MgQ87eLpfF4/EoEcvRXhlw/B4pjFPttc49M6LUJn5xJRLojq55GuYfgD3D0Bk+Snt2jyWOdIXpPkGt1YDBqdNgQghVje4+1kC8lRh/tgByXOWPjA5T8iJTpupNNjS4pEXo1HXKeVW0uIIoKUT0Dp+ko1N"
    $signatureText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $SignaturePath))).Trim()
    try {
        $signature = [Convert]::FromBase64String($signatureText)
    }
    catch {
        throw "Update manifest signature is not valid base64."
    }
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportParameters([Security.Cryptography.RSAParameters]@{
            Modulus = [Convert]::FromBase64String($publicModulus)
            Exponent = [Convert]::FromBase64String("AQAB")
        })
        if (-not $rsa.VerifyData(
            $manifestBytes,
            $signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
            throw "Update manifest signature verification failed."
        }
    }
    finally {
        $rsa.Dispose()
    }
}

$signatureState = if ([string]::IsNullOrWhiteSpace($SignaturePath)) { "Unsigned" } else { "Signed" }
Write-Host "$signatureState format-3 update contract accepted for v$versionText."
