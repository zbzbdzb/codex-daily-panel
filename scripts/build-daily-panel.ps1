param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('../artifacts/CodexDaily-' + (Get-Date -Format yyyyMMdd) + '-win-x64'))
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw 'Choose a new output directory; existing deliveries are never overwritten.' }
$taskArchive = $taskOutput.TrimEnd([IO.Path]::DirectorySeparatorChar) + '.zip'
$taskVerification = $taskOutput.TrimEnd([IO.Path]::DirectorySeparatorChar) + '.verification'
if (Test-Path -LiteralPath $taskArchive) { throw 'ZIP already exists; choose a new output directory.' }
if (Test-Path -LiteralPath $taskVerification) { throw 'Verification directory already exists; choose a new output directory.' }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.buildhome'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $taskRoot
try {
    [void](New-Item -ItemType Directory -Path $taskVerification)
    & dotnet run --project tests/CodexUsage.Daily.Tests -c Release | Tee-Object -FilePath (Join-Path $taskVerification 'aggregation-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Daily aggregation tests failed.' }
    & dotnet run --project tests/CodexUsage.DailyMonitor.Tests -c Release | Tee-Object -FilePath (Join-Path $taskVerification 'monitor-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Daily monitor tests failed.' }
    & dotnet run --project tests/CodexUsage.DailyFormatting.Tests -c Release | Tee-Object -FilePath (Join-Path $taskVerification 'formatting-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Daily total token tests failed.' }
    & dotnet run --project tests/CodexUsage.DailyPopup.Tests -c Release | Tee-Object -FilePath (Join-Path $taskVerification 'popup-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Daily tray popup tests failed.' }
    & dotnet publish src/CodexUsage.DailyPanel/CodexUsage.DailyPanel.csproj -c Release -r win-x64 --self-contained false -o $taskOutput --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Daily panel publish failed.' }
    & dotnet run --project tests/CodexUsage.Daily.GuiHarness -c Release "-p:ProductDirectory=$taskOutput" -- (Join-Path $taskVerification 'gui')
    if ($LASTEXITCODE -ne 0) { throw 'Published daily panel GUI checks failed.' }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/daily-panel-usage.md') -Destination (Join-Path $taskOutput '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination $taskOutput
    Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination $taskOutput -Recurse
    $taskPayload = @(Get-ChildItem -LiteralPath $taskOutput -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskOutput,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    [ordered]@{product='CodexDaily';version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskOutput 'CodexDaily.dll')).ProductVersion;builtAt=[DateTimeOffset]::Now;payload=$taskPayload} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskVerification 'payload.json') -Encoding utf8
    Compress-Archive -Path (Join-Path $taskOutput '*') -DestinationPath $taskArchive
    Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256 | Select-Object Path,Hash
} finally { Pop-Location }
