# Dot-source from the single scripts/test-native-ui.ps1 driver. This file never launches
# applications, creates a Serve, records a microphone, or selects another process's UI.
# All controls are actual example windows; only the isolated platform/model is synthetic.
function Read-NativeMediaRecords([string]$Directory, [string]$Name = 'native-media-records.json') {
    $path = Join-Path $Directory $Name
    if (!(Test-Path -LiteralPath $path)) { return @() }
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { return @([IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json) }
        catch { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 50 }
    }
}
function Get-NativeMediaCount([string]$Directory, [string]$Path) {
    return @(Read-NativeMediaRecords $Directory | Where-Object { $_.path -eq $Path }).Count
}
function Get-NativeMessageCount([string]$Directory) {
    return @(Read-NativeMediaRecords $Directory 'native-ui-records.json' | Where-Object { $_.kind -eq 'http' -and $_.method -eq 'POST' -and $_.path -match '^/v2/sessions/[^/]+/messages$' }).Count
}
function Wait-NativeMediaIdle($Media, [string]$Status) {
    $null = Wait-For {
        (Find-Id $Media 'MediaAction:刷新模型目录').Current.IsEnabled -and (Read-Text (Find-Id $Media 'MediaStatus')).Contains($Status)
    } ('media status ' + $Status)
}
function Find-NativeMediaDialog([int]$ProcessId) {
    $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $classCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $classCondition)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}
function Set-NativeMediaDialogFile([int]$ProcessId, [string]$Path) {
    $dialog = Wait-For { Find-NativeMediaDialog $ProcessId } 'owned native file dialog'
    $editor = Find-Id $dialog '1148'
    $value = $null
    if (!$editor -or !$editor.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$value)) {
        $editor = Find-Id $dialog '1001'; $value = $null
        if (!$editor -or !$editor.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$value)) {
            $type = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
            $editors = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type)
            $editor = @($editors | Where-Object { $_.Current.Name -match 'File name|文件名' }) | Select-Object -First 1
            if (!$editor) { throw 'The owned common file dialog has no file-name editor.' }
        }
    }
    Set-Text $editor ([IO.Path]::GetFullPath($Path))
    Click (Find-Id $dialog '1')
    $null = Wait-For { !(Find-NativeMediaDialog $ProcessId) } 'file dialog accepted'
}
function Confirm-NativeSpeechReset([int]$ProcessId) {
    $dialog = Wait-For { Find-NativeMediaDialog $ProcessId } 'explicit new paid batch confirmation'
    Click (Find-Id $dialog '1')
    $null = Wait-For { !(Find-NativeMediaDialog $ProcessId) } 'new speech batch confirmed'
}
function Invoke-NativeMediaModalAction($Button, [int]$ProcessId, [string]$Path, [switch]$Confirm) {
    # net48 InvokePattern may block while ShowDialog is active. Only the selected
    # application's exact button is invoked on this worker; the one driver still owns
    # every subsequent UI action, scoped to that process's native dialog.
    $worker = [PowerShell]::Create()
    $null = $worker.AddScript('param($button) $ErrorActionPreference = "Stop"; $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()').AddArgument($Button)
    $pending = $worker.BeginInvoke()
    try {
        if ($Confirm) { Confirm-NativeSpeechReset $ProcessId } else { Set-NativeMediaDialogFile $ProcessId $Path }
        $null = Wait-For { $pending.IsCompleted } 'owned modal invoke returned'
        $null = $worker.EndInvoke($pending)
        if ($worker.HadErrors) { throw ($worker.Streams.Error | Out-String) }
    }
    finally {
        if (!$pending.IsCompleted) {
            $dialog = Find-NativeMediaDialog $ProcessId
            if ($dialog) { $dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }
            $worker.Stop()
        }
        $worker.Dispose()
    }
}
function Open-NativeMediaWindow($MainWindow, [int]$ProcessId) {
    Click (Find-Id $MainWindow '媒体 / 转写 / 朗读')
    $media = Wait-For { Find-ProcessWindow $ProcessId 'MediaWindow' } 'media window'
    $null = Wait-For { (Find-Id $media 'MediaAction:刷新模型目录').Current.IsEnabled } 'media catalog loaded'
    return $media
}
function Close-NativeMediaWindow($Media) { $Media.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }
function Select-NativeMediaLastItem($Media, [string]$NamePattern) {
    $type = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $item = Wait-For {
        $items = (Find-Id $Media 'MediaItems').FindAll([System.Windows.Automation.TreeScope]::Descendants, $type)
        @($items | Where-Object { $_.Current.Name -match $NamePattern }) | Select-Object -Last 1
    } ('media item ' + $NamePattern)
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Invoke-NativeMediaScenario($MainWindow, [int]$ProcessId, [string]$HostName,
    [string]$FixtureDirectory, [string]$EvidenceDirectory, [string]$ServeEvidenceDirectory) {
    $png = Join-Path $FixtureDirectory 'blue.png'; $wav = Join-Path $FixtureDirectory 'silence.wav'
    foreach ($file in @($png, $wav)) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Synthetic media fixture is missing.' } }
    # Real native image picker -> SDK MessageBlock.Image -> real Serve protocol -> model assertion.
    Set-Text (Find-Id $MainWindow 'MessageDraft') ('UI_IMAGE_INPUT_' + $HostName)
    Invoke-NativeMediaModalAction (Find-Id $MainWindow '发送图片与草稿') $ProcessId $png
    Wait-Answer $MainWindow ('UI_DONE_IMAGE_INPUT_' + $HostName)
    Record $HostName 'media-image-input' 'native picker sent the legal PNG; real model boundary asserted an image block'

    Send-Prompt $MainWindow ('UI_MEDIA_BAD_IMAGE_' + $HostName)
    $null = Wait-For {
        if ((Pending-Count $MainWindow) -gt 0) { Click (Find-Id $MainWindow '批准') }
        $text = Read-Text (Find-Id $MainWindow 'Conversation')
        $text.Contains('UI_DONE_MEDIA_BAD_IMAGE_' + $HostName) -and $text.Contains('[status:idle]')
    } 'bad historical image generated by original ImageGen'
    $media = Open-NativeMediaWindow $MainWindow $ProcessId
    try {
        Click (Find-Id $media 'MediaAction:恢复历史媒体')
        $null = Wait-For { (Find-Id $media 'MediaAction:刷新模型目录').Current.IsEnabled } 'original history loaded'
        Select-NativeMediaLastItem $media '^历史 .*Image'
        Click (Find-Id $media 'MediaAction:预览选中产物'); Wait-NativeMediaIdle $media 'media_host_not_authorized'
        Record $HostName 'media-bad-history' 'original history artifact remained visible; unapproved HTTPS link rejected before download'

        $messagesBefore = Get-NativeMessageCount $ServeEvidenceDirectory
        $asrBefore = Get-NativeMediaCount $ServeEvidenceDirectory '/t1/asr'
        Invoke-NativeMediaModalAction (Find-Id $media 'MediaAction:音频文件转草稿') $ProcessId $wav
        Wait-NativeMediaIdle $media '未发送'
        if ((Read-Text (Find-Id $MainWindow 'MessageDraft')) -ne 'UI transcription draft 中文') { throw 'File transcription did not fill the main draft.' }
        if ((Get-NativeMessageCount $ServeEvidenceDirectory) -ne $messagesBefore) { throw 'ASR implicitly sent a user message.' }
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/asr') -ne ($asrBefore + 1)) { throw 'File transcription did not make exactly one original ASR call.' }
        Close-NativeMediaWindow $media; $media = Open-NativeMediaWindow $MainWindow $ProcessId
        Invoke-NativeMediaModalAction (Find-Id $media 'MediaAction:音频文件转草稿') $ProcessId $wav
        Wait-NativeMediaIdle $media '未发送'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/asr') -ne ($asrBefore + 1) -or (Get-NativeMessageCount $ServeEvidenceDirectory) -ne $messagesBefore) { throw 'Reopening the media window repeated or sent transcription.' }
        Record $HostName 'media-file-asr-draft' 'legal synthetic WAV; one ASR request, main draft filled, zero message submissions, no repeat on reopen; not a microphone test'

        $ttsBefore = Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts'
        Set-Text (Find-Id $media 'MediaText') 'abcdefghijklmnopABCDEFGHIJKLMNOP'
        Click (Find-Id $media 'MediaAction:建立新朗读批次'); Wait-NativeMediaIdle $media 'speech_segmentation_required'
        $toggle = (Find-Id $media 'MediaAllowSegmentation').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
        Click (Find-Id $media 'MediaAction:建立新朗读批次'); Wait-NativeMediaIdle $media '已规划 2 段'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne $ttsBefore) { throw 'Planning unexpectedly called a paid endpoint.' }
        Click (Find-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media '1/2'
        Start-Sleep -Milliseconds 400
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne ($ttsBefore + 1)) { throw 'The first explicit segment triggered extra synthesis.' }
        Click (Find-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media '2/2'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne ($ttsBefore + 2)) { throw 'The second explicit segment did not produce exactly one synthesis.' }
        Select-NativeMediaLastItem $media '^朗读段 2'
        Click (Find-Id $media 'MediaAction:预览选中产物'); Wait-NativeMediaIdle $media '已缓存'
        $saved = Join-Path $EvidenceDirectory ($HostName + '-native-speech.wav')
        if (Test-Path -LiteralPath $saved) { throw 'Use a new media save evidence path.' }
        Invoke-NativeMediaModalAction (Find-Id $media 'MediaAction:保存选中产物') $ProcessId $saved
        Wait-NativeMediaIdle $media '已保存'
        if ((Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $wav -Algorithm SHA256).Hash) { throw 'Saved native WAV differs from the actual API result.' }
        Click (Find-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media '已完成'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne ($ttsBefore + 2)) { throw 'Preview, save or completed next repeated TTS.' }
        Record $HostName 'media-segment-preview-save' ('explicit two segments; zero synthesis for plan/preview/save/completed-next; saved WAV SHA256=' + (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash)

        Set-Text (Find-Id $media 'MediaText') 'CANCEL'
        Invoke-NativeMediaModalAction (Find-Id $media 'MediaAction:建立新朗读批次') $ProcessId -Confirm
        Wait-NativeMediaIdle $media '已规划 1 段'
        Click (Find-Id $media 'MediaAction:合成 / 续合下一段')
        $null = Wait-For { (Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -eq ($ttsBefore + 3) } 'accepted held synthetic speech'
        Click (Find-Id $media 'MediaCancel'); Wait-NativeMediaIdle $media '取消'
        Click (Find-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media 'speech_result_unknown_do_not_repeat'
        Close-NativeMediaWindow $media; $media = Open-NativeMediaWindow $MainWindow $ProcessId
        Click (Find-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media 'speech_result_unknown_do_not_repeat'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne ($ttsBefore + 3)) { throw 'Cancelled unknown speech was automatically resubmitted.' }
        Record $HostName 'media-cancel-no-repeat' 'cancelled after original upstream acceptance; unknown status blocks duplicate synthesis, including after window reopen'
    }
    finally { if ($media) { Close-NativeMediaWindow $media } }
}

# Invoke only after the main driver explicitly creates a fresh session with the real
# fixture platform's capabilities disabled. This is not a claim of instant revocation
# of an already cached live session. The main driver owns/reset the control-file mode.
function Invoke-NativeMediaDeniedScenario($MainWindow, [int]$ProcessId, [string]$HostName, [string]$ServeEvidenceDirectory) {
    $before = @(Read-NativeMediaRecords $ServeEvidenceDirectory).Count
    $media = Open-NativeMediaWindow $MainWindow $ProcessId
    try {
        Click (Find-Id $media 'MediaAction:音频文件转草稿'); Wait-NativeMediaIdle $media 'transcription_model_unavailable'
        Set-Text (Find-Id $media 'MediaText') 'Forbidden media'
        Click (Find-Id $media 'MediaAction:建立新朗读批次'); Wait-NativeMediaIdle $media 'speech_model_unavailable'
        if (@(Read-NativeMediaRecords $ServeEvidenceDirectory).Count -ne $before) { throw 'Disabled media capability reached an upstream media endpoint.' }
        Record $HostName 'media-permission-off' 'fresh session consumed disabled platform capabilities; both ASR and TTS native actions failed visibly with zero upstream media calls'
    }
    finally { Close-NativeMediaWindow $media }
}
