#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$OutputFile,
    [string[]]$ForbiddenRoots=@(),
    [string[]]$ManagedAssemblies=@('CodexDaily.dll','CodexUsage.Core.dll','CodexUsage.DailyCore.dll')
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildSupport.ps1')
$payload=[IO.Path]::GetFullPath($PayloadDirectory)
$output=[IO.Path]::GetFullPath($OutputFile)
Assert-PhysicalBuildDirectory $payload
Assert-PhysicalBuildDirectory (Split-Path -Parent $output)
if(Test-Path -LiteralPath $output){throw 'Privacy verification requires a new evidence file.'}
if($output.StartsWith($payload.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Private verification evidence must be outside the distributable payload.'}
Add-Type -AssemblyName System.Reflection.Metadata
$checks=[Collections.Generic.List[object]]::new()
$violations=[Collections.Generic.List[object]]::new()
$vendor=[Collections.Generic.List[object]]::new()
$report=[ordered]@{schemaVersion=1;status='running';payload=$payload;checkedAt=[DateTimeOffset]::UtcNow.ToString('o');managedAssemblies=$ManagedAssemblies;checks=@();violations=@();vendorMetadata=@();error=$null}
function Test-MappedPath([string]$Value){
    $normalized=$Value.Replace('\','/')
    return $normalized.StartsWith('/_/codexdaily/',[StringComparison]::Ordinal)-and$normalized-notmatch '(^|/)\.\.(/|$)'
}
function Add-Violation([string]$File,[string]$Kind,[string]$Value){$violations.Add([ordered]@{file=$File;kind=$Kind;value=$Value})}
function Test-PdbReader($Reader,[string]$File,[string]$Kind){
    $documents=@($Reader.Documents|ForEach-Object {$document=$Reader.GetDocument($_);$Reader.GetString($document.Name)})
    foreach($document in $documents){if(-not(Test-MappedPath $document)){Add-Violation $File $Kind $document}}
    $checks.Add([ordered]@{file=$File;kind=$Kind;documentCount=$documents.Count;documents=$documents})
}
function Test-PrivateText([string]$Text,[string]$File,[string]$Kind){
    # Normalize slash spelling, including JSON-escaped backslashes, before matching.
    $normalized=$Text.Replace('\','/') -replace '/{2,}','/'
    foreach($root in $ForbiddenRoots){
        if([string]::IsNullOrWhiteSpace($root)){continue}
        $needle=([IO.Path]::GetFullPath($root).Replace('\','/').TrimEnd('/'))
        if($normalized.IndexOf($needle,[StringComparison]::OrdinalIgnoreCase)-ge0){Add-Violation $File $Kind 'contains a forbidden local build/source root'}
    }
    if($normalized-match '(?i)[a-z]:/Users/[^/\s"<>]+'){Add-Violation $File $Kind 'contains a Windows user-profile path'}
}
try {
    foreach($item in Get-ChildItem -LiteralPath $payload -Recurse -Force){
        if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw 'Linked payload members are not accepted by the privacy gate.'}
    }
    if(-not$ManagedAssemblies.Count){throw 'At least one managed product assembly is required.'}
    foreach($name in $ManagedAssemblies){
        if([IO.Path]::GetFileName($name)-cne$name-or[IO.Path]::GetExtension($name)-cne'.dll'){throw 'Managed assembly names must be simple DLL file names.'}
        $file=Join-Path $payload $name
        if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing product assembly: $name"}
        $stream=[IO.File]::OpenRead($file);$pe=$null
        try {
            $pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
            if(-not$pe.HasMetadata){throw "Product assembly is not managed: $name"}
            $entries=@($pe.ReadDebugDirectory())
            foreach($entry in $entries){
                if($entry.Type-eq[System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView){
                    $view=$pe.ReadCodeViewDebugDirectoryData($entry)
                    $checks.Add([ordered]@{file=$name;kind='managed-codeview';path=$view.Path})
                    if(-not(Test-MappedPath $view.Path)){Add-Violation $name 'managed-codeview' $view.Path}
                } elseif($entry.Type-eq[System.Reflection.PortableExecutable.DebugDirectoryEntryType]::EmbeddedPortablePdb){
                    $embedded=$pe.ReadEmbeddedPortablePdbDebugDirectoryData($entry)
                    try{$reader=[System.Reflection.Metadata.MetadataReaderProvider].GetMethod('GetMetadataReader').Invoke($embedded,@([System.Reflection.Metadata.MetadataReaderOptions]::Default,$null));Test-PdbReader $reader $name 'embedded-pdb-document'}finally{$embedded.Dispose()}
                }
            }
            $checks.Add([ordered]@{file=$name;kind='product-identity';sha256=(Get-FileHash -LiteralPath $file).Hash;debugEntries=$entries.Count})
        }finally{if($pe){$pe.Dispose()};$stream.Dispose()}
        $bytes=[IO.File]::ReadAllBytes($file)
        Test-PrivateText ([Text.Encoding]::UTF8.GetString($bytes)) $name 'managed-literal-utf8'
        Test-PrivateText ([Text.Encoding]::Unicode.GetString($bytes)) $name 'managed-literal-utf16'
    }
    foreach($file in Get-ChildItem -LiteralPath $payload -Filter '*.pdb' -File -Recurse){
        $stream=[IO.File]::OpenRead($file.FullName);$provider=$null
        try {
            $provider=[System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
            $reader=[System.Reflection.Metadata.MetadataReaderProvider].GetMethod('GetMetadataReader').Invoke($provider,@([System.Reflection.Metadata.MetadataReaderOptions]::Default,$null))
            Test-PdbReader $reader ([IO.Path]::GetRelativePath($payload,$file.FullName)) 'portable-pdb-document'
        }finally{if($provider){$provider.Dispose()};$stream.Dispose()}
    }
    # The apphost is Microsoft-built native code. Its known vendor build path is
    # disclosed separately, never confused with mapped product-source metadata.
    foreach($file in Get-ChildItem -LiteralPath $payload -Filter '*.exe' -File){
        $stream=[IO.File]::OpenRead($file.FullName);$pe=$null
        try {
            $pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
            foreach($entry in $pe.ReadDebugDirectory()){
                if($entry.Type-ne[System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView){continue}
                $view=$pe.ReadCodeViewDebugDirectoryData($entry)
                if(Test-MappedPath $view.Path){continue}
                $normalized=$view.Path.Replace('\','/')
                if(-not$pe.HasMetadata-and$normalized-match '^D:/a/_work/[^\r\n]+/(apphost|createdump)\.pdb$'){$vendor.Add([ordered]@{file=$file.Name;kind='Microsoft-native-build-path';path=$view.Path;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash})}
                else{Add-Violation $file.Name 'unexpected-executable-codeview' $view.Path}
            }
        }finally{if($pe){$pe.Dispose()};$stream.Dispose()}
    }
    $textExtensions=@('.md','.json','.txt','.xml','.config','.yml','.yaml')
    foreach($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object {$_.Extension-in$textExtensions}){
        if($file.Length-gt16MB){throw "Public text exceeds verification bound: $($file.Name)"}
        $relative=[IO.Path]::GetRelativePath($payload,$file.FullName)
        Test-PrivateText ([IO.File]::ReadAllText($file.FullName)) $relative 'public-text'
        $checks.Add([ordered]@{file=$relative;kind='public-text';sha256=(Get-FileHash -LiteralPath $file.FullName).Hash})
    }
    if($violations.Count){throw "Artifact privacy verification failed: $($violations.Count) violation(s)."}
    $report.status='passed'
}catch{$report.status='failed';$report.error=$_.Exception.Message;throw}
finally {
    $report.checks=@($checks);$report.violations=@($violations);$report.vendorMetadata=@($vendor)
    $report|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $output -Encoding utf8NoBOM
}
Write-Host "Artifact privacy verification passed ($($checks.Count) checks; $($vendor.Count) separately classified vendor metadata records)."
