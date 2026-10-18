[CmdletBinding(DefaultParameterSetName = 'Unsigned')]
param(
    [string]$Configuration = "Release",
    [string]$MsfsSdkRoot = "D:\MSFS 2024 SDK",
    # Retained for existing release scripts; normal packages require OAuth now.
    [switch]$RequireGoogleOAuthCredentials,
    [switch]$AllowMissingGoogleOAuthCredentials,
    [Parameter(ParameterSetName = 'Unsigned')] [switch]$Unsigned,
    [Parameter(Mandatory = $true, ParameterSetName = 'Signed')] [ValidateNotNullOrEmpty()] [string]$SigningCertificateThumbprint,
    [Parameter(ParameterSetName = 'Signed')] [string]$SignToolPath = "signtool.exe",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$signingRequested = $PSCmdlet.ParameterSetName -eq 'Signed'
if ($AllowMissingGoogleOAuthCredentials -and ($RequireGoogleOAuthCredentials -or $signingRequested)) {
    throw 'AllowMissingGoogleOAuthCredentials is only for unsigned developer/test packages and cannot be combined with RequireGoogleOAuthCredentials or signing.'
}
$requireGoogleOAuth = -not $AllowMissingGoogleOAuthCredentials
if ($requireGoogleOAuth) {
    foreach ($name in @('GOOGLE_OAUTH_CLIENT_ID', 'GOOGLE_OAUTH_CLIENT_SECRET')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Google Drive configuration is required for a normal package. Set $name, or explicitly use -AllowMissingGoogleOAuthCredentials for an unsigned developer/test package."
        }
    }
} else {
    Write-Warning 'Building an unsigned developer/test package that may not support Google Drive sign-in.'
}

function Sign-ApplicationFile([string]$Path) {
    if (-not $signingRequested) { return }
    if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) { throw 'A signing certificate thumbprint is required for a signed build.' }
    & $SignToolPath sign /sha1 $SigningCertificateThumbprint /tr http://time.certum.pl /td SHA256 /fd SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed: $Path" }
    & $SignToolPath verify /pa /all /tw $Path
    if ($LASTEXITCODE -ne 0) { throw "Code signature verification failed: $Path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $SigningCertificateThumbprint) {
        throw "The file must have a valid timestamped signature from the configured certificate: $Path"
    }
}

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
if ($signingRequested -and [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) { throw 'A signing certificate thumbprint is required for a signed build.' }
$defaultArtifacts = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$artifactsDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $defaultArtifacts } else { [IO.Path]::GetFullPath($OutputDirectory) }
if ($artifactsDirectory -ne $defaultArtifacts -and -not $artifactsDirectory.StartsWith($defaultArtifacts.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must stay within the repository artifacts directory.'
}
$workDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsDirectory "app-package-work"))
$packageDirectory = Join-Path $workDirectory "MSFS-Landing-Stats"
$payloadPath = Join-Path $workDirectory "payload.zip"
$singleFilePath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.exe"
$updaterArtifactPath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.Updater.exe"
$obsoletePackagePath = Join-Path $artifactsDirectory "MSFS-Landing-Stats.zip"
$obsoleteWorkDirectories = @(
    (Join-Path $artifactsDirectory "app-bundle-work"),
    (Join-Path $artifactsDirectory "bundle-work")
)
$allowedPrefix = $artifactsDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $workDirectory.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a package work directory outside artifacts."
}

$buildArguments = @(
    "build",
    (Join-Path $repositoryRoot "MsfsLandingStats.App.sln"),
    "-c",
    $Configuration,
    "/p:MsfsSdkRoot=$MsfsSdkRoot",
    '-m:1', '/nodeReuse:false'
)
$buildArguments += "/p:RequireGoogleOAuthCredentials=$($requireGoogleOAuth.ToString().ToLowerInvariant())"

dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Solution build failed."
}

$applicationOutput = Join-Path $repositoryRoot "src\LandingStats.App\bin\$Configuration\net48"
if ($requireGoogleOAuth) {
    # Verify the actual runtime owner, before replacing any previous package or
    # spending a signing operation. UI assembly metadata is not used by Drive.
    # A fresh process avoids cached assembly identities across repeated builds,
    # including Windows PowerShell's reflection-only load context.
    $powershellName = if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh.exe' }
    $global:LASTEXITCODE = $null
    & (Join-Path $PSHOME $powershellName) -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File (Join-Path $repositoryRoot 'verify-google-oauth-build.ps1') `
        -AssemblyPath (Join-Path $applicationOutput 'LandingStats.Delivery.dll')
    $verificationExitCode = $global:LASTEXITCODE
    if ($verificationExitCode -ne 0) { throw 'Google Drive configuration verification failed. The previous package has been preserved.' }
}

if (Test-Path -LiteralPath $workDirectory) {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force
}
foreach ($obsoleteWorkDirectory in $obsoleteWorkDirectories) {
    $resolvedObsoleteWorkDirectory = [IO.Path]::GetFullPath($obsoleteWorkDirectory)
    if (-not $resolvedObsoleteWorkDirectory.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a legacy package work directory outside artifacts."
    }
    if (Test-Path -LiteralPath $resolvedObsoleteWorkDirectory) {
        Remove-Item -LiteralPath $resolvedObsoleteWorkDirectory -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

foreach ($oldArtifact in @(
    $obsoletePackagePath,
    $singleFilePath,
    $updaterArtifactPath,
    (Join-Path $artifactsDirectory "update-manifest.txt"),
    (Join-Path $artifactsDirectory "update-manifest.sig"),
    (Join-Path $artifactsDirectory "update-channel.txt"),
    (Join-Path $artifactsDirectory "update-channel.sig")
)) {
    if (Test-Path -LiteralPath $oldArtifact) {
        Remove-Item -LiteralPath $oldArtifact -Force
    }
}

$payloadSource = Get-Content (Join-Path $repositoryRoot 'src\LandingStats.UpdateProtocol\RuntimePayload.cs') -Raw
$requiredPackageFiles = @([regex]::Matches($payloadSource, '(?m)^\s*"([^"\r\n]+)",\s*$') | ForEach-Object { $_.Groups[1].Value })
if ($requiredPackageFiles.Count -lt 10) { throw 'Runtime payload allowlist is invalid.' }

foreach ($file in $requiredPackageFiles) {
    $source = Join-Path $applicationOutput $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required application file is missing: $source"
    }
}

$packageFiles = @($requiredPackageFiles | Sort-Object -Unique)

foreach ($file in $packageFiles) {
    $source = Join-Path $applicationOutput $file
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageDirectory $file)
}

$updaterBuildPath = Join-Path $repositoryRoot "src\LandingStats.App.Updater\bin\$Configuration\net48\MSFS-Landing-Stats.Updater.exe"
if (-not (Test-Path -LiteralPath $updaterBuildPath)) {
    throw "Updater executable is missing: $updaterBuildPath"
}
Copy-Item -LiteralPath $updaterBuildPath -Destination $updaterArtifactPath
if ($signingRequested) {
    Sign-ApplicationFile $updaterArtifactPath
    foreach ($file in $packageFiles | Where-Object { $_ -eq 'MSFS-Landing-Stats.exe' -or $_ -match '^LandingStats\..*\.(dll|exe)$' }) {
        Sign-ApplicationFile (Join-Path $packageDirectory $file)
    }
}

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

if ($signingRequested) { Sign-ApplicationFile $singleFilePath }

$verification = Start-Process `
    -FilePath $singleFilePath `
    -ArgumentList '--verify-bundle' `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
if ($verification.ExitCode -ne 0) {
    throw "Single-file bundle verification failed with exit code $($verification.ExitCode)."
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

& (Join-Path $repositoryRoot "build-update-manifest.ps1") -ArtifactsDirectory $artifactsDirectory

$singleFileHash = (Get-FileHash -LiteralPath $singleFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
$singleFileSize = (Get-Item -LiteralPath $singleFilePath).Length
$updaterHash = (Get-FileHash -LiteralPath $updaterArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$updaterSize = (Get-Item -LiteralPath $updaterArtifactPath).Length
Write-Host "Single-file download: $singleFilePath"
Write-Host "Single-file size: $singleFileSize bytes"
Write-Host "Single-file SHA-256: $singleFileHash"
Write-Host "Updater: $updaterArtifactPath"
Write-Host "Updater size: $updaterSize bytes"
Write-Host "Updater SHA-256: $updaterHash"
Write-Host "Release version: $($appVersion.Major).$($appVersion.Minor).$($appVersion.Build)"

if (Test-Path -LiteralPath $workDirectory) {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force
}
