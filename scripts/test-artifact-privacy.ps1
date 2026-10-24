#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$DotNetExecutable='dotnet')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildSupport.ps1')
$output=[IO.Path]::GetFullPath($OutputDirectory);Assert-PhysicalBuildDirectory $output
if(Test-Path -LiteralPath $output){throw 'Use a new privacy selftest directory.'}
[void][IO.Directory]::CreateDirectory($output)
$results=[Collections.Generic.List[object]]::new();$state=$null
function Assert-Test([string]$Name,[bool]$Passed){$results.Add([ordered]@{name=$Name;passed=$Passed});if(-not$Passed){throw "FAIL $Name"};Write-Host "PASS $Name"}
function Quote-PS([string]$Value){return "'"+$Value.Replace("'","''")+"'"}
function Gate([string]$Label,[string]$Payload,[bool]$ShouldPass){
    $case=Join-Path $output $Label;[void][IO.Directory]::CreateDirectory($case)
    $runner=Join-Path $case 'run.ps1';$report=Join-Path $case 'privacy.json'
    ('$ErrorActionPreference=''Stop'''+[Environment]::NewLine+'& '+(Quote-PS (Join-Path $PSScriptRoot 'verify-artifact-privacy.ps1'))+' -PayloadDirectory '+(Quote-PS $Payload)+' -OutputFile '+(Quote-PS $report)+' -ForbiddenRoots @('+(Quote-PS $output)+') -ManagedAssemblies @(''Probe.dll'')')|Set-Content -LiteralPath $runner -Encoding utf8NoBOM
    $failed=$false
    try{$null=Invoke-BuildCommand (Get-Process -Id $PID).Path @('-NoProfile','-File',$runner) $case 'gate' $case}catch{$failed=$true}
    $command=Get-Content -LiteralPath (Join-Path $case 'gate.command.json') -Raw|ConvertFrom-Json
    Assert-Test "$Label-exit" (($ShouldPass-and$command.exitCode-eq0-and-not$failed)-or(-not$ShouldPass-and$command.exitCode-ne0-and$failed))
    $result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
    Assert-Test "$Label-result" ($result.status-ceq$(if($ShouldPass){'passed'}else{'failed'}))
    return $result
}
function Clone-Probe([string]$Name,[string]$Source,[switch]$WithoutPdb){
    $destination=Join-Path $output $Name;[void][IO.Directory]::CreateDirectory($destination)
    foreach($file in Get-ChildItem -LiteralPath $Source -File){if($WithoutPdb-and$file.Extension-eq'.pdb'){continue};[IO.File]::Copy($file.FullName,(Join-Path $destination $file.Name),$false)}
    return $destination
}
try {
    $state=New-BuildEnvironment (Join-Path $output 'environment') $DotNetExecutable
    $source=Join-Path $output 'synthetic-source';[void][IO.Directory]::CreateDirectory($source)
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><DebugType>portable</DebugType><Deterministic>true</Deterministic><AssemblyName>Probe</AssemblyName></PropertyGroup></Project>'|Set-Content -LiteralPath (Join-Path $source 'Probe.csproj') -Encoding utf8NoBOM
    'public static class Probe { public static int Add(int a,int b)=>a+b; }'|Set-Content -LiteralPath (Join-Path $source 'Probe.cs') -Encoding utf8NoBOM
    '{"sdk":{"version":"10.0.402","rollForward":"disable"}}'|Set-Content -LiteralPath (Join-Path $source 'global.json') -Encoding utf8NoBOM
    '<configuration><packageSources><clear /></packageSources></configuration>'|Set-Content -LiteralPath (Join-Path $source 'NuGet.Config') -Encoding utf8NoBOM
    foreach($mode in @('leaking','mapped')){
        $artifacts=Join-Path $output "$mode-build"
        $arguments=@('build','Probe.csproj','-c','Release','--artifacts-path',$artifacts,'-p:UseSharedCompilation=false')
        if($mode-eq'mapped'){$arguments+="-p:PathMap=$output=/_/codexdaily"}
        $null=Invoke-BuildCommand $state.Executable $arguments $source "$mode-build" $output
    }
    $leaking=Join-Path $output 'leaking-build/bin/Probe/release';$mapped=Join-Path $output 'mapped-build/bin/Probe/release'
    $red=Gate 'reject-unmapped' $leaking $false
    Assert-Test 'unmapped-pdb-and-codeview-both-rejected' (@($red.violations|Where-Object kind -eq 'portable-pdb-document').Count-gt0-and@($red.violations|Where-Object kind -eq 'managed-codeview').Count-gt0)
    $green=Gate 'accept-mapped' $mapped $true
    Assert-Test 'mapped-documents-inspected' (@($green.checks|Where-Object kind -eq 'portable-pdb-document').Count-eq1)
    $peOnly=Clone-Probe 'leaking-pe-only' $leaking -WithoutPdb
    $redPe=Gate 'reject-pe-without-pdb' $peOnly $false
    Assert-Test 'deleting-pdb-does-not-hide-codeview' (@($redPe.violations|Where-Object kind -eq 'managed-codeview').Count-gt0)
    $text=Clone-Probe 'mapped-with-text-leak' $mapped
    @{buildPath=(Join-Path $output 'synthetic-private/source.cs')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $text 'public.json') -Encoding utf8NoBOM
    $redText=Gate 'reject-json-escaped-path' $text $false
    Assert-Test 'escaped-public-root-rejected' (@($redText.violations|Where-Object kind -eq 'public-text').Count-gt0)
    $profile=Clone-Probe 'mapped-with-profile-leak' $mapped
    'Synthetic example of private metadata: C:\Users\synthetic-person\build\source.cs'|Set-Content -LiteralPath (Join-Path $profile 'usage.md') -Encoding utf8NoBOM
    $null=Gate 'reject-profile-path' $profile $false
    $corrupt=Clone-Probe 'mapped-with-corrupt-pdb' $mapped
    [IO.File]::WriteAllText((Join-Path $corrupt 'Probe.pdb'),'synthetic invalid PDB bytes')
    $null=Gate 'reject-corrupt-pdb' $corrupt $false
    $missing=Join-Path $output 'missing-managed';[void][IO.Directory]::CreateDirectory($missing)
    $null=Gate 'reject-missing-managed' $missing $false
}finally{
    [ordered]@{kind='compiled synthetic artifact privacy gates; no product execution';assertions=$results.Count;passed=@($results|Where-Object passed).Count;results=@($results)}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8NoBOM
    if($state){Restore-BuildEnvironment $state}
}
