param([Parameter(Mandatory = $true)][string]$AssemblyPath)

$ErrorActionPreference = 'Stop'
trap { Write-Host $_.Exception.Message; exit 1 }
# Load owned build bytes for metadata inspection without leaving its file locked.
# No configuration values are included in success or failure output.
$bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($AssemblyPath))
$assembly = if ($PSVersionTable.PSEdition -eq 'Desktop') {
    [Reflection.Assembly]::ReflectionOnlyLoad($bytes)
} else {
    [Reflection.Assembly]::Load($bytes)
}
if ($assembly.GetName().Name -cne 'LandingStats.Delivery') {
    throw 'Google Drive configuration must be verified in LandingStats.Delivery.dll.'
}
$attributes = @([Reflection.CustomAttributeData]::GetCustomAttributes($assembly) |
    Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute' })
foreach ($key in @('GoogleOAuthClientId', 'GoogleOAuthClientSecret')) {
    $matches = @($attributes | Where-Object { $_.ConstructorArguments[0].Value -ceq $key })
    if ($matches.Count -ne 1 -or [string]::IsNullOrWhiteSpace($matches[0].ConstructorArguments[1].Value)) {
        throw "LandingStats.Delivery.dll is missing required Google Drive configuration: $key."
    }
}
Write-Host 'Google Drive configuration verified in LandingStats.Delivery.dll.'
exit 0
