#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = '0.1.0.2',
    [switch]$Aot,
    [string]$LocalServeExecutable,
    [string]$LocalServeSha256,
    [string]$PreviousPackageDirectory,
    [string]$PreviousVersion,
    [switch]$RequireStandardUser
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '本入口消费 Windows x64 候选。其它平台不能沿用此结果。' }
$sourceRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (Test-Path -LiteralPath $OutputDirectory) { throw '消费证据目录已存在，请使用新的具名目录。' }
if ($Version -notmatch '^[0-9A-Za-z.+-]+$') { throw '非法包版本。' }
if (-not $PreviousVersion) { $PreviousVersion = $Version }
if ($PreviousVersion -notmatch '^[0-9A-Za-z.+-]+$') { throw '非法基线包版本。' }
if ([bool]$LocalServeExecutable -ne [bool]$LocalServeSha256) { throw '本地 Serve 路径与批准摘要须同时提供。' }
if ($LocalServeExecutable) {
    $LocalServeExecutable = (Resolve-Path -LiteralPath $LocalServeExecutable).Path
    if ((Get-FileHash -LiteralPath $LocalServeExecutable -Algorithm SHA256).Hash -ne $LocalServeSha256) { throw 'Serve 安装物摘要不匹配。' }
}
$target = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $target | Out-Null
. (Join-Path $PSScriptRoot 'consumer-process.ps1')
$packageUri = [Security.SecurityElement]::Escape($packageRoot)
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="$packageUri"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="Tansr.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath (Join-Path $target 'NuGet.Config') -Encoding utf8
$manifest = [ordered]@{
    source = (& git -C $sourceRoot rev-parse HEAD | Out-String).Trim(); version = $Version
    startedAt = [DateTime]::UtcNow.ToString('o'); outcome = 'incomplete'; rid = 'win-x64'
    packages = @(); consumers = @(); localServe = $LocalServeExecutable; localServeSha256 = $LocalServeSha256
    boundaries = 'Actual local package/UI/runtime consumption; not full P01-P16 UI parity, a clean OS, signing, NuGet publication or WPF Native AOT.'
}
function Invoke-BuildStep([string]$Name, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $target "$Name.log")
    if ($LASTEXITCODE -ne 0) { throw "$Name failed ($LASTEXITCODE)." }
}
function Run-Consumer([string]$Name, [string]$Executable, [bool]$SelfContained = $false) {
    $receipt = Join-Path $target "$Name.receipt.json"
    $arguments = @('--require-no-node', '--receipt', $receipt)
    if ($LocalServeExecutable) { $arguments += @('--local-serve', $LocalServeExecutable, '--local-serve-sha256', $LocalServeSha256) }
    $run = Invoke-NativeConsumer -Executable $Executable -Arguments $arguments -LogPrefix (Join-Path $target $Name) -SelfContained:$SelfContained
    if ($run.exitCode -ne 0 -or -not (Test-Path -LiteralPath $receipt)) { throw "$Name did not complete its package scenario." }
    $result = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($result.outcome -ne 'passed' -or $result.nodeOnPath -or -not $result.is64BitProcess) { throw "$Name environment/asset evidence failed." }
    $script:manifest.consumers += @{ name = $Name; executable = $Executable; sha256 = (Get-FileHash -LiteralPath $Executable).Hash.ToLowerInvariant(); selfContained = $SelfContained; run = $run; receipt = $result }
}
try {
    & (Join-Path $PSScriptRoot 'audit-packages.ps1') -PackageDirectory $packageRoot -OutputFile (Join-Path $target 'package-audit.json') -Version $Version
    foreach ($framework in @('net48', 'net10.0-windows')) {
        $consumer = Join-Path $target $framework
        New-Item -ItemType Directory -Path $consumer | Out-Null
        $frameworkProperties = if ($framework -eq 'net48') { '<UseWindowsForms>true</UseWindowsForms><StartupObject>WinFormsApplication</StartupObject>' } else { '' }
        $reference = if ($framework -eq 'net48') { '<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />' } else { '' }
        @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>$framework</TargetFramework><LangVersion>latest</LangVersion><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><PlatformTarget>x64</PlatformTarget><AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>$frameworkProperties</PropertyGroup><ItemGroup><PackageReference Include="Tansr.Sdk.Windows" Version="[$Version]"/>$reference</ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/PackageConsumer/Program.cs') -Destination $consumer
        if ($framework -eq 'net48') { Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/PackageConsumer/WinFormsApplication.cs') -Destination $consumer }
        Invoke-BuildStep "$framework-restore" @('restore', (Join-Path $consumer 'Consumer.csproj'), '--packages', (Join-Path $target 'packages'), '--configfile', (Join-Path $target 'NuGet.Config'))
        Invoke-BuildStep "$framework-build" @('build', (Join-Path $consumer 'Consumer.csproj'), '-c', 'Release', '--no-restore')
        Run-Consumer $framework (Join-Path $consumer "bin/Release/$framework/Consumer.exe")
    }
    $wpf = Join-Path $target 'wpf'
    New-Item -ItemType Directory -Path $wpf | Out-Null
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><StartupObject>WpfApplication</StartupObject><LangVersion>latest</LangVersion><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><RuntimeIdentifier>win-x64</RuntimeIdentifier><SelfContained>true</SelfContained><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><PackageReference Include="Tansr.Sdk.Windows" Version="[$Version]"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $wpf 'Consumer.csproj') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/PackageConsumer/Program.cs') -Destination $wpf
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/PackageConsumer/WpfApplication.cs') -Destination $wpf
    Invoke-BuildStep 'wpf-publish' @('publish', (Join-Path $wpf 'Consumer.csproj'), '-c', 'Release', '--configfile', (Join-Path $target 'NuGet.Config'), ('-p:RestorePackagesPath=' + (Join-Path $target 'packages')), '-o', (Join-Path $wpf 'publish'))
    foreach ($runtimeAsset in @('coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $wpf "publish/$runtimeAsset") -PathType Leaf)) { throw "Self-contained WPF asset missing: $runtimeAsset" }
    }
    Run-Consumer 'wpf-self-contained' (Join-Path $wpf 'publish/Consumer.exe') $true
    if ($PreviousPackageDirectory) {
        # A private cache is required: earlier candidates may have the same preview version but different bytes.
        $baseline = Join-Path $target 'previous'
        New-Item -ItemType Directory -Path $baseline | Out-Null
        $previousRoot = (Resolve-Path -LiteralPath $PreviousPackageDirectory).Path
        & (Join-Path $PSScriptRoot 'audit-packages.ps1') -PackageDirectory $previousRoot -OutputFile (Join-Path $baseline 'package-audit.json') -Version $PreviousVersion
        $previousUri = [Security.SecurityElement]::Escape($previousRoot)
        (Get-Content -LiteralPath (Join-Path $target 'NuGet.Config') -Raw).Replace($packageUri, $previousUri) | Set-Content -LiteralPath (Join-Path $baseline 'NuGet.Config') -Encoding utf8
        (Get-Content -LiteralPath (Join-Path $target 'net10.0-windows/Consumer.csproj') -Raw).Replace("[$Version]", "[$PreviousVersion]") | Set-Content -LiteralPath (Join-Path $baseline 'Consumer.csproj') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/PackageConsumer/Program.cs') -Destination $baseline
        Invoke-BuildStep 'previous-restore' @('restore', (Join-Path $baseline 'Consumer.csproj'), '--packages', (Join-Path $baseline 'packages'), '--configfile', (Join-Path $baseline 'NuGet.Config'))
        Invoke-BuildStep 'previous-build' @('build', (Join-Path $baseline 'Consumer.csproj'), '-c', 'Release', '--no-restore')
        & (Join-Path $PSScriptRoot 'test-installation.ps1') -CurrentDirectory (Join-Path $target 'net10.0-windows/bin/Release/net10.0-windows') -PreviousDirectory (Join-Path $baseline 'bin/Release/net10.0-windows') -OutputDirectory (Join-Path $target 'installation') -RequireStandardUser:$RequireStandardUser
    }
    if ($Aot) {
        $consumer = Join-Path $target 'aot'
        New-Item -ItemType Directory -Path $consumer | Out-Null
        @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><PublishAot>true</PublishAot><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><PackageReference Include="Tansr.Sdk" Version="[$Version]"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Aot.csproj') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/AotConsumer/Program.cs') -Destination $consumer
        Invoke-BuildStep 'aot-publish' @('publish', (Join-Path $consumer 'Aot.csproj'), '-c', 'Release', '-r', 'win-x64', '--configfile', (Join-Path $target 'NuGet.Config'), ('-p:RestorePackagesPath=' + (Join-Path $target 'packages')))
        $executable = Join-Path $consumer 'bin/Release/net10.0/win-x64/publish/Aot.exe'
        $run = Invoke-NativeConsumer -Executable $executable -Arguments @() -LogPrefix (Join-Path $target 'aot') -SelfContained
        if ($run.exitCode -ne 0 -or -not ((Get-Content -LiteralPath (Join-Path $target 'aot.stdout.log') -Raw).Contains('AOT_CONSUMER_OK'))) { throw 'AOT package consumption failed.' }
        $manifest.consumers += @{ name = 'aot'; executable = $executable; sha256 = (Get-FileHash -LiteralPath $executable).Hash.ToLowerInvariant(); selfContained = $true; run = $run }
    }
    & (Join-Path $PSScriptRoot 'audit-packages.ps1') -PackageDirectory $packageRoot -OutputFile (Join-Path $target 'dependency-audit.json') -Version $Version -RestoredPackageDirectory (Join-Path $target 'packages')
    $manifest.packages = (Get-Content -LiteralPath (Join-Path $target 'package-audit.json') -Raw | ConvertFrom-Json).packages
    $manifest.outcome = 'passed'
}
finally {
    $manifest.finishedAt = [DateTime]::UtcNow.ToString('o')
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8
}
