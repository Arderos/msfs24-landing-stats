param([string]$ArtifactsDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$candidate = Join-Path $artifacts 'MSFS-Landing-Stats.exe'
$updater = Join-Path $artifacts 'MSFS-Landing-Stats.Updater.exe'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($candidate).Version
$version = [Version]::new($assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('msfs-issued-clients-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $base = 'https://github.com/Arderos/msfs24-landing-stats/releases/download/'
    $bridge = Join-Path $scratch '0.7.6'
    New-Item -ItemType Directory -Path $bridge | Out-Null
    foreach ($file in @('MSFS-Landing-Stats.exe', 'MSFS-Landing-Stats.Updater.exe', 'update-manifest.txt', 'update-manifest.sig')) {
        Invoke-WebRequest ($base + 'v0.7.6/' + $file) -OutFile (Join-Path $bridge $file)
    }
    & "$PSScriptRoot\verify-update-chain.ps1" `
        -BridgeManifestPath (Join-Path $bridge 'update-manifest.txt') `
        -BridgeSignaturePath (Join-Path $bridge 'update-manifest.sig') `
        -BridgePackagePath (Join-Path $bridge 'MSFS-Landing-Stats.exe') `
        -BridgeUpdaterPath (Join-Path $bridge 'MSFS-Landing-Stats.Updater.exe') `
        -ChannelManifestPath (Join-Path $artifacts 'update-channel.txt') `
        -ChannelSignaturePath (Join-Path $artifacts 'update-channel.sig') `
        -ChannelPackagePath $candidate -ChannelUpdaterPath $updater -ExpectedCurrentVersion $version

    # Every currently issued client, not only the most recent one. Keep adding
    # released versions here; never silently drop an old compatibility baseline.
    $baselines = @('0.7.3','0.7.4','0.7.5','0.7.6','0.7.7','0.7.8','0.7.9',
        '0.8.0','0.8.1','0.8.2','0.8.3','0.8.4','0.8.5','0.8.6')
    foreach ($previous in $baselines) {
        $directory = Join-Path $scratch $previous
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $previousPath = Join-Path $directory 'MSFS-Landing-Stats.exe'
        if (-not (Test-Path $previousPath)) {
            Invoke-WebRequest ($base + "v$previous/MSFS-Landing-Stats.exe") -OutFile $previousPath
        }
        $legacy = [Version]$previous -lt [Version]'0.7.6'
        $destination = if ($legacy) { $bridge } else { $artifacts }
        $channel = if ($legacy) { 'update-manifest' } else { 'update-channel' }
        $expected = if ($legacy) { '0.7.6' } else { $version.ToString() }
        # Isolate reflection loads of identically named assemblies from different versions.
        & (Get-Process -Id $PID).Path -NoProfile -File "$PSScriptRoot\verify-previous-client-update.ps1" `
            -PreviousPackagePath $previousPath -PreviousVersion $previous `
            -ManifestPath (Join-Path $destination "$channel.txt") `
            -SignaturePath (Join-Path $destination "$channel.sig") -ExpectedVersion $expected `
            -CandidatePackagePath (Join-Path $destination 'MSFS-Landing-Stats.exe') `
            -CandidateUpdaterPath (Join-Path $destination 'MSFS-Landing-Stats.Updater.exe')
        if ($LASTEXITCODE -ne 0) { throw "Issued client v$previous update failed." }
    }
    # Publish the exact bridge metadata verified above, without modifying its signature.
    foreach ($file in @('update-manifest.txt','update-manifest.sig')) {
        Copy-Item -LiteralPath (Join-Path $bridge $file) -Destination (Join-Path $artifacts $file) -Force
    }
} finally {
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ([IO.Path]::GetFullPath($scratch).StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
Write-Host "All 14 published clients have a verified automatic path to v$version."
