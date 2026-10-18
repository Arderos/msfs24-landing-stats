param([string]$ArtifactsDirectory = (Join-Path $PSScriptRoot '..\artifacts\google-oauth-guard-tests'))

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = [IO.Path]::GetFullPath($ArtifactsDirectory)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts')).TrimEnd('\') + '\'
if (-not $testRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Guard test outputs must stay inside repository artifacts.'
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$dotnetExecutable = (Get-Command dotnet -CommandType Application).Source
$powershellName = if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh.exe' }
$powershellExecutable = Join-Path $PSHOME $powershellName
$savedId = $env:GOOGLE_OAUTH_CLIENT_ID
$savedSecret = $env:GOOGLE_OAUTH_CLIENT_SECRET
$savedTemp = $env:TEMP
$savedTmp = $env:TMP

function Assert-Fails([scriptblock]$Action, [string]$Expected) {
    try { & $Action; throw 'The expected rejection did not occur.' }
    catch { if ($_.Exception.Message -notlike "*$Expected*") { throw } }
}

# Stop the real packaging script exactly at its external build boundary. No
# solution output, previous package, signing operation or network is touched.
function dotnet {
    $global:GoogleOAuthGuardArguments = @($args)
    if ($global:GoogleOAuthGuardContinueAfterBuild) { $global:LASTEXITCODE = 0; return }
    throw 'TEST_BUILD_BOUNDARY'
}

try {
    $env:TEMP = $testRoot; $env:TMP = $testRoot
    $build = Join-Path $repository 'build-app.ps1'
    $verifier = Join-Path $repository 'verify-google-oauth-build.ps1'
    $env:GOOGLE_OAUTH_CLIENT_ID = $null; $env:GOOGLE_OAUTH_CLIENT_SECRET = $null
    $global:GoogleOAuthGuardArguments = @()
    Assert-Fails { & $build } 'GOOGLE_OAUTH_CLIENT_ID'
    Assert-Fails { & $build -RequireGoogleOAuthCredentials:$false } 'GOOGLE_OAUTH_CLIENT_ID'
    Assert-Fails { & $build -RequireGoogleOAuthCredentials } 'GOOGLE_OAUTH_CLIENT_ID'
    if ($global:GoogleOAuthGuardArguments.Count -ne 0) { throw 'Missing credentials reached the build.' }
    $env:GOOGLE_OAUTH_CLIENT_ID = 'test-only-id'
    Assert-Fails { & $build } 'GOOGLE_OAUTH_CLIENT_SECRET'
    $env:GOOGLE_OAUTH_CLIENT_SECRET = '   '
    Assert-Fails { & $build } 'GOOGLE_OAUTH_CLIENT_SECRET'
    $env:GOOGLE_OAUTH_CLIENT_SECRET = 'test-only-secret'
    Assert-Fails { & $build } 'TEST_BUILD_BOUNDARY'
    if ($global:GoogleOAuthGuardArguments -notcontains '/p:RequireGoogleOAuthCredentials=true') { throw 'Normal build did not require the Delivery guard.' }
    Assert-Fails { & $build -RequireGoogleOAuthCredentials } 'TEST_BUILD_BOUNDARY'
    $env:GOOGLE_OAUTH_CLIENT_ID = $null; $env:GOOGLE_OAUTH_CLIENT_SECRET = $null
    Assert-Fails { & $build -AllowMissingGoogleOAuthCredentials -WarningAction SilentlyContinue } 'TEST_BUILD_BOUNDARY'
    if ($global:GoogleOAuthGuardArguments -notcontains '/p:RequireGoogleOAuthCredentials=false') { throw 'Explicit developer opt-out was not forwarded.' }
    Assert-Fails { & $build -AllowMissingGoogleOAuthCredentials -RequireGoogleOAuthCredentials } 'only for unsigned developer/test'
    Assert-Fails { & $build -AllowMissingGoogleOAuthCredentials -SigningCertificateThumbprint 'test-only-not-a-certificate' } 'only for unsigned developer/test'
    Write-Host 'PASS normal/legacy/explicit opt-out/signed-conflict package policy'

    # Exercise the actual target in the assembly which owns configuration,
    # without compiling/restoring any production project or using shared obj.
    $delivery = Join-Path $repository 'src\LandingStats.Delivery\LandingStats.Delivery.csproj'
    foreach ($case in @(
        @{ Id = ''; Secret = ''; Required = 'true'; Success = $false },
        @{ Id = 'test-only-id'; Secret = ''; Required = 'true'; Success = $false },
        @{ Id = ''; Secret = 'test-only-secret'; Required = 'true'; Success = $false },
        @{ Id = 'test-only-id'; Secret = 'test-only-secret'; Required = 'true'; Success = $true },
        @{ Id = ''; Secret = ''; Required = 'false'; Success = $true }
    )) {
        $output = & $dotnetExecutable msbuild $delivery -t:ValidateGoogleOAuthCredentials -nologo -v:quiet -nr:false `
            "-p:UseArtifactsOutput=true" "-p:ArtifactsPath=$testRoot\target-output" `
            "-p:RequireGoogleOAuthCredentials=$($case.Required)" "-p:GoogleOAuthClientId=$($case.Id)" "-p:GoogleOAuthClientSecret=$($case.Secret)" 2>&1
        if (($LASTEXITCODE -eq 0) -ne $case.Success) { throw 'Delivery build-time OAuth validation returned the wrong outcome.' }
    }
    Write-Host 'PASS Delivery assembly build-time required/optional credential validation'

    # Real compiled metadata, all values synthetic. Use the same isolated
    # inspection process as packaging so Desktop never reuses a loaded identity.
    $fixtureProject = Join-Path $testRoot 'Fixture.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net48</TargetFramework><AssemblyName>$(FixtureAssembly)</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup>
    <AssemblyMetadata Include="GoogleOAuthClientId" Value="$(FixtureId)" />
    <AssemblyMetadata Include="GoogleOAuthClientSecret" Value="$(FixtureSecret)" />
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net48" Version="1.0.3" PrivateAssets="all" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $fixtureProject
    $fixtureBuild = Join-Path $testRoot 'fixture-output'
    foreach ($case in @(
        @{ Assembly = 'LandingStats.Delivery'; Id = ''; Secret = ''; Success = $false },
        @{ Assembly = 'LandingStats.Delivery'; Id = 'test-only-id'; Secret = ''; Success = $false },
        @{ Assembly = 'MSFS-Landing-Stats'; Id = 'test-only-id'; Secret = 'test-only-secret'; Success = $false },
        @{ Assembly = 'LandingStats.Delivery'; Id = 'test-only-id'; Secret = 'test-only-secret'; Success = $true }
    )) {
        $output = & $dotnetExecutable build $fixtureProject -nologo -v:quiet -m:1 -nr:false `
            "-p:UseArtifactsOutput=true" "-p:ArtifactsPath=$fixtureBuild" `
            "-p:FixtureAssembly=$($case.Assembly)" "-p:FixtureId=$($case.Id)" "-p:FixtureSecret=$($case.Secret)" 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'Could not compile synthetic OAuth metadata fixture.' }
        $assemblyPath = Join-Path $fixtureBuild "bin\Fixture\debug\$($case.Assembly).dll"
        try {
            $ErrorActionPreference = 'Continue'
            $output = & $powershellExecutable -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $verifier -AssemblyPath $assemblyPath 2>&1
            $verificationExitCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = 'Stop' }
        if (($verificationExitCode -eq 0) -ne $case.Success) { throw 'Compiled OAuth metadata verification returned the wrong outcome.' }
        if (-not $case.Success) {
            $expected = if ($case.Assembly -eq 'LandingStats.Delivery') { 'missing required Google Drive' } else { 'must be verified in LandingStats.Delivery' }
            if (($output | Out-String) -notlike "*$expected*") { throw 'Metadata fixture failed for an unexpected reason.' }
        }
        if ($case.Id -eq '' -and $case.Secret -eq '') {
            $missingFixture = Join-Path $testRoot 'unconfigured-delivery.dll'
            Copy-Item -LiteralPath $assemblyPath -Destination $missingFixture -Force
        }
    }
    Write-Host 'PASS actual Delivery metadata required; configured UI metadata cannot substitute'

    # A successful build returning an unconfigured Delivery DLL must fail before
    # deleting a previous package. Use a separate fake repository, not real bin.
    $fakeRepository = Join-Path $testRoot 'package-repository'
    $fakeOutput = Join-Path $fakeRepository 'src\LandingStats.App\bin\Release\net48'
    $fakeArtifacts = Join-Path $fakeRepository 'artifacts'
    New-Item -ItemType Directory -Path $fakeOutput, $fakeArtifacts -Force | Out-Null
    Copy-Item -LiteralPath $build, $verifier -Destination $fakeRepository -Force
    Copy-Item -LiteralPath $missingFixture -Destination (Join-Path $fakeOutput 'LandingStats.Delivery.dll') -Force
    $previousPackage = Join-Path $fakeArtifacts 'MSFS-Landing-Stats.exe'
    [IO.File]::WriteAllText($previousPackage, 'previous-package-sentinel')
    $env:GOOGLE_OAUTH_CLIENT_ID = 'test-only-id'; $env:GOOGLE_OAUTH_CLIENT_SECRET = 'test-only-secret'
    $global:GoogleOAuthGuardContinueAfterBuild = $true
    Assert-Fails { & (Join-Path $fakeRepository 'build-app.ps1') } 'Google Drive configuration verification failed'
    if ([IO.File]::ReadAllText($previousPackage) -cne 'previous-package-sentinel') { throw 'Metadata failure destroyed the previous package.' }
    Write-Host 'PASS post-build runtime metadata rejection preserves previous package before signing'

    # Also exercise acceptance through the actual packaging caller: a shadowed
    # LASTEXITCODE can otherwise make all rejection tests pass but reject valid
    # metadata. Stop at the next fixture-only allowlist boundary, before signing.
    Copy-Item -LiteralPath $assemblyPath -Destination (Join-Path $fakeOutput 'LandingStats.Delivery.dll') -Force
    $fakeProtocol = Join-Path $fakeRepository 'src\LandingStats.UpdateProtocol'
    New-Item -ItemType Directory -Path $fakeProtocol -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fakeProtocol 'RuntimePayload.cs'), 'test-only-next-boundary')
    Assert-Fails { & (Join-Path $fakeRepository 'build-app.ps1') } 'Runtime payload allowlist is invalid'
    if (Test-Path -LiteralPath $previousPackage) { throw 'Configured Delivery metadata did not reach package creation.' }
    Write-Host 'PASS configured Delivery metadata proceeds through the real packaging caller'
} finally {
    $env:GOOGLE_OAUTH_CLIENT_ID = $savedId; $env:GOOGLE_OAUTH_CLIENT_SECRET = $savedSecret
    $env:TEMP = $savedTemp; $env:TMP = $savedTmp
    Remove-Variable -Name GoogleOAuthGuardArguments, GoogleOAuthGuardContinueAfterBuild -Scope Global -ErrorAction SilentlyContinue
}
