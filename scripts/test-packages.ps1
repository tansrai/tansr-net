param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Version = '0.1.0-preview.1',
    [switch]$Aot
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (Test-Path -LiteralPath $OutputDirectory) { throw '消费证据目录已存在，请使用新的具名目录。' }
$target = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $target | Out-Null
$packageUri = [Security.SecurityElement]::Escape($packageRoot)
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="$packageUri"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="Tansr.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath (Join-Path $target 'NuGet.Config') -Encoding utf8
foreach ($framework in @('net48', 'net10.0-windows')) {
    $consumer = Join-Path $target $framework
    New-Item -ItemType Directory -Path $consumer | Out-Null
    $reference = if ($framework -eq 'net48') { '<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />' } else { '' }
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>$framework</TargetFramework><LangVersion>latest</LangVersion><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><PackageReference Include="Tansr.Sdk.Windows" Version="[$Version]"/>$reference</ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/PackageConsumer/Program.cs') -Destination (Join-Path $consumer 'Program.cs')
    & dotnet restore (Join-Path $consumer 'Consumer.csproj') --packages (Join-Path $target 'packages') --configfile (Join-Path $target 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "$framework 包恢复失败" }
    & dotnet run --project (Join-Path $consumer 'Consumer.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "$framework 包消费失败" }
}
if ($Aot) {
    $consumer = Join-Path $target 'aot'
    New-Item -ItemType Directory -Path $consumer | Out-Null
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><PublishAot>true</PublishAot><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><PackageReference Include="Tansr.Sdk" Version="[$Version]"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Aot.csproj') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/AotConsumer/Program.cs') -Destination (Join-Path $consumer 'Program.cs')
    $restorePackages = '-p:RestorePackagesPath=' + (Join-Path $target 'packages')
    & dotnet publish (Join-Path $consumer 'Aot.csproj') -c Release -r win-x64 --configfile (Join-Path $target 'NuGet.Config') $restorePackages
    if ($LASTEXITCODE -ne 0) { throw 'AOT 发布失败' }
    & (Join-Path $consumer 'bin/Release/net10.0/win-x64/publish/Aot.exe')
    if ($LASTEXITCODE -ne 0) { throw 'AOT 消费失败' }
}
