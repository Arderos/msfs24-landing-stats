[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$MsvcToolsetVersion,
    [string]$WindowsSdkVersion,
    [switch]$TestRecovery
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-NativeBuildHash([string]$Path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $sourceStream = [IO.File]::OpenRead($Path)
    try { [BitConverter]::ToString($hash.ComputeHash($sourceStream)).Replace('-', '') }
    finally { $sourceStream.Dispose(); $hash.Dispose() }
}

# Use installed tools only. This works from ordinary PowerShell/dotnet build,
# without relying on a developer prompt or installing an SDK during the build.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer/vswhere is required to build the native chart renderer.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) { throw 'Install the Visual Studio C++ x64 build tools before building the application.' }
$installation = $installation.Trim()
if ([string]::IsNullOrWhiteSpace($MsvcToolsetVersion)) {
    $MsvcToolsetVersion = (Get-Content -LiteralPath (Join-Path $installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
}
if ($MsvcToolsetVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid MSVC toolset version.' }
$compilerRoot = Join-Path $installation "VC\Tools\MSVC\$MsvcToolsetVersion"
$compilerDirectory = Join-Path $compilerRoot 'bin\Hostx64\x64'
$compiler = Join-Path $compilerDirectory 'cl.exe'
$sdkRoot = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10).KitsRoot10
if ([string]::IsNullOrWhiteSpace($WindowsSdkVersion)) {
    $candidate = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'shared\d3d9.h')) -and
            (Test-Path -LiteralPath (Join-Path $sdkRoot "Lib\$($_.Name)\um\x64\d3d9.lib")) -and
            (Test-Path -LiteralPath (Join-Path $sdkRoot "Lib\$($_.Name)\ucrt\x64\libucrt.lib")) -and
            (Test-Path -LiteralPath (Join-Path $sdkRoot "bin\$($_.Name)\x64\rc.exe")) } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($null -eq $candidate) { throw 'An installed Windows SDK with x64 headers, libraries and resource compiler is required.' }
    $WindowsSdkVersion = $candidate.Name
}
if ($WindowsSdkVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid Windows SDK version.' }
$includeDirectories = @((Join-Path $compilerRoot 'include'), (Join-Path $sdkRoot "Include\$WindowsSdkVersion\ucrt"),
    (Join-Path $sdkRoot "Include\$WindowsSdkVersion\shared"), (Join-Path $sdkRoot "Include\$WindowsSdkVersion\um"))
$libraryDirectories = @((Join-Path $compilerRoot 'lib\x64'), (Join-Path $sdkRoot "Lib\$WindowsSdkVersion\ucrt\x64"),
    (Join-Path $sdkRoot "Lib\$WindowsSdkVersion\um\x64"))
$sdkBinaryDirectory = Join-Path $sdkRoot "bin\$WindowsSdkVersion\x64"
foreach ($path in @($compiler, (Join-Path $sdkBinaryDirectory 'rc.exe')) + $includeDirectories + $libraryDirectories) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Native build prerequisite is missing: $path" }
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot "obj\$Configuration\x64" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stem = Join-Path $OutputDirectory $(if ($TestRecovery) { 'ChartDeviceRecovery.Tests' } else { 'LandingStats.Graphics.Native' })
$binary = if ($TestRecovery) { "$stem.exe" } else { "$stem.dll" }
$stamp = Join-Path $OutputDirectory $(if ($TestRecovery) { 'recovery-test-inputs.json' } else { 'build-inputs.json' })
$source = if ($TestRecovery) { Join-Path $PSScriptRoot '../../tests/LandingStats.Graphics.Native.Tests/ChartDeviceRecoveryTests.cpp' } else { Join-Path $PSScriptRoot 'ChartFlipNative.cpp' }
$inputs = [ordered]@{ Configuration = $Configuration; Compiler = $compiler; MsvcToolset = $MsvcToolsetVersion; WindowsSdk = $WindowsSdkVersion; TestRecovery = [bool]$TestRecovery }
foreach ($file in @('ChartFlipNative.cpp', 'ChartFlipNative.h', 'ChartDeviceRecovery.h', 'ChartInitialBackground.h', 'build-native.ps1', 'verify-native.ps1')) {
    $inputs[$file] = Get-NativeBuildHash (Join-Path $PSScriptRoot $file)
}
if ($TestRecovery) { $inputs['RecoveryTests'] = Get-NativeBuildHash $source }
function Verify-NativeOutput {
    if ($TestRecovery) {
        & $binary
        if ($LASTEXITCODE -ne 0) { throw "Native chart recovery regressions failed: $LASTEXITCODE" }
    } else {
        & (Join-Path $PSScriptRoot 'verify-native.ps1') -Path $binary | Out-Null
    }
}
$fingerprint = $inputs | ConvertTo-Json -Compress
$savedFingerprint = if (Test-Path -LiteralPath $stamp) { [IO.File]::ReadAllText($stamp) } else { '' }
if ((Test-Path -LiteralPath $binary) -and $savedFingerprint -ceq ($fingerprint + "`n" + (Get-NativeBuildHash $binary))) {
    Verify-NativeOutput
    Write-Host "Native chart build is current (MSVC $MsvcToolsetVersion; Windows SDK $WindowsSdkVersion)."
    return
}

$arguments = @('/nologo', '/EHsc', '/std:c++17', '/W4', '/WX', '/MT', '/Brepro', '/DUNICODE', '/D_UNICODE')
if (-not $TestRecovery) { $arguments += '/LD' }
if ($Configuration -eq 'Debug') { $arguments += @('/Od', '/Z7') } else { $arguments += '/O2' }
$arguments += $includeDirectories | ForEach-Object { '/I' + $_ }
$arguments += @(("/Fo$stem.obj"), ("/Fe$binary"), $source,
    '/link', '/MACHINE:X64', '/INCREMENTAL:NO', '/Brepro', '/MANIFEST:EMBED')
if (-not $TestRecovery) { $arguments += "/IMPLIB:$stem.lib" }
if ($Configuration -eq 'Debug') { $arguments += @('/DEBUG:FULL', ("/PDB:$stem.pdb")) }
$arguments += $libraryDirectories | ForEach-Object { '/LIBPATH:' + $_ }
$arguments += @('d3d9.lib', 'user32.lib', 'gdi32.lib')
$originalPath = $env:PATH
$originalCl = $env:CL
$originalAdditionalCl = $env:_CL_
try {
    # Ignore ambient compiler switches: command line and recorded tool versions
    # define this binary, not a developer's interactive shell configuration.
    $env:CL = $null; $env:_CL_ = $null
    $env:PATH = $sdkBinaryDirectory + ';' + $compilerDirectory + ';' + $originalPath
    $purpose = if ($TestRecovery) { 'device recovery regressions (no GPU required)' } else { 'renderer' }
    Write-Host "Building native chart $purpose (MSVC $MsvcToolsetVersion; Windows SDK $WindowsSdkVersion; $Configuration x64 /MT)."
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Native chart renderer compilation failed: $LASTEXITCODE" }
} finally {
    $env:PATH = $originalPath; $env:CL = $originalCl; $env:_CL_ = $originalAdditionalCl
}
Verify-NativeOutput
[IO.File]::WriteAllText($stamp, $fingerprint + "`n" + (Get-NativeBuildHash $binary))
Get-Item -LiteralPath $binary | Select-Object FullName, Length
