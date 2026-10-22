#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $taskName = 'core-rebuild-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
    $OutputDirectory = Join-Path 'artifacts' $taskName
}
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory, $taskRepository)
if (Test-Path -LiteralPath $taskOutput) {
    throw 'Choose a new output directory; existing outputs are never overwritten or deleted.'
}
[void](New-Item -ItemType Directory -Path $taskOutput)

$taskEnvironment = [ordered]@{
    DOTNET_CLI_HOME = Join-Path $taskOutput '.buildhome'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
}
$taskPreviousEnvironment = [ordered]@{}
foreach ($taskKey in $taskEnvironment.Keys) {
    $taskPreviousEnvironment[$taskKey] = [Environment]::GetEnvironmentVariable($taskKey, 'Process')
}

function Get-FileEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $taskFile = Get-Item -LiteralPath $Path
    if ($taskFile.PSIsContainer) { throw "Expected a file: $Path" }
    return [ordered]@{
        path = $taskFile.FullName
        length = $taskFile.Length
        sha256 = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
    }
}

function Test-FileBytesEqual {
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )

    $taskLeftStream = [IO.File]::OpenRead($Left)
    try {
        $taskRightStream = [IO.File]::OpenRead($Right)
        try {
            if ($taskLeftStream.Length -ne $taskRightStream.Length) { return $false }
            $taskLeftBuffer = [byte[]]::new(65536)
            $taskRightBuffer = [byte[]]::new(65536)
            while (($taskRead = $taskLeftStream.Read($taskLeftBuffer, 0, $taskLeftBuffer.Length)) -gt 0) {
                $taskRightRead = 0
                while ($taskRightRead -lt $taskRead) {
                    $taskChunk = $taskRightStream.Read($taskRightBuffer, $taskRightRead, $taskRead - $taskRightRead)
                    if ($taskChunk -eq 0) { return $false }
                    $taskRightRead += $taskChunk
                }
                for ($taskIndex = 0; $taskIndex -lt $taskRead; $taskIndex++) {
                    if ($taskLeftBuffer[$taskIndex] -ne $taskRightBuffer[$taskIndex]) { return $false }
                }
            }
            return $taskRightStream.ReadByte() -eq -1
        } finally {
            $taskRightStream.Dispose()
        }
    } finally {
        $taskLeftStream.Dispose()
    }
}

$taskExpectedSdk = '9.0.318'
$taskExpectedHash = '6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE'
$taskProject = Join-Path $taskRepository 'src/CodexUsage.Core/CodexUsage.Core.csproj'
$taskNuGetConfig = Join-Path $taskRepository 'NuGet.Config'
$taskFrozen = Join-Path $taskRepository 'frozen-core/CodexUsage.Core.dll'
# Keep this explicit: no session, log, authentication or other user-data discovery.
$taskSourceNames = @(
    'AccountQuota.cs'
    'CliThreadUsage.cs'
    'EventParser.cs'
    'IncrementalLogReader.cs'
    'LocalStateStore.cs'
    'LogMonitor.cs'
    'Models.cs'
    'OfficialAccountRpc.cs'
    'UsageEngine.cs'
)
$taskResult = [ordered]@{
    schemaVersion = 1
    status = 'running'
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    completedAtUtc = $null
    repository = $taskRepository
    outputDirectory = $taskOutput
    expectedSdk = $taskExpectedSdk
    sdk = $null
    sdkVersionLog = 'dotnet-version.txt'
    dotnetInfoLog = 'dotnet-info.txt'
    osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    expectedFrozenSha256 = $taskExpectedHash
    frozen = $null
    sourceFiles = @()
    buildInputs = @()
    builds = @()
    comparison = [ordered]@{
        firstSecondBytesEqual = $null
        firstFrozenBytesEqual = $null
        secondFrozenBytesEqual = $null
        allSha256MatchExpected = $null
    }
    error = $null
}
$taskLocationPushed = $false
try {
    foreach ($taskKey in $taskEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($taskKey, $taskEnvironment[$taskKey], 'Process')
    }
    [void](New-Item -ItemType Directory -Path $taskEnvironment.DOTNET_CLI_HOME)
    Push-Location -LiteralPath $taskRepository
    $taskLocationPushed = $true

    & dotnet --version *> (Join-Path $taskOutput $taskResult.sdkVersionLog)
    if ($LASTEXITCODE -ne 0) { throw "dotnet --version failed with exit code $LASTEXITCODE." }
    $taskResult.sdk = (Get-Content -LiteralPath (Join-Path $taskOutput $taskResult.sdkVersionLog) -Raw).Trim()
    if ($taskResult.sdk -cne $taskExpectedSdk) {
        throw "Expected SDK $taskExpectedSdk; dotnet --version returned '$($taskResult.sdk)'."
    }
    & dotnet --info *> (Join-Path $taskOutput $taskResult.dotnetInfoLog)
    if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE." }

    $taskResult.frozen = Get-FileEvidence -Path $taskFrozen
    if ($taskResult.frozen.sha256 -cne $taskExpectedHash) {
        throw 'The frozen Core SHA-256 does not match the pinned expected value.'
    }
    foreach ($taskSourceName in $taskSourceNames) {
        $taskResult.sourceFiles += Get-FileEvidence -Path (Join-Path $taskRepository "src/CodexUsage.Core/$taskSourceName")
    }
    foreach ($taskInput in @($taskProject, (Join-Path $taskRepository 'Directory.Build.props'),
            (Join-Path $taskRepository 'global.json'), $taskNuGetConfig)) {
        $taskResult.buildInputs += Get-FileEvidence -Path $taskInput
    }

    foreach ($taskPass in @('first', 'second')) {
        $taskArtifacts = Join-Path $taskOutput $taskPass
        $taskBuildLog = "$taskPass-build.txt"
        $taskBuildArguments = @('build', $taskProject, '--configuration', 'Release',
            '--artifacts-path', $taskArtifacts, '--configfile', $taskNuGetConfig,
            '--nologo', '--verbosity', 'minimal')
        & dotnet @taskBuildArguments *> (Join-Path $taskOutput $taskBuildLog)
        $taskBuildExitCode = $LASTEXITCODE
        $taskBuildResult = [ordered]@{
            pass = $taskPass
            command = 'dotnet'
            arguments = $taskBuildArguments
            log = $taskBuildLog
            exitCode = $taskBuildExitCode
            dll = $null
        }
        $taskResult.builds += $taskBuildResult
        if ($taskBuildExitCode -ne 0) {
            throw "The $taskPass Core build failed with exit code $taskBuildExitCode. See $taskBuildLog."
        }
        $taskBuildResult.dll = Get-FileEvidence -Path (Join-Path $taskArtifacts 'bin/CodexUsage.Core/release/CodexUsage.Core.dll')
    }

    $taskFirst = $taskResult.builds[0].dll
    $taskSecond = $taskResult.builds[1].dll
    $taskResult.comparison.firstSecondBytesEqual = Test-FileBytesEqual -Left $taskFirst.path -Right $taskSecond.path
    $taskResult.comparison.firstFrozenBytesEqual = Test-FileBytesEqual -Left $taskFirst.path -Right $taskFrozen
    $taskResult.comparison.secondFrozenBytesEqual = Test-FileBytesEqual -Left $taskSecond.path -Right $taskFrozen
    $taskResult.comparison.allSha256MatchExpected = $taskFirst.sha256 -ceq $taskExpectedHash -and $taskSecond.sha256 -ceq $taskExpectedHash
    if (-not ($taskResult.comparison.firstSecondBytesEqual -and $taskResult.comparison.firstFrozenBytesEqual -and
            $taskResult.comparison.secondFrozenBytesEqual -and $taskResult.comparison.allSha256MatchExpected)) {
        throw 'The rebuilt Core DLLs do not both match each other and the pinned frozen DLL byte for byte.'
    }
    $taskResult.status = 'passed'
} catch {
    $taskResult.status = 'failed'
    $taskResult.error = $_.Exception.Message
    throw
} finally {
    try {
        $taskResult.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        $taskResult | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskOutput 'result.json') -Encoding utf8
    } finally {
        try {
            if ($taskLocationPushed) { Pop-Location }
        } finally {
            foreach ($taskKey in $taskPreviousEnvironment.Keys) {
                if ($null -eq $taskPreviousEnvironment[$taskKey]) {
                    Remove-Item -LiteralPath ('Env:' + $taskKey) -ErrorAction SilentlyContinue
                } else {
                    [Environment]::SetEnvironmentVariable($taskKey, $taskPreviousEnvironment[$taskKey], 'Process')
                }
            }
        }
    }
}

Write-Host "Core rebuild verified: both Release DLLs match frozen-core byte for byte (SHA-256 $taskExpectedHash)."
Write-Host "Evidence: $(Join-Path $taskOutput 'result.json')"
