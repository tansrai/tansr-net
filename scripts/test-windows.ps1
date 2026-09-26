param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$CliRoot
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '此验收入口需要 Windows。' }
$sourceRoot = Split-Path -Parent $PSScriptRoot
$cliSource = (Resolve-Path -LiteralPath $CliRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $cliSource 'node_modules/tsx/dist/loader.mjs') -PathType Leaf)) {
    throw 'CLI 源码目录缺少已安装的 tsx；先按其锁文件准备依赖。原 Node 双向消费不能跳过。'
}
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw '验收输出目录已存在，请指定新的具名目录；脚本不会覆盖旧候选。' }
$null = Get-Command dotnet -ErrorAction Stop
$null = Get-Command node -ErrorAction Stop
$null = Get-Command git -ErrorAction Stop

$consoleProject = Join-Path $sourceRoot 'examples/ConsoleAssistant/ConsoleAssistant.csproj'
$solution = Join-Path $sourceRoot 'Tansr.Sdk.slnx'
$testProject = Join-Path $sourceRoot 'tests/Tansr.Sdk.Windows.Tests/Tansr.Sdk.Windows.Tests.csproj'
# NuGet.targets 使用 NuGetLockFilePath。PackagesLockFile 不是此 SDK 的恢复参数，不能用它隔离 RID 锁文件。
$lockProperty = '-p:NuGetLockFilePath=obj/mcp-publish.packages.lock.json'
$resolvedLock = & dotnet msbuild $consoleProject '-nologo' $lockProperty '-getProperty:NuGetLockFilePath'
if ($LASTEXITCODE -ne 0 -or ($resolvedLock | Out-String).Trim().Replace('\', '/') -ne 'obj/mcp-publish.packages.lock.json') {
    throw '无法确认发布恢复使用独立的 NuGetLockFilePath；未启动发布。'
}
$lockFiles = @(& git -C $sourceRoot ls-files -- '*packages.lock.json')
if ($LASTEXITCODE -ne 0 -or $lockFiles.Count -eq 0) { throw '无法读取正式依赖锁文件清单。' }
$lockHashes = @{}
foreach ($relative in $lockFiles) { $lockHashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $sourceRoot $relative) -Algorithm SHA256).Hash }
function Assert-FormalLocksUnchanged {
    foreach ($relative in $lockHashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $relative) -Algorithm SHA256).Hash -ne $lockHashes[$relative]) {
            throw "正式依赖锁文件被改变：$relative。停止验收，不能把 RID 发布锁替换为产品默认锁。"
        }
    }
}

New-Item -ItemType Directory -Path $target | Out-Null
$candidateDirectory = Join-Path $target 'mcp-consumer'
$results = Join-Path $target 'results'
$oldMcp = [Environment]::GetEnvironmentVariable('TANSR_TEST_MCP_EXE', 'Process')
$oldCli = [Environment]::GetEnvironmentVariable('TANSR_TEST_CLI_ROOT', 'Process')
$publishAttempted = $false
$defaultRestoreAttempted = $false
$manifest = [ordered]@{
    task = 'NET-05'; scope = 'Windows tests and published native MCP candidate'; startedAt = (Get-Date).ToUniversalTime().ToString('o')
    sourceRoot = $sourceRoot; cliRoot = $cliSource; outputDirectory = $target; nugetLockFilePath = 'obj/mcp-publish.packages.lock.json'
    publishExitCode = $null; restoreExitCode = $null; testExitCode = $null; candidate = $null; candidateSha256 = $null; outcome = 'incomplete'
    limitation = 'SDK1 MCP socket fixture is controlled; this gate does not prove real Serve model, paid media, microphone or codec parity.'
}
try {
    $publishAttempted = $true
    & dotnet publish $consoleProject -c Release -f net10.0-windows -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' $lockProperty -o $candidateDirectory 2>&1 |
        Tee-Object -FilePath (Join-Path $target 'publish.log')
    $manifest.publishExitCode = $LASTEXITCODE
    Assert-FormalLocksUnchanged
    if ($manifest.publishExitCode -ne 0) { throw '原生 MCP 候选发布失败。' }
    $candidate = Join-Path $candidateDirectory 'ConsoleAssistant.exe'
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw '发布未产生 ConsoleAssistant.exe。' }
    $manifest.candidate = $candidate
    $manifest.candidateSha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()

    # 自包含发布会改变 obj/project.assets.json；恢复产品默认锁定资产后再构建 Windows 测试。
    $defaultRestoreAttempted = $true
    & dotnet restore $solution --locked-mode 2>&1 | Tee-Object -FilePath (Join-Path $target 'restore-default.log')
    $manifest.restoreExitCode = $LASTEXITCODE
    Assert-FormalLocksUnchanged
    if ($manifest.restoreExitCode -ne 0) { throw '产品默认锁定资产恢复失败。' }
    [Environment]::SetEnvironmentVariable('TANSR_TEST_MCP_EXE', $candidate, 'Process')
    [Environment]::SetEnvironmentVariable('TANSR_TEST_CLI_ROOT', $cliSource, 'Process')
    & dotnet test $testProject -c Release --no-restore --results-directory $results --logger 'trx;LogFileName=windows.trx' 2>&1 |
        Tee-Object -FilePath (Join-Path $target 'windows-tests.log')
    $manifest.testExitCode = $LASTEXITCODE
    Assert-FormalLocksUnchanged
    if ($manifest.testExitCode -ne 0) { throw 'Windows 测试或真实原生 MCP 消费未通过。' }
    $manifest.outcome = 'passed'
}
finally {
    [Environment]::SetEnvironmentVariable('TANSR_TEST_MCP_EXE', $oldMcp, 'Process')
    [Environment]::SetEnvironmentVariable('TANSR_TEST_CLI_ROOT', $oldCli, 'Process')
    # 发布中途失败也可能已写入 RID 资产；只做一次默认恢复，不自动重发发布或测试。
    try {
        if ($publishAttempted -and -not $defaultRestoreAttempted) {
            $defaultRestoreAttempted = $true
            & dotnet restore $solution --locked-mode 2>&1 | Tee-Object -FilePath (Join-Path $target 'restore-default.log')
            $manifest.restoreExitCode = $LASTEXITCODE
        }
    }
    finally {
        $manifest.finishedAt = (Get-Date).ToUniversalTime().ToString('o')
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8
    }
}
