param(
    [Parameter(Mandatory = $true)] [string]$BridgeManifestPath,
    [Parameter(Mandatory = $true)] [string]$BridgeSignaturePath,
    [Parameter(Mandatory = $true)] [string]$BridgePackagePath,
    [Parameter(Mandatory = $true)] [string]$BridgeUpdaterPath,
    [Parameter(Mandatory = $true)] [string]$ChannelManifestPath,
    [Parameter(Mandatory = $true)] [string]$ChannelSignaturePath,
    [Parameter(Mandatory = $true)] [string]$ChannelPackagePath,
    [Parameter(Mandatory = $true)] [string]$ChannelUpdaterPath,
    [Parameter(Mandatory = $true)] [Version]$ExpectedCurrentVersion
)

$ErrorActionPreference = "Stop"
$bridgeVersion = [Version]"0.7.6"
$currentVersion = [Version]::new(
    $ExpectedCurrentVersion.Major,
    $ExpectedCurrentVersion.Minor,
    $ExpectedCurrentVersion.Build)

if ($currentVersion -lt $bridgeVersion) {
    throw "Manifest v3 releases must be v$bridgeVersion or newer."
}

& (Join-Path $PSScriptRoot "verify-issued-update-compatibility.ps1") `
    -ManifestPath $BridgeManifestPath `
    -SignaturePath $BridgeSignaturePath `
    -PackagePath $BridgePackagePath `
    -UpdaterPath $BridgeUpdaterPath `
    -ExpectedVersion $bridgeVersion

& (Join-Path $PSScriptRoot "verify-issued-update-compatibility.ps1") `
    -ManifestPath $ChannelManifestPath `
    -SignaturePath $ChannelSignaturePath `
    -PackagePath $ChannelPackagePath `
    -UpdaterPath $ChannelUpdaterPath `
    -ExpectedVersion $currentVersion

if ($currentVersion -eq $bridgeVersion) {
    $bridgeHash = (Get-FileHash -LiteralPath $BridgeManifestPath -Algorithm SHA256).Hash
    $channelHash = (Get-FileHash -LiteralPath $ChannelManifestPath -Algorithm SHA256).Hash
    if ($bridgeHash -cne $channelHash) {
        throw "The bridge release must publish identical bootstrap and channel manifests."
    }
}

Write-Host "Update chain accepted: v0.7.5 -> v$bridgeVersion -> v$currentVersion."
