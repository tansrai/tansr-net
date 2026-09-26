param(
    [Parameter(Mandatory = $true)][string]$WpfExecutable,
    [Parameter(Mandatory = $true)][string]$WinFormsExecutable,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$ServeUrl
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$owned = @()
$evidence = New-Object 'System.Collections.Generic.List[object]'
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Use a new UI evidence directory.' }
[IO.Directory]::CreateDirectory($target) | Out-Null
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 25) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        try { $value = & $Condition; if ($value) { return $value } } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
    throw "UI assertion timed out: $Description"
}
function Find-Id($Parent, [string]$Id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-ProcessWindow([int]$ProcessId, [string]$Id) {
    $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $idCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $idCondition)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}
function Pending-Count($Window) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    return (Find-Id $Window 'PendingRequests').FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition).Count
}
function Read-Text($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
    return $Element.Current.Name
}
function Set-Text($Element, [string]$Value) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}
function Click($Element) {
    if ($null -eq $Element) { throw 'Required native control missing.' }
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function Record([string]$HostName, [string]$Action, [string]$Fact) {
    $evidence.Add([pscustomobject]@{ host = $HostName; action = $Action; fact = $Fact; utc = [DateTimeOffset]::UtcNow.ToString('O') })
    Write-Output "$HostName $Action passed"
}
function Send-Prompt($Window, [string]$Prompt) { Set-Text (Find-Id $Window 'MessageDraft') $Prompt; Click (Find-Id $Window '发送') }
function Wait-Answer($Window, [string]$Answer, [bool]$Idle = $true) {
    $null = Wait-For { $text = Read-Text (Find-Id $Window 'Conversation'); $text.Contains($Answer) -and (!$Idle -or $text.Contains('[status:idle]')) } $Answer
}
function Select-NamedItem($Control, [string]$Name, [int]$ProcessId) {
    $expand = $null
    if ($Control.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { $expand.Expand() }
    $item = Wait-For {
        $nameCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        $typeCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $itemCondition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
        $found = $Control.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)
        if ($found) { return $found }
        $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($ownedWindow in $windows) {
            $found = $ownedWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)
            if ($found) { return $found }
        }
    } ('select ' + $Name)
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ($expand) { $expand.Collapse() }
}
function Workspace-Action($Window, [string]$Name, [string]$Expected, [int]$ProcessId) {
    Select-NamedItem (Find-Id $Window 'WorkspaceAction') $Name $ProcessId
    Click (Find-Id $Window 'WorkspaceExecute')
    $null = Wait-For { (Read-Text (Find-Id $Window 'WorkspaceResult')).Contains($Expected) -and (Find-Id $Window 'WorkspaceExecute').Current.IsEnabled } $Expected
}
function Control-Action($Window, [string]$Name, [string]$Argument, [string]$Expected, [int]$ProcessId) {
    Select-NamedItem (Find-Id $Window 'SessionControlAction') $Name $ProcessId
    Set-Text (Find-Id $Window 'SessionControlArgument') $Argument
    Click (Find-Id $Window 'SessionControlExecute')
    $null = Wait-For { (Read-Text (Find-Id $Window 'SessionControlResult')).Contains($Expected) -and (Find-Id $Window 'SessionControlExecute').Current.IsEnabled } $Expected
}
function Approve-SyntheticPending($MainWindow, [int]$ProcessId) {
    if ((Pending-Count $MainWindow) -gt 0) { Click (Find-Id $MainWindow '批准') }
    $local = Find-ProcessWindow $ProcessId 'DeviceApproval'
    if ($local) { Click (Find-Id $local 'DeviceAllow') }
}
. ([ScriptBlock]::Create([IO.File]::ReadAllText($env:TANSR_NATIVE_UI_MEDIA_SCRIPT, [Text.Encoding]::UTF8)))
. ([ScriptBlock]::Create([IO.File]::ReadAllText($env:TANSR_NATIVE_UI_STORAGE_SCRIPT, [Text.Encoding]::UTF8)))
try {
    foreach ($entry in @(@('WPF', $WpfExecutable), @('WINFORMS', $WinFormsExecutable))) {
        $hostName = $entry[0]
        $env:TANSR_SERVE_URL = $ServeUrl
        $env:TANSR_SESSION_TOKEN = $env:TANSR_NATIVE_UI_TOKEN
        $env:TANSR_SESSION_CONTRACT = 'sdk1'
        $env:TANSR_ALLOW_HTTP_LOOPBACK = '1'
        $env:TANSR_EXAMPLE_STATE_FILE = Join-Path $target ($hostName + '.state.json')
        $env:TANSR_SKILL_INLINE = 'Synthetic native UI skill: keep the original request and return readable outcomes.'
        $skillDirectory = Join-Path $target ($hostName + '-skills')
        [IO.Directory]::CreateDirectory($skillDirectory) | Out-Null
        [IO.File]::WriteAllText((Join-Path $skillDirectory 'SKILL.md'), 'Synthetic directory skill: preserve the original request and approved scope.', (New-Object Text.UTF8Encoding($false)))
        $env:TANSR_SKILL_DIRECTORY = $skillDirectory
        $fixtureReady = [IO.File]::ReadAllText((Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'ready.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
        $env:TANSR_MCP_HTTP_URL = $fixtureReady.mcpUrl
        $env:TANSR_MCP_HTTP_ALLOW_LOOPBACK = '1'
        $env:TANSR_MCP_HTTP_BEARER = $env:TANSR_NATIVE_UI_TOKEN
        $env:TANSR_TERMINAL_PREVIEW = '1'
        $scopeFile = Join-Path $target ($hostName + '.scope.json')
        $snapshotFile = Join-Path $target ($hostName + '.snapshot.json')
        $snapshotDirectory = Join-Path $target ($hostName + '-snapshots')
        [IO.File]::WriteAllText($scopeFile, (@{ principal = 'native-ui-user'; scope = @{ applicationScopeId = 'native-ui-app'; endUserId = 'native-ui-user'; authorizationRevision = '1' } } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText($snapshotFile, (@{ scopeFile = $scopeFile; directory = $snapshotDirectory } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        $env:TANSR_TRUSTED_SCOPE_FILE = $scopeFile
        $env:TANSR_SNAPSHOT_CONFIGURATION = $snapshotFile
        # These are the two explicitly selected acceptance applications, not arbitrary desktop windows.
        $process = Start-Process -FilePath $entry[1] -PassThru
        $owned += $process
        $window = Wait-For {
            $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        } "$hostName window"
        Click (Find-Id $window '连接 / 创建')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } "$hostName connected"
        $session = Read-Text (Find-Id $window 'ResumeSession')
        if ([string]::IsNullOrEmpty($session)) { throw 'Session ID was not rendered.' }
        Record $hostName 'connect' ('session=' + $session)
        Send-Prompt $window ('UI_TEXT_' + $hostName)
        Wait-Answer $window ('UI_DONE_TEXT_' + $hostName)
        Record $hostName 'text-stream' 'actual controls -> public SDK -> real Serve/kernel -> SSE -> UI'
        Click (Find-Id $window '配置 / 记忆（preview）')
        $controls = Wait-For { Find-ProcessWindow $process.Id 'SessionControls' } 'configuration view'
        Control-Action $controls '授权模型目录' '' 'ui-large' $process.Id
        Select-NamedItem (Find-Id $controls 'AuthorizedModelSelector') 'UI Large [ui-large]' $process.Id
        Click (Find-Id $controls 'ApplyAuthorizedModel')
        $null = Wait-For { (Read-Text (Find-Id $controls 'SessionControlResult')).Contains('changed') -and (Find-Id $controls 'ApplyAuthorizedModel').Current.IsEnabled } 'authorized catalog selection applied'
        Control-Action $controls '本人最近1天用量' '' '1d' $process.Id
        Record $hostName 'authorized-catalog-own-usage' 'real profile catalog populated selector; original CAS selected model; own 1d usage shown with numerical quota explicitly unavailable'
        Select-NamedItem (Find-Id $controls 'SessionControlAction') '读取配置' $process.Id
        Click (Find-Id $controls 'SessionControlExecute')
        $null = Wait-For { (Read-Text (Find-Id $controls 'SessionControlResult')).Contains('configuration') } 'authoritative configuration'
        Select-NamedItem (Find-Id $controls 'SessionControlAction') '修改模型' $process.Id
        Set-Text (Find-Id $controls 'SessionControlArgument') 'ui-large'
        Click (Find-Id $controls 'SessionControlExecute')
        $null = Wait-For { (Read-Text (Find-Id $controls 'SessionControlResult')).Contains('ui-large') -and (Find-Id $controls 'SessionControlExecute').Current.IsEnabled } 'configured model'
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Changing the model replaced the session identity.' }
        Record $hostName 'configuration-model' 'explicit preview, trusted scope, original configuration operation; same session'
        Control-Action $controls '修改思考预算' '1024' '1024' $process.Id
        Control-Action $controls '修改模型' 'not-an-authorized-ui-model' 'invalid_request' $process.Id
        Control-Action $controls '读取配置' '' 'ui-large' $process.Id
        $configuration = ([regex]::Split((Read-Text (Find-Id $controls 'SessionControlResult')), '\r?\n\r?\n'))[0] | ConvertFrom-Json
        if ($configuration.configuration.model -ne 'ui-large' -or $configuration.configuration.thinking.budget -ne 1024) { throw 'A failed model change altered the original model/thinking configuration.' }
        Control-Action $controls '修改思考预算' 'null' 'changed' $process.Id
        Control-Action $controls '重放原配置' '' 'replayed' $process.Id
        Record $hostName 'configuration-thinking-rollback-replay' 'thinking changed, unavailable model refused without changing current configuration, original request replayed'
        $controls.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Send-Prompt $window ('UI_SKILL_' + $hostName)
        Wait-Answer $window ('UI_DONE_SKILL_' + $hostName)
        Record $hostName 'inline-skill' 'host configured native_skill completed through the original remote tool bridge'
        Send-Prompt $window ('UI_SKILL_DIRECTORY_' + $hostName)
        Wait-Answer $window ('UI_DONE_SKILL_DIRECTORY_' + $hostName)
        Record $hostName 'directory-skill' 'same original tool bridge loaded the explicit host directory skill'
        Send-Prompt $window ('UI_MCP_' + $hostName)
        Wait-Answer $window ('UI_DONE_MCP_' + $hostName)
        Record $hostName 'mcp-http' 'public MCP client initialized, checked frozen approved tools/list, and called bounded echo over real HTTP'
        Send-Prompt $window ('UI_ALLOW_' + $hostName)
        $null = Wait-For { (Pending-Count $window) -gt 0 } 'permission rendered'
        Click (Find-Id $window '批准')
        Wait-Answer $window ('UI_DONE_ALLOW_' + $hostName)
        $null = Wait-For { $window.Current.Name -eq ('Approved ' + $hostName + ' ALLOW') } 'native delegate changed window title'
        Record $hostName 'permission-allow' $window.Current.Name
        Send-Prompt $window ('UI_DENY_' + $hostName)
        $null = Wait-For { (Pending-Count $window) -gt 0 } 'denial rendered'
        Click (Find-Id $window '拒绝')
        Wait-Answer $window ('UI_DONE_DENY_' + $hostName)
        if ($window.Current.Name -ne ('Approved ' + $hostName + ' ALLOW')) { throw 'Denied native delegate changed the window.' }
        Record $hostName 'permission-deny' 'window title unchanged'
        Send-Prompt $window ('UI_EXPIRE_' + $hostName)
        $null = Wait-For { (Pending-Count $window) -gt 0 } 'expiring permission rendered'
        Wait-Answer $window ('UI_DONE_EXPIRE_' + $hostName)
        $null = Wait-For { (Pending-Count $window) -eq 0 } 'expired request removed'
        if ($window.Current.Name -ne ('Approved ' + $hostName + ' ALLOW')) { throw 'Expired native delegate changed the window.' }
        Record $hostName 'permission-expiry' 'expired original request removed; native side effect never happened'
        Send-Prompt $window ('UI_QUESTION_' + $hostName)
        $answer = Wait-For { Find-Id $window 'QuestionAnswer_q-ui' } 'question editor'
        Set-Text $answer 'Native free-text answer'
        Click (Find-Id $window '提交问题答案')
        Wait-Answer $window ('UI_DONE_QUESTION_' + $hostName)
        Record $hostName 'question' 'free-text answer uses its original question ID'
        Send-Prompt $window ('UI_TODO_' + $hostName)
        Wait-Answer $window ('UI_DONE_TODO_' + $hostName)
        Click (Find-Id $window '会话工作台 / 能力 / Task')
        $workspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'workspace view'
        # Owned WPF windows may be separate desktop children; scope only this process.
        if (!$workspace) { throw 'Workspace view missing.' }
        Workspace-Action $workspace '工具 / Task / 待办 / 用量' 'Native UI todo' $process.Id
        Workspace-Action $workspace '能力与接入状态' 'Serve 能力' $process.Id
        Set-Text (Find-Id $workspace 'WorkspaceArgument') '0'
        Workspace-Action $workspace '会话列表（参数：页码）' $session $process.Id
        Workspace-Action $workspace '历史分页（参数：页码）' 'messages' $process.Id
        Set-Text (Find-Id $workspace 'WorkspaceArgument') '1'
        Workspace-Action $workspace '历史分页（参数：页码）' 'messages' $process.Id
        Set-Text (Find-Id $workspace 'WorkspaceArgument') ('native-ui-' + $hostName)
        Workspace-Action $workspace '创建快照（参数：标签）' 'checkpointId' $process.Id
        $checkpoint = ((Read-Text (Find-Id $workspace 'WorkspaceResult')) | ConvertFrom-Json).checkpointId
        if ([string]::IsNullOrEmpty($checkpoint)) { throw 'Original checkpoint receipt missing.' }
        Set-Text (Find-Id $workspace 'WorkspaceArgument') $checkpoint
        Workspace-Action $workspace '列出快照' $checkpoint $process.Id
        $exported = Join-Path $target ($hostName + '-context-export.json')
        Invoke-NativeMediaModalAction (Find-Id $workspace 'WorkspaceExport') $process.Id $exported
        $null = Wait-For { (Read-Text (Find-Id $workspace 'WorkspaceResult')).Contains('已写入新快照文件') } 'original context export'
        if (!(Test-Path -LiteralPath $exported -PathType Leaf)) { throw 'Native export did not create the selected file.' }
        Invoke-NativeMediaModalAction (Find-Id $workspace 'WorkspaceImport') $process.Id $exported
        $null = Wait-For { (Read-Text (Find-Id $workspace 'WorkspaceResult')).Contains('checkpointId') -and (Find-Id $workspace 'WorkspaceImport').Current.IsEnabled } 'original context import'
        $imported = ((Read-Text (Find-Id $workspace 'WorkspaceResult')) | ConvertFrom-Json).checkpointId
        if ([string]::IsNullOrEmpty($imported) -or $imported -eq $checkpoint) { throw 'Import did not produce its independent checkpoint receipt.' }
        Set-Text (Find-Id $workspace 'WorkspaceArgument') $imported
        Workspace-Action $workspace '恢复快照（参数：ID）' 'restored' $process.Id
        Workspace-Action $workspace '从快照分叉（参数：ID）' 'forkSessionId=' $process.Id
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Checkpoint restore/fork replaced the connected original session.' }
        Workspace-Action $workspace '删除快照（参数：ID）' 'null' $process.Id
        Workspace-Action $workspace '恢复快照（参数：ID）' 'checkpoint_not_found' $process.Id
        Set-Text (Find-Id $workspace 'WorkspaceArgument') $env:TANSR_NATIVE_UI_DIRECTORY
        Workspace-Action $workspace '改变服务工作区（参数：受信路径）' 'cwd' $process.Id
        Set-Text (Find-Id $workspace 'WorkspaceArgument') 'relative-invalid'
        Workspace-Action $workspace '改变服务工作区（参数：受信路径）' 'invalid' $process.Id
        Set-Text (Find-Id $workspace 'WorkspaceArgument') ''
        Record $hostName 'workspace-checkpoints-pagination-cwd' 'actual list/history pagination, checkpoint create/export/import/restore/fork/delete and rejected reuse; original session retained; explicit cwd only'
        Workspace-Action $workspace '打开 SDK1 本地镜像配置（参数：受信 JSON 路径）' '默认关闭' $process.Id
        if (Test-Path -LiteralPath $snapshotDirectory) { throw 'Opening the default-off mirror created local storage.' }
        Workspace-Action $workspace '启用 SDK1 本地镜像' '已开启' $process.Id
        Workspace-Action $workspace '读取 SDK1 本地镜像摘要' 'checkpoint=' $process.Id
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Enabling the local mirror replaced the session.' }
        Record $hostName 'workspace-and-snapshot' 'capability/task facts plus explicit default-off encrypted SDK1 mirror; original session retained'
        $close = $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $close.Close()
        foreach ($kind in @('IMAGE', 'VIDEO', 'SPEAK', 'STT')) {
            Send-Prompt $window ('UI_MEDIA_' + $kind + '_' + $hostName)
            $null = Wait-For {
                if ((Pending-Count $window) -gt 0) { Click (Find-Id $window '批准') }
                $text = Read-Text (Find-Id $window 'Conversation')
                $text.Contains('UI_DONE_MEDIA_' + $kind + '_' + $hostName) -and $text.Contains('[status:idle]')
            } ('media tool ' + $kind)
            Click (Find-Id $window '媒体 / 转写 / 朗读')
            $media = Wait-For { Find-ProcessWindow $process.Id 'MediaWindow' } 'media window'
            $null = Wait-For { (Find-Id $media 'MediaAction:预览选中产物').Current.IsEnabled } 'media catalog loaded'
            Click (Find-Id $media 'MediaAction:预览选中产物')
            $null = Wait-For { (Read-Text (Find-Id $media 'MediaStatus')).Contains('已缓存') } ('native media preview ' + $kind)
            Record $hostName ('media-' + $kind.ToLowerInvariant()) (Read-Text (Find-Id $media 'MediaStatus'))
            $media.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        }
        Invoke-NativeMediaScenario $window $process.Id $hostName $env:TANSR_NATIVE_UI_MEDIA_DIRECTORY $target $env:TANSR_NATIVE_UI_DIRECTORY
        Click (Find-Id $window '会话工作台 / 能力 / Task')
        $workspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'mirror inspection'
        Workspace-Action $workspace '读取 SDK1 本地镜像摘要' 'checkpoint=' $process.Id
        Workspace-Action $workspace '关闭并删除 SDK1 本地镜像' '已关闭并删除' $process.Id
        Workspace-Action $workspace '读取 SDK1 本地镜像摘要' '没有本地上下文镜像' $process.Id
        Record $hostName 'snapshot-disable' 'explicit disable removes mirror content without replacing the original session'
        $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Send-Prompt $window ('UI_HOLD_' + $hostName)
        Wait-Answer $window ('UI_HOLDING_' + $hostName) $false
        Set-Text (Find-Id $window 'MessageDraft') 'Synthetic same-turn follow-up'
        Click (Find-Id $window '同轮插入草稿')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('accepted') } 'same-turn accepted'
        Click (Find-Id $window '查原插入回执')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('accepted') } 'original input query'
        Click (Find-Id $window '显式重投原插入')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('accepted') } 'original input explicit replay'
        Click (Find-Id $window '取消当前工作')
        $null = Wait-For { (Read-Text (Find-Id $window 'Conversation')).Contains('[status:idle]') } 'turn cancelled'
        Record $hostName 'input-and-cancel' 'same original session; cancellation is explicit'
        Click (Find-Id $window '仅断开本机连接')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'detached'
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Detaching replaced the session identity.' }
        Click (Find-Id $window '连接 / 创建')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'resumed session connected'
        Send-Prompt $window ('UI_RESUMED_' + $hostName)
        $null = Wait-For { (Pending-Count $window) -gt 0 } 'resumed fresh permission'
        Click (Find-Id $window '批准')
        Wait-Answer $window ('UI_DONE_RESUMED_' + $hostName)
        $null = Wait-For { $window.Current.Name -eq ('Approved ' + $hostName + ' RESUMED') } 'resumed fresh native delegate executed'
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Resumed native tool used a replacement session.' }
        Record $hostName 'resume-fresh-tool' 'same original session, initialized replay boundary, newly approved native delegate executed'
        Click (Find-Id $window '撤销本机 Skills / MCP')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已撤销') } 'extensions revoked'
        foreach ($extension in @('MCP', 'SKILL')) {
            Send-Prompt $window ('UI_' + $extension + '_REVOKED_' + $hostName)
            Wait-Answer $window ('UI_DONE_' + $extension + '_REVOKED_' + $hostName)
        }
        Record $hostName 'extension-revocation' 'same session retained tool declarations but both original delegates returned errors after explicit host revocation'
        Click (Find-Id $window '仅断开本机连接')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'final detached'
        foreach ($mode in @('stream', 'final', 'off')) {
            Select-NamedItem (Find-Id $window 'ThinkingDeliveryMode') $mode $process.Id
            Select-NamedItem (Find-Id $window 'TextDeliveryMode') $(if ($mode -eq 'final') { 'final' } else { 'stream' }) $process.Id
            Click (Find-Id $window '连接 / 创建')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } ('resume thinking mode ' + $mode)
            Send-Prompt $window ('UI_THINK_' + $mode.ToUpperInvariant() + '_' + $hostName)
            Wait-Answer $window ('UI_DONE_THINK_' + $mode.ToUpperInvariant() + '_' + $hostName)
            $shown = Read-Text (Find-Id $window 'Conversation')
            $thinking = 'UI_REASONING_' + $mode.ToUpperInvariant() + '_' + $hostName
            if (($mode -eq 'off' -and $shown.Contains($thinking)) -or ($mode -ne 'off' -and !$shown.Contains($thinking))) { throw 'Thinking delivery mode did not control native presentation.' }
            Record $hostName ('thinking-' + $mode) 'original session resumed, real thinking/text events projected according to the selected delivery mode'
            Click (Find-Id $window '仅断开本机连接')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'thinking mode detached'
        }
        $mediaControl = Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'native-media-control.json'
        try {
            [IO.File]::WriteAllText($mediaControl, '{"enabled":false}', (New-Object Text.UTF8Encoding($false)))
            Set-Text (Find-Id $window 'ResumeSession') ''
            Click (Find-Id $window '连接 / 创建')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'explicit fresh media-disabled session'
            if ((Read-Text (Find-Id $window 'ResumeSession')) -eq $session) { throw 'Media-off scenario did not create the explicitly requested new session.' }
            Invoke-NativeMediaDeniedScenario $window $process.Id $hostName $env:TANSR_NATIVE_UI_DIRECTORY
            Click (Find-Id $window '仅断开本机连接')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'media-disabled session detached'
        } finally { [IO.File]::WriteAllText($mediaControl, '{"enabled":true}', (New-Object Text.UTF8Encoding($false))) }
        $ready = [IO.File]::ReadAllText((Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'ready.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
        $memoryConfiguration = $ready.memory
        [IO.File]::WriteAllText($memoryConfiguration.controlFile, '{"enabled":true,"minimumDeletionGeneration":"0"}', (New-Object Text.UTF8Encoding($false)))
        Set-Text (Find-Id $window 'ResumeSession') ''
        Click (Find-Id $window '连接 / 创建')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'explicit new device-memory session'
        $memorySession = Read-Text (Find-Id $window 'ResumeSession')
        $memoryIdentity = Wait-For {
            if (Test-Path -LiteralPath $memoryConfiguration.identityFile) {
                @([IO.File]::ReadAllText($memoryConfiguration.identityFile, [Text.Encoding]::UTF8) | ConvertFrom-Json) | Where-Object { $_.sessionId -eq $memorySession } | Select-Object -First 1
            }
        } 'trusted original memory source identity'
        $memoryRoot = Join-Path $target ($hostName + '-device-memory')
        $memoryWorkspace = Join-Path $memoryRoot 'workspace'
        [IO.Directory]::CreateDirectory($memoryWorkspace) | Out-Null
        $deviceConfiguration = Join-Path $memoryRoot 'device.json'
        $device = @{
            format = 'tansr-example-terminal-device-v1'; enablePreview = $true; serveUrl = $ServeUrl; allowInsecureLoopback = $true
            sessionId = $memorySession; executorId = $memoryConfiguration.executorId; trustedScopeFile = $scopeFile
            controllerTokenEnvironment = 'TANSR_NATIVE_UI_TOKEN'; deviceTokenEnvironment = 'TANSR_NATIVE_UI_TOKEN'
            bindingRequestId = ('native-ui-memory-' + $hostName); allowedTools = @('SearchMemory', 'Read', 'List', 'Write', 'Edit')
            workspace = @{ path = $memoryWorkspace; id = 'native-ui-workspace'; revision = '1'; allWritersCooperate = $true }
            journal = @{ path = (Join-Path $memoryRoot 'executor.sqlite'); mode = 'create' }
            publication = @{ path = (Join-Path $memoryRoot 'memory.sqlite'); mode = 'create'; identity = $memoryIdentity.deviceIdentity; maxTransfers = 64; maxStagingBytes = 8388608; maxPages = 32768 }
        }
        [IO.File]::WriteAllText($deviceConfiguration, ($device | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
        Invoke-NativeMediaModalAction (Find-Id $window '连接本机设备工具') $process.Id $deviceConfiguration
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('device=') } 'same-session terminal and publication backend bound'
        Send-Prompt $window ('UI_MEMORY_SEED_' + $hostName)
        $null = Wait-For {
            Approve-SyntheticPending $window $process.Id
            $text = Read-Text (Find-Id $window 'Conversation')
            $text.Contains('UI_DONE_MEMORY_SEED_' + $hostName) -and $text.Contains('[status:idle]')
        } 'real memory seed turn' 50
        $null = Wait-For {
            Approve-SyntheticPending $window $process.Id
            $statusFile = Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'native-memory-status.json'
            if (!(Test-Path -LiteralPath $statusFile)) { return $false }
            $state = @([IO.File]::ReadAllText($statusFile, [Text.Encoding]::UTF8) | ConvertFrom-Json) | Where-Object { $_.sessionId -eq $memorySession } | Select-Object -First 1
            $state -and $state.idle -and $state.extracted
        } 'original memory extraction settled' 50
        Click (Find-Id $window '配置 / 记忆（preview）')
        $controls = Wait-For { Find-ProcessWindow $process.Id 'SessionControls' } 'memory commands view'
        Control-Action $controls '读取记忆来源' '' 'client-managed' $process.Id
        Control-Action $controls '置顶文字' ('Native UI pin ' + $hostName) 'committed' $process.Id
        Control-Action $controls '查询原记忆操作' '' 'committed' $process.Id
        Control-Action $controls '重放原记忆操作' '' 'committed' $process.Id
        Select-NamedItem (Find-Id $controls 'SessionControlAction') '记住本轮' $process.Id
        Click (Find-Id $controls 'SessionControlExecute')
        $null = Wait-For {
            Approve-SyntheticPending $window $process.Id
            (Read-Text (Find-Id $controls 'SessionControlResult')).Contains('committed') -and (Find-Id $controls 'SessionControlExecute').Current.IsEnabled
        } 'original remember command and promotion approval' 50
        $null = Wait-For {
            Approve-SyntheticPending $window $process.Id
            $state = @([IO.File]::ReadAllText((Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'native-memory-status.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json) | Where-Object { $_.sessionId -eq $memorySession } | Select-Object -First 1
            $state -and $state.idle
        } 'memory promotion settled' 50
        Control-Action $controls '遗忘主题' $memoryConfiguration.topic 'committed' $process.Id
        Control-Action $controls '读取记忆来源' '' 'deletionGeneration' $process.Id
        $memoryState = ([regex]::Split((Read-Text (Find-Id $controls 'SessionControlResult')), '\r?\n\r?\n'))[0] | ConvertFrom-Json
        [IO.File]::WriteAllText($memoryConfiguration.controlFile, (@{ enabled = $true; minimumDeletionGeneration = [string]$memoryState.memory.deletionGeneration } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        Record $hostName 'device-memory-commands' 'original source and SQLite publication bound through one terminal host; real extraction, pin/query/replay, approved remember and forget completed'
        $controls.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Click (Find-Id $window '关闭会话')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('核心资源已确认排空') } 'memory session close with original resource completion' 30
        [IO.File]::WriteAllText($memoryConfiguration.controlFile, '{"enabled":false}', (New-Object Text.UTF8Encoding($false)))
        $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        if (!$process.WaitForExit(10000)) { throw 'Native application failed to close.' }
        Record $hostName 'detach-close' 'no implicit remote session replacement or process left running'
        Invoke-NativeStorageScenario $entry[1] $hostName $target $ready.storage
    }
    [IO.File]::WriteAllText((Join-Path $target 'result.json'), ($evidence | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
} catch {
    [IO.File]::WriteAllText((Join-Path $target 'failure.txt'), ($_ | Out-String), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $target 'partial.json'), ($evidence | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    try {
        $states = foreach ($selectedProcess in $owned) {
            if ($selectedProcess.HasExited) { continue }
            foreach ($id in @('TansrAssistant', 'SessionControls', 'SessionWorkspace', 'MediaWindow', 'DeviceApproval')) {
                $selectedWindow = Find-ProcessWindow $selectedProcess.Id $id
                if (!$selectedWindow) { continue }
                $values = @{}
                foreach ($controlId in @('ConnectionStatus', 'Conversation', 'SessionControlResult', 'WorkspaceResult', 'MediaStatus')) {
                    $element = Find-Id $selectedWindow $controlId
                    if ($element) { $value = [string](Read-Text $element); $values[$controlId] = $value.Substring([Math]::Max(0, $value.Length - 8192)) }
                }
                [pscustomobject]@{ processId = $selectedProcess.Id; window = $id; title = $selectedWindow.Current.Name; values = $values }
            }
        }
        [IO.File]::WriteAllText((Join-Path $target 'failure-state.json'), (@($states) | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    } catch { } # Diagnostic failure must not replace the original assertion failure.
    throw
} finally {
    foreach ($process in $owned) {
        if (!$process.HasExited) { $null = $process.CloseMainWindow(); if (!$process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force } }
        $process.Dispose()
    }
}
