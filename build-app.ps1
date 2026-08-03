param(
    [string]$Configuration = "Release",
    [string]$MsfsSdkRoot = "D:\MSFS 2024 SDK"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$artifactsDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$workDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsDirectory "app-bundle-work"))
$payloadDirectory = Join-Path $workDirectory "payload"
$zipPath = Join-Path $workDirectory "payload.zip"
$outputPath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.exe"
$allowedPrefix = $artifactsDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $workDirectory.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a bundle work directory outside artifacts."
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
New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null

$applicationOutput = Join-Path $repositoryRoot "src\LandingStats.App\bin\$Configuration\net48"
$payloadFiles = @(
    "LandingStats.App.exe",
    "LandingStats.App.exe.config",
    "LandingStats.Core.dll",
    "Microsoft.FlightSimulator.SimConnect.dll",
    "SimConnect.dll"
)

foreach ($file in $payloadFiles) {
    $source = Join-Path $applicationOutput $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required payload file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $payloadDirectory $file)
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $payloadDirectory,
    $zipPath,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

$launcherPath = Join-Path $repositoryRoot "src\LandingStats.App.Launcher\bin\$Configuration\net48\MSFS-Landing-Stats.exe"
if (-not (Test-Path -LiteralPath $launcherPath)) {
    throw "Launcher stub is missing: $launcherPath"
}

Copy-Item -LiteralPath $launcherPath -Destination $outputPath -Force
$zipBytes = [IO.File]::ReadAllBytes($zipPath)
$magicBytes = [Text.Encoding]::ASCII.GetBytes("MSFSLSABUNDLE1")
$stream = [IO.File]::Open($outputPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
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

$hash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $outputPath).Length
Write-Host "Application single binary: $outputPath"
Write-Host "Size: $size bytes"
Write-Host "SHA-256: $hash"
