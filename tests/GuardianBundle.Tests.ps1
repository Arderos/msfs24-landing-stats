param([Parameter(Mandatory=$true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not ('LandingStats.Packaging.BundlePayload' -as [type])) {
    [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($PackagePath)) | Out-Null
}
Add-Type -AssemblyName System.IO.Compression
$bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($PackagePath))
$stream = [IO.MemoryStream]::new($bytes, $false)
try {
    $bounds = [LandingStats.Packaging.BundlePayload]::Locate($stream)
    $payload = [IO.MemoryStream]::new($bytes, [int]$bounds.Offset, $bounds.Length, $false)
    $archive = [IO.Compression.ZipArchive]::new($payload, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $input = $entry.Open(); $output = [IO.MemoryStream]::new()
            try { $input.CopyTo($output); $entries.Add($entry.FullName, $output.ToArray()) }
            finally { $input.Dispose(); $output.Dispose() }
        }
    } finally { $archive.Dispose(); $payload.Dispose() }
} finally { $stream.Dispose() }
$prefix = [byte[]]::new([int]$bounds.Offset)
[Array]::Copy($bytes, $prefix, $prefix.Length)
# Mutated verifier fixtures are intentionally unsigned, even when testing a
# signed candidate. Remove only the PE security-directory pointer/length.
$pe = [BitConverter]::ToInt32($prefix, 0x3c); $optional = $pe + 24
$directoryOffset = if ([BitConverter]::ToUInt16($prefix, $optional) -eq 0x20b) { 112 } else { 96 }
[Array]::Clear($prefix, $optional + $directoryOffset + 32, 8)
$root = Join-Path ([IO.Path]::GetTempPath()) ('guardian-bundle-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    # Run the actual net48 launcher's extraction/hash check in a private cache.
    # Reflection invokes only these pure file methods, never its application Main.
    $probeSource = Join-Path $root 'CacheProbe.cs'
    $probeExe = Join-Path $root 'CacheProbe.exe'
    $probePayload = Join-Path $root 'payload.zip'
    $originalPayload = [byte[]]::new($bounds.Length)
    [Array]::Copy($bytes, [int]$bounds.Offset, $originalPayload, 0, $originalPayload.Length)
    [IO.File]::WriteAllBytes($probePayload, $originalPayload)
    @'
using System;
using System.IO;
using System.Reflection;
class CacheProbe {
 static int Main(string[] args) {
  var type=Assembly.LoadFile(Path.GetFullPath(args[0])).GetType("LandingStats.App.Launcher.Program",true);
  var flags=BindingFlags.Static|BindingFlags.NonPublic;
  if(type.Assembly.GetType("LandingStats.App.Runtime.GuardianClient")!=null)throw new Exception("Standalone launcher contains the application guardian client");
  var payload=File.ReadAllBytes(args[1]); var directory=args[2];
  var files=type.GetMethod("ValidateArchive",flags).Invoke(null,new object[]{payload});
  type.GetMethod("ExtractArchive",flags).Invoke(null,new object[]{payload,directory});
  var verify=type.GetMethod("RuntimeMatchesPayload",flags);
  if(!(bool)verify.Invoke(null,new object[]{directory,payload,files}))throw new Exception("Extracted runtime differs");
  var path=Path.Combine(directory,"LandingStats.WorkerHost.exe"); var original=File.ReadAllBytes(path);
  var corrupt=(byte[])original.Clone();corrupt[corrupt.Length-1]^=1;File.WriteAllBytes(path,corrupt);
  if((bool)verify.Invoke(null,new object[]{directory,payload,files}))throw new Exception("Same-length corruption accepted");
  File.WriteAllBytes(path,original);File.Delete(Path.Combine(directory,"LandingStats.WorkerHost.exe.config"));
  if((bool)verify.Invoke(null,new object[]{directory,payload,files}))throw new Exception("Deleted helper configuration accepted");
  var cleanup=type.GetMethod("CleanupOldRuntimeDirectories",flags);
  var cleanupRoot=Path.Combine(directory,"cleanup");Directory.CreateDirectory(cleanupRoot);
  var current=Path.Combine(cleanupRoot,"0.9.5-guardian");Directory.CreateDirectory(current);
  var old=Path.Combine(cleanupRoot,"0.9.4");Directory.CreateDirectory(old);
  var config=Path.Combine(old,"app.config");File.WriteAllText(config,"preserve until all files are free");
  var executable=Path.Combine(old,"app.exe");File.WriteAllText(executable,"loaded image fixture");
  using(var held=new FileStream(executable,FileMode.Open,FileAccess.Read,FileShare.Read)) {
   cleanup.Invoke(null,new object[]{cleanupRoot,current});
   if(!File.Exists(config)||!File.Exists(executable))throw new Exception("Cleanup partially deleted an in-use runtime");
  }
  var staging=Path.Combine(cleanupRoot,".staging-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
  File.WriteAllText(Path.Combine(staging,"partial.dll"),"abandoned extraction");Directory.SetLastWriteTimeUtc(staging,DateTime.UtcNow.AddDays(-2));
  var durable=Path.Combine(cleanupRoot,"cloud");Directory.CreateDirectory(durable);File.WriteAllText(Path.Combine(durable,"state.json"),"durable");
  var nested=Path.Combine(cleanupRoot,"0.9.3");Directory.CreateDirectory(Path.Combine(nested,"history"));
  cleanup.Invoke(null,new object[]{cleanupRoot,current});
  if(Directory.Exists(old)||Directory.Exists(staging)||!Directory.Exists(current)||!Directory.Exists(nested)||!File.Exists(Path.Combine(durable,"state.json")))
   throw new Exception("Runtime cleanup state: old="+Directory.Exists(old)+", staging="+Directory.Exists(staging)+", current="+Directory.Exists(current)+", nested="+Directory.Exists(nested)+", durable="+File.Exists(Path.Combine(durable,"state.json")));
  Console.WriteLine("Runtime cache: extraction verified; same-length corruption and deleted helper configuration detected.");return 0;
 }
}
'@ | Set-Content -LiteralPath $probeSource -Encoding UTF8
    $dotnetRoot = Split-Path (Get-Command dotnet).Source
    $sdkVersion = (dotnet --version).Trim()
    $compiler = Join-Path $dotnetRoot "sdk\$sdkVersion\Roslyn\bincore\csc.dll"
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    dotnet $compiler /nologo /nostdlib+ "/reference:$framework\mscorlib.dll" "/reference:$framework\System.dll" "/reference:$framework\System.Core.dll" "/out:$probeExe" $probeSource
    if ($LASTEXITCODE -ne 0) { throw 'Runtime cache verification probe did not compile.' }
    & $probeExe ([IO.Path]::GetFullPath($PackagePath)) $probePayload (Join-Path $root 'cache')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime cache verification failed.' }
    foreach ($case in @('valid','missing-host','missing-host-config','missing-contracts','missing-archive-reader','missing-zstd','unexpected-exe','unexpected-dll','nested-file')) {
        $payload = [IO.MemoryStream]::new()
        $zip = [IO.Compression.ZipArchive]::new($payload, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in $entries.Keys) {
                if (($case -eq 'missing-host' -and $name -eq 'LandingStats.WorkerHost.exe') -or
                    ($case -eq 'missing-host-config' -and $name -eq 'LandingStats.WorkerHost.exe.config') -or
                    ($case -eq 'missing-contracts' -and $name -eq 'LandingStats.Runtime.Contracts.dll') -or
                    ($case -eq 'missing-archive-reader' -and $name -eq 'LandingStats.Archives.dll') -or
                    ($case -eq 'missing-zstd' -and $name -eq 'ZstdSharp.dll')) { continue }
                $entry = $zip.CreateEntry($name); $output = $entry.Open()
                try { $output.Write($entries[$name], 0, $entries[$name].Length) } finally { $output.Dispose() }
            }
            if ($case -in @('unexpected-exe','unexpected-dll','nested-file')) {
                $name = if ($case -eq 'unexpected-exe') { 'unowned.exe' } elseif ($case -eq 'unexpected-dll') { 'unowned.dll' } else { 'nested\LandingStats.WorkerHost.exe' }
                $output = $zip.CreateEntry($name).Open()
                try { $output.WriteByte(1) } finally { $output.Dispose() }
            }
        } finally { $zip.Dispose() }
        $path = Join-Path $root ($case + ' %20.exe'); $output = [IO.File]::Create($path)
        try {
            $output.Write($prefix, 0, $prefix.Length); $payload.Position = 0; $payload.CopyTo($output)
            $length = [BitConverter]::GetBytes([long]$payload.Length); $output.Write($length, 0, $length.Length)
            $marker = [Text.Encoding]::ASCII.GetBytes('MSFSLSABUNDLE1'); $output.Write($marker, 0, $marker.Length)
        } finally { $output.Dispose(); $payload.Dispose() }
        $errorPath = Join-Path $root ($case + '.stderr.txt')
        $process = Start-Process -FilePath $path -ArgumentList '--verify-bundle' -PassThru -WindowStyle Hidden -RedirectStandardError $errorPath
        try {
            if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw "Bundle verifier stalled: $case" }
            if (($case -eq 'valid') -ne ($process.ExitCode -eq 0)) { throw "Bundle verifier accepted/rejected the wrong topology: $case ($($process.ExitCode))" }
            if ($case -ne 'valid') {
                $expectedError = if ($case.StartsWith('missing-')) { 'The application bundle is incomplete.' } else { 'The application bundle contains an unexpected or unsafe entry.' }
                $actualError = [IO.File]::ReadAllText($errorPath).Trim()
                if ($process.ExitCode -ne 1 -or $actualError -cne $expectedError) { throw "Wrong rejection for ${case}: exit=$($process.ExitCode), $actualError" }
            }
        } finally { $process.Dispose() }
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Write-Host 'Guardian bundle: valid topology; missing helper/config/contracts/archive codec and unexpected executables rejected as expected.'
