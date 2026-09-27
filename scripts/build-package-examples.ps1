#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = '0.1.0-preview.1'
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '原生示例候选在 Windows x64 构建；本入口不执行 UI 或业务验收。' }
if ($Version -notmatch '^[0-9A-Za-z.+-]+$') { throw '非法候选包版本。' }
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw '请使用新的具名 archive 目录，不覆盖旧候选。' }
foreach ($inputRoot in @($sourceRoot, $packageRoot)) {
    if ($target.StartsWith($inputRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $inputRoot.StartsWith($target.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '示例输出须与原仓及原候选包目录隔离。'
    }
}
$snapshot = Join-Path $target 'source'
$consumer = Join-Path $target 'consumer'
$feed = Join-Path $target 'candidate-feed'
$cache = Join-Path $target 'packages'
$config = Join-Path $consumer 'NuGet.Config'
$exampleNames = @('ConsoleAssistant', 'WpfAssistant', 'WinFormsAssistant')
$scopes = @('examples/ConsoleAssistant', 'examples/WpfAssistant', 'examples/WinFormsAssistant', 'examples/Shared', 'Directory.Build.props', 'global.json')
$files = @(& git -C $sourceRoot -c core.quotepath=false ls-files --cached --others --exclude-standard -- @scopes | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw '无法列举原示例源码。' }
$sourceCommit = (& git -C $sourceRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw '无法确定源码提交。' }
$sourceStatus = @(& git -C $sourceRoot status --porcelain -- @scopes)
if ($LASTEXITCODE -ne 0) { throw '无法确定示例工作区状态。' }
[IO.Directory]::CreateDirectory($target) | Out-Null
$manifest = [ordered]@{
    format = 'tansr-net-package-examples-v1'; outcome = 'incomplete'; sourceCommit = $sourceCommit
    sourceRoot = $sourceRoot; sourceStatus = $sourceStatus; version = $Version; rid = 'win-x64'
    startedAt = [DateTime]::UtcNow.ToString('o'); scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
    files = @(); projects = @(); packages = @(); builds = @(); executables = @{}; environment = @{}
    boundary = 'Original example source compiled against local candidate NuGet packages. No example execution, UI acceptance, clean-user claim, signature or publication.'
}
function Write-Manifest {
    $manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8NoBOM
}
function File-Record([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return @{ path = $item.FullName; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName).Hash.ToLowerInvariant() }
}
function Convert-Project([string]$Path) {
    $xml = [Xml.XmlDocument]::new()
    $xml.PreserveWhitespace = $true
    $xml.Load($Path)
    $changes = @()
    foreach ($reference in @($xml.SelectNodes('//ProjectReference'))) {
        $id = switch ($reference.GetAttribute('Include').Replace('\', '/')) {
            '../../src/Tansr.Sdk/Tansr.Sdk.csproj' { 'Tansr.Sdk' }
            '../../src/Tansr.Sdk.Windows/Tansr.Sdk.Windows.csproj' { 'Tansr.Sdk.Windows' }
            default { throw '存在未登记的原工程引用，不能悄悄引用源码。' }
        }
        $replacement = $xml.CreateElement('PackageReference')
        foreach ($attribute in $reference.Attributes) {
            if ($attribute.Name -ne 'Include') { $replacement.SetAttribute($attribute.Name, $attribute.Value) }
        }
        foreach ($child in @($reference.ChildNodes)) { [void]$replacement.AppendChild($child.CloneNode($true)) }
        $replacement.SetAttribute('Include', $id)
        $replacement.SetAttribute('Version', "[$Version]")
        [void]$reference.ParentNode.ReplaceChild($replacement, $reference)
        $changes += @{ kind = 'project-to-exact-package'; id = $id; version = "[$Version]" }
    }
    foreach ($import in @($xml.SelectNodes('//Import'))) {
        if ($import.GetAttribute('Project').Replace('\', '/') -eq '../../src/Tansr.Sdk.Windows/buildTransitive/Tansr.Sdk.Windows.targets') {
            [void]$import.ParentNode.RemoveChild($import)
            $changes += @{ kind = 'remove-source-import'; reason = 'Original package buildTransitive/net48 target is imported by NuGet.' }
        }
    }
    if ($changes.Count -eq 0 -or $xml.SelectNodes('//ProjectReference').Count -ne 0 -or $xml.OuterXml.Contains('../../src/')) { throw '消费副本仍有原源码引用。' }
    $xml.Save($Path)
    return $changes
}
function Invoke-BuildStep([string]$Name, [string[]]$Arguments) {
    $step = [ordered]@{ name = $Name; command = 'dotnet'; arguments = $Arguments; cwd = $consumer; startedAt = [DateTime]::UtcNow.ToString('o') }
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $target "$Name.log")
    $step.exitCode = $LASTEXITCODE
    $step.finishedAt = [DateTime]::UtcNow.ToString('o')
    $manifest.builds += $step
    Write-Manifest
    if ($step.exitCode -ne 0) { throw "$Name failed ($($step.exitCode))." }
}
$savedEnvironment = @{}
$locationPushed = $false
try {
    foreach ($relative in $files) {
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split([char[]]@('/', '\')).Contains('..')) { throw '非法源码相对路径。' }
        $original = Join-Path $sourceRoot $relative
        if ((Get-Item -LiteralPath $original).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '示例源码快照不跟随重解析点。' }
        $originalRecord = File-Record $original
        $copy = Join-Path $snapshot $relative
        $working = Join-Path $consumer $relative
        foreach ($destination in @($copy, $working)) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $original -Destination $destination
            if ((Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -ne $originalRecord.sha256) { throw '复制期间示例源发生变化。' }
        }
        $manifest.files += @{ relative = $relative; original = $originalRecord; snapshot = $copy; consumer = $working }
    }
    [IO.Directory]::CreateDirectory($feed) | Out-Null
    foreach ($id in @('Tansr.Sdk', 'Tansr.Sdk.Windows')) {
        $original = Join-Path $packageRoot "$id.$Version.nupkg"
        $record = File-Record $original
        $copy = Join-Path $feed "$id.$Version.nupkg"
        Copy-Item -LiteralPath $original -Destination $copy
        if ((Get-FileHash -LiteralPath $copy).Hash.ToLowerInvariant() -ne $record.sha256) { throw '候选包复制校验失败。' }
        $archive = [IO.Compression.ZipFile]::OpenRead($copy)
        try {
            $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
            if ($entries.Count -ne 1) { throw '候选包应只含一个 nuspec。' }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
            if ($metadata.id -ne $id -or $metadata.version -ne $Version) { throw '候选包标识或版本与参数不符。' }
        } finally { $archive.Dispose() }
        $manifest.packages += @{ id = $id; version = $Version; original = $record; candidate = $copy }
    }
    $feedUri = [Security.SecurityElement]::Escape($feed)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="$feedUri"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders><packageSourceMapping><packageSource key="candidate"><package pattern="Tansr.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM
    foreach ($name in $exampleNames) {
        $project = Join-Path $consumer "examples/$name/$name.csproj"
        $changes = @(Convert-Project $project)
        $manifest.projects += @{ name = $name; project = File-Record $project; changes = $changes }
    }
    foreach ($entry in @{
        NUGET_PACKAGES = $cache
        NUGET_HTTP_CACHE_PATH = (Join-Path $target 'nuget-http-cache')
        NUGET_PLUGINS_CACHE_PATH = (Join-Path $target 'nuget-plugin-cache')
    }.GetEnumerator()) {
        $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    Push-Location -LiteralPath $consumer
    $locationPushed = $true
    Write-Manifest
    foreach ($name in $exampleNames) {
        $project = Join-Path $consumer "examples/$name/$name.csproj"
        $framework = if ($name -eq 'WinFormsAssistant') { 'net48' } else { 'net10.0-windows' }
        $selfContained = $name -eq 'WpfAssistant'
        $properties = @('-p:PlatformTarget=x64', '-p:BuildProjectReferences=false', '-p:RestorePackagesWithLockFile=true')
        # Select the existing Windows target without changing the Console example's two-TFM declaration.
        if ($name -eq 'ConsoleAssistant') { $properties += '-p:TargetFrameworks=net10.0-windows' }
        if ($name -ne 'WinFormsAssistant') { $properties += @('-p:RuntimeIdentifier=win-x64', ('-p:SelfContained=' + $selfContained.ToString().ToLowerInvariant())) }
        Invoke-BuildStep "$name-restore" (@('restore', $project, '--force-evaluate', '--packages', $cache, '--configfile', $config) + $properties)
        $lockFile = Join-Path ([IO.Path]::GetDirectoryName($project)) 'packages.lock.json'
        $lock = File-Record $lockFile
        $assetsFile = Join-Path ([IO.Path]::GetDirectoryName($project)) 'obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
        if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0) { throw '原始 SDK 源工程不能参与包示例构建。' }
        if ($assets.packageFolders.Count -ne 1 -or [IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('\', '/') -ne $cache.TrimEnd('\', '/')) { throw '恢复未使用唯一隔离包缓存。' }
        foreach ($package in $manifest.packages) {
            $key = "$($package.id)/$Version"
            if (-not $assets.libraries.ContainsKey($key) -or $assets.libraries[$key].type -ne 'package') { throw "未解析到候选包 $key。" }
            $restored = Join-Path $cache ($assets.libraries[$key].path + '/' + $package.id.ToLowerInvariant() + ".$Version.nupkg")
            if ((Get-FileHash -LiteralPath $restored).Hash.ToLowerInvariant() -ne $package.original.sha256) { throw '恢复命中了不同字节的 SDK 候选。' }
        }
        $output = Join-Path $target "executables/$name"
        $verb = if ($name -eq 'WinFormsAssistant') { 'build' } else { 'publish' }
        Invoke-BuildStep "$name-$verb" (@($verb, $project, '-c', 'Release', '-f', $framework, '--no-restore', '-o', $output, '-p:RestoreLockedMode=true') + $properties)
        if ((Get-FileHash -LiteralPath $lockFile).Hash.ToLowerInvariant() -ne $lock.sha256) { throw '构建期间依赖锁发生变化。' }
        $executable = Join-Path $output "$name.exe"
        $payload = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object { File-Record $_.FullName })
        $manifest.executables[$name] = @{ executable = File-Record $executable; framework = $framework; selfContained = $selfContained; dependencyLock = $lock; assets = File-Record $assetsFile; payload = $payload }
    }
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath $file.snapshot).Hash.ToLowerInvariant() -ne $file.original.sha256) { throw '原始源码快照被更改。' }
        if ($file.relative -notmatch '(\.csproj|packages\.lock\.json)$' -and (Get-FileHash -LiteralPath $file.consumer).Hash.ToLowerInvariant() -ne $file.original.sha256) { throw '示例消费副本的代码或配置被改动。' }
    }
    foreach ($project in $manifest.projects) {
        if ((Get-FileHash -LiteralPath $project.project.path).Hash.ToLowerInvariant() -ne $project.project.sha256) { throw '构建改变了消费工程引用。' }
    }
    $manifest.environment = @{
        TANSR_NATIVE_CONSUMER_EXAMPLE = $manifest.executables.ConsoleAssistant.executable.path
        TANSR_NATIVE_UI_WPF = $manifest.executables.WpfAssistant.executable.path
        TANSR_NATIVE_UI_WINFORMS = $manifest.executables.WinFormsAssistant.executable.path
    }
    $manifest.environment | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'example-paths.json') -Encoding utf8NoBOM
    $manifest.outcome = 'built-not-executed'
}
catch {
    $manifest.failure = $_.Exception.Message
    throw
}
finally {
    if ($locationPushed) { Pop-Location }
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    $manifest.finishedAt = [DateTime]::UtcNow.ToString('o')
    Write-Manifest
}
