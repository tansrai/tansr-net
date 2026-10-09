param(
    [Parameter(Mandatory = $true)][string]$WpfExecutable,
    [Parameter(Mandatory = $true)][string]$WinFormsExecutable,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$ServeUrl,
    [ValidateSet('WPF','WINFORMS')][string[]]$Hosts = @('WPF','WINFORMS'),
    [ValidateSet('primary','media','media-tail','speech','continuity','memory','storage','legacy')][string[]]$Groups = @('primary','storage','legacy')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$script:nativeUiProcesses = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$evidence = New-Object 'System.Collections.Generic.List[object]'
$caseFailures = New-Object 'System.Collections.Generic.List[object]'
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
function Wait-Id($Parent, [string]$Id) {
    # Retry only lookup/readiness. The caller mutates the resolved control once;
    # an Invoke failure is unknown and must never automatically resend an action.
    return Wait-For {
        $control = Find-Id $Parent $Id
        if ($control -and $control.Current.IsEnabled) { return $control }
    } ('owned control ready: ' + $Id)
}
function Find-ProcessWindow([int]$ProcessId, [string]$Id) {
    $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $idCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $idCondition)
    $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
    foreach ($root in $roots) {
        if ($root.Current.AutomationId -eq $Id) { return $root }
        $child = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($child) { return $child }
    }
    return $null
}
function Pending-Count($Window) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $pending = Find-Id $Window 'PendingRequests'
    if (!$pending) { throw (New-Object System.Windows.Automation.ElementNotAvailableException('The owned pending view is transitioning.')) }
    return $pending.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition).Count
}
function Read-Text($Element) {
    if (!$Element) { throw (New-Object System.Windows.Automation.ElementNotAvailableException('The owned text control is not available in this UIA snapshot.')) }
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
    $handle=[IntPtr]$Element.Current.NativeWindowHandle
    if ([TansrNativeDialogClick]::IsEditor($handle,$Element.Current.ProcessId)) { return [TansrNativeDialogClick]::GetEditorText($handle,$Element.Current.ProcessId) }
    return $Element.Current.Name
}
function Set-Text($Element, [string]$Value) {
    if (!$Element) { throw (New-Object System.Windows.Automation.ElementNotAvailableException('The owned editable control is not available.')) }
    $pattern=$null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)) { $pattern.SetValue($Value); return }
    # .NET Framework multiline edits expose TextPattern without ValuePattern.
    # Use their observed owned native edit HWND; setting text never clicks Send.
    [TansrNativeDialogClick]::SetEditorText([IntPtr]$Element.Current.NativeWindowHandle,$Element.Current.ProcessId,$Value)
}
function Click($Element) {
    if ($null -eq $Element) { throw 'Required native control missing.' }
    $handle=[IntPtr]$Element.Current.NativeWindowHandle
    if ([TansrNativeDialogClick]::IsFormsButton($handle,$Element.Current.ProcessId)) {
        [TansrNativeDialogClick]::Click($handle,$Element.Current.ProcessId)
        return
    }
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function Record([string]$HostName, [string]$Action, [string]$Fact) {
    $evidence.Add([pscustomobject]@{ host = $HostName; action = $Action; fact = $Fact; utc = [DateTimeOffset]::UtcNow.ToString('O') })
    Write-Output "$HostName $Action passed"
}
function Record-GroupFailure([string]$HostName, [string]$Group, $Failure, [bool]$CloseProcesses = $true) {
    $states = foreach ($selected in $script:nativeUiProcesses) {
        if ($selected.HasExited) { continue }
        foreach ($id in @('TansrAssistant', 'SessionControls', 'SessionWorkspace', 'MediaWindow', 'DeviceApproval')) {
            try {
                $view = Find-ProcessWindow $selected.Id $id
                if (!$view) { continue }
                $values = @{}
                foreach ($controlId in @('ConnectionStatus', 'Conversation', 'SessionControlResult', 'WorkspaceResult', 'MediaStatus')) {
                    $control = Find-Id $view $controlId
                    if ($control) { $value=[string](Read-Text $control); $values[$controlId]=$value.Substring([Math]::Max(0,$value.Length-8192)) }
                }
                [pscustomobject]@{processId=$selected.Id;window=$id;values=$values}
            } catch [System.Windows.Automation.ElementNotAvailableException] { }
        }
    }
    $caseFailures.Add([pscustomobject]@{host=$HostName;action=$Group;result='failed';error=[string]$Failure;stack=$Failure.ScriptStackTrace;states=@($states);continuation='The original assertion remains failed; only fresh independent process/session groups follow.'})
    [IO.File]::WriteAllText((Join-Path $target 'case-failures.json'),($caseFailures.ToArray()|ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $target 'partial.json'),($evidence.ToArray()|ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
    Write-Output ($HostName+' '+$Group+' FAILED; preserving original failure, continuing independent groups')
    if (!$CloseProcesses) { return }
    foreach ($selected in $script:nativeUiProcesses) {
        if (!$selected.HasExited) { $null=$selected.CloseMainWindow(); if(!$selected.WaitForExit(3000)){Stop-Process -Id $selected.Id -Force} }
    }
}
function Send-Prompt($Window, [string]$Prompt) { Set-Text (Wait-Id $Window 'MessageDraft') $Prompt; Click (Wait-Id $Window '发送') }
function Wait-Answer($Window, [string]$Answer, [bool]$Idle = $true) {
    $null = Wait-For { $text = Read-Text (Find-Id $Window 'Conversation'); $text.Contains($Answer) -and (!$Idle -or $text.Contains('[status:idle]')) } $Answer
}
function Select-NamedItem($Control, [string]$Name, [int]$ProcessId) {
    $expand = $null
    if ($Control.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { $expand.Expand() }
    $seen = New-Object 'System.Collections.Generic.List[object]'
    function Find-DisplayedItem($Root) {
        $type = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $label = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        foreach ($candidate in $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type)) {
            if ($candidate.Current.ProcessId -ne $ProcessId) { continue }
            $display = $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $label)
            $seen.Add([pscustomobject]@{ itemName=$candidate.Current.Name; renderedLabel=if($display){$display.Current.Name}else{$null} })
            # WPF data-bound objects can expose their ToString() as the item Name
            # while DisplayMemberPath supplies an exact visible descendant label.
            if ($candidate.Current.Name -eq $Name -or $display) { return $candidate }
        }
        return $null
    }
    $item = $null
    try { $item = Wait-For {
        $found = Find-DisplayedItem $Control
        if ($found) { return $found }
        $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($ownedWindow in $windows) {
            $found = Find-DisplayedItem $ownedWindow
            if ($found) { return $found }
        }
    } ('select ' + $Name) }
    finally { [IO.File]::AppendAllText((Join-Path $target 'selection-items.jsonl'), (([pscustomobject]@{processId=$ProcessId;control=$Control.Current.AutomationId;requested=$Name;selected=if($item){$item.Current.Name}else{$null};observed=$seen.ToArray()}) | ConvertTo-Json -Depth 4 -Compress) + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false))) }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ($expand) { $expand.Collapse() }
}
function Workspace-Action($Window, [string]$Name, [string]$Expected, [int]$ProcessId) {
    Select-NamedItem (Wait-Id $Window 'WorkspaceAction') $Name $ProcessId
    Click (Wait-Id $Window 'WorkspaceExecute')
    $null = Wait-For { (Read-Text (Find-Id $Window 'WorkspaceResult')).Contains($Expected) -and (Find-Id $Window 'WorkspaceExecute').Current.IsEnabled } $Expected
}
function Control-Action($Window, [string]$Name, [string]$Argument, [string]$Expected, [int]$ProcessId) {
    Select-NamedItem (Wait-Id $Window 'SessionControlAction') $Name $ProcessId
    Set-Text (Wait-Id $Window 'SessionControlArgument') $Argument
    Click (Wait-Id $Window 'SessionControlExecute')
    $null = Wait-For { (Read-Text (Find-Id $Window 'SessionControlResult')).Contains($Expected) -and (Find-Id $Window 'SessionControlExecute').Current.IsEnabled } $Expected
}
function Approve-SyntheticPending($MainWindow, [int]$ProcessId) {
    if ((Pending-Count $MainWindow) -gt 0) { Click (Wait-Id $MainWindow '批准') }
    $local = Find-ProcessWindow $ProcessId 'DeviceApproval'
    if ($local) { Click (Wait-Id $local 'DeviceAllow') }
}
function Wait-ApprovedAnswer($Window, [int]$ProcessId, [string]$Answer) {
    $null = Wait-For {
        Approve-SyntheticPending $Window $ProcessId
        $text = Read-Text (Find-Id $Window 'Conversation')
        $text.Contains($Answer) -and $text.Contains('[status:idle]')
    } $Answer
}
function New-NativePrivateWorkspace([string]$Path) {
    if (Test-Path -LiteralPath $Path) { throw 'A synthetic workspace must be created once in this new evidence directory.' }
    $directory = [IO.Directory]::CreateDirectory($Path)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner($identity); $acl.SetAccessRuleProtection($true, $false)
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, [Security.AccessControl.FileSystemRights]::FullControl, ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit), [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
    # The PS5 acceptance host may inherit a PS7 module path. Use the same .NET
    # ACL operation directly rather than autoloading a mismatched Security module.
    $acl.AddAccessRule($rule); $directory.SetAccessControl($acl)
}
function Connect-NativeUiTools($MainWindow, [int]$ProcessId, [string]$HostName, [string]$Phase, [string]$SessionId, [string]$ScopeFile, $Ready) {
    $root = Join-Path $target ($HostName + '-device-' + $Phase)
    $work = Join-Path $root 'workspace'
    New-NativePrivateWorkspace $work
    $configurationPath = Join-Path $root 'device.json'
    $configuration = @{
        format = 'tansr-example-terminal-device-v1'; enablePreview = $true; serveUrl = $ServeUrl; allowInsecureLoopback = $true
        sessionId = $SessionId; executorId = $Ready.memory.executorId; trustedScopeFile = $ScopeFile
        controllerTokenEnvironment = 'TANSR_NATIVE_UI_TOKEN'; deviceTokenEnvironment = 'TANSR_NATIVE_UI_TOKEN'
        bindingRequestId = ('native-ui-' + $HostName.ToLowerInvariant() + '-' + $Phase)
        allowedTools = @('Read', 'List', 'Write', 'Edit', 'AskUser', 'TodoWrite', 'ImageGen', 'VideoGen', 'TextToSpeech', 'SpeechToText', 'set_window_title', 'native_skill', 'mcp_echo', 'Task')
        workspace = @{ path = $work; id = 'native-ui-workspace'; revision = '1'; allWritersCooperate = $true }
        journal = @{ path = (Join-Path $root 'executor.sqlite'); mode = 'create' }
    }
    [IO.File]::WriteAllText($configurationPath, ($configuration | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    Invoke-NativeMediaModalAction (Wait-Id $MainWindow '连接本机设备工具') $ProcessId $configurationPath
    $null = Wait-For { (Read-Text (Find-Id $MainWindow 'ConnectionStatus')).Contains('device=') } 'public terminal initialization and binding'
}
function Invoke-NativeLegacyWorkspaceScenario([string]$Executable, [string]$HostName, $Legacy) {
    $env:TANSR_SERVE_URL = $Legacy.url
    $env:TANSR_SESSION_TOKEN = $env:TANSR_NATIVE_UI_TOKEN
    $env:TANSR_SESSION_CONTRACT = 'sdk1'
    $env:TANSR_RESUME_SESSION = $null
    $env:TANSR_EXAMPLE_STATE_FILE = Join-Path $target ($HostName + '-legacy-cwd.state.json')
    $legacyProcess = Start-Process -FilePath $Executable -PassThru
    $script:nativeUiProcesses.Add($legacyProcess)
    $main = Wait-For { Find-ProcessWindow $legacyProcess.Id 'TansrAssistant' } 'trusted SDK1 native window'
    Click (Wait-Id $main '连接 / 创建')
    $null = Wait-For { (Read-Text (Find-Id $main 'ConnectionStatus')).Contains('已连接') } 'trusted SDK1 connected'
    $original = Read-Text (Find-Id $main 'ResumeSession')
    Click (Wait-Id $main '会话工作台 / 能力 / Task')
    $workspace = Wait-For { Find-ProcessWindow $legacyProcess.Id 'SessionWorkspace' } 'trusted SDK1 workspace'
    Set-Text (Wait-Id $workspace 'WorkspaceArgument') $Legacy.cwdTarget
    Workspace-Action $workspace '改变服务工作区（参数：受信路径）' 'cwd' $legacyProcess.Id
    $receipt = (Read-Text (Find-Id $workspace 'WorkspaceResult')) | ConvertFrom-Json
    if ($receipt.cwd -ne [IO.Path]::GetFullPath($Legacy.cwdTarget) -or $receipt.from -eq $receipt.cwd) { throw 'Trusted SDK1 did not return the original changed from/to cwd receipt.' }
    Set-Text (Wait-Id $workspace 'WorkspaceArgument') 'relative-invalid'
    Workspace-Action $workspace '改变服务工作区（参数：受信路径）' 'cwd_invalid' $legacyProcess.Id
    if ((Read-Text (Find-Id $main 'ResumeSession')) -ne $original) { throw 'The trusted cwd control replaced the original session.' }
    Record $HostName 'trusted-sdk1-cwd' 'original native control changed cwd only inside the explicit developer allowlist; original from/to checked, relative path refused, same session'
    $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Click (Wait-Id $main '关闭会话')
    $null = Wait-For { (Read-Text (Find-Id $main 'ConnectionStatus')).Contains('核心资源已确认排空') } 'trusted SDK1 resources settled' 30
    $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (!$legacyProcess.WaitForExit(10000)) { throw 'Trusted SDK1 native application failed to close.' }
}
. ([ScriptBlock]::Create([IO.File]::ReadAllText($env:TANSR_NATIVE_UI_MEDIA_SCRIPT, [Text.Encoding]::UTF8)))
. ([ScriptBlock]::Create([IO.File]::ReadAllText($env:TANSR_NATIVE_UI_STORAGE_SCRIPT, [Text.Encoding]::UTF8)))
try {
    foreach ($entry in @(@('WPF', $WpfExecutable), @('WINFORMS', $WinFormsExecutable))) {
        $hostName = $entry[0]
        if ($Hosts -notcontains $hostName) { continue }
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
        [IO.File]::WriteAllText($fixtureReady.memory.controlFile, '{"enabled":false}', (New-Object Text.UTF8Encoding($false)))
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
        if ($Groups -contains 'primary' -or $Groups -contains 'media' -or $Groups -contains 'media-tail' -or $Groups -contains 'speech' -or $Groups -contains 'continuity' -or $Groups -contains 'memory') { try {
        # These are the two explicitly selected acceptance applications, not arbitrary desktop windows.
        $process = Start-Process -FilePath $entry[1] -PassThru
        $script:nativeUiProcesses.Add($process)
        $window = Wait-For {
            $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        } "$hostName window"
        Click (Wait-Id $window '连接 / 创建')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } "$hostName connected"
        $session = Read-Text (Find-Id $window 'ResumeSession')
        if ([string]::IsNullOrEmpty($session)) { throw 'Session ID was not rendered.' }
        Record $hostName 'connect' ('session=' + $session)
        Connect-NativeUiTools $window $process.Id $hostName 'initial' $session $scopeFile $fixtureReady
        Send-Prompt $window ('UI_TEXT_' + $hostName)
        Wait-Answer $window ('UI_DONE_TEXT_' + $hostName)
        Record $hostName 'text-stream' 'actual controls -> public SDK -> real Serve/kernel -> SSE -> UI'
        if ($Groups -contains 'primary') {
        Click (Wait-Id $window '配置 / 记忆（preview）')
        $controls = Wait-For { Find-ProcessWindow $process.Id 'SessionControls' } 'configuration view'
        Control-Action $controls '授权模型目录' '' 'ui-large' $process.Id
        Select-NamedItem (Wait-Id $controls 'AuthorizedModelSelector') 'UI Large [ui-large]' $process.Id
        Click (Wait-Id $controls 'ApplyAuthorizedModel')
        $null = Wait-For { (Read-Text (Find-Id $controls 'SessionControlResult')).Contains('changed') -and (Find-Id $controls 'ApplyAuthorizedModel').Current.IsEnabled } 'authorized catalog selection applied'
        Control-Action $controls '本人最近1天用量' '' '1d' $process.Id
        Record $hostName 'authorized-catalog-own-usage' 'real profile catalog populated selector; original CAS selected model; own 1d usage shown with numerical quota explicitly unavailable'
        Select-NamedItem (Wait-Id $controls 'SessionControlAction') '读取配置' $process.Id
        Click (Wait-Id $controls 'SessionControlExecute')
        $null = Wait-For { (Read-Text (Find-Id $controls 'SessionControlResult')).Contains('configuration') } 'authoritative configuration'
        Select-NamedItem (Wait-Id $controls 'SessionControlAction') '修改模型' $process.Id
        Set-Text (Wait-Id $controls 'SessionControlArgument') 'ui-large'
        Click (Wait-Id $controls 'SessionControlExecute')
        $null = Wait-For { (Read-Text (Find-Id $controls 'SessionControlResult')).Contains('ui-large') -and (Find-Id $controls 'SessionControlExecute').Current.IsEnabled } 'configured model'
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Changing the model replaced the session identity.' }
        Record $hostName 'configuration-model' 'explicit preview, trusted scope, original configuration operation; same session'
        try {
            Control-Action $controls '修改思考预算' '1024' '1024' $process.Id
            Select-NamedItem (Wait-Id $controls 'SessionControlAction') '修改模型' $process.Id
            Set-Text (Wait-Id $controls 'SessionControlArgument') 'not-an-authorized-ui-model'
            $previousControlText = Read-Text (Find-Id $controls 'SessionControlResult')
            Click (Wait-Id $controls 'SessionControlExecute')
            $null = Wait-For { (Find-Id $controls 'SessionControlExecute').Current.IsEnabled -and (Read-Text (Find-Id $controls 'SessionControlResult')) -ne $previousControlText } 'original unavailable-model response'
            $refusal = Read-Text (Find-Id $controls 'SessionControlResult')
            if (!$refusal.Contains('invalid_request')) { throw ('Expected a definite original configuration rejection, received: ' + $refusal) }
            Control-Action $controls '读取配置' '' 'ui-large' $process.Id
            $configuration = ([regex]::Split((Read-Text (Find-Id $controls 'SessionControlResult')), '\r?\n\r?\n'))[0] | ConvertFrom-Json
            if ($configuration.configuration.model -ne 'ui-large' -or $configuration.configuration.thinking.budget -ne 1024) { throw 'A failed model change altered the original model/thinking configuration.' }
            Control-Action $controls '修改思考预算' 'null' 'changed' $process.Id
            Control-Action $controls '重放原配置' '' 'replayed' $process.Id
            if ((Wait-For { Read-Text (Find-Id $window 'ResumeSession') } 'original session identity readable') -ne $session) { throw 'Configuration refusal/replay replaced the connected session.' }
            [IO.File]::WriteAllText((Join-Path $target ($hostName + '-configuration-receipt.json')), (([pscustomobject]@{ sessionId=$session; refusal=$refusal; configurationAfterRefusal=$configuration; finalReplay=(Read-Text (Wait-Id $controls 'SessionControlResult')); sameSession=$true }) | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
            Record $hostName 'configuration-thinking-rollback-replay' 'thinking changed, unavailable model refused without changing current configuration, original request replayed'
        } catch {
            # Keep the original failed assertion and unknown request intact. The
            # independent tool/media/storage scenarios do not rewrite this journal.
            $failure = [pscustomobject]@{host=$hostName;action='configuration-thinking-rollback-replay';result='failed';error=[string]$_;originalUi=(Read-Text (Find-Id $controls 'SessionControlResult'));continuation='Independent scenarios only; unresolved original configuration is not cleared or replaced.'}
            $caseFailures.Add($failure)
            [IO.File]::WriteAllText((Join-Path $target 'case-failures.json'), ($caseFailures.ToArray() | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
            Write-Output ($hostName + ' configuration-thinking-rollback-replay FAILED; continuing independent original scenarios')
        }
        $controls.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Send-Prompt $window ('UI_SKILL_' + $hostName)
        Wait-ApprovedAnswer $window $process.Id ('UI_DONE_SKILL_' + $hostName)
        Record $hostName 'inline-skill' 'host configured native_skill completed through the original remote tool bridge'
        Send-Prompt $window ('UI_SKILL_DIRECTORY_' + $hostName)
        Wait-ApprovedAnswer $window $process.Id ('UI_DONE_SKILL_DIRECTORY_' + $hostName)
        Record $hostName 'directory-skill' 'same original tool bridge loaded the explicit host directory skill'
        Send-Prompt $window ('UI_MCP_' + $hostName)
        Wait-ApprovedAnswer $window $process.Id ('UI_DONE_MCP_' + $hostName)
        Record $hostName 'mcp-http' 'public MCP client initialized, checked frozen approved tools/list, and called bounded echo over real HTTP'
        Send-Prompt $window ('UI_ALLOW_' + $hostName)
        $null = Wait-For { (Pending-Count $window) -gt 0 } 'permission rendered'
        Click (Wait-Id $window '批准')
        Wait-ApprovedAnswer $window $process.Id ('UI_DONE_ALLOW_' + $hostName)
        $null = Wait-For { $window.Current.Name -eq ('Approved ' + $hostName + ' ALLOW') } 'native delegate changed window title'
        Record $hostName 'permission-allow' $window.Current.Name
        Send-Prompt $window ('UI_DENY_' + $hostName)
        $null = Wait-For { (Pending-Count $window) -gt 0 } 'denial rendered'
        Click (Wait-Id $window '拒绝')
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
        Click (Wait-Id $window '提交问题答案')
        Wait-Answer $window ('UI_DONE_QUESTION_' + $hostName)
        Record $hostName 'question' 'free-text answer uses its original question ID'
        Send-Prompt $window ('UI_TODO_' + $hostName)
        Wait-Answer $window ('UI_DONE_TODO_' + $hostName)
        Click (Wait-Id $window '会话工作台 / 能力 / Task')
        $workspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'workspace view'
        # Owned WPF windows may be separate desktop children; scope only this process.
        if (!$workspace) { throw 'Workspace view missing.' }
        Workspace-Action $workspace '工具 / Task / 待办 / 用量' 'Native UI todo' $process.Id
        Workspace-Action $workspace '能力与接入状态' 'Serve 能力' $process.Id
        Set-Text (Wait-Id $workspace 'WorkspaceArgument') '0'
        Workspace-Action $workspace '会话列表（参数：页码）' $session $process.Id
        Workspace-Action $workspace '历史分页（参数：页码）' 'messages' $process.Id
        Set-Text (Wait-Id $workspace 'WorkspaceArgument') '1'
        Workspace-Action $workspace '历史分页（参数：页码）' 'messages' $process.Id
        Set-Text (Wait-Id $workspace 'WorkspaceArgument') ('native-ui-' + $hostName)
        Workspace-Action $workspace '创建快照（参数：标签）' 'checkpointId' $process.Id
        $checkpoint = ((Read-Text (Find-Id $workspace 'WorkspaceResult')) | ConvertFrom-Json).checkpointId
        if ([string]::IsNullOrEmpty($checkpoint)) { throw 'Original checkpoint receipt missing.' }
        Set-Text (Wait-Id $workspace 'WorkspaceArgument') $checkpoint
        Workspace-Action $workspace '列出快照' $checkpoint $process.Id
        $exported = Join-Path $target ($hostName + '-context-export.json')
        Invoke-NativeMediaModalAction (Wait-Id $workspace 'WorkspaceExport') $process.Id $exported
        $null = Wait-For { (Read-Text (Find-Id $workspace 'WorkspaceResult')).Contains('已写入新快照文件') } 'original context export'
        if (!(Test-Path -LiteralPath $exported -PathType Leaf)) { throw 'Native export did not create the selected file.' }
        Invoke-NativeMediaModalAction (Wait-Id $workspace 'WorkspaceImport') $process.Id $exported
        $null = Wait-For { (Read-Text (Find-Id $workspace 'WorkspaceResult')).Contains('checkpointId') -and (Find-Id $workspace 'WorkspaceImport').Current.IsEnabled } 'original context import'
        $imported = ((Read-Text (Find-Id $workspace 'WorkspaceResult')) | ConvertFrom-Json).checkpointId
        if ([string]::IsNullOrEmpty($imported) -or $imported -eq $checkpoint) { throw 'Import did not produce its independent checkpoint receipt.' }
        Set-Text (Wait-Id $workspace 'WorkspaceArgument') $imported
        Workspace-Action $workspace '恢复快照（参数：ID）' 'restored' $process.Id
        Workspace-Action $workspace '从快照分叉（参数：ID）' 'forkSessionId=' $process.Id
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Checkpoint restore/fork replaced the connected original session.' }
        Workspace-Action $workspace '删除快照（参数：ID）' 'null' $process.Id
        Workspace-Action $workspace '恢复快照（参数：ID）' 'checkpoint_not_found' $process.Id
        Set-Text (Wait-Id $workspace 'WorkspaceArgument') $env:TANSR_NATIVE_UI_DIRECTORY
        Workspace-Action $workspace '改变服务工作区（参数：受信路径）' 'forbidden' $process.Id
        Set-Text (Wait-Id $workspace 'WorkspaceArgument') 'relative-invalid'
        Workspace-Action $workspace '改变服务工作区（参数：受信路径）' 'forbidden' $process.Id
        Set-Text (Wait-Id $workspace 'WorkspaceArgument') ''
        Record $hostName 'workspace-checkpoints-pagination' 'actual list/history pagination, checkpoint create/export/import/restore/fork/delete and rejected reuse; original session retained'
        Record $hostName 'bound-cwd-isolation' 'the original bound-device execution contract refuses Serve host cwd changes, including absolute and relative paths; positive trusted SDK1 control is separate'
        Workspace-Action $workspace '打开 SDK1 本地镜像配置（参数：受信 JSON 路径）' '默认关闭' $process.Id
        if (Test-Path -LiteralPath $snapshotDirectory) { throw 'Opening the default-off mirror created local storage.' }
        Workspace-Action $workspace '启用 SDK1 本地镜像' '已开启' $process.Id
        Workspace-Action $workspace '读取 SDK1 本地镜像摘要' 'checkpoint=' $process.Id
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Enabling the local mirror replaced the session.' }
        Record $hostName 'workspace-and-snapshot' 'capability/task facts plus explicit default-off encrypted SDK1 mirror; original session retained'
        $close = $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $close.Close()
        }
        if ($Groups -contains 'primary' -or $Groups -contains 'media' -or $Groups -contains 'media-tail' -or $Groups -contains 'speech') {
        if ($Groups -contains 'media' -and $Groups -notcontains 'primary') {
            # Preserve the complete mirror lifecycle around real subsequent turns,
            # without rerunning the already accepted checkpoint/pagination group.
            Click (Wait-Id $window '会话工作台 / 能力 / Task')
            $workspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'standalone media mirror setup'
            Set-Text (Wait-Id $workspace 'WorkspaceArgument') ''
            Workspace-Action $workspace '打开 SDK1 本地镜像配置（参数：受信 JSON 路径）' '默认关闭' $process.Id
            if (Test-Path -LiteralPath $snapshotDirectory) { throw 'Opening the default-off mirror created local storage.' }
            Workspace-Action $workspace '启用 SDK1 本地镜像' '已开启' $process.Id
            Workspace-Action $workspace '读取 SDK1 本地镜像摘要' 'checkpoint=' $process.Id
            if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Enabling the local mirror replaced the session.' }
            Record $hostName 'workspace-and-snapshot' 'explicit default-off encrypted SDK1 mirror enabled before actual media turns; original session retained'
            $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        }
        if ($Groups -contains 'primary' -or $Groups -contains 'media') { foreach ($kind in @('IMAGE', 'VIDEO', 'SPEAK', 'STT')) {
            Send-Prompt $window ('UI_MEDIA_' + $kind + '_' + $hostName)
            $null = Wait-For {
                if ((Pending-Count $window) -gt 0) { Click (Wait-Id $window '批准') }
                $text = Read-Text (Find-Id $window 'Conversation')
                $text.Contains('UI_DONE_MEDIA_' + $kind + '_' + $hostName) -and $text.Contains('[status:idle]')
            } ('media tool ' + $kind)
            Click (Wait-Id $window '媒体 / 转写 / 朗读')
            $media = Wait-For { Find-ProcessWindow $process.Id 'MediaWindow' } 'media window'
            $null = Wait-For { (Find-Id $media 'MediaAction:预览选中产物').Current.IsEnabled } 'media catalog loaded'
            Click (Wait-Id $media 'MediaAction:预览选中产物')
            $null = Wait-For { (Read-Text (Find-Id $media 'MediaStatus')).Contains('已缓存') } ('native media preview ' + $kind)
            Record $hostName ('media-' + $kind.ToLowerInvariant()) (Read-Text (Find-Id $media 'MediaStatus'))
            $media.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        } }
        $mediaTail = $Groups -contains 'media-tail' -and $Groups -notcontains 'primary' -and $Groups -notcontains 'media'
        $speechOnly = $Groups -contains 'speech' -and !$mediaTail -and $Groups -notcontains 'primary' -and $Groups -notcontains 'media'
        try { Invoke-NativeMediaScenario $window $process.Id $hostName $env:TANSR_NATIVE_UI_MEDIA_DIRECTORY $target $env:TANSR_NATIVE_UI_DIRECTORY -SpeechOnly:$speechOnly -RecordingAndSpeechOnly:$mediaTail }
        catch {
            Record-GroupFailure $hostName 'native-media-actions' $_ $false
            # Direct ASR/TTS failure does not authorize retries. Continue only if
            # the original agent session is independently idle; its journals stay intact.
            $null = Wait-For { (Read-Text (Find-Id $window 'Conversation')).Contains('[status:idle]') } 'original session idle after independent media failure'
        }
        }
        if ($Groups -contains 'primary' -or $Groups -contains 'media') {
        Click (Wait-Id $window '会话工作台 / 能力 / Task')
        $workspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'mirror inspection'
        Workspace-Action $workspace '读取 SDK1 本地镜像摘要' 'checkpoint=' $process.Id
        Workspace-Action $workspace '关闭并删除 SDK1 本地镜像' '已关闭并删除' $process.Id
        Workspace-Action $workspace '读取 SDK1 本地镜像摘要' '没有本地上下文镜像' $process.Id
        Record $hostName 'snapshot-disable' 'explicit disable removes mirror content without replacing the original session'
        $workspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        }
        if ($Groups -contains 'primary' -or $Groups -contains 'continuity') {
        Select-NamedItem (Wait-Id $window 'TextDeliveryMode') 'stream' $process.Id
        Select-NamedItem (Wait-Id $window 'ThinkingDeliveryMode') 'stream' $process.Id
        Send-Prompt $window ('UI_DELIVERY_' + $hostName)
        Wait-Answer $window ('UI_DELIVERY_STARTED_' + $hostName) $false
        Wait-Answer $window ('UI_DELIVERY_PRIOR_THINKING_' + $hostName) $false
        Select-NamedItem (Wait-Id $window 'TextDeliveryMode') 'final' $process.Id
        Select-NamedItem (Wait-Id $window 'ThinkingDeliveryMode') 'off' $process.Id
        $null = Wait-For { !(Read-Text (Find-Id $window 'Conversation')).Contains('UI_DELIVERY_PRIOR_THINKING_' + $hostName) } 'Off immediately removes previously displayed thinking'
        $deliveryControl = Join-Path $env:TANSR_NATIVE_UI_DIRECTORY ('native-delivery-' + $hostName + '.json')
        [IO.File]::WriteAllText($deliveryControl, '{"phase":1}', (New-Object Text.UTF8Encoding($false)))
        Wait-Answer $window ('UI_DELIVERY_CONTINUED_' + $hostName) $false
        $deliveryText = Read-Text (Find-Id $window 'Conversation')
        if ($deliveryText.Contains('UI_DELIVERY_HIDDEN_' + $hostName) -or $deliveryText.Contains('UI_DELIVERY_FINAL_' + $hostName)) { throw 'Dynamic delivery changed the old block or prematurely published a new final/off block.' }
        [IO.File]::WriteAllText($deliveryControl, '{"phase":2}', (New-Object Text.UTF8Encoding($false)))
        Wait-Answer $window ('UI_DELIVERY_FINAL_' + $hostName)
        if ((Read-Text (Find-Id $window 'Conversation')).Contains('UI_DELIVERY_HIDDEN_' + $hostName)) { throw 'Off thinking became visible at completion.' }
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Dynamic delivery replaced the original session.' }
        Record $hostName 'dynamic-delivery' 'same active session and view; old stream block continued, new off/final blocks respected new policy and original completion'
        Select-NamedItem (Wait-Id $window 'TextDeliveryMode') 'stream' $process.Id
        Select-NamedItem (Wait-Id $window 'ThinkingDeliveryMode') 'stream' $process.Id
        if ((Read-Text (Find-Id $window 'Conversation')).Contains('UI_DELIVERY_PRIOR_THINKING_' + $hostName)) { throw 'Switching thinking back on revived previously cleared thinking.' }
        Send-Prompt $window ('UI_HOLD_' + $hostName)
        Wait-Answer $window ('UI_HOLDING_' + $hostName) $false
        Set-Text (Wait-Id $window 'MessageDraft') 'Synthetic same-turn follow-up'
        Click (Wait-Id $window '同轮插入草稿')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('accepted') } 'same-turn accepted'
        Click (Wait-Id $window '查原插入回执')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('accepted') } 'original input query'
        Click (Wait-Id $window '显式重投原插入')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('accepted') } 'original input explicit replay'
        Click (Wait-Id $window '取消当前工作')
        $null = Wait-For { (Read-Text (Find-Id $window 'Conversation')).Contains('[status:idle]') } 'turn cancelled'
        Record $hostName 'input-and-cancel' 'same original session; cancellation is explicit'
        Click (Wait-Id $window '仅断开本机连接')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'detached'
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Detaching replaced the session identity.' }
        Click (Wait-Id $window '连接 / 创建')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'resumed session connected'
        Connect-NativeUiTools $window $process.Id $hostName 'resumed' $session $scopeFile $fixtureReady
        Send-Prompt $window ('UI_RESUMED_' + $hostName)
        $null = Wait-For { (Pending-Count $window) -gt 0 } 'resumed fresh permission'
        Click (Wait-Id $window '批准')
        Wait-ApprovedAnswer $window $process.Id ('UI_DONE_RESUMED_' + $hostName)
        $null = Wait-For { $window.Current.Name -eq ('Approved ' + $hostName + ' RESUMED') } 'resumed fresh native delegate executed'
        if ((Read-Text (Find-Id $window 'ResumeSession')) -ne $session) { throw 'Resumed native tool used a replacement session.' }
        Record $hostName 'resume-fresh-tool' 'same original session, initialized replay boundary, newly approved native delegate executed'
        Click (Wait-Id $window '撤销本机 Skills / MCP')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已撤销') } 'extensions revoked'
        foreach ($extension in @('MCP', 'SKILL')) {
            Send-Prompt $window ('UI_' + $extension + '_REVOKED_' + $hostName)
            Wait-ApprovedAnswer $window $process.Id ('UI_DONE_' + $extension + '_REVOKED_' + $hostName)
        }
        Record $hostName 'extension-revocation' 'same session retained tool declarations but both original delegates returned errors after explicit host revocation'
        Click (Wait-Id $window '仅断开本机连接')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'final detached'
        foreach ($mode in @('stream', 'final', 'off')) {
            $narratorMode = if ($mode -eq 'stream') { 'quiet' } elseif ($mode -eq 'final') { 'normal' } else { 'verbose' }
            Select-NamedItem (Wait-Id $window 'NarratorVerbosity') $narratorMode $process.Id
            Select-NamedItem (Wait-Id $window 'ThinkingDeliveryMode') $mode $process.Id
            Select-NamedItem (Wait-Id $window 'TextDeliveryMode') $(if ($mode -eq 'final') { 'final' } else { 'stream' }) $process.Id
            Click (Wait-Id $window '连接 / 创建')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } ('resume thinking mode ' + $mode)
            Connect-NativeUiTools $window $process.Id $hostName ('thinking-' + $mode) $session $scopeFile $fixtureReady
            Send-Prompt $window ('UI_THINK_' + $mode.ToUpperInvariant() + '_' + $hostName)
            Wait-Answer $window ('UI_DONE_THINK_' + $mode.ToUpperInvariant() + '_' + $hostName)
            $shown = Read-Text (Find-Id $window 'Conversation')
            $thinking = 'UI_REASONING_' + $mode.ToUpperInvariant() + '_' + $hostName
            if (($mode -eq 'off' -and $shown.Contains($thinking)) -or ($mode -ne 'off' -and !$shown.Contains($thinking))) { throw 'Thinking delivery mode did not control native presentation.' }
            Record $hostName ('thinking-' + $mode) 'original session resumed, real thinking/text events projected according to the selected delivery mode'
            Click (Wait-Id $window '运行叙述')
            $narratorWindow = Wait-For { Find-ProcessWindow $process.Id 'SessionNarrator' } 'incremental narration window'
            $null = Wait-For { (Read-Text (Find-Id $narratorWindow 'SessionNarration')).Contains('turn completed') } 'narration original terminal event'
            $narration = Read-Text (Find-Id $narratorWindow 'SessionNarration')
            if ($narration.Contains('UI_REASONING_')) { throw 'Narration exposed thinking body.' }
            if ($narratorMode -eq 'quiet' -and $narration -match 'tool [^\r\n]+ (started|completed|proposed)') { throw 'Quiet narration exposed tool detail.' }
            if ($narratorMode -eq 'normal' -and $narration.Contains('assistant:')) { throw 'Normal narration emitted verbose assistant previews.' }
            if ($narratorMode -eq 'verbose' -and (!$narration.Contains('assistant:') -or !$narration.Contains('thinking finished'))) { throw 'Verbose narration omitted actual block completion summaries.' }
            $narratorWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
            Record $hostName ('narrator-' + $narratorMode) 'same original event pump, incremental narration, original verbosity filters, no thinking body'
            Click (Wait-Id $window '仅断开本机连接')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'thinking mode detached'
        }
        $mediaControl = Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'native-media-control.json'
        try {
            [IO.File]::WriteAllText($mediaControl, '{"enabled":false}', (New-Object Text.UTF8Encoding($false)))
            Set-Text (Wait-Id $window 'ResumeSession') ''
            Click (Wait-Id $window '连接 / 创建')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'explicit fresh media-disabled session'
            if ((Read-Text (Find-Id $window 'ResumeSession')) -eq $session) { throw 'Media-off scenario did not create the explicitly requested new session.' }
            Invoke-NativeMediaDeniedScenario $window $process.Id $hostName $env:TANSR_NATIVE_UI_DIRECTORY
            Click (Wait-Id $window '仅断开本机连接')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'media-disabled session detached'
        } finally { [IO.File]::WriteAllText($mediaControl, '{"enabled":true}', (New-Object Text.UTF8Encoding($false))) }
        } else {
            Click (Wait-Id $window '仅断开本机连接')
            $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'independent group detached'
        }
        if ($Groups -contains 'media' -and $Groups -notcontains 'primary' -and $Groups -notcontains 'continuity') {
            $mediaControl = Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'native-media-control.json'
            try {
                [IO.File]::WriteAllText($mediaControl, '{"enabled":false}', (New-Object Text.UTF8Encoding($false)))
                Set-Text (Wait-Id $window 'ResumeSession') ''
                Click (Wait-Id $window '连接 / 创建')
                $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'explicit fresh media-disabled session'
                if ((Read-Text (Find-Id $window 'ResumeSession')) -eq $session) { throw 'Media-off scenario did not create the explicitly requested new session.' }
                Invoke-NativeMediaDeniedScenario $window $process.Id $hostName $env:TANSR_NATIVE_UI_DIRECTORY
                Click (Wait-Id $window '仅断开本机连接')
                $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开') } 'media-disabled session detached'
            } finally { [IO.File]::WriteAllText($mediaControl, '{"enabled":true}', (New-Object Text.UTF8Encoding($false))) }
        }
        if ($Groups -contains 'primary' -or $Groups -contains 'memory') {
        $ready = [IO.File]::ReadAllText((Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'ready.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
        $memoryConfiguration = $ready.memory
        [IO.File]::WriteAllText($memoryConfiguration.controlFile, '{"enabled":true,"minimumDeletionGeneration":"0"}', (New-Object Text.UTF8Encoding($false)))
        Set-Text (Wait-Id $window 'ResumeSession') ''
        Click (Wait-Id $window '连接 / 创建')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接') } 'explicit new device-memory session'
        $memorySession = Read-Text (Find-Id $window 'ResumeSession')
        $memoryIdentity = Wait-For {
            if (Test-Path -LiteralPath $memoryConfiguration.identityFile) {
                Read-NativeMediaRecords ([IO.Path]::GetDirectoryName($memoryConfiguration.identityFile)) ([IO.Path]::GetFileName($memoryConfiguration.identityFile)) | Where-Object { $_.sessionId -eq $memorySession } | Select-Object -First 1
            }
        } 'trusted original memory source identity'
        $memoryRoot = Join-Path $target ($hostName + '-device-memory')
        $memoryWorkspace = Join-Path $memoryRoot 'workspace'
        New-NativePrivateWorkspace $memoryWorkspace
        $deviceConfiguration = Join-Path $memoryRoot 'device.json'
        $device = @{
            format = 'tansr-example-terminal-device-v1'; enablePreview = $true; serveUrl = $ServeUrl; allowInsecureLoopback = $true
            sessionId = $memorySession; executorId = $memoryConfiguration.executorId; trustedScopeFile = $scopeFile
            controllerTokenEnvironment = 'TANSR_NATIVE_UI_TOKEN'; deviceTokenEnvironment = 'TANSR_NATIVE_UI_TOKEN'
            bindingRequestId = ('native-ui-memory-' + $hostName); allowedTools = @('SearchMemory', 'Read', 'List', 'Write', 'Edit', 'Task')
            workspace = @{ path = $memoryWorkspace; id = 'native-ui-workspace'; revision = '1'; allWritersCooperate = $true }
            journal = @{ path = (Join-Path $memoryRoot 'executor.sqlite'); mode = 'create'; compactCompletedReceipts = $true }
            encryption = @{ provider = 'dpapi-current-user'; path = (Join-Path $memoryRoot 'memory.key'); keyId = 'native-ui-memory'; mode = 'create' }
            publication = @{ path = (Join-Path $memoryRoot 'memory.sqlite'); mode = 'create'; identity = $memoryIdentity.deviceIdentity; maxTransfers = 64; maxStagingBytes = 8388608; maxPages = 32768 }
        }
        [IO.File]::WriteAllText($deviceConfiguration, ($device | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
        Invoke-NativeMediaModalAction (Wait-Id $window '连接本机设备工具') $process.Id $deviceConfiguration
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('device=') } 'same-session terminal and publication backend bound'
        Send-Prompt $window ('UI_TASK_' + $hostName)
        $null = Wait-For {
            Approve-SyntheticPending $window $process.Id
            $text = Read-Text (Find-Id $window 'Conversation')
            $text.Contains('UI_DONE_TASK_' + $hostName) -and $text.Contains('[status:idle]')
        } 'original foreground Task completed' 35
        Click (Wait-Id $window '会话工作台 / 能力 / Task')
        $taskWorkspace = Wait-For { Find-ProcessWindow $process.Id 'SessionWorkspace' } 'original child event projection'
        Workspace-Action $taskWorkspace '工具 / Task / 待办 / 用量' 'Task ' $process.Id
        $taskText = Read-Text (Find-Id $taskWorkspace 'WorkspaceResult')
        if ($taskText -notmatch 'Task [^\r\n]+ · (completed|settled) ·') { throw 'Task did not render an original completed/settled child projection.' }
        $taskWorkspace.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Record $hostName 'foreground-task' 'original Serve Task ran its bounded child prompt with no child tools; original child completion reached native Workbench.Agents'
        $null = Wait-For {
            Approve-SyntheticPending $window $process.Id
            $statusFile = Join-Path $env:TANSR_NATIVE_UI_DIRECTORY 'native-memory-status.json'
            if (!(Test-Path -LiteralPath $statusFile)) { return $false }
            $state = Read-NativeMediaRecords ([IO.Path]::GetDirectoryName($statusFile)) ([IO.Path]::GetFileName($statusFile)) | Where-Object { $_.sessionId -eq $memorySession } | Select-Object -First 1
            $state -and $state.idle
        } 'original Task background resources idle' 50
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
            $state = Read-NativeMediaRecords ([IO.Path]::GetDirectoryName($statusFile)) ([IO.Path]::GetFileName($statusFile)) | Where-Object { $_.sessionId -eq $memorySession } | Select-Object -First 1
            $state -and $state.idle -and $state.extracted
        } 'original memory extraction settled' 50
        Click (Wait-Id $window '配置 / 记忆（preview）')
        $controls = Wait-For { Find-ProcessWindow $process.Id 'SessionControls' } 'memory commands view'
        Control-Action $controls '读取记忆来源' '' 'client-managed' $process.Id
        Control-Action $controls '置顶文字' ('Native UI pin ' + $hostName) 'committed' $process.Id
        Control-Action $controls '查询原记忆操作' '' 'committed' $process.Id
        Control-Action $controls '重放原记忆操作' '' 'committed' $process.Id
        Select-NamedItem (Wait-Id $controls 'SessionControlAction') '记住本轮' $process.Id
        Click (Wait-Id $controls 'SessionControlExecute')
        $null = Wait-For {
            Approve-SyntheticPending $window $process.Id
            (Read-Text (Find-Id $controls 'SessionControlResult')).Contains('committed') -and (Find-Id $controls 'SessionControlExecute').Current.IsEnabled
        } 'original remember command and promotion approval' 50
        $null = Wait-For {
            Approve-SyntheticPending $window $process.Id
            $state = Read-NativeMediaRecords $env:TANSR_NATIVE_UI_DIRECTORY 'native-memory-status.json' | Where-Object { $_.sessionId -eq $memorySession } | Select-Object -First 1
            $state -and $state.idle
        } 'memory promotion settled' 50
        Control-Action $controls '遗忘主题' $memoryConfiguration.topic 'committed' $process.Id
        Control-Action $controls '读取记忆来源' '' 'deletionGeneration' $process.Id
        $memoryState = ([regex]::Split((Read-Text (Find-Id $controls 'SessionControlResult')), '\r?\n\r?\n'))[0] | ConvertFrom-Json
        [IO.File]::WriteAllText((Join-Path $memoryRoot 'memory-metadata-response.json'),($memoryState | ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText($memoryConfiguration.controlFile, (@{ enabled = $true; minimumDeletionGeneration = [string]$memoryState.memory.deletionGeneration } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        Record $hostName 'device-memory-commands' 'original source and SQLite publication bound through one terminal host; real extraction, pin/query/replay, approved remember and forget completed'
        $controls.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Click (Wait-Id $window '关闭会话')
        $null = Wait-For { (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('核心资源已确认排空') } 'memory session close with original resource completion' 30
        [IO.File]::WriteAllText($memoryConfiguration.controlFile, '{"enabled":false}', (New-Object Text.UTF8Encoding($false)))
        $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        if (!$process.WaitForExit(10000)) { throw 'Native application failed to close.' }
        Record $hostName 'detach-close' 'no implicit remote session replacement or process left running'
        } else {
            $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
            if (!$process.WaitForExit(10000)) { throw 'Native continuity application failed to close.' }
            Record $hostName 'detach-close' 'original detached continuity consumer exited without replacing a remote session'
        }
        } catch { Record-GroupFailure $hostName 'primary-session-media-memory' $_ } }
        if ($Groups -contains 'storage') {
            try { Invoke-NativeStorageScenario $entry[1] $hostName $target $fixtureReady.storage }
            catch { Record-GroupFailure $hostName 'sdk2-storage' $_ }
        }
        if ($Groups -contains 'legacy') {
            try { Invoke-NativeLegacyWorkspaceScenario $entry[1] $hostName $fixtureReady.legacy }
            catch { Record-GroupFailure $hostName 'trusted-legacy-cwd' $_ }
        }
    }
    if ($caseFailures.Count -gt 0) { throw ('Original native UI cases failed: ' + $caseFailures.Count + '; inspect case-failures.json. No complete acceptance pass.') }
    [IO.File]::WriteAllText((Join-Path $target 'result.json'), ($evidence | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
} catch {
    [IO.File]::WriteAllText((Join-Path $target 'failure.txt'), (($_ | Out-String) + [Environment]::NewLine + $_.ScriptStackTrace), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $target 'partial.json'), ($evidence | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    try {
        $states = foreach ($selectedProcess in $script:nativeUiProcesses) {
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
    foreach ($process in $script:nativeUiProcesses) {
        if (!$process.HasExited) { $null = $process.CloseMainWindow(); if (!$process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force } }
        $process.Dispose()
    }
}
