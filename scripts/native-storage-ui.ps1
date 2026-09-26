# Same-process native controls consume host-issued configuration; this driver never
# invents archive bindings, retention generations, cache tickets or wire requests.
function Invoke-NativeStorageScenario([string]$Executable, [string]$HostName, [string]$EvidenceDirectory, $Storage) {
    $env:TANSR_SERVE_URL = $Storage.url
    $env:TANSR_SESSION_CONTRACT = $Storage.contract
    $env:TANSR_SESSION_REQUEST_ID = 'native-storage-' + $HostName.ToLowerInvariant()
    $env:TANSR_TRUSTED_SCOPE_FILE = $Storage.scopeFile
    $env:TANSR_EXAMPLE_STATE_FILE = Join-Path $EvidenceDirectory ($HostName + '-archive-presentation.json')
    $env:TANSR_SKILL_INLINE = $null
    $env:TANSR_SKILL_DIRECTORY = $null
    $env:TANSR_MCP_HTTP_URL = $null
    $env:TANSR_MCP_HTTP_BEARER = $null
    $process = Start-Process -FilePath $Executable -PassThru
    $script:owned += $process
    $window = Wait-For { Find-ProcessWindow $process.Id 'TansrAssistant' } 'SDK2 native window'
    Click (Find-Id $window '连接 / 创建')
    $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'explicit SDK2 connection'
    $session = Read-Text (Find-Id $window 'ResumeSession')
    $configuration = Wait-For {
        if (!(Test-Path -LiteralPath $Storage.configurationFile)) { return $null }
        $material = [IO.File]::ReadAllText($Storage.configurationFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
        @($material.sessions) | Where-Object { $_.sessionId -eq $session } | Select-Object -First 1
    } 'real same-scope archive configuration'
    Click (Find-Id $window '会话工作台 / 能力 / Task')
    $workspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'SDK2 workspace'
    Set-Text (Find-Id $workspace 'WorkspaceArgument') $configuration.archiveConfiguration
    Workspace-Action $workspace '连接 SDK2 本地档案（参数：受信 JSON 路径）' '事件通道=已连接' $process.Id
    $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Send-Prompt $window ('UI_ARCHIVE_' + $HostName)
    Wait-Answer $window ('UI_DONE_ARCHIVE_' + $HostName)
    Click (Find-Id $window '会话工作台 / 能力 / Task')
    $workspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'archive after real turn'
    Workspace-Action $workspace '同步 SDK2 档案与原 ACK' '已确认 ACK=' $process.Id
    Workspace-Action $workspace '读取 SDK2 本地档案' ('UI_ARCHIVE_' + $HostName) $process.Id
    Workspace-Action $workspace 'SDK2 档案状态' '待对账 ACK=False' $process.Id
    Record $HostName 'archive-real-ack-material-host' 'same-scope original SDK2 session, host-issued binding/retention, encrypted SQLite, public ArchiveSessionHost event consumer and durable original ACK'
    Set-Text (Find-Id $workspace 'WorkspaceArgument') $configuration.cacheConfiguration
    Workspace-Action $workspace '打开缓存连续性配置（参数：受信 JSON 路径）' 'unknown' $process.Id
    Workspace-Action $workspace '新建逻辑缓存绑定' '原请求已确认' $process.Id
    Workspace-Action $workspace '查询缓存原请求' '原请求已确认' $process.Id
    Workspace-Action $workspace '读取缓存诊断' '诊断行数=' $process.Id
    Workspace-Action $workspace '本机缓存接入状态' 'unknown' $process.Id
    Workspace-Action $workspace '停止 SDK2 本机档案连接' '未删除档案' $process.Id
    $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Click (Find-Id $window '仅断开本机连接')
    $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'SDK2 detach preserves original authority'
    if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'SDK2 detach changed the original session.' }
    Click (Find-Id $window '连接 / 创建')
    $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'SDK2 same-session resume'
    Click (Find-Id $window '会话工作台 / 能力 / Task')
    $workspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'SDK2 reopened workspace'
    Set-Text (Find-Id $workspace 'WorkspaceArgument') $configuration.archiveReopenConfiguration
    Workspace-Action $workspace '连接 SDK2 本地档案（参数：受信 JSON 路径）' '事件通道=已连接' $process.Id
    Workspace-Action $workspace '读取 SDK2 本地档案' ('UI_ARCHIVE_' + $HostName) $process.Id
    Set-Text (Find-Id $workspace 'WorkspaceArgument') $configuration.cacheReopenConfiguration
    Workspace-Action $workspace '打开缓存连续性配置（参数：受信 JSON 路径）' 'unknown' $process.Id
    Workspace-Action $workspace '查询缓存原请求' '原请求已确认' $process.Id
    Workspace-Action $workspace '显式关闭逻辑缓存' '状态=closed' $process.Id
    Record $HostName 'cache-original-reopen-query-close' 'original protected cache state reopened after detach, original request queried without replacement, explicit close; provider savings remain unknown'
    $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Click (Find-Id $window '关闭会话')
    $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('核心资源已确认排空') } 'SDK2 original resources settled' 30
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (!$process.WaitForExit(10000)) { throw 'SDK2 native application failed to close.' }

    # A new native process reads the same authorized archive without constructing a
    # client or having a credential. The ordinary draft mirror is not the archive.
    $env:TANSR_SESSION_TOKEN = $null
    $env:TANSR_SERVE_URL = 'http://127.0.0.1:1'
    $cold = Start-Process -FilePath $Executable -PassThru
    $script:owned += $cold
    $offlineWindow = Wait-For { Find-ProcessWindow $cold.Id 'TansrAssistant' } 'cold native archive reader'
    Invoke-NativeMediaModalAction (Find-Id $offlineWindow '离线授权档案') $cold.Id $configuration.archiveReopenConfiguration
    $offline = Wait-For { Find-ProcessWindow $cold.Id 'OfflineStorage' } 'offline original archive'
    $text = Read-Text (Find-Id $offline 'OfflineStorageResult')
    if (!$text.Contains('离线只读') -or !$text.Contains('UI_ARCHIVE_' + $HostName)) { throw 'Cold native reader did not display the original authorized archive with the offline boundary.' }
    $offline.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $offlineWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (!$cold.WaitForExit(10000)) { throw 'Cold native archive reader failed to close.' }
    Record $HostName 'archive-cold-offline-native' 'new native process, no supplied token, no connect action; original DPAPI/SQLite archive read under still-valid trusted offline authority'
}
