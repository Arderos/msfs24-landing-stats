param(
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot "artifacts"),
    [Version]$BridgeVersion = [Version]"0.7.6"
)

$ErrorActionPreference = "Stop"

$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$packagePath = Join-Path $artifacts "MSFS-Landing-Stats.exe"
$updaterPath = Join-Path $artifacts "MSFS-Landing-Stats.Updater.exe"
$channelManifestPath = Join-Path $artifacts "update-channel.txt"
$bridgeManifestPath = Join-Path $artifacts "update-manifest.txt"

foreach ($path in @($packagePath, $updaterPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release artifact is missing: $path"
    }
}

$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($updaterPath).Version
$version = [Version]::new($assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build)
$versionText = "$($version.Major).$($version.Minor).$($version.Build)"
$package = Get-Item -LiteralPath $packagePath
$updater = Get-Item -LiteralPath $updaterPath
$packageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$updaterSha256 = (Get-FileHash -LiteralPath $updaterPath -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = @(
    "format=3"
    "version=$versionText"
    "package=MSFS-Landing-Stats.exe"
    "package-size=$($package.Length)"
    "package-sha256=$packageSha256"
    "updater=MSFS-Landing-Stats.Updater.exe"
    "updater-size=$($updater.Length)"
    "updater-sha256=$updaterSha256"
) -join "`n"
$manifest += "`n"
[IO.File]::WriteAllBytes($channelManifestPath, [Text.UTF8Encoding]::new($false).GetBytes($manifest))

& (Join-Path $PSScriptRoot "verify-issued-update-compatibility.ps1") `
    -ManifestPath $channelManifestPath `
    -PackagePath $packagePath `
    -UpdaterPath $updaterPath `
    -ExpectedVersion $versionText

if ($version -eq $BridgeVersion) {
    [IO.File]::WriteAllBytes($bridgeManifestPath, [IO.File]::ReadAllBytes($channelManifestPath))
}
elseif (Test-Path -LiteralPath $bridgeManifestPath) {
    Remove-Item -LiteralPath $bridgeManifestPath -Force
}

Write-Host "Unsigned current-channel manifest: $channelManifestPath"
if ($version -eq $BridgeVersion) {
    Write-Host "Unsigned bootstrap bridge manifest: $bridgeManifestPath"
}
