# Shared by the independent package and user-installation gates. No build or download runs here.
function Invoke-NativeConsumer {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogPrefix,
        [switch]$SelfContained,
        [int]$TimeoutSeconds = 90
    )
    $resolved = (Resolve-Path -LiteralPath $Executable).Path
    $start = [Diagnostics.ProcessStartInfo]::new($resolved)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = Split-Path -Parent $resolved
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment.Clear()
    foreach ($name in @('SystemRoot', 'WINDIR', 'TEMP', 'TMP', 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'ProgramData', 'ProgramFiles', 'ProgramFiles(x86)', 'CommonProgramFiles')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $start.Environment[$name] = $value }
    }
    $start.Environment['PATH'] = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    if ($SelfContained) {
        $start.Environment['DOTNET_ROOT'] = Join-Path $start.WorkingDirectory 'uninstalled-dotnet-runtime'
        $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    }
    else {
        $start.Environment['DOTNET_ROOT'] = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
        $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath "$LogPrefix.stdout.log" -Encoding utf8
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath "$LogPrefix.stderr.log" -Encoding utf8
        if ($timedOut) { throw "Consumer exceeded $TimeoutSeconds seconds; its owned process tree was stopped." }
        return [ordered]@{ exitCode = $process.ExitCode; executable = $resolved; path = $start.Environment['PATH']; selfContained = [bool]$SelfContained; processId = $process.Id }
    }
    finally { $process.Dispose() }
}
