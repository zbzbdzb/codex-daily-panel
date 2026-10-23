#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [string]$DotNetExecutable='dotnet')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildSupport.ps1')
$output=[IO.Path]::GetFullPath($OutputDirectory)
Assert-PhysicalBuildDirectory $output
if (Test-Path -LiteralPath $output) { throw 'Choose a new synthetic script-test directory.' }
[void][IO.Directory]::CreateDirectory($output)
$results=[Collections.Generic.List[object]]::new()
function Assert-BuildTest([string]$Name,[bool]$Condition) {
    $results.Add([ordered]@{name=$Name;passed=$Condition})
    if(-not $Condition){ throw "FAIL $Name" }
    Write-Host "PASS $Name"
}
function Invoke-ExpectedFailure([string]$Label,[scriptblock]$Operation,[string]$Expected) {
    $message=$null
    try { & $Operation } catch { $message=$_.Exception.Message; $_ | Out-String | Set-Content -LiteralPath (Join-Path $output "$Label.expected-failure.log") -Encoding utf8NoBOM }
    Assert-BuildTest $Label ($null -ne $message -and $message.Contains($Expected))
}
try {
    $keys=@('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP','DOTNET_CLI_HOME','DOTNET_GENERATE_ASPNET_CERTIFICATE','TEMP','TMP','NUGET_PACKAGES','APPDATA','LOCALAPPDATA','USERPROFILE','HOME','CODEX_HOME')
    $before=@{};foreach($key in $keys){$before[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
    $state=$null
    try {
        $state=New-BuildEnvironment (Join-Path $output 'environment') $DotNetExecutable
        Assert-BuildTest 'isolated-temp-and-home' ($env:TEMP.StartsWith($output) -and $env:CODEX_HOME.StartsWith($output) -and $env:USERPROFILE.StartsWith($output))
        Assert-BuildTest 'certificate-autogeneration-disabled' ($env:DOTNET_GENERATE_ASPNET_CERTIFICATE -ceq 'false')
        $failureScript=Join-Path $output 'intentional-exit.ps1'
        'Write-Output "synthetic expected error"; [Console]::Error.WriteLine("stderr evidence"); exit 7' | Set-Content -LiteralPath $failureScript -Encoding utf8NoBOM
        Invoke-ExpectedFailure 'nonzero-command-throws' {Invoke-BuildCommand (Get-Process -Id $PID).Path @('-NoProfile','-File',$failureScript) $output 'exit-seven' $output} 'exit code 7'
        $record=Get-Content -LiteralPath (Join-Path $output 'exit-seven.command.json') -Raw | ConvertFrom-Json
        Assert-BuildTest 'actual-exit-code-captured' ($record.exitCode -eq 7)
        Assert-BuildTest 'both-output-streams-captured' ((Get-Content (Join-Path $output 'exit-seven.stdout.log') -Raw).Contains('synthetic expected error') -and (Get-Content (Join-Path $output 'exit-seven.stderr.log') -Raw).Contains('stderr evidence'))
    } finally {if($null -ne $state){Restore-BuildEnvironment $state}}
    foreach($key in $keys){Assert-BuildTest "environment-restored-$key" ([Environment]::GetEnvironmentVariable($key,'Process') -ceq $before[$key])}
    $existing=Join-Path $output 'existing';[void][IO.Directory]::CreateDirectory($existing)
    $sentinel=Join-Path $existing 'sentinel.txt';'preserve these synthetic bytes'|Set-Content -LiteralPath $sentinel -Encoding utf8NoBOM
    $sentinelBefore=(Get-FileHash -LiteralPath $sentinel).Hash
    Invoke-ExpectedFailure 'existing-build-output-refused' { & (Join-Path $PSScriptRoot 'build-daily-panel.ps1') -OutputDirectory $existing } 'existing evidence'
    Assert-BuildTest 'existing-output-unchanged' ((Get-FileHash -LiteralPath $sentinel).Hash -ceq $sentinelBefore)
    Invoke-ExpectedFailure 'cannot-package-with-skipped-tests' { & (Join-Path $PSScriptRoot 'build-daily-panel.ps1') -OutputDirectory (Join-Path $output 'forbidden-package') -Package -SkipTests } 'requires all build-time test gates'
    Assert-BuildTest 'rejected-package-created-nothing' (-not (Test-Path -LiteralPath (Join-Path $output 'forbidden-package')))
    $payload=Join-Path $output 'synthetic-payload';[void][IO.Directory]::CreateDirectory($payload)
    $data=Join-Path $payload 'data.txt';'synthetic payload v1'|Set-Content -LiteralPath $data -Encoding utf8NoBOM
    $manifest=Join-Path $output 'manifest.json'
    @(Get-BuildFileEvidence $data $payload) | ConvertTo-Json -Depth 5 -AsArray | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
    $archive=Join-Path $output 'synthetic.zip';Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -Archive $archive -Manifest $manifest -OutputDirectory (Join-Path $output 'package-valid')
    $valid=Get-Content (Join-Path $output 'package-valid/result.json') -Raw | ConvertFrom-Json
    Assert-BuildTest 'archive-extract-hash-match' ($valid.status -ceq 'passed' -and $valid.files.Count -eq 1)
    'synthetic payload altered'|Set-Content -LiteralPath $data -Encoding utf8NoBOM
    $tampered=Join-Path $output 'tampered.zip';Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $tampered
    Invoke-ExpectedFailure 'tampered-payload-rejected' { & (Join-Path $PSScriptRoot 'verify-package.ps1') -Archive $tampered -Manifest $manifest -OutputDirectory (Join-Path $output 'package-tampered') } 'Payload hash/size mismatch'
    $traversal=Join-Path $output 'traversal.zip'
    $zip=[IO.Compression.ZipFile]::Open($traversal,[IO.Compression.ZipArchiveMode]::Create)
    try {$entry=$zip.CreateEntry('../escape.txt');$writer=[IO.StreamWriter]::new($entry.Open());try{$writer.Write('synthetic traversal')}finally{$writer.Dispose()}}finally{$zip.Dispose()}
    Invoke-ExpectedFailure 'zip-traversal-rejected' { & (Join-Path $PSScriptRoot 'verify-package.ps1') -Archive $traversal -Manifest $manifest -OutputDirectory (Join-Path $output 'package-traversal') } 'Unsafe archive entry'
    Assert-BuildTest 'zip-traversal-never-extracted' (-not (Test-Path -LiteralPath (Join-Path $output 'package-traversal/extracted')))
} finally {
    [ordered]@{assertions=$results.Count;passed=@($results | Where-Object passed).Count;results=@($results)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8NoBOM
}
