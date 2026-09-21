$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\src\LandingStats.UpdateProtocol\BundlePayload.cs')

function New-Fixture([bool]$Pe64, [int]$Padding = -1) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $stream.SetLength(512)
    $writer.Write([uint16]0x5a4d)
    $stream.Position = 0x3c; $writer.Write([uint32]128)
    $stream.Position = 128; $writer.Write([uint32]0x4550)
    $stream.Position = 134; $writer.Write([uint16]1)
    $stream.Position = 148; $writer.Write([uint16]240)
    $stream.Position = 152; $writer.Write([uint16]$(if ($Pe64) {0x20b} else {0x10b}))
    $directories = if ($Pe64) {112} else {96}
    $stream.Position = 152 + $directories - 4; $writer.Write([uint32]16)
    $stream.Position = 152 + 240 + 16
    $writer.Write([uint32]72); $writer.Write([uint32]440)
    $stream.Position = 512
    $payloadLength = if ($Padding -lt 0) {11} else { (10 - $Padding) % 8 + 8 }
    $writer.Write([byte[]]::new($payloadLength))
    $writer.Write([long]$payloadLength)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('MSFSLSABUNDLE1'))
    if ($Padding -ge 0) {
        $writer.Write([byte[]]::new($Padding))
        [uint32]$certOffset = $stream.Position
        if ($certOffset % 8 -ne 0) { throw 'Bad test alignment' }
        $writer.Write([uint32]16); $writer.Write([uint16]0x200); $writer.Write([uint16]2)
        $writer.Write([long]0)
        $stream.Position = 152 + $directories + 32
        $writer.Write($certOffset); $writer.Write([uint32]16)
    }
    $bytes = $stream.ToArray()
    $writer.Dispose(); $stream.Dispose()
    return ,$bytes
}

function Assert-Rejected([byte[]]$Bytes, [string]$Name) {
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    try {
        $rejected = $false
        try { $null = [LandingStats.Packaging.BundlePayload]::Locate($stream) }
        catch { if ($_.Exception.InnerException -is [IO.InvalidDataException]) { $rejected = $true } else { throw } }
        if (-not $rejected) { throw "Invalid bundle accepted: $Name" }
    } finally { $stream.Dispose() }
}

foreach ($pe64 in @($false, $true)) {
    foreach ($padding in -1..7) {
        $bytes = New-Fixture $pe64 $padding
        $stream = [IO.MemoryStream]::new($bytes, $false)
        try {
            $range = [LandingStats.Packaging.BundlePayload]::Locate($stream)
            if ($range.Offset -ne 512 -or $range.Length -le 0) { throw 'Wrong payload bounds' }
        } finally { $stream.Dispose() }
        Assert-Rejected ($bytes[0..($bytes.Length-2)]) 'truncated'
        $bad = $bytes.Clone(); $bad[0] = 0; Assert-Rejected $bad 'not PE'
        $bad = $bytes.Clone(); [BitConverter]::GetBytes([uint32]0x7fffffff).CopyTo($bad, 0x3c)
        Assert-Rejected $bad 'out of bounds PE'
        $bad = $bytes.Clone(); $bad[512 + $range.Length + 8] = 0
        Assert-Rejected $bad 'bad bundle marker'
        $bad = $bytes.Clone(); [BitConverter]::GetBytes([long]::MaxValue).CopyTo($bad, 512 + $range.Length)
        Assert-Rejected $bad 'payload overflow'
        $bad = $bytes.Clone(); [BitConverter]::GetBytes([long]($range.Length + 1)).CopyTo($bad, 512 + $range.Length)
        Assert-Rejected $bad 'payload overlaps image'
        if ($padding -gt 0) {
            $bad = $bytes.Clone(); $bad[$bytes.Length-17] = 1
            Assert-Rejected $bad 'nonzero signing padding'
        }
        if ($padding -ge 0) {
            $bad = $bytes.Clone(); $bad[$bytes.Length-16] = 32
            Assert-Rejected $bad 'certificate outside file'
            $bad = $bytes.Clone(); $bad[$bytes.Length-10] = 1
            Assert-Rejected $bad 'wrong certificate type'
            # A trailing forged footer must not hide a malformed certificate table.
            $bad = [byte[]]($bytes + [BitConverter]::GetBytes([long]1) + [Text.Encoding]::ASCII.GetBytes('MSFSLSABUNDLE1'))
            Assert-Rejected $bad 'footer after signature'
        }
    }
}
Write-Host 'Bundle parser: PE32/PE32+, unsigned, all eight signing alignments and malformed bounds passed.'
