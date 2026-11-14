[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Path))
function Require-Range([long]$Offset, [long]$Length) {
    if ($Offset -lt 0 -or $Length -lt 0 -or $Offset -gt $bytes.LongLength - $Length) { throw 'Native renderer has invalid PE bounds.' }
}
function Read-U16([long]$Offset) { Require-Range $Offset 2; [BitConverter]::ToUInt16($bytes, [int]$Offset) }
function Read-U32([long]$Offset) { Require-Range $Offset 4; [BitConverter]::ToUInt32($bytes, [int]$Offset) }
if ((Read-U16 0) -ne 0x5a4d) { throw 'Native renderer is not a PE file.' }
$pe = [long](Read-U32 0x3c)
if ((Read-U32 $pe) -ne 0x4550 -or (Read-U16 ($pe + 4)) -ne 0x8664 -or
    ((Read-U16 ($pe + 22)) -band 0x2000) -eq 0) { throw 'Native renderer must be an AMD64 DLL.' }
$optional = $pe + 24
$optionalSize = Read-U16 ($pe + 20)
if ($optionalSize -lt 240 -or (Read-U16 $optional) -ne 0x20b -or (Read-U32 ($optional + 108)) -lt 15) { throw 'Native renderer must use a complete PE32+ optional header.' }
Require-Range $optional $optionalSize
if ((Read-U32 ($optional + 224)) -ne 0) { throw 'Native renderer unexpectedly contains a CLR header.' }
$sectionCount = Read-U16 ($pe + 6)
$sectionTable = $optional + $optionalSize
Require-Range $sectionTable ($sectionCount * 40)
function Resolve-Rva([long]$Rva) {
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $section = $sectionTable + $i * 40
        $start = [long](Read-U32 ($section + 12))
        $length = [long](Read-U32 ($section + 16))
        if ($Rva -ge $start -and $Rva - $start -lt $length) {
            $offset = [long](Read-U32 ($section + 20)) + $Rva - $start
            Require-Range $offset 1
            return $offset
        }
    }
    throw 'Native renderer has an unmapped export RVA.'
}
$exports = Resolve-Rva (Read-U32 ($optional + 112))
Require-Range $exports 40
$count = Read-U32 ($exports + 24)
$expected = @('Flip_Create', 'Flip_Destroy', 'Flip_Resize', 'Flip_UploadLayer', 'Flip_RemoveLayer',
    'Flip_Present', 'Flip_LastError', 'Flip_GetDiagnostics', 'Flip_GetPathInfo', 'Flip_SetCursor') | Sort-Object
if ($count -ne $expected.Count) { throw 'Native renderer export count does not match its managed ABI.' }
$names = Resolve-Rva (Read-U32 ($exports + 32))
Require-Range $names ($count * 4)
$actual = @(for ($i = 0; $i -lt $count; $i++) {
    $start = Resolve-Rva (Read-U32 ($names + $i * 4))
    $end = $start
    while ($end -lt $bytes.LongLength -and $bytes[$end] -ne 0) { $end++ }
    Require-Range $end 1
    [Text.Encoding]::ASCII.GetString($bytes, [int]$start, [int]($end - $start))
}) | Sort-Object
if ([string]::Join(',', $actual) -cne [string]::Join(',', $expected)) { throw 'Native renderer exports do not match its managed ABI.' }
$hash = [Security.Cryptography.SHA256]::Create()
try { $digest = [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-', '') }
finally { $hash.Dispose() }
[pscustomobject]@{ Path = [IO.Path]::GetFullPath($Path); Machine = 'AMD64'; Exports = $actual.Count; SHA256 = $digest }
