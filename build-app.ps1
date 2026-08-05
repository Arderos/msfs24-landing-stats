param(
    [string]$Configuration = "Release",
    [string]$MsfsSdkRoot = "D:\MSFS 2024 SDK"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$artifactsDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$workDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsDirectory "app-package-work"))
$packageDirectory = Join-Path $workDirectory "MSFS-Landing-Stats"
$payloadPath = Join-Path $workDirectory "payload.zip"
$singleFilePath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.exe"
$updaterArtifactPath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.Updater.exe"
$legacyPackagePath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.zip"
$allowedPrefix = $artifactsDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $workDirectory.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a package work directory outside artifacts."
}

dotnet build (Join-Path $repositoryRoot "MsfsLandingStats.App.sln") `
    -c $Configuration `
    "/p:MsfsSdkRoot=$MsfsSdkRoot"
if ($LASTEXITCODE -ne 0) {
    throw "Solution build failed."
}

if (Test-Path -LiteralPath $workDirectory) {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

foreach ($oldArtifact in @($legacyPackagePath, $singleFilePath, $updaterArtifactPath)) {
    if (Test-Path -LiteralPath $oldArtifact) {
        Remove-Item -LiteralPath $oldArtifact -Force
    }
}

$applicationOutput = Join-Path $repositoryRoot "src\LandingStats.App\bin\$Configuration\net48"
$packageFiles = @(
    "MSFS-Landing-Stats.exe",
    "MSFS-Landing-Stats.exe.config",
    "LandingStats.Core.dll",
    "Microsoft.FlightSimulator.SimConnect.dll",
    "SimConnect.dll"
)

foreach ($file in $packageFiles) {
    $source = Join-Path $applicationOutput $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required application file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageDirectory $file)
}

$updaterBuildPath = Join-Path $repositoryRoot "src\LandingStats.App.Updater\bin\$Configuration\net48\MSFS-Landing-Stats.Updater.exe"
if (-not (Test-Path -LiteralPath $updaterBuildPath)) {
    throw "Updater executable is missing: $updaterBuildPath"
}
Copy-Item -LiteralPath $updaterBuildPath -Destination $updaterArtifactPath

$launcherBuildPath = Join-Path $repositoryRoot "src\LandingStats.App.Launcher\bin\$Configuration\net48\MSFS-Landing-Stats.exe"
if (-not (Test-Path -LiteralPath $launcherBuildPath)) {
    throw "Single-file bootstrap is missing: $launcherBuildPath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $packageDirectory,
    $payloadPath,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

$archive = [IO.Compression.ZipFile]::OpenRead($payloadPath)
try {
    $actualFiles = @($archive.Entries | ForEach-Object FullName | Sort-Object)
    $expectedFiles = @($packageFiles | Sort-Object)
    if ($actualFiles.Count -ne $expectedFiles.Count -or
        [string]::Join("`n", $actualFiles) -cne [string]::Join("`n", $expectedFiles)) {
        throw "Portable package topology is invalid."
    }
}
finally {
    $archive.Dispose()
}

Copy-Item -LiteralPath $launcherBuildPath -Destination $singleFilePath
$zipBytes = [IO.File]::ReadAllBytes($payloadPath)
$magicBytes = [Text.Encoding]::ASCII.GetBytes("MSFSLSABUNDLE1")
$stream = [IO.File]::Open($singleFilePath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $stream.Write($zipBytes, 0, $zipBytes.Length)
    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        $writer.Write([Int64]$zipBytes.LongLength)
        $writer.Write($magicBytes)
        $writer.Flush()
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $stream.Dispose()
}

$verification = Start-Process `
    -FilePath $singleFilePath `
    -ArgumentList '--verify-bundle' `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
if ($verification.ExitCode -ne 0) {
    throw "Single-file bundle verification failed with exit code $($verification.ExitCode)."
}

$legacyArchive = [IO.Compression.ZipFile]::Open(
    $legacyPackagePath,
    [IO.Compression.ZipArchiveMode]::Create)
try {
    $entry = $legacyArchive.CreateEntry(
        "MSFS-Landing-Stats.exe",
        [IO.Compression.CompressionLevel]::Optimal)
    $input = [IO.File]::OpenRead($singleFilePath)
    $output = $entry.Open()
    try {
        $input.CopyTo($output)
    }
    finally {
        $output.Dispose()
        $input.Dispose()
    }
}
finally {
    $legacyArchive.Dispose()
}

$legacyVerification = [IO.Compression.ZipFile]::OpenRead($legacyPackagePath)
try {
    if ($legacyVerification.Entries.Count -ne 1 -or
        $legacyVerification.Entries[0].FullName -cne "MSFS-Landing-Stats.exe" -or
        $legacyVerification.Entries[0].Length -ne (Get-Item -LiteralPath $singleFilePath).Length) {
        throw "Legacy update package topology is invalid."
    }
}
finally {
    $legacyVerification.Dispose()
}

$appVersion = [Reflection.AssemblyName]::GetAssemblyName(
    (Join-Path $packageDirectory "MSFS-Landing-Stats.exe")).Version
$singleFileVersion = [Reflection.AssemblyName]::GetAssemblyName($singleFilePath).Version
$updaterVersion = [Reflection.AssemblyName]::GetAssemblyName($updaterArtifactPath).Version
if ($appVersion.Major -ne $singleFileVersion.Major -or
    $appVersion.Minor -ne $singleFileVersion.Minor -or
    $appVersion.Build -ne $singleFileVersion.Build -or
    $appVersion.Major -ne $updaterVersion.Major -or
    $appVersion.Minor -ne $updaterVersion.Minor -or
    $appVersion.Build -ne $updaterVersion.Build) {
    throw "Application and updater versions do not match."
}

$singleFileHash = (Get-FileHash -LiteralPath $singleFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
$singleFileSize = (Get-Item -LiteralPath $singleFilePath).Length
$updaterHash = (Get-FileHash -LiteralPath $updaterArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$updaterSize = (Get-Item -LiteralPath $updaterArtifactPath).Length
Write-Host "Single-file download: $singleFilePath"
Write-Host "Single-file size: $singleFileSize bytes"
Write-Host "Single-file SHA-256: $singleFileHash"
Write-Host "Legacy updater package: $legacyPackagePath"
Write-Host "Updater: $updaterArtifactPath"
Write-Host "Updater size: $updaterSize bytes"
Write-Host "Updater SHA-256: $updaterHash"
Write-Host "Release version: $($appVersion.Major).$($appVersion.Minor).$($appVersion.Build)"
