param(
    [string]$Configuration = "Release",
    [string]$MsfsSdkRoot = "D:\MSFS 2024 SDK"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$artifactsDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$workDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsDirectory "app-package-work"))
$packageDirectory = Join-Path $workDirectory "MSFS-Landing-Stats"
$packagePath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.zip"
$updaterArtifactPath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.Updater.exe"
$obsoleteBundlePath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.exe"
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

foreach ($oldArtifact in @($packagePath, $updaterArtifactPath, $obsoleteBundlePath)) {
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

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $packageDirectory,
    $packagePath,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
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

$appVersion = [Reflection.AssemblyName]::GetAssemblyName(
    (Join-Path $packageDirectory "MSFS-Landing-Stats.exe")).Version
$updaterVersion = [Reflection.AssemblyName]::GetAssemblyName($updaterArtifactPath).Version
if ($appVersion.Major -ne $updaterVersion.Major -or
    $appVersion.Minor -ne $updaterVersion.Minor -or
    $appVersion.Build -ne $updaterVersion.Build) {
    throw "Application and updater versions do not match."
}

$packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$packageSize = (Get-Item -LiteralPath $packagePath).Length
$updaterHash = (Get-FileHash -LiteralPath $updaterArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$updaterSize = (Get-Item -LiteralPath $updaterArtifactPath).Length
Write-Host "Portable package: $packagePath"
Write-Host "Package size: $packageSize bytes"
Write-Host "Package SHA-256: $packageHash"
Write-Host "Updater: $updaterArtifactPath"
Write-Host "Updater size: $updaterSize bytes"
Write-Host "Updater SHA-256: $updaterHash"
Write-Host "Release version: $($appVersion.Major).$($appVersion.Minor).$($appVersion.Build)"
