#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$CurrentDirectory,
    [Parameter(Mandatory = $true)][string]$PreviousDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ExecutableName = 'Consumer.exe',
    [switch]$RequireStandardUser,
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows installation acceptance requires Windows.' }
if ($ExecutableName -ne [IO.Path]::GetFileName($ExecutableName)) { throw 'ExecutableName must be a file name, not a path.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Evidence directory already exists.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$previous = (Resolve-Path -LiteralPath $PreviousDirectory).Path
$current = (Resolve-Path -LiteralPath $CurrentDirectory).Path
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $elevated = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $administratorMember = @($identity.Groups | Where-Object { $_.Value -eq 'S-1-5-32-544' }).Count -gt 0
}
finally { $identity.Dispose() }
if ($RequireStandardUser -and ($elevated -or $administratorMember)) { throw 'Current identity has administrator membership. Run this entry from a real standard-user session.' }
. (Join-Path $PSScriptRoot 'consumer-process.ps1')
function Get-Payload([string]$Root) {
    $resolved = (Resolve-Path -LiteralPath $Root).Path
    $items = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)
    if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint -or @($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Reparse points are not accepted in a managed installation payload.' }
    return @($items | ForEach-Object {
        if ($_.PSIsContainer) { @{ kind = 'directory'; path = [IO.Path]::GetRelativePath($resolved, $_.FullName); bytes = 0; sha256 = '' } }
        else { @{ kind = 'file'; path = [IO.Path]::GetRelativePath($resolved, $_.FullName); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() } }
    } | Sort-Object { $_.path })
}
function Payload-Text($Payload) { return (($Payload | ForEach-Object { $_.kind + '|' + $_.path + '|' + $_.bytes + '|' + $_.sha256 }) -join "`n") }
$oldPayload = Get-Payload $previous
$newPayload = Get-Payload $current
if ((Payload-Text $oldPayload) -eq (Payload-Text $newPayload)) { throw 'Upgrade acceptance requires two different payloads, not two labels on one build.' }
foreach ($root in @($previous, $current)) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $ExecutableName) -PathType Leaf)) { throw 'Missing package-consumer executable.' }
}
New-Item -ItemType Directory -Path $output | Out-Null
$ownedId = [Guid]::NewGuid().ToString('N')
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$installRoot = [IO.Path]::GetFullPath((Join-Path $localAppData "Tansr/Acceptance/原生 SDK $ownedId"))
if (-not $installRoot.StartsWith([IO.Path]::GetFullPath($localAppData) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $installRoot)) { throw 'Installation root must be a fresh child of the current user LocalApplicationData.' }
New-Item -ItemType Directory -Path $installRoot | Out-Null
Set-Content -LiteralPath (Join-Path $installRoot 'acceptance-owner.txt') -Value $ownedId -NoNewline
$dataRoot = Join-Path $installRoot 'user-data'
New-Item -ItemType Directory -Path $dataRoot | Out-Null
$userData = Join-Path $dataRoot 'draft.txt'
[IO.File]::WriteAllText($userData, '用户档案保留，不属于安装物。😀', [Text.UTF8Encoding]::new($false))
$dataHash = (Get-FileHash -LiteralPath $userData).Hash
$manifest = [ordered]@{
    startedAt = [DateTime]::UtcNow.ToString('o'); outcome = 'incomplete'; installationRoot = $installRoot
    currentSource = $current; previousSource = $previous; elevatedToken = $elevated; administratorMembership = $administratorMember
    standardUserGate = $(if ($elevated -or $administratorMember) { 'not-satisfied' } else { 'current-standard-user; clean-OS identity isolation still requires separate evidence' })
    operations = @(); retainedUserData = $userData; sourcePayloads = @{ previous = $oldPayload; current = $newPayload }
}
function Install-Payload([string]$Version, [string]$Source, $Payload) {
    $destination = Join-Path $installRoot $Version
    if (Test-Path -LiteralPath $destination) { throw 'Install does not overwrite an existing version.' }
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($entry in $Payload) {
        $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.path))
        if (-not $target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload path escaped installation version.' }
        if ($entry.kind -eq 'directory') { New-Item -ItemType Directory -Path $target -Force | Out-Null; continue }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $Source $entry.path) -Destination $target
    }
    if ((Payload-Text (Get-Payload $destination)) -ne (Payload-Text $Payload)) { throw 'Installed bytes differ from the approved payload.' }
}
function Select-Version([string]$Version) {
    $pointer = Join-Path $installRoot 'current.json'
    $next = Join-Path $installRoot 'current.next.json'
    [IO.File]::WriteAllText($next, (@{ version = $Version } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $pointer) { [IO.File]::Replace($next, $pointer, $null) } else { [IO.File]::Move($next, $pointer) }
}
function Run-Installed([string]$Step) {
    $version = (Get-Content -LiteralPath (Join-Path $installRoot 'current.json') -Raw | ConvertFrom-Json).version
    if ($version -notin @('previous', 'current')) { throw 'Invalid managed version pointer.' }
    $receipt = Join-Path $output "$Step.receipt.json"
    $run = Invoke-NativeConsumer -Executable (Join-Path $installRoot "$version/$ExecutableName") -Arguments @('--require-no-node', '--receipt', $receipt) -LogPrefix (Join-Path $output $Step) -SelfContained:$SelfContained
    if ($run.exitCode -ne 0 -or -not (Test-Path -LiteralPath $receipt)) { throw "Installed $Step failed." }
    $result = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($result.outcome -ne 'passed' -or $result.nodeOnPath -or $result.elevated -ne $elevated) { throw "Installed $Step returned invalid identity/runtime evidence." }
    if ((Get-FileHash -LiteralPath $userData).Hash -ne $dataHash) { throw 'Upgrade/rollback changed retained user data.' }
    $script:manifest.operations += @{ step = $Step; selectedVersion = $version; run = $run; receipt = $result }
}
function Remove-OwnedPayload([string]$Version, $Payload) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $installRoot $Version))
    if ($Version -notin @('previous', 'current') -or -not $absolute.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-Content -LiteralPath (Join-Path $installRoot 'acceptance-owner.txt') -Raw) -ne $ownedId) { throw 'Refused removal outside this owned acceptance installation.' }
    if ((Payload-Text (Get-Payload $absolute)) -ne (Payload-Text $Payload)) { throw 'Installed files changed; preserve the directory for inspection rather than deleting unknown files.' }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
try {
    Install-Payload 'previous' $previous $oldPayload
    Select-Version 'previous'
    Run-Installed 'installed'
    Install-Payload 'current' $current $newPayload
    Select-Version 'current'
    Run-Installed 'upgraded'
    Select-Version 'previous'
    Run-Installed 'rolled-back'
    Remove-OwnedPayload 'current' $newPayload
    Remove-OwnedPayload 'previous' $oldPayload
    Remove-Item -LiteralPath (Join-Path $installRoot 'current.json')
    if ((Get-FileHash -LiteralPath $userData).Hash -ne $dataHash -or (Test-Path -LiteralPath (Join-Path $installRoot 'current')) -or (Test-Path -LiteralPath (Join-Path $installRoot 'previous'))) { throw 'Uninstall/data retention check failed.' }
    $manifest.operations += @{ step = 'uninstalled'; userDataRetained = $true; unknownFilesDeleted = $false }
    $manifest.outcome = 'passed'
}
finally {
    $manifest.finishedAt = [DateTime]::UtcNow.ToString('o')
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
    # Deliberately retain the ownership marker and synthetic user-data example, and all failure evidence.
}
