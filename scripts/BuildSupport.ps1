# Shared build helpers. Dot-source only; caller owns its try/finally.
Set-StrictMode -Version Latest

function Get-BuildFileEvidence {
    param([Parameter(Mandatory)][string]$Path, [string]$RelativeTo)
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer) { throw "Expected a file: $Path" }
    [ordered]@{
        path = $(if ($RelativeTo) { [IO.Path]::GetRelativePath($RelativeTo, $item.FullName).Replace('\', '/') } else { $item.FullName })
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}

function New-BuildEnvironment {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$DotNetExecutable)
    $dotnet = (Get-Command $DotNetExecutable -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $root = Split-Path -Parent $dotnet
    $values = [ordered]@{
        DOTNET_ROOT = $root; DOTNET_ROOT_X64 = $root; DOTNET_MULTILEVEL_LOOKUP = '0'
        DOTNET_CLI_HOME = Join-Path $Directory 'cli-home'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
        DOTNET_NOLOGO = '1'; MSBUILDDISABLENODEREUSE = '1'
        TEMP = Join-Path $Directory 'temp'; TMP = Join-Path $Directory 'temp'
        NUGET_PACKAGES = Join-Path $Directory 'packages'
        NUGET_HTTP_CACHE_PATH = Join-Path $Directory 'http-cache'
        NUGET_PLUGINS_CACHE_PATH = Join-Path $Directory 'plugin-cache'
        APPDATA = Join-Path $Directory 'appdata'; LOCALAPPDATA = Join-Path $Directory 'localappdata'
        USERPROFILE = Join-Path $Directory 'profile'; HOME = Join-Path $Directory 'profile'
        CODEX_HOME = Join-Path $Directory 'codex-home'
        XDG_CONFIG_HOME = Join-Path $Directory 'xdg-config'; XDG_DATA_HOME = Join-Path $Directory 'xdg-data'
    }
    $previous = [ordered]@{}
    foreach ($key in $values.Keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
    foreach ($key in @('DOTNET_CLI_HOME','TEMP','NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','NUGET_PLUGINS_CACHE_PATH','APPDATA','LOCALAPPDATA','USERPROFILE','CODEX_HOME','XDG_CONFIG_HOME','XDG_DATA_HOME')) {
        [void][IO.Directory]::CreateDirectory($values[$key])
    }
    foreach ($key in $values.Keys) { [Environment]::SetEnvironmentVariable($key, $values[$key], 'Process') }
    [pscustomobject]@{ Previous = $previous; Values = $values; Executable = $dotnet }
}

function Restore-BuildEnvironment {
    param([Parameter(Mandatory)]$State)
    foreach ($key in $State.Previous.Keys) {
        if ($null -eq $State.Previous[$key]) { Remove-Item -LiteralPath ('Env:' + $key) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($key, $State.Previous[$key], 'Process') }
    }
}

function Invoke-BuildCommand {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$EvidenceDirectory)
    $record = [ordered]@{ executable = $Executable; arguments = $Arguments; workingDirectory = $Directory
        startedAt = [DateTimeOffset]::Now.ToString('o'); completedAt = $null; exitCode = $null
        stdout = "$Label.stdout.log"; stderr = "$Label.stderr.log" }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Executable; $psi.WorkingDirectory = $Directory
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $psi.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $psi
    try {
        if (-not $process.Start()) { throw "Failed to start $Executable" }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $EvidenceDirectory $record.stdout), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $EvidenceDirectory $record.stderr), $stderr.GetAwaiter().GetResult())
        $record.exitCode = $process.ExitCode
    } finally {
        $record.completedAt = [DateTimeOffset]::Now.ToString('o')
        $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory "$Label.command.json") -Encoding utf8NoBOM
        $process.Dispose()
    }
    if ($record.exitCode -ne 0) { throw "Command $Label failed with exit code $($record.exitCode); see saved stdout/stderr." }
    return $record
}

function Assert-PhysicalBuildDirectory {
    param([Parameter(Mandatory)][string]$Directory)
    $current = [IO.Path]::GetFullPath($Directory)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked build paths are not allowed: $current" }
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
