param(
    [Parameter(Mandatory=$true)] [string]$CertificateThumbprint,
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$LaunchOnHostedRunner
)
$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$package = Join-Path $artifacts 'MSFS-Landing-Stats.exe'
$updater = Join-Path $artifacts 'MSFS-Landing-Stats.Updater.exe'
function Assert-Signature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
        throw "A valid timestamped publisher signature is required: $Path"
    }
}
Assert-Signature $package
Assert-Signature $updater
if (-not ('LandingStats.Packaging.BundlePayload' -as [type])) {
    Add-Type -Path "$PSScriptRoot\src\LandingStats.UpdateProtocol\BundlePayload.cs"
}
Add-Type -AssemblyName System.IO.Compression
$input = [IO.File]::OpenRead($package)
try {
    $bounds = [LandingStats.Packaging.BundlePayload]::Locate($input)
    $input.Position = $bounds.Offset
    $reader = [IO.BinaryReader]::new($input)
    $payload = [IO.MemoryStream]::new($reader.ReadBytes($bounds.Length), $false)
} finally { $input.Dispose() }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('msfs-signed-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$archive = [IO.Compression.ZipArchive]::new($payload, [IO.Compression.ZipArchiveMode]::Read)
try {
    foreach ($name in @('MSFS-Landing-Stats.exe','LandingStats.Core.dll')) {
        $entry = $archive.GetEntry($name)
        if ($null -eq $entry) { throw "Signed runtime missing: $name" }
        $path = Join-Path $scratch $name
        $source = $entry.Open(); $output = [IO.File]::Create($path)
        try { $source.CopyTo($output) } finally { $output.Dispose(); $source.Dispose() }
        Assert-Signature $path
    }
    $verify = Start-Process $package -ArgumentList '--verify-bundle' -PassThru -WindowStyle Hidden
    if (-not $verify.WaitForExit(30000)) { $verify.Kill(); throw 'Bundle verification timed out.' }
    if ($verify.ExitCode -ne 0) { throw 'Signed launcher failed bundle verification.' }

    if ($LaunchOnHostedRunner) {
        if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
            throw 'The automatic UI smoke test may run only on an isolated GitHub-hosted runner.'
        }
        if (Get-Process -Name MSFS-Landing-Stats -ErrorAction SilentlyContinue) { throw 'Another application is already running.' }
        $launcher = Start-Process $package -PassThru -WindowStyle Hidden
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            $ready = $false
            do {
                Start-Sleep -Milliseconds 500
                $children = @(Get-Process -Name MSFS-Landing-Stats -ErrorAction SilentlyContinue)
                foreach ($child in $children) {
                    if ($child.MainWindowTitle -eq 'Landing Stats' -and $child.Responding) { $ready = $true }
                }
            } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
            if (-not $ready) { throw 'Signed application did not open its main window.' }
            Write-Host 'Signed application launched: responsive Landing Stats main window.'
        } finally {
            # The hosted VM was empty before this test; never run this cleanup on a user's PC.
            Get-Process -Name MSFS-Landing-Stats -ErrorAction SilentlyContinue | Stop-Process -Force
            $launcher.Dispose()
        }
    }
} finally {
    $archive.Dispose(); $payload.Dispose()
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ([IO.Path]::GetFullPath($scratch).StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
Write-Host 'Outer launcher, embedded application, Core and updater signatures verified.'
