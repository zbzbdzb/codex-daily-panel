#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotNetExecutable = 'dotnet',
    [string]$LegacyDotNetExecutable = 'dotnet',
    [string]$RuntimeFeed,
    [switch]$Package,
    [switch]$SkipTests
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BuildSupport.ps1')

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory, $repository)
Assert-PhysicalBuildDirectory $repository
Assert-PhysicalBuildDirectory $output
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; existing evidence and deliveries are never overwritten.' }
if ($Package -and $SkipTests) { throw 'Packaging requires all build-time test gates. Remove -SkipTests.' }
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$status = & $git -C $repository status --porcelain=v1 --untracked-files=all
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Git status.' }
if ($status) { throw 'A clean committed source tree is required. Preserve/commit pending work before building a candidate.' }
$revision = (& $git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'Cannot resolve source commit.' }
$tree = (& $git -C $repository rev-parse 'HEAD^{tree}').Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source tree.' }
$treeEntries = @(& $git -C $repository ls-tree -r --full-tree $revision)
if ($LASTEXITCODE -ne 0) { throw 'Cannot list committed inputs.' }
if ($treeEntries | Where-Object { $_ -match '^(120000|160000) ' }) { throw 'Linked files and submodules require separate provenance and are not supported by this build.' }
[void][IO.Directory]::CreateDirectory($output)
$commands = Join-Path $output 'commands'; [void][IO.Directory]::CreateDirectory($commands)
$state = $null
$record = [ordered]@{
    schemaVersion = 2; status = 'running'; startedAt = [DateTimeOffset]::Now.ToString('o'); completedAt = $null
    sourceCommit = $revision; sourceTree = $tree; sourceCleanAtStart = $true; sourceCleanAtEnd = $null
    sdkExpected = '10.0.402'; runtimeExpected = '10.0.13'; sdkActual = $null
    targetFramework = 'net10.0-windows'; runtimeIdentifier = $(if ($Package) { 'win-x64' } else { $null })
    selfContained = [bool]$Package; testsExecuted = -not [bool]$SkipTests
    sourceInputs = @(); compilerInputs = @(); productFiles = @(); package = $null; error = $null
}
try {
    $state = New-BuildEnvironment -Directory (Join-Path $output 'environment') -DotNetExecutable $DotNetExecutable
    $record.environment = $state.Values
    $record.dotnetExecutable = Get-BuildFileEvidence $state.Executable
    $record.compiler = Get-BuildFileEvidence (Join-Path (Split-Path -Parent $state.Executable) 'sdk/10.0.402/Roslyn/bincore/csc.dll')
    $source = Join-Path $output 'source'
    $archive = Join-Path $output 'source.zip'
    $null = Invoke-BuildCommand $git @('-C',$repository,'archive','--format=zip',"--output=$archive",$revision) $repository 'source-archive' $commands
    Expand-Archive -LiteralPath $archive -DestinationPath $source
    $record.sourceArchive = Get-BuildFileEvidence $archive $output
    $record.sourceInputs = @(Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName | ForEach-Object { Get-BuildFileEvidence $_.FullName $source })
    $null = Invoke-BuildCommand $state.Executable @('--version') $source 'sdk-version' $commands
    $record.sdkActual = (Get-Content -LiteralPath (Join-Path $commands 'sdk-version.stdout.log') -Raw).Trim()
    if ($record.sdkActual -cne $record.sdkExpected) { throw "Expected SDK $($record.sdkExpected), got $($record.sdkActual)." }
    $null = Invoke-BuildCommand $state.Executable @('--info') $source 'sdk-info' $commands
    $null = Invoke-BuildCommand $state.Executable @('--list-runtimes') $source 'runtimes' $commands
    $runtimeText = Get-Content -LiteralPath (Join-Path $commands 'runtimes.stdout.log') -Raw
    if ($runtimeText -notmatch '(?m)^Microsoft.NETCore.App 10\.0\.13 ' -or $runtimeText -notmatch '(?m)^Microsoft.WindowsDesktop.App 10\.0\.13 ') { throw 'Expected .NET and WindowsDesktop 10.0.13 runtime in selected SDK installation.' }
    if (-not $SkipTests) {
        $pwsh = (Get-Process -Id $PID).Path
        $null = Invoke-BuildCommand $pwsh @('-NoProfile','-File',(Join-Path $source 'scripts/verify-core-build.ps1'),'-OutputDirectory',(Join-Path $output 'historical-core'),'-DotNetExecutable',$LegacyDotNetExecutable) $source 'historical-core' $commands
        $legacy = Get-Content -LiteralPath (Join-Path $output 'historical-core/result.json') -Raw | ConvertFrom-Json
        if ($legacy.status -cne 'passed') { throw 'Historical SDK9 Core reproduction failed.' }
    }
    $capture = Join-Path $output 'capture.targets'
    @'
<Project>
 <Target Name="CodexCaptureCompilation" BeforeTargets="CoreCompile">
  <MakeDir Directories="$(CodexEvidenceDirectory)/$(MSBuildProjectName)" />
  <WriteLinesToFile File="$(CodexEvidenceDirectory)/$(MSBuildProjectName)/sources.txt" Lines="@(Compile->'%(FullPath)')" Overwrite="true" />
  <WriteLinesToFile File="$(CodexEvidenceDirectory)/$(MSBuildProjectName)/references.txt" Lines="@(ReferencePath->'%(FullPath)')" Overwrite="true" />
  <WriteLinesToFile File="$(CodexEvidenceDirectory)/$(MSBuildProjectName)/project.txt" Lines="$(MSBuildProjectFullPath);$(TargetFramework);$(NETCoreSdkVersion);$(RuntimeFrameworkVersion);$(AssemblyName);$(InformationalVersion)" Overwrite="true" />
 </Target>
</Project>
'@ | Set-Content -LiteralPath $capture -Encoding utf8NoBOM
    $artifacts = Join-Path $output 'build'
    $compile = Join-Path $output 'compile-inputs'
    $payload = Join-Path $output 'payload'
    $common = @('--configuration','Release','--artifacts-path',$artifacts,'--nologo','--verbosity','minimal','-p:CodexTargetFramework=net10.0',"-p:CodexSourceRevision=$revision", "-p:CustomAfterMicrosoftCommonTargets=$capture", "-p:CodexEvidenceDirectory=$compile", "-p:PathMap=$output=/_/codexdaily")
    $restore = @('--configfile',(Join-Path $source 'NuGet.Config'))
    if ($RuntimeFeed) {
        $feed = [IO.Path]::GetFullPath($RuntimeFeed, $repository)
        Assert-PhysicalBuildDirectory $feed
        if (-not (Test-Path -LiteralPath $feed -PathType Container)) { throw 'RuntimeFeed must be an existing physical local package directory.' }
        $pinnedPackages = Get-Content -LiteralPath (Join-Path $source 'scripts/runtime-packages.json') -Raw | ConvertFrom-Json
        foreach ($runtimePackage in $pinnedPackages) {
            $packagePath = Join-Path $feed $runtimePackage.file
            $actualSha512 = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($packagePath)))
            if ($actualSha512 -cne $runtimePackage.sha512Base64) { throw "Microsoft runtime package SHA512 mismatch: $($runtimePackage.file)" }
            foreach ($notice in $runtimePackage.notices) {
                $actualNotice = Get-BuildFileEvidence (Join-Path $source $notice.path)
                if ($actualNotice.sha256 -cne $notice.sha256 -or $actualNotice.bytes -ne $notice.bytes) { throw "Runtime redistribution notice mismatch: $($notice.path)" }
            }
        }
        $restore += @('--source',$feed)
        $record.runtimeFeedFiles = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File | Sort-Object Name | ForEach-Object { Get-BuildFileEvidence $_.FullName $feed })
    }
    $publish = @('publish',(Join-Path $source 'src/CodexUsage.DailyPanel/CodexUsage.DailyPanel.csproj')) + $common + $restore + @('--output',$payload,'--self-contained',$([bool]$Package).ToString().ToLowerInvariant(),'-p:DisableTransitiveFrameworkReferenceDownloads=true',"-bl:$(Join-Path $commands 'product.binlog')")
    if ($Package) { $publish += @('--runtime','win-x64','-p:RuntimeFrameworkVersion=10.0.13','-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:PublishReadyToRun=false') }
    $null = Invoke-BuildCommand $state.Executable $publish $source 'product-publish' $commands
    # Later test restores share the artifacts tree and can replace project.assets.
    # Preserve and check the actual publish closure before running those builds.
    $publishAssets = Join-Path $output 'publish-assets'
    [void][IO.Directory]::CreateDirectory($publishAssets)
    $record.publishRestoreInputs = @()
    foreach ($productProject in @('CodexUsage.Core','CodexUsage.DailyCore','CodexUsage.DailyPanel')) {
        $assetsPath = Join-Path $artifacts "obj/$productProject/project.assets.json"
        $savedAssets = Join-Path $publishAssets "$productProject.project.assets.json"
        Copy-Item -LiteralPath $assetsPath -Destination $savedAssets
        $assetsData = Get-Content -LiteralPath $savedAssets -Raw | ConvertFrom-Json
        foreach ($framework in $assetsData.project.frameworks.PSObject.Properties) {
            $frameworkNames = @($framework.Value.frameworkReferences.PSObject.Properties.Name | Sort-Object)
            $downloads = @()
            if ($framework.Value.PSObject.Properties['downloadDependencies']) { $downloads = @($framework.Value.downloadDependencies) }
            $expectedFrameworks = @('Microsoft.NETCore.App')
            if ($productProject -ceq 'CodexUsage.DailyPanel') { $expectedFrameworks += 'Microsoft.WindowsDesktop.App' }
            if (($frameworkNames -join '|') -cne (($expectedFrameworks | Sort-Object) -join '|')) { throw "Unexpected product framework closure: $productProject" }
            if ($Package) {
                $expectedDownloads = @($expectedFrameworks | ForEach-Object { "$_" + '.Runtime.win-x64' } | Sort-Object)
                if ((@($downloads.name | Sort-Object) -join '|') -cne ($expectedDownloads -join '|') -or ($downloads | Where-Object { $_.version -cne '[10.0.13, 10.0.13]' })) { throw "Unexpected product runtime downloads: $productProject" }
            }
            $record.publishRestoreInputs += [ordered]@{project=$productProject;framework=$framework.Name;file=(Get-BuildFileEvidence $savedAssets $output);frameworkReferences=$frameworkNames;downloadDependencies=$downloads}
        }
    }
    $record.productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'CodexDaily.dll')).ProductVersion
    if ($record.productVersion -cne "0.2.0+$revision") { throw "Product version does not identify the exported source commit: $($record.productVersion)" }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'CodexDaily.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.tfm -cne 'net10.0') { throw 'Product runtime configuration is not .NET 10.' }
    if ($Package) {
        $includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
        foreach ($name in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
            if (-not ($includedFrameworks | Where-Object { $_.name -ceq $name -and $_.version -ceq '10.0.13' })) { throw "Self-contained runtime configuration does not include $name 10.0.13." }
        }
    }
    $record.compilerInputs = @(Get-ChildItem -LiteralPath $compile -Filter sources.txt -File -Recurse | ForEach-Object {
        $project = $_.Directory.Name
        foreach ($inputPath in Get-Content -LiteralPath $_.FullName) { [ordered]@{project=$project; file=(Get-BuildFileEvidence $inputPath $source)} }
    })
    $references = @(Get-ChildItem -LiteralPath $compile -Filter references.txt -File -Recurse | ForEach-Object { Get-Content -LiteralPath $_.FullName })
    if ($references | Where-Object { $_ -match '[/\\]frozen-core[/\\]' }) { throw 'Product compiler unexpectedly referenced the historical frozen DLL.' }
    $record.referenceInputs = @($references | Sort-Object -Unique | ForEach-Object { Get-BuildFileEvidence $_ })
    if (-not $SkipTests) {
        $projects = @(Get-ChildItem -LiteralPath (Join-Path $source 'tests') -Filter '*.Tests.csproj' -File -Recurse | Sort-Object FullName)
        foreach ($project in $projects) {
            $testLabel = $project.BaseName
            $testCommon = @($common | Where-Object { $_ -notlike '-p:CodexEvidenceDirectory=*' }) + @("-p:CodexEvidenceDirectory=$(Join-Path $output "compile-tests/$testLabel")")
            $buildArgs = @('build',$project.FullName) + $testCommon + $restore + @("-bl:$(Join-Path $commands "$testLabel.binlog")")
            $null = Invoke-BuildCommand $state.Executable $buildArgs $source "$testLabel-build" $commands
            $dll = Join-Path $artifacts "bin/$testLabel/release/$testLabel.dll"
            if (-not (Test-Path -LiteralPath $dll)) { throw "Missing built test DLL: $dll" }
            $testArguments = @($dll)
            if ($testLabel -ceq 'CodexUsage.DailySettings.Tests') {
                $testArguments += @('--test-root',(Join-Path $output 'settings-test-fixtures'))
            }
            $null = Invoke-BuildCommand $state.Executable $testArguments $source "$testLabel-run" $commands
        }
        $harness = Join-Path $source 'tests/CodexUsage.Daily.GuiHarness/CodexUsage.Daily.GuiHarness.csproj'
        $harnessCommon = @($common | Where-Object { $_ -notlike '-p:CodexEvidenceDirectory=*' }) + @("-p:CodexEvidenceDirectory=$(Join-Path $output 'compile-tests/gui-harness')")
        $null = Invoke-BuildCommand $state.Executable (@('build',$harness) + $harnessCommon + $restore + @("-p:ProductDirectory=$payload")) $source 'gui-harness-build' $commands
        $harnessDll = Join-Path $artifacts 'bin/CodexUsage.Daily.GuiHarness/release/CodexUsage.Daily.GuiHarness.dll'
        $null = Invoke-BuildCommand $state.Executable @($harnessDll,(Join-Path $output 'gui-harness')) $source 'gui-harness-run' $commands
        $null = Invoke-BuildCommand (Get-Process -Id $PID).Path @('-NoProfile','-File',(Join-Path $source 'scripts/test-artifact-privacy.ps1'),'-OutputDirectory',(Join-Path $output 'privacy-selftests'),'-DotNetExecutable',$state.Executable) $source 'privacy-selftests' $commands
    }
    Copy-Item -LiteralPath (Join-Path $source 'docs/daily-panel-usage.md') -Destination (Join-Path $payload '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $source 'THIRD_PARTY_NOTICES.md') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $source 'licenses') -Destination $payload -Recurse
    $publicProvenance = [ordered]@{ product='CodexDaily'; version='0.2.0'; sourceCommit=$revision; sourceTree=$tree; sdk=$record.sdkActual; runtime=$record.runtimeExpected; targetFramework=$record.targetFramework; runtimeIdentifier=$record.runtimeIdentifier; selfContained=$record.selfContained; compiledSources=@($record.compilerInputs | Where-Object { $_.project -in @('CodexUsage.Core','CodexUsage.DailyCore','CodexUsage.DailyPanel') } | ForEach-Object { $_.file } | Where-Object { $_.path -notlike '../*' }); verificationScope='Synthetic build-time checks only; ordinary EXE, UI and sustained acceptance are separate gates.' }
    $publicProvenance | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $payload 'BUILD-PROVENANCE.json') -Encoding utf8NoBOM
    $privacyReport = Join-Path $output 'artifact-privacy.json'
    $privacyRunner = Join-Path $output 'invoke-artifact-privacy.ps1'
    # Preserve the array argument across the native PowerShell boundary without string interpolation by the child shell.
    $privacyLiterals = @((Join-Path $source 'scripts/verify-artifact-privacy.ps1'),$payload,$privacyReport,$repository,$output) | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }
    @(
        '$ErrorActionPreference = ''Stop'''
        "& $($privacyLiterals[0]) -PayloadDirectory $($privacyLiterals[1]) -OutputFile $($privacyLiterals[2]) -ForbiddenRoots @($($privacyLiterals[3]),$($privacyLiterals[4]))"
    ) | Set-Content -LiteralPath $privacyRunner -Encoding utf8NoBOM
    $null = Invoke-BuildCommand (Get-Process -Id $PID).Path @('-NoProfile','-File',$privacyRunner) $source 'artifact-privacy' $commands
    $privacyResult = Get-Content -LiteralPath $privacyReport -Raw | ConvertFrom-Json
    if ($privacyResult.status -cne 'passed') { throw 'Artifact privacy verification did not pass; no package will be created.' }
    $record.artifactPrivacy = Get-BuildFileEvidence $privacyReport $output
    $record.productFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object { Get-BuildFileEvidence $_.FullName $payload })
    $record.productFiles | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'payload-manifest.json') -Encoding utf8NoBOM
    if ($Package) {
        foreach ($required in @('CodexDaily.exe','CodexDaily.dll','CodexUsage.Core.dll','CodexUsage.DailyCore.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','PresentationFramework.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw "Self-contained payload missing $required" }
        }
        $zip = Join-Path $output 'CodexDaily-v0.2.0-win-x64.zip'
        Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
        $record.package = Get-BuildFileEvidence $zip $output
        "$($record.package.sha256)  $($record.package.path)" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
        $null = Invoke-BuildCommand (Get-Process -Id $PID).Path @('-NoProfile','-File',(Join-Path $source 'scripts/verify-package.ps1'),'-Archive',$zip,'-Manifest',(Join-Path $output 'payload-manifest.json'),'-OutputDirectory',(Join-Path $output 'package-verification')) $source 'verify-package' $commands
    }
    $endStatus = & $git -C $repository status --porcelain=v1 --untracked-files=all
    if ($LASTEXITCODE -ne 0) { throw 'Cannot recheck Git status.' }
    $endRevision = (& $git -C $repository rev-parse HEAD).Trim()
    $record.sourceCleanAtEnd = -not [bool]$endStatus -and $endRevision -ceq $revision
    if (-not $record.sourceCleanAtEnd) { throw 'Source working tree or HEAD changed during the candidate build; exported inputs are preserved but candidate is not accepted.' }
    $record.status = 'passed'
} catch {
    $record.status = 'failed'; $record.error = $_.Exception.Message
    throw
} finally {
    $record.completedAt = [DateTimeOffset]::Now.ToString('o')
    try { $record | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $output 'build-result.json') -Encoding utf8NoBOM }
    finally { if ($null -ne $state) { Restore-BuildEnvironment $state } }
}
Write-Host "Build verified. Evidence: $(Join-Path $output 'build-result.json')"
