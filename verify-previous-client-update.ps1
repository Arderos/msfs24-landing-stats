param(
    [Parameter(Mandatory = $true)] [string]$PreviousPackagePath,
    [Parameter(Mandatory = $true)] [Version]$PreviousVersion,
    [Parameter(Mandatory = $true)] [string]$ManifestPath,
    [Parameter(Mandatory = $true)] [string]$SignaturePath,
    [Parameter(Mandatory = $true)] [Version]$ExpectedVersion
)

$ErrorActionPreference = "Stop"

foreach ($path in @($PreviousPackagePath, $ManifestPath, $SignaturePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Previous-client compatibility input is missing: $path"
    }
}

function Normalize-Version([Version]$Version) {
    if ($Version.Build -lt 0) {
        throw "A three-component release version is required."
    }
    return [Version]::new($Version.Major, $Version.Minor, $Version.Build)
}

$previous = Normalize-Version $PreviousVersion
$expected = Normalize-Version $ExpectedVersion
if ($expected -le $previous) {
    throw "Candidate version v$expected must be newer than previous client v$previous."
}

$previousPackage = (Resolve-Path -LiteralPath $PreviousPackagePath).Path
$outerAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($previousPackage).Version
$outerVersion = Normalize-Version $outerAssemblyVersion
if ($outerVersion -ne $previous) {
    throw "Previous package assembly is v$outerVersion, expected v$previous."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "msfs-previous-client-update-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $magic = [Text.Encoding]::ASCII.GetBytes("MSFSLSABUNDLE1")
    $stream = [IO.File]::OpenRead($previousPackage)
    try {
        if ($stream.Length -le $magic.Length + 8) {
            throw "Previous package is not a complete single-file bundle."
        }

        $stream.Position = $stream.Length - $magic.Length
        $actualMagic = [byte[]]::new($magic.Length)
        if ($stream.Read($actualMagic, 0, $actualMagic.Length) -ne $actualMagic.Length -or
            -not [Linq.Enumerable]::SequenceEqual($actualMagic, $magic)) {
            throw "Previous package bundle marker is invalid."
        }

        $stream.Position = $stream.Length - $magic.Length - 8
        $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
        try {
            [long]$payloadLength = $reader.ReadInt64()
        }
        finally {
            $reader.Dispose()
        }

        [long]$payloadOffset = $stream.Length - $magic.Length - 8 - $payloadLength
        if ($payloadLength -le 0 -or $payloadOffset -le 0) {
            throw "Previous package payload bounds are invalid."
        }

        $payloadPath = Join-Path $testRoot "payload.zip"
        $stream.Position = $payloadOffset
        $output = [IO.File]::Create($payloadPath)
        try {
            $buffer = [byte[]]::new(65536)
            [long]$remaining = $payloadLength
            while ($remaining -gt 0) {
                $count = [int][Math]::Min($buffer.Length, $remaining)
                $read = $stream.Read($buffer, 0, $count)
                if ($read -le 0) {
                    throw "Previous package payload ended early."
                }
                $output.Write($buffer, 0, $read)
                $remaining -= $read
            }
        }
        finally {
            $output.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $runtimeRoot = Join-Path $testRoot "runtime"
    New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
    $expectedEntries = @(
        "LandingStats.Core.dll",
        "Microsoft.FlightSimulator.SimConnect.dll",
        "MSFS-Landing-Stats.exe",
        "MSFS-Landing-Stats.exe.config",
        "SimConnect.dll"
    ) | Sort-Object
    $archive = [IO.Compression.ZipFile]::OpenRead($payloadPath)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        if ($entries.Count -ne $expectedEntries.Count -or
            [string]::Join("`n", $entries) -cne [string]::Join("`n", $expectedEntries)) {
            throw "Previous package runtime topology is unexpected."
        }

        foreach ($entry in $archive.Entries) {
            $destination = Join-Path $runtimeRoot $entry.FullName
            $input = $entry.Open()
            $output = [IO.File]::Create($destination)
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $previousApplication = Join-Path $runtimeRoot "MSFS-Landing-Stats.exe"
    $innerAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($previousApplication).Version
    $innerVersion = Normalize-Version $innerAssemblyVersion
    if ($innerVersion -ne $previous) {
        throw "Embedded previous application is v$innerVersion, expected v$previous."
    }

    # Load from bytes so the verification process does not keep the extracted
    # previous runtime locked and the release gate can clean up deterministically.
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($previousApplication))
    $protocolType = $assembly.GetType(
        "LandingStats.UpdateProtocol.ReleaseUpdateProtocol",
        $true)
    $flags = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static
    $channelManifestName = $protocolType.GetField("ChannelManifestName", $flags).GetValue($null)
    $channelSignatureName = $protocolType.GetField("ChannelSignatureName", $flags).GetValue($null)
    if ($channelManifestName -cne "update-channel.txt" -or
        $channelSignatureName -cne "update-channel.sig") {
        throw "Published v$previous client does not use the expected moving channel."
    }

    $manifestBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ManifestPath))
    $signatureText = [Text.Encoding]::ASCII.GetString(
        [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $SignaturePath)))
    $verify = $protocolType.GetMethod("VerifyAndParse", $flags)
    try {
        $parsed = $verify.Invoke($null, @($manifestBytes, $signatureText))
    }
    catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }

    $parsedVersion = Normalize-Version $parsed.Version
    if ($parsedVersion -ne $expected -or
        $parsed.PackageAsset -cne "MSFS-Landing-Stats.exe" -or
        $parsed.UpdaterAsset -cne "MSFS-Landing-Stats.Updater.exe") {
        throw "Published v$previous client parsed an unexpected v$expected update contract."
    }
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Host "Published v$previous client accepts the signed v$expected update channel."
