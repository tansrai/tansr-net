#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$CliRoot,
    [string]$RecoveryCliRoot,
    [string]$SessionCliRoot,
    [string]$PreviousPackageDirectory,
    [ValidatePattern('^[0-9A-Za-z.+-]+$')][string]$PreviousVersion,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'This entry requires Windows x64 PowerShell 7.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Use a new evidence directory; previous results are never overwritten.' }
if ($target.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep CI evidence outside the source checkout.' }
foreach ($name in @('CliRoot', 'RecoveryCliRoot', 'SessionCliRoot', 'PreviousPackageDirectory')) {
    $value = Get-Variable -Name $name -ValueOnly
    if ($value) { Set-Variable -Name $name -Value (Resolve-Path -LiteralPath $value).Path }
}
$globalJson = Get-Content -LiteralPath (Join-Path $repository 'global.json') -Raw | ConvertFrom-Json
[xml]$properties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^[0-9A-Za-z.+-]+$') { throw 'Missing explicit package version.' }
if ($PreviousVersion -and -not $PreviousPackageDirectory) { throw 'PreviousVersion requires PreviousPackageDirectory.' }
$scope = [ordered]@{
    cliRoot = $CliRoot; recoveryCliRoot = $RecoveryCliRoot; sessionCliRoot = if ($SessionCliRoot) { $SessionCliRoot } else { $CliRoot }
    previousPackageDirectory = $PreviousPackageDirectory
    previousVersion = if ($PreviousVersion) { $PreviousVersion } elseif ($PreviousPackageDirectory) { $version } else { $null }
    excludedWindowsMethods = @(); notRun = @(
        @{ name = 'ServeSourceIntegration / five clients / real UI / Electron performance'; reason = 'Run their existing explicit source, runtime and device entry points separately. This script does not provision those environments.' }
        @{ name = 'Standard-user OS, signing and NuGet publication'; reason = 'This script records its actual runner identity and neither creates users nor signs or publishes.' }
    )
}
function Exclude-WindowsMethod([string]$Name, [string]$Reason) {
    $scope.excludedWindowsMethods += @{ name = "Tansr.Sdk.Windows.Tests.$Name"; reason = $Reason; status = 'not-run' }
}
if (-not $CliRoot) {
    Exclude-WindowsMethod 'Storage.OriginalNodeStorageTests.OriginalNodeAndCSharpReopenTheSameOwnedDatabase' 'CliRoot: pinned original CLI source and installed dependencies are not supplied.'
    Exclude-WindowsMethod 'Storage.SqliteArchiveSyncTests.OriginalNodeAndCSharpConsumeEachOthersCacheCheckpointsAndReceipts' 'CliRoot: original Node cache/checkpoint implementation is not supplied.'
    $scope.notRun += @{ name = 'Live upstream contract/mapping and original Node session comparison'; reason = 'No CliRoot; repository snapshots are still checked, not reported as live source comparison.' }
}
if (-not $RecoveryCliRoot) {
    Exclude-WindowsMethod 'Storage.OriginalNodeRecoveryTests.ActualNodeAndCSharpReadEachOthersPreparedAndCompletedRecoveryLedger' 'RecoveryCliRoot: frozen recovery implementation and dependencies are not supplied.'
    Exclude-WindowsMethod 'Storage.OriginalNodeMemoryPublicationTests.OriginalNodeAndWindowsExchangeStagingCasAndImmutableReceipts' 'RecoveryCliRoot: original Node memory publication implementation is not supplied.'
}
Exclude-WindowsMethod 'Execution.WindowsExecutionBenchmarkTests.OriginalElectronAndServeShareSameFlushedCommandAndQpcCollector' 'Requires its existing Electron/Serve/QPC benchmark runner; not an ordinary unit test.'
if (-not $PreviousPackageDirectory) { $scope.notRun += @{ name = 'Previous-package public API and upgrade/rollback'; reason = 'No approved previous package input. Never compare a new package to itself as compatibility evidence.' } }
$filter = ($scope.excludedWindowsMethods | ForEach-Object { 'FullyQualifiedName!=' + $_.name }) -join '&'
$steps = @('toolchain', 'node-version', 'contract', 'parity', 'parity-tests', 'api-routes-check', 'api-routes-tests', 'ci-receipt-tests')
if ($CliRoot) { $steps += @('upstream-contract', 'upstream-parity', 'node-session-compatibility') }
$steps += @('native-mcp-publish', 'locked-restore', 'release-build', 'core-tests', 'windows-tests', 'sandbox-golden', 'framework-mcp', 'framework-session', 'format', 'pack-core', 'pack-windows', 'package-notices', 'package-consumers', 'package-examples')
if ($PreviousPackageDirectory) { $steps += 'public-api' }
[IO.Directory]::CreateDirectory((Join-Path $target 'logs')) | Out-Null
$manifest = [ordered]@{
    format = 'tansr-net-ci-v1'; outcome = 'incomplete'; startedAt = [DateTime]::UtcNow.ToString('o')
    source = @{ root = $repository; commit = (& git -C $repository rev-parse HEAD | Out-String).Trim(); status = @(& git -C $repository status --porcelain) }
    sdk = $globalJson.sdk; nodeWorkflowVersion = '22.22.1'; packageVersion = $version; scope = $scope
    steps = @($steps | ForEach-Object { [ordered]@{ name = $_; status = 'not-started' } }); tests = @(); packages = @()
    windowsFilter = $filter; planOnly = [bool]$PlanOnly
    boundary = 'Only the recorded standalone/explicitly supplied-source gates. Not all original NET-A01 through NET-A24, signing, publication or external integration.'
}
$manifestPath = Join-Path $target 'manifest.json'
function Save-Manifest { $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM }
function Hash-File([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Invoke-CiStep([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $step = @($manifest.steps | Where-Object { $_.name -eq $Name })
    if ($step.Count -ne 1) { throw "Unknown CI step: $Name" }
    $step = $step[0]
    $step.status = 'running'; $step.executable = $Executable; $step.arguments = $Arguments
    $step.startedAt = [DateTime]::UtcNow.ToString('o'); $step.log = Join-Path $target "logs/$Name.log"
    Save-Manifest
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath $step.log
    $step.exitCode = $LASTEXITCODE
    $step.finishedAt = [DateTime]::UtcNow.ToString('o')
    $step.status = if ($step.exitCode -eq 0) { 'passed' } else { 'failed' }
    Save-Manifest
    if ($step.exitCode -ne 0) { throw "CI step $Name failed ($($step.exitCode)); later steps were not executed." }
}
function Read-TestResult([string]$Name, [string]$Path, [switch]$RecordOnly) {
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $summaries = @($trx.SelectNodes("/*[local-name()='TestRun']/*[local-name()='ResultSummary']"))
    if ($summaries.Count -ne 1) { throw "Missing or ambiguous test summary: $Name" }
    $counters = @($summaries[0].SelectNodes("*[local-name()='Counters']"))
    if ($counters.Count -ne 1) { throw "Missing or ambiguous test counters: $Name" }
    $results = @($trx.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
    $record = @{ name = $Name; path = $Path; sha256 = Hash-File $Path; summaryOutcome = $summaries[0].GetAttribute('outcome'); resultCount = $results.Count
        nonPassed = @($results | Where-Object { $_.outcome -cne 'Passed' } | ForEach-Object { @{ name = $_.testName; outcome = $_.outcome } }) }
    foreach ($key in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
        $value = 0
        if (-not [int]::TryParse($counters[0].GetAttribute($key), [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
            throw "Invalid test counter ${key}: $Name"
        }
        $record[$key] = $value
    }
    $actualPassed = @($results | Where-Object { $_.outcome -ceq 'Passed' }).Count
    $actualFailed = @($results | Where-Object { $_.outcome -ceq 'Failed' }).Count
    $actualNotExecuted = @($results | Where-Object { $_.outcome -ceq 'NotExecuted' }).Count
    $record.resultsConsistent = $record.total -eq $results.Count -and $record.passed -eq $actualPassed -and
        $record.failed -eq $actualFailed -and $record.notExecuted -eq $actualNotExecuted -and
        $record.executed -eq ($results.Count - $actualNotExecuted)
    $manifest.tests += $record
    Save-Manifest
    if (-not $RecordOnly -and (-not $record.resultsConsistent -or $record.total -eq 0 -or
        $record.executed -ne $record.total -or $record.passed -ne $record.total -or $record.nonPassed.Count -or
        @('Completed', 'Passed') -cnotcontains $record.summaryOutcome)) {
        throw "$Name has inconsistent, unexecuted or failed results; do not count them as passed."
    }
}
function Export-Evidence {
    # Explicit receipt/log/package allowlist. Never upload private NuGet caches or complete self-contained payloads.
    $selected = @($manifestPath)
    foreach ($relative in @('source-files.json', 'source-locks.json', 'package-notices.json')) {
        $file = Join-Path $target $relative
        if (Test-Path -LiteralPath $file -PathType Leaf) { $selected += $file }
    }
    foreach ($relative in @('logs', 'results')) {
        $directory = Join-Path $target $relative
        if (Test-Path -LiteralPath $directory) { $selected += @(Get-ChildItem -LiteralPath $directory -File -Recurse | Where-Object { $_.Extension -in @('.log', '.trx') } | ForEach-Object FullName) }
    }
    foreach ($relative in @('consumers', 'consumers/installation', 'examples', 'api', 'framework-mcp', 'framework-session')) {
        $directory = Join-Path $target $relative
        if (Test-Path -LiteralPath $directory) { $selected += @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Extension -in @('.json', '.log') } | ForEach-Object FullName) }
    }
    $packageDirectory = Join-Path $target 'packages'
    if (Test-Path -LiteralPath $packageDirectory) { $selected += @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*.nupkg' | ForEach-Object FullName) }
    $evidence = Join-Path $target 'evidence'
    $records = @()
    foreach ($file in @($selected | Sort-Object -Unique)) {
        $relative = [IO.Path]::GetRelativePath($target, $file)
        $copy = Join-Path $evidence $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
        Copy-Item -LiteralPath $file -Destination $copy
        $records += @{ path = $relative.Replace('\', '/'); bytes = (Get-Item -LiteralPath $copy).Length; sha256 = Hash-File $copy }
    }
    @{ format = 'tansr-net-ci-artifacts-v1'; files = $records } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'artifact-manifest.json') -Encoding utf8NoBOM
}
$savedEnvironment = @{}
$sourceFiles = @()
$locationPushed = $false
$publishAttempted = $false
$defaultRestoreAttempted = $false
try {
    Push-Location -LiteralPath $repository
    $locationPushed = $true
    $files = @(& git -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0 -or -not $files.Count) { throw 'Source inventory failed.' }
    foreach ($relative in $files) {
        $file = Join-Path $repository $relative
        $sourceFiles += @{ path = $relative; bytes = (Get-Item -LiteralPath $file).Length; sha256 = Hash-File $file }
    }
    $sourceFiles | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $target 'source-files.json') -Encoding utf8NoBOM
    $locks = @($sourceFiles | Where-Object { $_.path.EndsWith('packages.lock.json') })
    $locks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $target 'source-locks.json') -Encoding utf8NoBOM
    Save-Manifest
    if ($PlanOnly) { $manifest.outcome = 'planned-not-run' }
    else {
        foreach ($entry in @{ TANSR_TEST_CLI_ROOT = $CliRoot; TANSR_TEST_RECOVERY_CLI_ROOT = $RecoveryCliRoot; TANSR_TEST_MCP_EXE = (Join-Path $target 'mcp-consumer/ConsoleAssistant.exe'); TANSR_ELECTRON_BENCHMARK_ENTRY = $null }.GetEnumerator()) {
            $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
        $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
        Invoke-CiStep 'toolchain' 'dotnet' @('--info')
        Invoke-CiStep 'node-version' 'node' @('--version')
        Invoke-CiStep 'contract' $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'check-contract.ps1'))
        Invoke-CiStep 'parity' 'node' @('scripts/check-parity.mjs')
        Invoke-CiStep 'parity-tests' 'node' @('--test', 'scripts/check-parity.test.mjs')
        Invoke-CiStep 'api-routes-check' 'node' @('scripts/generate-api-routes.mjs', '--check')
        Invoke-CiStep 'api-routes-tests' 'node' @('--test', 'scripts/generate-api-routes.test.mjs')
        Invoke-CiStep 'ci-receipt-tests' $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'test-ci-receipts.ps1'))
        if ($CliRoot) {
            Invoke-CiStep 'upstream-contract' $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'check-contract.ps1'), '-SourceRoot', $CliRoot)
            Invoke-CiStep 'upstream-parity' 'node' @('scripts/check-parity.mjs', '--source-root', $CliRoot)
            $sessionSource = if ($SessionCliRoot) { $SessionCliRoot } else { $CliRoot }
            $loader = [Uri]::new((Join-Path $sessionSource 'node_modules/tsx/dist/loader.mjs')).AbsoluteUri
            Invoke-CiStep 'node-session-compatibility' 'node' @('--import', $loader, 'scripts/check-session-compatibility.mjs', $sessionSource)
        }
        # Original test-windows publication mechanism: per-project obj lock, then one default locked restore.
        $publishAttempted = $true
        Invoke-CiStep 'native-mcp-publish' 'dotnet' @('publish', 'examples/ConsoleAssistant/ConsoleAssistant.csproj', '-c', 'Release', '-f', 'net10.0-windows', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:NuGetLockFilePath=obj/mcp-publish.packages.lock.json', '-o', (Join-Path $target 'mcp-consumer'))
        foreach ($lock in $locks) { if ((Hash-File (Join-Path $repository $lock.path)) -ne $lock.sha256) { throw "Formal lock changed during MCP publish: $($lock.path)" } }
        $defaultRestoreAttempted = $true
        Invoke-CiStep 'locked-restore' 'dotnet' @('restore', 'Tansr.Sdk.slnx', '--locked-mode')
        Invoke-CiStep 'release-build' 'dotnet' @('build', 'Tansr.Sdk.slnx', '-c', 'Release', '--no-restore')
        foreach ($suite in @(
            @{ name = 'core-tests'; project = 'tests/Tansr.Sdk.Tests/Tansr.Sdk.Tests.csproj'; filter = $null },
            @{ name = 'windows-tests'; project = 'tests/Tansr.Sdk.Windows.Tests/Tansr.Sdk.Windows.Tests.csproj'; filter = $filter },
            @{ name = 'sandbox-golden'; project = 'tests/Tansr.Sdk.IntegrationTests/Tansr.Sdk.IntegrationTests.csproj'; filter = 'FullyQualifiedName=Tansr.Sdk.IntegrationTests.ServeExecutionPipelineTests.FrozenSandboxGoldenCasesMatchTheIndependentCodec' }
        )) {
            $results = Join-Path $target ('results/' + $suite.name)
            $arguments = @('test', $suite.project, '-c', 'Release', '--no-build', '--no-restore', '--results-directory', $results, '--logger', ('trx;LogFileName=' + $suite.name + '.trx'))
            if ($suite.filter) { $arguments += @('--filter', $suite.filter) }
            $trx = Join-Path $results ($suite.name + '.trx')
            try {
                Invoke-CiStep $suite.name 'dotnet' $arguments
                Read-TestResult $suite.name $trx
            }
            catch {
                # A nonzero test exit must still retain any real TRX counters, then stop the pipeline.
                if (Test-Path -LiteralPath $trx -PathType Leaf) {
                    if (@($manifest.tests | Where-Object { $_.name -eq $suite.name }).Count -eq 0) {
                        try { Read-TestResult $suite.name $trx -RecordOnly }
                        catch { $manifest.testReceiptFailure = $_.Exception.Message }
                    }
                }
                @($manifest.steps | Where-Object { $_.name -eq $suite.name })[0].status = 'failed'
                Save-Manifest
                throw
            }
        }
        Invoke-CiStep 'framework-mcp' $pwsh @('-NoProfile', '-File', (Join-Path $repository 'tests/Tansr.Sdk.Windows.Tests/Mcp/test-framework-mcp.ps1'), '-LibraryDirectory', (Join-Path $repository 'examples/WinFormsAssistant/bin/Release/net48'), '-OutputDirectory', (Join-Path $target 'framework-mcp'))
        Invoke-CiStep 'framework-session' $pwsh @('-NoProfile', '-File', (Join-Path $repository 'tests/Tansr.Sdk.Tests/Transport/test-framework-session.ps1'), '-LibraryDirectory', (Join-Path $repository 'examples/WinFormsAssistant/bin/Release/net48'), '-OutputDirectory', (Join-Path $target 'framework-session'))
        Invoke-CiStep 'format' 'dotnet' @('format', 'Tansr.Sdk.slnx', '--verify-no-changes', '--no-restore')
        $packages = Join-Path $target 'packages'
        Invoke-CiStep 'pack-core' 'dotnet' @('pack', 'src/Tansr.Sdk/Tansr.Sdk.csproj', '-c', 'Release', '--no-build', '--no-restore', '-o', $packages)
        Invoke-CiStep 'pack-windows' 'dotnet' @('pack', 'src/Tansr.Sdk.Windows/Tansr.Sdk.Windows.csproj', '-c', 'Release', '--no-build', '--no-restore', '-o', $packages)
        $manifest.packages = @(Get-ChildItem -LiteralPath $packages -File -Filter '*.nupkg' | ForEach-Object { @{ path = $_.FullName; bytes = $_.Length; sha256 = Hash-File $_.FullName } })
        if ($manifest.packages.Count -ne 2) { throw 'Exactly two product packages are required.' }
        Invoke-CiStep 'package-notices' $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'audit-packages.ps1'), '-PackageDirectory', $packages, '-OutputFile', (Join-Path $target 'package-notices.json'), '-Version', $version, '-RequireNotices')
        $arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'test-packages.ps1'), '-PackageDirectory', $packages, '-OutputDirectory', (Join-Path $target 'consumers'), '-Version', $version, '-Aot')
        if ($PreviousPackageDirectory) { $arguments += @('-PreviousPackageDirectory', $PreviousPackageDirectory) }
        if ($PreviousVersion) { $arguments += @('-PreviousVersion', $PreviousVersion) }
        Invoke-CiStep 'package-consumers' $pwsh $arguments
        Invoke-CiStep 'package-examples' $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'build-package-examples.ps1'), '-PackageDirectory', $packages, '-OutputDirectory', (Join-Path $target 'examples'), '-Version', $version)
        if ($PreviousPackageDirectory) { Invoke-CiStep 'public-api' $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'check-public-api.ps1'), '-BaselinePackageDirectory', $PreviousPackageDirectory, '-OutputDirectory', (Join-Path $target 'api')) }
        $manifest.outcome = 'passed-with-external-gates-not-run'
    }
}
catch { $manifest.outcome = 'failed'; $manifest.failure = $_.Exception.Message; throw }
finally {
    if ($publishAttempted -and -not $defaultRestoreAttempted) {
        # Match test-windows.ps1: repair default assets once after a failed RID publish; never rerun that failed gate.
        $defaultRestoreAttempted = $true
        $cleanup = [ordered]@{ startedAt = [DateTime]::UtcNow.ToString('o'); log = Join-Path $target 'logs/cleanup-restore-default.log'; command = @('dotnet', 'restore', (Join-Path $repository 'Tansr.Sdk.slnx'), '--locked-mode'); exitCode = $null }
        try {
            & dotnet restore (Join-Path $repository 'Tansr.Sdk.slnx') --locked-mode 2>&1 | Tee-Object -FilePath $cleanup.log
            $cleanup.exitCode = $LASTEXITCODE
        }
        catch { $cleanup.failure = $_.Exception.Message }
        $cleanup.finishedAt = [DateTime]::UtcNow.ToString('o')
        $manifest.cleanupRestore = $cleanup
        # The original exception continues through finally even when cleanup succeeds.
        if ($cleanup.exitCode -ne 0) { $manifest.outcome = 'failed' }
    }
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    if ($locationPushed) { Pop-Location }
    $changed = @($sourceFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repository $_.path)) -or (Hash-File (Join-Path $repository $_.path)) -ne $_.sha256 } | ForEach-Object path)
    $manifest.changedSourceFiles = $changed
    if ($changed.Count) { $manifest.outcome = 'failed'; $manifest.sourceFailure = 'Tracked/input source or formal locks changed during CI.' }
    $manifest.finishedAt = [DateTime]::UtcNow.ToString('o')
    Save-Manifest
    Export-Evidence
    if ($env:GITHUB_STEP_SUMMARY) {
        $summary = @('# Tansr .NET standalone gates', '', ('Outcome: **' + $manifest.outcome + '**'), '', 'This is not the complete NET-A01–NET-A24 acceptance or a publication.', '', '## Windows methods not run', '')
        $summary += @($scope.excludedWindowsMethods | ForEach-Object { '- `' + $_.name + '`: ' + $_.reason })
        $summary += @('', '## Other gates not run', '') + @($scope.notRun | ForEach-Object { '- ' + $_.name + ': ' + $_.reason })
        $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
    if ($changed.Count) { throw 'CI source identity changed; inspect manifest.json.' }
}
