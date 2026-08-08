param(
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot "..\artifacts")
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$packagePath = Join-Path $artifacts "MSFS-Landing-Stats.exe"
$updaterPath = Join-Path $artifacts "MSFS-Landing-Stats.Updater.exe"
$manifestPath = Join-Path $artifacts "update-channel.txt"
$bridgeManifestPath = Join-Path $artifacts "update-manifest.txt"
$obsoleteZipPath = Join-Path $artifacts "MSFS-Landing-Stats.zip"
$verifier = Join-Path $repositoryRoot "verify-issued-update-compatibility.ps1"

foreach ($path in @($packagePath, $updaterPath, $manifestPath, $verifier)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Compatibility test input is missing: $path"
    }
}
if (Test-Path -LiteralPath $obsoleteZipPath) {
    throw "Format-3 build still produced the obsolete ZIP transport."
}

$bridgeVersion = [Version]"0.7.6"
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($updaterPath).Version
$currentVersion = [Version]::new(
    $assemblyVersion.Major,
    $assemblyVersion.Minor,
    $assemblyVersion.Build)
if ($currentVersion -lt $bridgeVersion) {
    throw "Format-3 channel builds must be v$bridgeVersion or newer."
}

& $verifier `
    -ManifestPath $manifestPath `
    -PackagePath $packagePath `
    -UpdaterPath $updaterPath `
    -ExpectedVersion $currentVersion

if ($currentVersion -eq $bridgeVersion) {
    if (-not (Test-Path -LiteralPath $bridgeManifestPath -PathType Leaf)) {
        throw "The bridge build did not produce its immutable bootstrap manifest."
    }
    $channelHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    $bridgeHash = (Get-FileHash -LiteralPath $bridgeManifestPath -Algorithm SHA256).Hash
    if ($channelHash -cne $bridgeHash) {
        throw "The bridge build did not produce identical bootstrap and channel manifests."
    }
}
elseif (Test-Path -LiteralPath $bridgeManifestPath) {
    throw "A post-bridge build attempted to replace the immutable bootstrap manifest."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("msfs-update-gate-test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $original = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false, $true))
    $currentVersionText = "$($currentVersion.Major).$($currentVersion.Minor).$($currentVersion.Build)"
    $wrongVersion = [Version]::new($currentVersion.Major, $currentVersion.Minor, $currentVersion.Build + 1)
    $wrongVersionText = "$($wrongVersion.Major).$($wrongVersion.Minor).$($wrongVersion.Build)"

    function Assert-Rejected([string]$Name, [string]$Manifest) {
        $candidatePath = Join-Path $testRoot ($Name + ".txt")
        [IO.File]::WriteAllBytes($candidatePath, [Text.UTF8Encoding]::new($false).GetBytes($Manifest))
        $rejected = $false
        try {
            & $verifier `
                -ManifestPath $candidatePath `
                -PackagePath $packagePath `
                -UpdaterPath $updaterPath `
                -ExpectedVersion $currentVersion
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Release gate accepted invalid case: $Name"
        }
    }

    Assert-Rejected "format2" ($original.Replace("format=3", "format=2"))
    Assert-Rejected "wrong-version" ($original.Replace("version=$currentVersionText", "version=$wrongVersionText"))
    Assert-Rejected "wrong-package-hash" ($original -replace '(?m)^package-sha256=.*$', ('package-sha256=' + ('0' * 64)))
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Host "Update compatibility negative tests passed."
