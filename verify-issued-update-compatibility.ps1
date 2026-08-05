param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$PublicExecutablePath,
    [Parameter(Mandatory = $true)]
    [string]$UpdaterPath
)

$ErrorActionPreference = "Stop"

foreach ($path in @($ManifestPath, $PackagePath, $PublicExecutablePath, $UpdaterPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Issued-client compatibility input is missing: $path"
    }
}

$manifestBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ManifestPath))
$manifestText = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes)
$lines = @($manifestText.Split([char[]]@("`n"), [StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object { $_.TrimEnd("`r") })

# This is the exact parser contract embedded in the issued v0.7.3 client. Do
# not replace it when adding a new protocol; publish new protocols in parallel.
if ($lines.Count -ne 8 -or $lines[0] -cne "format=2") {
    throw "Latest update manifest is incompatible with the issued v0.7.3 client."
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
if ($version.Build -lt 0 -or $version.Revision -ge 0) {
    throw "Update manifest version must contain exactly three numeric components."
}

[long]$packageSize = 0
[long]$updaterSize = 0
$integerStyle = [Globalization.NumberStyles]::None
$invariant = [Globalization.CultureInfo]::InvariantCulture
if ($packageName -cne "MSFS-Landing-Stats.zip" -or
    -not [long]::TryParse($packageSizeText, $integerStyle, $invariant, [ref]$packageSize) -or
    $packageSize -le 0 -or $packageSize -gt 128MB -or
    $packageHash -cnotmatch '^[0-9a-f]{64}$' -or
    $updaterName -cne "MSFS-Landing-Stats.Updater.exe" -or
    -not [long]::TryParse($updaterSizeText, $integerStyle, $invariant, [ref]$updaterSize) -or
    $updaterSize -le 0 -or $updaterSize -gt 16MB -or
    $updaterHash -cnotmatch '^[0-9a-f]{64}$') {
    throw "Update manifest values are incompatible with the issued v0.7.3 client."
}

$package = Get-Item -LiteralPath $PackagePath
$updater = Get-Item -LiteralPath $UpdaterPath
if ($package.Name -cne $packageName -or
    $package.Length -ne $packageSize -or
    (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $packageHash) {
    throw "Compatibility package does not match the update manifest."
}
if ($updater.Name -cne $updaterName -or
    $updater.Length -ne $updaterSize -or
    (Get-FileHash -LiteralPath $updater.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $updaterHash) {
    throw "Updater does not match the update manifest."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    if ($archive.Entries.Count -ne 1 -or
        $archive.Entries[0].FullName -cne "MSFS-Landing-Stats.exe" -or
        $archive.Entries[0].Name -cne "MSFS-Landing-Stats.exe") {
        throw "Compatibility package must contain exactly one root-level MSFS-Landing-Stats.exe."
    }

    $publicExecutable = Get-Item -LiteralPath $PublicExecutablePath
    if ($archive.Entries[0].Length -ne $publicExecutable.Length) {
        throw "Compatibility package executable size differs from the public executable."
    }

    $entryStream = $archive.Entries[0].Open()
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $entryHash = [BitConverter]::ToString($sha256.ComputeHash($entryStream)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $entryStream.Dispose()
    }
    $publicHash = (Get-FileHash -LiteralPath $publicExecutable.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($entryHash -cne $publicHash) {
        throw "Compatibility package does not contain the public executable byte-for-byte."
    }
}
finally {
    $archive.Dispose()
}

foreach ($assemblyPath in @($PublicExecutablePath, $UpdaterPath)) {
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Resolve-Path -LiteralPath $assemblyPath)).Version
    if ($assemblyVersion.Major -ne $version.Major -or
        $assemblyVersion.Minor -ne $version.Minor -or
        $assemblyVersion.Build -ne $version.Build) {
        throw "Release assembly version does not match the issued-client manifest."
    }
}

Write-Host "Issued v0.7.3 update contract accepted for v$versionText."
