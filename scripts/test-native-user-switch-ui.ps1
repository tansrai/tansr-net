# Runs the original native example controls against ServeNativeUserSwitchFixture.
# The fixture supplies local synthetic identities; no production token is used.
# Use a fresh evidence directory. The helper owns source validation and shutdown.
# Run once for WINFORMS and once for WPF against the same bounded fixture.
param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$ReadyFile,[Parameter(Mandatory=$true)][string]$OutputDirectory,[ValidateSet('WINFORMS','WPF')][string]$HostName='WINFORMS')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$driver=Join-Path $repository 'scripts/test-native-ui.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText($driver,[Text.Encoding]::UTF8),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Original UI driver parse failure'}
foreach($name in @('Wait-For','Find-Id','Wait-Id','Find-ProcessWindow','Read-Text','Set-Text','Click','Pending-Count')){
 $definition=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
 if(!$definition){throw ('Original helper missing '+$name)};. ([scriptblock]::Create($definition.Extent.Text))
}
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $repository 'scripts/native-media-ui.ps1'),[Text.Encoding]::UTF8)))
$ready=[IO.File]::ReadAllText($ReadyFile,[Text.Encoding]::UTF8)|ConvertFrom-Json
$target=[IO.Path]::GetFullPath($OutputDirectory);if(Test-Path -LiteralPath $target){throw 'Preserve prior evidence'};[void][IO.Directory]::CreateDirectory($target)
$rows=New-Object 'System.Collections.Generic.List[object]';$violations=New-Object 'System.Collections.Generic.List[string]'
$scopeFile=Join-Path $target 'trusted-scope.json'
function Save-Scope($User){[IO.File]::WriteAllText($scopeFile,(@{principal=$User.principal;scope=$User.scope}|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))}
function Evidence([string]$Stage,$Value){$rows.Add([pscustomobject]@{stage=$Stage;value=$Value;utc=[DateTimeOffset]::UtcNow.ToString('O')});[IO.File]::WriteAllText((Join-Path $target 'partial.json'),($rows|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))}
function Set-SyntheticIdentityToken($Element,[string]$Value){
 if($HostName -ne 'WPF'){Set-Text $Element $Value;return}
 if($Value -notmatch '^[a-f0-9]{64}$' -or $Element.Current.ProcessId -ne $process.Id -or $Element.Current.AutomationId -ne 'SessionToken'){throw 'Only the owned synthetic identity password field may receive test input'}
 Add-Type -AssemblyName System.Windows.Forms
 $Element.SetFocus()
 $null=Wait-For {$focused=[System.Windows.Automation.AutomationElement]::FocusedElement;$focused -and $focused.Current.ProcessId -eq $process.Id -and $focused.Current.AutomationId -eq 'SessionToken'} 'owned synthetic token field focused'
 # WPF PasswordBox intentionally has no ValuePattern. Type only the fixture's
 # restricted hex value into its verified focused field, without clipboard use.
 [System.Windows.Forms.SendKeys]::SendWait('^a'+$Value)
}
function Observe-SyntheticToken([string]$Stage){
 if($HostName -ne 'WINFORMS'){return}
 $field=Wait-Id $window 'SessionToken';$handle=[IntPtr]$field.Current.NativeWindowHandle
 if($field.Current.ProcessId -ne $process.Id -or ![TansrNativeDialogClick]::IsEditor($handle,$process.Id)){throw 'Owned synthetic token control missing'}
 $actual=[TansrNativeDialogClick]::GetEditorText($handle,$process.Id)
 $resume=Wait-Id $window 'ResumeSession'
 Evidence $Stage @{tokenMatchesB=($actual -ceq $ready.users[1].token);tokenLength=$actual.Length;tokenHandle=$handle.ToInt64();resumeHandle=$resume.Current.NativeWindowHandle;resume=Read-Text $resume;connectEnabled=(Find-Id $window '连接 / 创建').Current.IsEnabled;status=Read-Text (Find-Id $window 'ConnectionStatus')}
 if($actual -cne $ready.users[1].token){throw 'Owned synthetic B token differs before explicit connection'}
}
function Send-Identity([string]$Prompt,[string]$Expected){Set-Text (Wait-Id $window 'MessageDraft') $Prompt;Click (Wait-Id $window '发送');$null=Wait-For { $text=Read-Text (Find-Id $window 'Conversation');$text.Contains($Expected) -and $text.Contains('[status:idle]')} $Expected}
foreach($variable in Get-ChildItem Env:){if($variable.Name -match '(TOKEN|SECRET|PASSWORD|API.?KEY|APP.?KEY|CREDENTIAL)' -or $variable.Name -like 'TANSR_*'){[Environment]::SetEnvironmentVariable($variable.Name,$null,'Process')}}
Save-Scope $ready.users[0]
$env:TANSR_SERVE_URL=$ready.url;$env:TANSR_SESSION_TOKEN=$ready.users[0].token;$env:TANSR_SESSION_CONTRACT='sdk1';$env:TANSR_ALLOW_HTTP_LOOPBACK='1';$env:TANSR_TERMINAL_PREVIEW='1';$env:TANSR_TRUSTED_SCOPE_FILE=$scopeFile;$env:TANSR_EXAMPLE_STATE_FILE=Join-Path $target 'original-presentation.json'
$process=$null
try{
 $process=Start-Process -FilePath $Executable -PassThru
 $window=Wait-For {Find-ProcessWindow $process.Id 'TansrAssistant'} 'owned identity example'
 Click (Wait-Id $window '连接 / 创建');$null=Wait-For {(Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接')} 'A original session connected'
 $sessionA=Read-Text (Find-Id $window 'ResumeSession')
 Send-Identity ('UI_USER_A_' + $HostName) ('UI_DONE_USER_A_' + $HostName)
 Set-Text (Wait-Id $window 'MessageDraft') ('UI_USER_PENDING_A_' + $HostName);Click (Wait-Id $window '发送')
 $null=Wait-For {(Pending-Count $window) -gt 0} 'A original pending permission'
 Set-Text (Wait-Id $window 'MessageDraft') 'NATIVE_PRIVATE_DRAFT_A'
 Evidence 'A-before-switch' @{sessionId=$sessionA;pending=Pending-Count $window;draft=Read-Text (Find-Id $window 'MessageDraft');conversation=Read-Text (Find-Id $window 'Conversation')}
 Click (Wait-Id $window '仅断开本机连接');$null=Wait-For {(Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已断开')} 'A detached without close'
 Save-Scope $ready.users[1]
 Set-SyntheticIdentityToken (Wait-Id $window 'SessionToken') $ready.users[1].token
 Click (Wait-Id $window '连接 / 创建')
 $null=Wait-For { (Find-Id $window '连接 / 创建').Current.IsEnabled -and (Read-Text (Find-Id $window 'ConnectionStatus')).Contains('forbidden') } 'B refused original A session'
 Evidence 'B-original-A-session-rejected' @{sessionId=Read-Text (Find-Id $window 'ResumeSession');status=Read-Text (Find-Id $window 'ConnectionStatus');draft=Read-Text (Find-Id $window 'MessageDraft')}
 if((Read-Text (Find-Id $window 'MessageDraft')).Length -ne 0){$violations.Add('B retained A draft after rejected cross-user resume')}
 if((Read-Text (Find-Id $window 'Conversation')) -match 'UI_USER_A_|UI_DONE_USER_A_|UI_USER_PENDING_A_'){$violations.Add('Rejected cross-user resume retained A presentation')}
 if((Pending-Count $window) -ne 0){$violations.Add('Rejected cross-user resume retained A pending permission')}
 Observe-SyntheticToken 'B-token-before-clear-resume'
 Set-Text (Wait-Id $window 'ResumeSession') ''
 Observe-SyntheticToken 'B-token-after-clear-resume'
 Click (Wait-Id $window '连接 / 创建');$null=Wait-For {(Read-Text (Find-Id $window 'ConnectionStatus')).Contains('已连接')} 'B independent session connected'
 $sessionB=Read-Text (Find-Id $window 'ResumeSession');if($sessionA -eq $sessionB){throw 'B reused A session'}
 $null=Wait-For {!(Read-Text (Find-Id $window 'Conversation')).Contains(('UI_USER_A_' + $HostName))} 'B view no A conversation'
 $draft=Read-Text (Find-Id $window 'MessageDraft');$pending=Pending-Count $window
 Evidence 'B-new-session-presentation' @{sessionId=$sessionB;draft=$draft;pending=$pending;conversation=Read-Text (Find-Id $window 'Conversation')}
 if($draft.Length -ne 0){$violations.Add('B retained A draft')};if($pending -ne 0){$violations.Add('B retained A pending permission')}
 Click (Wait-Id $window '历史')
 $historyView=Wait-For {
  $owned=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
  foreach($root in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$owned)){
   foreach($candidate in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)){
    if($candidate.Current.IsPassword -or $candidate.Current.AutomationId -eq 'SessionToken'){continue}
    if($candidate.Current.ControlType -ne [System.Windows.Automation.ControlType]::Edit -and $candidate.Current.ControlType -ne [System.Windows.Automation.ControlType]::Document -and $candidate.Current.ClassName -ne 'TextBox'){continue}
    $value=Read-Text $candidate
    if(!$value.StartsWith('{')){continue}
    try{$document=$value|ConvertFrom-Json}catch{continue}
    if($document.sessionId -ne $sessionB -or !$document.PSObject.Properties['messages']){continue}
    $owner=$candidate;$close=$null
    while($owner -and $owner.Current.ProcessId -eq $process.Id){
     if($owner.Current.AutomationId -ne 'TansrAssistant' -and $owner.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern,[ref]$close)){return @{window=$owner;text=$value;class=$candidate.Current.ClassName;title=$owner.Current.Name}}
     $owner=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($owner)
    }
   }
  }
 } 'B original owned history JSON view'
 $historyWindow=$historyView.window;$history=$historyView.text
 Evidence 'B-history-accessibility' @{title=$historyView.title;textControlClass=$historyView.class}
 if($history -match 'UI_USER_A_|NATIVE_PRIVATE_DRAFT_A|UI_USER_PENDING_A_'){$violations.Add('B history contains A data')}
 Evidence 'B-original-history' $history
 $historyWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Send-Identity ('UI_USER_B_' + $HostName) ('UI_DONE_USER_B_' + $HostName)
 Evidence 'B-original-model-turn' @{sessionId=$sessionB;conversation=Read-Text (Find-Id $window 'Conversation')}
 Set-Text (Wait-Id $window 'MessageDraft') 'NATIVE_PRIVATE_DRAFT_B';Click (Wait-Id $window '保存本机草稿')
 Click (Wait-Id $window '关闭会话');$null=Wait-For {(Read-Text (Find-Id $window 'ConnectionStatus')).Contains('核心资源已确认排空')} 'B resources settled' 30
 $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close();if(!$process.WaitForExit(10000)){throw 'Identity example exit timeout'}
 $process.Dispose();$process=$null
 # Cold restart under the host's current B scope must recover B's own draft,
 # while returning to A must retain A's untouched store. Neither opens a client.
 foreach($account in @(@{index=1;expected='NATIVE_PRIVATE_DRAFT_B';forbidden='NATIVE_PRIVATE_DRAFT_A'},@{index=0;expected='NATIVE_PRIVATE_DRAFT_A';forbidden='NATIVE_PRIVATE_DRAFT_B'})){
  Save-Scope $ready.users[$account.index];$env:TANSR_SESSION_TOKEN=$null
  $process=Start-Process -FilePath $Executable -PassThru
  $window=Wait-For {Find-ProcessWindow $process.Id 'TansrAssistant'} 'current identity cold restart'
  $draft=Read-Text (Wait-Id $window 'MessageDraft');$presentation=Read-Text (Wait-Id $window 'Conversation')
  if($draft -ne $account.expected -or $presentation.Contains($account.forbidden)){$violations.Add('Cold identity partition mixed or lost its original draft')}
  Click (Wait-Id $window '恢复本机草稿')
  if((Read-Text (Wait-Id $window 'MessageDraft')) -ne $account.expected){$violations.Add('Original restore action did not return current identity draft')}
  Evidence ('cold-user-'+$account.index) @{draft=$draft;presentation=$presentation;noToken=$true;connected=$false}
  $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close();if(!$process.WaitForExit(10000)){throw 'Cold identity consumer exit timeout'}
  $process.Dispose();$process=$null
 }
 if($violations.Count){throw ($violations -join '; ')}
 [IO.File]::WriteAllText((Join-Path $target 'result.json'),($rows|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
}catch{
 $failure=$_
 try{if($process -and !$process.HasExited){$state=@{};foreach($id in @('ConnectionStatus','ResumeSession','MessageDraft','Conversation')){$control=Find-Id $window $id;if($control){$state[$id]=Read-Text $control}};Evidence 'failure-owned-ui' $state}}catch{}
 [IO.File]::WriteAllText((Join-Path $target 'failure.txt'),(($failure|Out-String)+[Environment]::NewLine+$failure.ScriptStackTrace),[Text.UTF8Encoding]::new($false));throw $failure
}
finally{if($process){if(!$process.HasExited){$null=$process.CloseMainWindow();if(!$process.WaitForExit(3000)){Stop-Process -Id $process.Id -Force}};$process.Dispose()}}