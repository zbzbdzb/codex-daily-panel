#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BuildSupport.ps1')
$output = [IO.Path]::GetFullPath($OutputDirectory)
Assert-PhysicalBuildDirectory $output
if (Test-Path -LiteralPath $output) { throw 'Package verification requires a new output directory.' }
[void][IO.Directory]::CreateDirectory($output)
$result = [ordered]@{schemaVersion=1;status='running';archive=(Get-BuildFileEvidence $Archive);manifest=(Get-BuildFileEvidence $Manifest);files=@();error=$null}
$zip = $null
try {
    $expected = @(Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json)
    $expectedByPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $expected) {
        $path = $entry.path.Replace('\','/')
        if ([IO.Path]::IsPathRooted($path) -or $path -match '(^|/)\.\.(/|$)|:|^/' -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw "Unsafe manifest entry: $path" }
        if (-not $expectedByPath.TryAdd($path,$entry)) { throw "Duplicate manifest entry: $path" }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
    $observed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        $path = $entry.FullName.Replace('\','/')
        if ([IO.Path]::IsPathRooted($path) -or $path -match '(^|/)\.\.(/|$)|:|^/' -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "Unsafe archive entry: $path" }
        if ($path.EndsWith('/')) { continue }
        if (-not $observed.Add($path) -or -not $expectedByPath.ContainsKey($path)) { throw "Duplicate or unexpected archive payload: $path" }
    }
    if ($observed.Count -ne $expectedByPath.Count) { throw 'Archive payload file count differs from manifest.' }
    $zip.Dispose(); $zip=$null
    $extracted = Join-Path $output 'extracted'
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Archive), $extracted, $false)
    foreach ($path in $expectedByPath.Keys | Sort-Object) {
        $actual = Get-BuildFileEvidence (Join-Path $extracted $path) $extracted
        $wanted = $expectedByPath[$path]
        if ($actual.sha256 -cne $wanted.sha256.ToUpperInvariant() -or $actual.bytes -ne $wanted.bytes) { throw "Payload hash/size mismatch: $path" }
        $result.files += $actual
    }
    $result.status='passed'
} catch {
    $result.status='failed'; $result.error=$_.Exception.Message; throw
} finally {
    if ($null -ne $zip) { $zip.Dispose() }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8NoBOM
}
Write-Host "Package byte verification passed ($($result.files.Count) files)."
