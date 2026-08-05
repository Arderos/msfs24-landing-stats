param(
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot "artifacts")
)

$ErrorActionPreference = "Stop"

$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$packagePath = Join-Path $artifacts "MSFS-Landing-Stats.zip"
$publicExecutablePath = Join-Path $artifacts "MSFS-Landing-Stats.exe"
$updaterPath = Join-Path $artifacts "MSFS-Landing-Stats.Updater.exe"
$manifestPath = Join-Path $artifacts "update-manifest.txt"

foreach ($path in @($packagePath, $publicExecutablePath, $updaterPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release artifact is missing: $path"
    }
}

$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($updaterPath).Version
$version = "$($assemblyVersion.Major).$($assemblyVersion.Minor).$($assemblyVersion.Build)"
$package = Get-Item -LiteralPath $packagePath
$updater = Get-Item -LiteralPath $updaterPath
$packageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$updaterSha256 = (Get-FileHash -LiteralPath $updaterPath -Algorithm SHA256).Hash.ToLowerInvariant()

# `latest` must remain consumable by every issued updater. v0.7.3 accepts only
# this exact format-2 shape; future protocols must be additional assets.
$manifest = @(
    "format=2"
    "version=$version"
    "package=MSFS-Landing-Stats.zip"
    "package-size=$($package.Length)"
    "package-sha256=$packageSha256"
    "updater=MSFS-Landing-Stats.Updater.exe"
    "updater-size=$($updater.Length)"
    "updater-sha256=$updaterSha256"
) -join "`n"
$manifest += "`n"
[IO.File]::WriteAllBytes($manifestPath, [Text.UTF8Encoding]::new($false).GetBytes($manifest))

& (Join-Path $PSScriptRoot "verify-issued-update-compatibility.ps1") `
    -ManifestPath $manifestPath `
    -PackagePath $packagePath `
    -PublicExecutablePath $publicExecutablePath `
    -UpdaterPath $updaterPath

Write-Host "Unsigned update manifest: $manifestPath"
