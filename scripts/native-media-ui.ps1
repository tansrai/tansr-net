# Dot-source from the single scripts/test-native-ui.ps1 driver. This file never launches
# applications, creates a Serve, or selects another process's UI. The one authorized
# recording case explicitly selects the observed Mobiola input; never a default device.
# All controls are actual example windows; only the isolated platform/model is synthetic.
if (-not ('TansrNativeDialogClick' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class TansrNativeDialogClick {
    [DllImport("user32.dll", SetLastError=true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageTimeoutW", SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    private static void Check(IntPtr button, int expectedProcess) {
        uint process; var kind=new StringBuilder(64);
        if(button==IntPtr.Zero || GetWindowThreadProcessId(button,out process)==0 || process!=(uint)expectedProcess || GetClassName(button,kind,kind.Capacity)==0 || (kind.ToString()!="Button" && !kind.ToString().StartsWith("WindowsForms10.BUTTON.",StringComparison.Ordinal)))
            throw new InvalidOperationException("The selected native dialog button no longer belongs to the expected application.");
    }
    public static void Click(IntPtr button, int expectedProcess) {
        Check(button,expectedProcess);
        IntPtr result;
        if(SendMessageTimeout(button,0x00F5,IntPtr.Zero,IntPtr.Zero,0x0002,3000,out result)==IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),"The original dialog button click was not confirmed; do not repeat blindly.");
    }
    public static bool IsFormsButton(IntPtr window,int expectedProcess) {
        uint process; var kind=new StringBuilder(128);
        return window!=IntPtr.Zero && GetWindowThreadProcessId(window,out process)!=0 && process==(uint)expectedProcess &&
            GetClassName(window,kind,kind.Capacity)!=0 && kind.ToString().StartsWith("WindowsForms10.BUTTON.",StringComparison.Ordinal);
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageTimeoutW", SetLastError=true)] private static extern IntPtr SendText(IntPtr window,uint message,IntPtr w,string text,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageTimeoutW", SetLastError=true)] private static extern IntPtr ReadText(IntPtr window,uint message,IntPtr w,StringBuilder text,uint flags,uint timeout,out IntPtr result);
    public static bool IsEditor(IntPtr window,int expectedProcess) {
        uint process; var kind=new StringBuilder(128);
        if(window==IntPtr.Zero || GetWindowThreadProcessId(window,out process)==0 || process!=(uint)expectedProcess || GetClassName(window,kind,kind.Capacity)==0) return false;
        var name=kind.ToString();
        return name.Equals("Edit",StringComparison.OrdinalIgnoreCase) || name.StartsWith("RichEdit",StringComparison.OrdinalIgnoreCase) || name.StartsWith("WindowsForms10.EDIT.",StringComparison.OrdinalIgnoreCase) || name.StartsWith("WindowsForms10.RichEdit",StringComparison.OrdinalIgnoreCase);
    }
    public static void SetEditorText(IntPtr window,int expectedProcess,string value) {
        if(!IsEditor(window,expectedProcess)) throw new InvalidOperationException("The original owned edit control is unavailable.");
        IntPtr result;
        if(SendText(window,0x000C,IntPtr.Zero,value,0x0002,3000,out result)==IntPtr.Zero || result==IntPtr.Zero) throw new InvalidOperationException("Original edit value was not confirmed; no action is retried.");
    }
    public static string GetEditorText(IntPtr window,int expectedProcess) {
        if(!IsEditor(window,expectedProcess)) throw new InvalidOperationException("The original owned edit control is unavailable.");
        IntPtr size;
        if(SendMessageTimeout(window,0x000E,IntPtr.Zero,IntPtr.Zero,0x0002,3000,out size)==IntPtr.Zero) throw new InvalidOperationException("Original edit length unavailable.");
        var count=size.ToInt64(); if(count<0 || count>2097152) throw new InvalidOperationException("Original edit text exceeds the bounded UI evidence limit.");
        var value=new StringBuilder((int)count+1); IntPtr result;
        if(ReadText(window,0x000D,new IntPtr(value.Capacity),value,0x0002,3000,out result)==IntPtr.Zero) throw new InvalidOperationException("Original edit text unavailable.");
        return value.ToString();
    }
    public static string GetComboSelection(IntPtr window,int expectedProcess) {
        uint process; var kind=new StringBuilder(128);
        if(window==IntPtr.Zero || GetWindowThreadProcessId(window,out process)==0 || process!=(uint)expectedProcess || GetClassName(window,kind,kind.Capacity)==0 ||
           (kind.ToString()!="ComboBox" && !kind.ToString().StartsWith("WindowsForms10.COMBOBOX.",StringComparison.Ordinal)))
            throw new InvalidOperationException("The original owned recording selector is unavailable.");
        IntPtr index, length;
        if(SendMessageTimeout(window,0x0147,IntPtr.Zero,IntPtr.Zero,0x0002,3000,out index)==IntPtr.Zero || index.ToInt64()<0 || index.ToInt64()>1024)
            throw new InvalidOperationException("The original recording selection is not confirmed.");
        if(SendMessageTimeout(window,0x0149,index,IntPtr.Zero,0x0002,3000,out length)==IntPtr.Zero || length.ToInt64()<0 || length.ToInt64()>1024)
            throw new InvalidOperationException("The original recording label is unavailable.");
        var value=new StringBuilder((int)length.ToInt64()+1); IntPtr result;
        // CB_GETLBTEXTLEN is an allocation bound and may overestimate for DBCS;
        // CB_GETLBTEXT returns the actual Unicode character count.
        if(ReadText(window,0x0148,index,value,0x0002,3000,out result)==IntPtr.Zero || result.ToInt64()<0 || result.ToInt64()>length.ToInt64() || result.ToInt64()!=value.Length)
            throw new InvalidOperationException("The original recording label was not fully read.");
        return value.ToString();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    public static bool SelectFormsListLabel(IntPtr window,int expectedProcess,string expectedLabel) {
        uint process; var kind=new StringBuilder(128);
        if(window==IntPtr.Zero || GetWindowThreadProcessId(window,out process)==0 || process!=(uint)expectedProcess || GetClassName(window,kind,kind.Capacity)==0) return false;
        if(!kind.ToString().StartsWith("WindowsForms10.LISTBOX.",StringComparison.Ordinal)) return false;
        IntPtr count,result; int selected=-1;
        if(SendMessageTimeout(window,0x018B,IntPtr.Zero,IntPtr.Zero,0x0002,3000,out count)==IntPtr.Zero || count.ToInt64()<0 || count.ToInt64()>4096)
            throw new InvalidOperationException("Original native list size unavailable.");
        for(int index=0;index<count.ToInt64();index++)
            if(String.Equals(ReadListLabel(window,new IntPtr(index)),expectedLabel,StringComparison.Ordinal)) selected=index;
        if(selected<0) throw new InvalidOperationException("Exact original media label missing in the owned list.");
        if(SendMessageTimeout(window,0x0186,new IntPtr(selected),IntPtr.Zero,0x0002,3000,out result)==IntPtr.Zero || result.ToInt64()!=selected)
            throw new InvalidOperationException("Original media selection was not confirmed; do not repeat blindly.");
        return NotifyListSelection(window,expectedProcess,expectedLabel);
    }
    public static bool NotifyListSelection(IntPtr window,int expectedProcess,string expectedLabel) {
        uint process; var kind=new StringBuilder(128);
        if(window==IntPtr.Zero || GetWindowThreadProcessId(window,out process)==0 || process!=(uint)expectedProcess || GetClassName(window,kind,kind.Capacity)==0) return false;
        if(!kind.ToString().StartsWith("WindowsForms10.LISTBOX.",StringComparison.Ordinal)) return false;
        var selected=ReadSelectedListLabel(window);
        if(!String.Equals(selected,expectedLabel,StringComparison.Ordinal)) throw new InvalidOperationException("Original native list selection differs from the exact UIA item.");
        var parent=GetParent(window); var id=GetDlgCtrlID(window);
        if(parent==IntPtr.Zero || GetWindowThreadProcessId(parent,out process)==0 || process!=(uint)expectedProcess)
            throw new InvalidOperationException("The original list parent is not the owned application.");
        // The native UIA proxy changes LB_GETCURSEL without LBN_SELCHANGE.
        // WinForms SelectedItem then retains its old managed selection, which
        // RefreshItems restores. Deliver the same notification as a user selection.
        IntPtr result;
        // WinForms may allocate a full-width control ID; WM_COMMAND carries
        // only its LOWORD plus the notification code, and the original HWND.
        if(SendMessageTimeout(parent,0x0111,new IntPtr((id & 0xffff) | 0x10000),window,0x0002,3000,out result)==IntPtr.Zero)
            throw new InvalidOperationException("Original list selection notification was not confirmed; do not repeat blindly.");
        if(!String.Equals(ReadSelectedListLabel(window),expectedLabel,StringComparison.Ordinal))
            throw new InvalidOperationException("Original list selection changed after its native notification.");
        return true;
    }
    private static string ReadSelectedListLabel(IntPtr window) {
        IntPtr index;
        if(SendMessageTimeout(window,0x0188,IntPtr.Zero,IntPtr.Zero,0x0002,3000,out index)==IntPtr.Zero || index.ToInt64()<0 || index.ToInt64()>65535)
            throw new InvalidOperationException("Original list index unavailable.");
        return ReadListLabel(window,index);
    }
    private static string ReadListLabel(IntPtr window,IntPtr index) {
        IntPtr length,result;
        if(SendMessageTimeout(window,0x018A,index,IntPtr.Zero,0x0002,3000,out length)==IntPtr.Zero || length.ToInt64()<0 || length.ToInt64()>8192)
            throw new InvalidOperationException("Original list label length unavailable.");
        var value=new StringBuilder((int)length.ToInt64()+1);
        if(ReadText(window,0x0189,index,value,0x0002,3000,out result)==IntPtr.Zero || result.ToInt64()<0 || result.ToInt64()>length.ToInt64() || result.ToInt64()!=value.Length)
            throw new InvalidOperationException("Original list label unavailable.");
        return value.ToString();
    }
    public static void PostClick(IntPtr button, int expectedProcess) {
        Check(button,expectedProcess);
        if(!PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
'@
}
function Get-NativeFileSha256([string]$Path) {
    $stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Path))
    $digest = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($digest.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $digest.Dispose(); $stream.Dispose() }
}
function Read-NativeMediaRecords([string]$Directory, [string]$Name = 'native-media-records.json') {
    $path = Join-Path $Directory $Name
    if (!(Test-Path -LiteralPath $path)) { return @() }
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            # PS5 ConvertFrom-Json emits an array as one pipeline object. Assign
            # first, then emit each record so counts/filtering see actual requests.
            $stream = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader = New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8)
            try { $records = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose(); $stream.Dispose() }
            foreach ($record in $records) { Write-Output $record }
            return
        }
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
    try {
        $null = Wait-For {
            (Find-Id $Media 'MediaAction:刷新模型目录').Current.IsEnabled -and (Read-Text (Find-Id $Media 'MediaStatus')).Contains($Status)
        } ('media status ' + $Status)
    } catch {
        $original = $_
        try {
            $actual = Read-Text (Find-Id $Media 'MediaStatus')
            $model = Find-Id $Media 'MediaSpeechModel'
            $selected = @($model.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name })
            [IO.File]::AppendAllText((Join-Path $target 'media-status-failures.jsonl'), (([pscustomobject]@{expected=$Status;actual=$actual;selectedModels=$selected;draft=(Read-Text (Find-Id $Media 'MediaText'))})|ConvertTo-Json -Depth 4 -Compress)+[Environment]::NewLine,(New-Object Text.UTF8Encoding($false)))
        } catch { }
        throw $original
    }
}
function Find-NativeMediaDialog([int]$ProcessId) {
    $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $classCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $classCondition)
    $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
    foreach ($root in $roots) {
        if ($root.Current.ClassName -eq '#32770') { return $root }
        $owned = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($owned) { return $owned }
    }
    return $null
}
function Set-NativeMediaDialogFile([int]$ProcessId, [string]$Path) {
    # IDs repeat inside the shell: a global 1001 can be the address toolbar and
    # a global 1 can be a file-list item. The shell window can appear before its
    # contents; reacquire the original dialog's ready controls before mutating.
    $editType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $buttonId = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1')
    $buttonType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $ready = Wait-For {
        $dialog = Find-NativeMediaDialog $ProcessId
        if (!$dialog) { return $null }
        $fileName = Find-Id $dialog '1148'
        $editor = $null
        if ($fileName) { $editor = $fileName.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editType) }
        if (!$editor -and $fileName) {
            $value=$null
            if ($fileName.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$value)) { $editor=$fileName }
        }
        if (!$editor) {
            # The observed Save dialog uses a standalone named Edit 1001, while
            # Open wraps its filename edit in ComboBox 1148. Never accept the
            # address bar's reused 1001 ID: require its exact filename label too.
            $nameId = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1001')
            $candidate = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($nameId, $editType)))
            if ($candidate -and $candidate.Current.ProcessId -eq $ProcessId -and $candidate.Current.Name -match '^(文件名|File name)[:：]?$') { $editor=$candidate }
        }
        $accept = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($buttonId, $buttonType)))
        if ($editor -and $editor.Current.IsEnabled -and $accept -and $accept.Current.IsEnabled -and $accept.Current.Name -match '打开|Open|保存|Save') {
            return @{ dialog=$dialog; editor=$editor; accept=$accept }
        }
    } 'owned file-name editor and accept button ready'
    $editor=$ready.editor; $accept=$ready.accept
    $absolute = [IO.Path]::GetFullPath($Path)
    $editor.SetFocus()
    Set-Text $editor $absolute
    $null = Wait-For { (Read-Text $editor) -eq $absolute } 'native file-name edit committed'
    # InvokePattern on this shell button can reject its current state. Send the
    # normal native button-click message only to its observed, PID-checked HWND.
    [TansrNativeDialogClick]::Click([IntPtr]$accept.Current.NativeWindowHandle, $ProcessId)
    $null = Wait-For { !(Find-NativeMediaDialog $ProcessId) } 'file dialog accepted'
}
function Confirm-NativeSpeechReset([int]$ProcessId) {
    $buttonId = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1')
    $buttonType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $accept = Wait-For {
        $dialog=Find-NativeMediaDialog $ProcessId
        if (!$dialog) { return $null }
        $button=$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($buttonId, $buttonType)))
        if ($button -and $button.Current.IsEnabled -and $button.Current.Name -match '确定|OK|是|Yes') { return $button }
    } 'explicit original paid-batch confirmation ready'
    [TansrNativeDialogClick]::Click([IntPtr]$accept.Current.NativeWindowHandle, $ProcessId)
    $null = Wait-For { !(Find-NativeMediaDialog $ProcessId) } 'new speech batch confirmed'
}
function Invoke-NativeMediaModalAction($Button, [int]$ProcessId, [string]$Path, [switch]$Confirm) {
    # net48 InvokePattern may block while ShowDialog is active. Only the selected
    # application's exact button is invoked on this worker; the one driver still owns
    # every subsequent UI action, scoped to that process's native dialog.
    if (!$Button -or $Button.Current.ProcessId -ne $ProcessId) { throw 'The modal action requires the selected application''s exact button.' }
    $null = Wait-For { $Button.Current.IsEnabled } 'owned modal button enabled'
    $worker = $null; $pending = $null
    if ($Button.Current.NativeWindowHandle -ne 0 -and $Button.Current.ClassName.StartsWith('WindowsForms10.BUTTON.', [StringComparison]::Ordinal)) {
        # A native WinForms modal button processes synchronously; queue its actual
        # click, then the sole driver observes and completes its owned dialog.
        [TansrNativeDialogClick]::PostClick([IntPtr]$Button.Current.NativeWindowHandle, $ProcessId)
    } else {
        $worker = [PowerShell]::Create()
        $null = $worker.AddScript('param($button) $ErrorActionPreference = "Stop"; Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes; $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()').AddArgument($Button)
        $pending = $worker.BeginInvoke()
    }
    try {
        $null = Wait-For {
            if ($worker -and $worker.HadErrors) { throw (($worker.Streams.Error | Out-String) + [string]$worker.InvocationStateInfo.Reason) }
            Find-NativeMediaDialog $ProcessId
        } 'owned modal invocation and file dialog'
        if ($Confirm) { Confirm-NativeSpeechReset $ProcessId } else { Set-NativeMediaDialogFile $ProcessId $Path }
        if ($worker) {
            $null = Wait-For { $pending.IsCompleted } 'owned modal invoke returned'
            $null = $worker.EndInvoke($pending)
            if ($worker.HadErrors) { throw (($worker.Streams.Error | Out-String) + [string]$worker.InvocationStateInfo.Reason) }
        }
    }
    catch {
        try {
            $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
            $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
            $windowType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
            $dialog = Find-NativeMediaDialog $ProcessId
            $dialogFields = @()
            if ($dialog) {
                foreach ($id in @('1001', '1148', '1')) {
                    $field = Find-Id $dialog $id
                    if ($field) { $dialogFields += @{ id = $id; name = $field.Current.Name; type = $field.Current.ControlType.ProgrammaticName; enabled = $field.Current.IsEnabled; value = (Read-Text $field) } }
                }
                $buttonType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
                foreach ($dialogButton in $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,$buttonType)) {
                    if ($dialogButton.Current.AutomationId -eq '1') { $dialogFields += @{ id='1-button'; name=$dialogButton.Current.Name; enabled=$dialogButton.Current.IsEnabled; class=$dialogButton.Current.ClassName } }
                }
            }
            $diagnostic = @{ processId = $ProcessId; button = $Button.Current.AutomationId; enabled = $Button.Current.IsEnabled; workerCompleted = $pending.IsCompleted; workerErrors = ($worker.Streams.Error | Out-String); fileDialogFields = $dialogFields; windows = @($windows | ForEach-Object { @{ id = $_.Current.AutomationId; name = $_.Current.Name; class = $_.Current.ClassName; children = @($_.FindAll([System.Windows.Automation.TreeScope]::Descendants, $windowType) | ForEach-Object { @{ id = $_.Current.AutomationId; name = $_.Current.Name; class = $_.Current.ClassName; processId = $_.Current.ProcessId } }) } }) }
            [IO.File]::WriteAllText((Join-Path $target ('modal-failure-' + [Guid]::NewGuid().ToString('N') + '.json')), ($diagnostic | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
        } catch { }
        throw
    }
    finally {
        if ($worker -and !$pending.IsCompleted) {
            $dialog = Find-NativeMediaDialog $ProcessId
            if ($dialog) { $dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }
            $worker.Stop()
        }
        if ($worker) { $worker.Dispose() }
    }
}
function Open-NativeMediaWindow($MainWindow, [int]$ProcessId) {
    Click (Wait-Id $MainWindow '媒体 / 转写 / 朗读')
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
    $label = $item.Current.Name
    $list = Find-Id $Media 'MediaItems'
    if (![TansrNativeDialogClick]::SelectFormsListLabel([IntPtr]$list.Current.NativeWindowHandle,$list.Current.ProcessId,$label)) {
        $selection = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        if (!$selection.Current.IsSelected) { throw 'Original media item selection was not confirmed.' }
    }
}
function Select-NativeRecordingInput($Media, [int]$ProcessId) {
    $control = Find-Id $Media 'MediaRecordingDevice'
    if (!$control -or $control.Current.ProcessId -ne $ProcessId) { throw 'The owned media window has no input-device selector.' }
    $expand = $null
    if ($control.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { $expand.Expand() }
    $item = Wait-For {
        $type = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $found = @($control.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type) | Where-Object { $_.Current.Name -eq '麦克风 (Mobiola Wave Audio Device  [2]' })
        if ($found.Count -eq 0) {
            $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
            foreach ($owned in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)) {
                $found += @($owned.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type) | Where-Object { $_.Current.Name -eq '麦克风 (Mobiola Wave Audio Device  [2]' })
            }
        }
        if ($found.Count -gt 1) { throw 'The Mobiola input identity is ambiguous; do not select another input.' }
        if ($found.Count -eq 1) { return $found[0] }
    } 'explicit Mobiola input 2 (no default or alternate input)'
    $name = $item.Current.Name
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ($expand) { $expand.Collapse() }
    $selection = $null
    if ($control.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern,[ref]$selection)) {
        $selected = $selection.Current.GetSelection()
        if ($selected.Count -ne 1 -or $selected[0].Current.Name -ne $name) { throw 'The observed Mobiola input selection was not retained.' }
    } elseif ([TansrNativeDialogClick]::GetComboSelection([IntPtr]$control.Current.NativeWindowHandle,$ProcessId) -ne $name) {
        throw 'The original net48 recording selector does not retain the exact Mobiola input.'
    }
    return $name
}
function Invoke-NativeMediaScenario($MainWindow, [int]$ProcessId, [string]$HostName,
    [string]$FixtureDirectory, [string]$EvidenceDirectory, [string]$ServeEvidenceDirectory, [switch]$SpeechOnly, [switch]$RecordingAndSpeechOnly) {
    $png = Join-Path $FixtureDirectory 'blue.png'; $wav = Join-Path $FixtureDirectory 'silence.wav'
    foreach ($file in @($png, $wav)) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Synthetic media fixture is missing.' } }
    $fullMedia = !$SpeechOnly -and !$RecordingAndSpeechOnly
    if ($fullMedia) {
    # Real native image picker -> SDK MessageBlock.Image -> real Serve protocol -> model assertion.
    Set-Text (Wait-Id $MainWindow 'MessageDraft') ('UI_IMAGE_INPUT_' + $HostName)
    Invoke-NativeMediaModalAction (Wait-Id $MainWindow '发送图片与草稿') $ProcessId $png
    Wait-Answer $MainWindow ('UI_DONE_IMAGE_INPUT_' + $HostName)
    Record $HostName 'media-image-input' 'native picker sent the legal PNG; real model boundary asserted an image block'

    Send-Prompt $MainWindow ('UI_MEDIA_BAD_IMAGE_' + $HostName)
    $null = Wait-For {
        if ((Pending-Count $MainWindow) -gt 0) { Click (Wait-Id $MainWindow '批准') }
        $text = Read-Text (Find-Id $MainWindow 'Conversation')
        $text.Contains('UI_DONE_MEDIA_BAD_IMAGE_' + $HostName) -and $text.Contains('[status:idle]')
    } 'bad historical image generated by original ImageGen'
    }
    $media = Open-NativeMediaWindow $MainWindow $ProcessId
    try {
        if ($fullMedia) {
        Click (Wait-Id $media 'MediaAction:恢复历史媒体')
        $null = Wait-For { (Find-Id $media 'MediaAction:刷新模型目录').Current.IsEnabled } 'original history loaded'
        Select-NativeMediaLastItem $media '^历史 .*Image'
        Click (Wait-Id $media 'MediaAction:预览选中产物'); Wait-NativeMediaIdle $media 'media_host_not_authorized'
        Record $HostName 'media-bad-history' 'original history artifact remained visible; unapproved HTTPS link rejected before download'

        $messagesBefore = Get-NativeMessageCount $ServeEvidenceDirectory
        $asrBefore = Get-NativeMediaCount $ServeEvidenceDirectory '/t1/asr'
        Invoke-NativeMediaModalAction (Wait-Id $media 'MediaAction:音频文件转草稿') $ProcessId $wav
        Wait-NativeMediaIdle $media '未发送'
        if ((Read-Text (Find-Id $MainWindow 'MessageDraft')) -ne 'UI transcription draft 中文') { throw 'File transcription did not fill the main draft.' }
        if ((Get-NativeMessageCount $ServeEvidenceDirectory) -ne $messagesBefore) { throw 'ASR implicitly sent a user message.' }
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/asr') -ne ($asrBefore + 1)) { throw 'File transcription did not make exactly one original ASR call.' }
        Close-NativeMediaWindow $media; $media = Open-NativeMediaWindow $MainWindow $ProcessId
        Invoke-NativeMediaModalAction (Wait-Id $media 'MediaAction:音频文件转草稿') $ProcessId $wav
        Wait-NativeMediaIdle $media '未发送'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/asr') -ne ($asrBefore + 1) -or (Get-NativeMessageCount $ServeEvidenceDirectory) -ne $messagesBefore) { throw 'Reopening the media window repeated or sent transcription.' }
        Record $HostName 'media-file-asr-draft' 'legal synthetic WAV; one ASR request, main draft filled, zero message submissions, no repeat on reopen; not a microphone test'
        }

        if (!$SpeechOnly) {
        # Actual original Recorder -> WAV -> SDK -> local synthetic ASR. The input
        # is explicit; neither system defaults nor other microphones are selected.
        $recordingMessagesBefore = Get-NativeMessageCount $ServeEvidenceDirectory
        $recordingAsrBefore = Get-NativeMediaCount $ServeEvidenceDirectory '/t1/asr'
        Set-Text (Wait-Id $MainWindow 'MessageDraft') ''
        $inputName = Select-NativeRecordingInput $media $ProcessId
        $startLabel = if ($HostName -eq 'WPF') { 'MediaAction:开始麦克风录音' } else { 'MediaAction:开始录音' }
        Click (Wait-Id $media $startLabel)
        $null = Wait-For { (Read-Text (Find-Id $media 'MediaStatus')).Contains('录音中') } 'selected input recording active'
        Start-Sleep -Milliseconds 1000
        Click (Wait-Id $media 'MediaAction:停止并转草稿')
        Wait-NativeMediaIdle $media '未发送'
        $recordingRequests = @(Read-NativeMediaRecords $ServeEvidenceDirectory | Where-Object { $_.path -eq '/t1/asr' })
        if ($recordingRequests.Count -ne ($recordingAsrBefore + 1)) { throw 'This recording did not produce exactly one original ASR request.' }
        $recording = $recordingRequests[-1]
        $staticWaveHash = (Get-NativeFileSha256 $wav)
        if ($recording.audioSha256 -notmatch '^[a-f0-9]{64}$' -or $recording.audioSha256 -eq $staticWaveHash -or
            !$recording.wave.recorderLayout -or $recording.wave.formatTag -ne 1 -or $recording.wave.channels -ne 1 -or
            $recording.wave.sampleRate -ne 16000 -or $recording.wave.bitsPerSample -ne 16 -or $recording.wave.dataOffset -ne 44 -or
            $recording.wave.pcmBytes -le 0 -or $recording.wave.pcmBytes -ne ($recording.audioBytes - 44) -or
            $recording.wave.durationMs -lt 250 -or $recording.wave.durationMs -gt 3000) { throw 'The actual recorder request lacks the expected bounded PCM WAV evidence.' }
        $expectedDraft = 'UI recording draft 中文 ' + $recording.audioSha256.Substring(0, 12)
        if ((Read-Text (Find-Id $MainWindow 'MessageDraft')) -ne $expectedDraft) { throw 'The recorder result did not fill a draft tied to this request hash.' }
        if ((Get-NativeMessageCount $ServeEvidenceDirectory) -ne $recordingMessagesBefore) { throw 'Recording implicitly submitted a user message.' }
        Record $HostName 'media-recorder-asr-draft' ($inputName + '; original PCM16k mono16 WAV bytes=' + $recording.audioBytes + '; durationMs=' + $recording.wave.durationMs + '; SHA256=' + $recording.audioSha256 + '; exactly one local synthetic ASR, current draft, zero message submissions; no PCM saved or speech-accuracy claim')
        }

        $ttsBefore = Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts'
        Set-Text (Wait-Id $media 'MediaText') 'abcdefghijklmnopABCDEFGHIJKLMNOP'
        Click (Wait-Id $media 'MediaAction:建立新朗读批次'); Wait-NativeMediaIdle $media 'speech_segmentation_required'
        $checkbox = Wait-Id $media 'MediaAllowSegmentation'
        $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            if ([TansrNativeDialogClick]::IsFormsButton([IntPtr]$checkbox.Current.NativeWindowHandle,$ProcessId)) { Click $checkbox }
            else { $toggle.Toggle() }
        }
        # WinForms paints the checkbox itself; BM_GETCHECK does not represent
        # its managed Checked value. Read the original accessible toggle state.
        $null = Wait-For { $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On } 'original segmentation checkbox checked'
        Click (Wait-Id $media 'MediaAction:建立新朗读批次'); Wait-NativeMediaIdle $media '已规划 2 段'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne $ttsBefore) { throw 'Planning unexpectedly called a paid endpoint.' }
        Click (Wait-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media '1/2'
        Start-Sleep -Milliseconds 400
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne ($ttsBefore + 1)) { throw 'The first explicit segment triggered extra synthesis.' }
        Click (Wait-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media '2/2'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne ($ttsBefore + 2)) { throw 'The second explicit segment did not produce exactly one synthesis.' }
        Select-NativeMediaLastItem $media '^朗读段 2'
        Click (Wait-Id $media 'MediaAction:预览选中产物'); Wait-NativeMediaIdle $media '已缓存'
        $saved = Join-Path $EvidenceDirectory ($HostName + '-native-speech.wav')
        if (Test-Path -LiteralPath $saved) { throw 'Use a new media save evidence path.' }
        Invoke-NativeMediaModalAction (Wait-Id $media 'MediaAction:保存选中产物') $ProcessId $saved
        Wait-NativeMediaIdle $media '已保存'
        if ((Get-NativeFileSha256 $saved) -ne (Get-NativeFileSha256 $wav)) { throw 'Saved native WAV differs from the actual API result.' }
        Click (Wait-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media '已完成'
        if ((Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -ne ($ttsBefore + 2)) { throw 'Preview, save or completed next repeated TTS.' }
        Record $HostName 'media-segment-preview-save' ('explicit two segments; zero synthesis for plan/preview/save/completed-next; saved WAV SHA256=' + (Get-NativeFileSha256 $saved))

        Set-Text (Wait-Id $media 'MediaText') 'CANCEL'
        Invoke-NativeMediaModalAction (Wait-Id $media 'MediaAction:建立新朗读批次') $ProcessId -Confirm
        Wait-NativeMediaIdle $media '已规划 1 段'
        Click (Wait-Id $media 'MediaAction:合成 / 续合下一段')
        $null = Wait-For { (Get-NativeMediaCount $ServeEvidenceDirectory '/t1/tts') -eq ($ttsBefore + 3) } 'accepted held synthetic speech'
        Click (Wait-Id $media 'MediaCancel'); Wait-NativeMediaIdle $media '取消'
        Click (Wait-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media 'speech_result_unknown_do_not_repeat'
        Close-NativeMediaWindow $media; $media = Open-NativeMediaWindow $MainWindow $ProcessId
        Click (Wait-Id $media 'MediaAction:合成 / 续合下一段'); Wait-NativeMediaIdle $media 'speech_result_unknown_do_not_repeat'
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
        Click (Wait-Id $media 'MediaAction:音频文件转草稿'); Wait-NativeMediaIdle $media 'transcription_model_unavailable'
        Set-Text (Wait-Id $media 'MediaText') 'Forbidden media'
        Click (Wait-Id $media 'MediaAction:建立新朗读批次'); Wait-NativeMediaIdle $media 'speech_model_unavailable'
        if (@(Read-NativeMediaRecords $ServeEvidenceDirectory).Count -ne $before) { throw 'Disabled media capability reached an upstream media endpoint.' }
        Record $HostName 'media-permission-off' 'fresh session consumed disabled platform capabilities; both ASR and TTS native actions failed visibly with zero upstream media calls'
    }
    finally { Close-NativeMediaWindow $media }
}
