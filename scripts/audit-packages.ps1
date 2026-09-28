#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$OutputFile,
    [string]$Version = '0.1.0.2',
    [string]$RestoredPackageDirectory,
    [switch]$RequireNotices
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $OutputFile) { throw '审计回执已存在，不覆盖。' }
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$records = @()
foreach ($id in @('Tansr.Sdk', 'Tansr.Sdk.Windows')) {
    $path = Join-Path $packageRoot "$id.$Version.nupkg"
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $entries = @()
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if (-not $names.Add($name) -or $name.Contains('\') -or $name.StartsWith('/') -or $name.Split('/').Contains('..') -or $name.Contains(':')) { throw "Unsafe/duplicate package entry: $name" }
            if ($name -match '(^|/)(node_modules|node(\.exe)?|\.env|id_rsa|id_ed25519|appsettings\.production\.json)(/|$)' -or $name -match '\.(pem|pfx|key)$') { throw "Unexpected runtime/secret entry: $name" }
            $entries += @{ path = $name; bytes = $entry.Length }
        }
        $nuspec = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
        if ($nuspec.Count -ne 1) { throw 'Expected one nuspec per SDK package.' }
        $reader = [IO.StreamReader]::new($nuspec[0].Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        if ($metadata.id -ne $id -or $metadata.version -ne $Version -or $metadata.license.InnerText -ne 'MIT') { throw "Package identity/version/license mismatch: $id" }
        $dlls = @($entries | Where-Object { $_.path -match '^lib/[^/]+/[^/]+\.dll$' } | ForEach-Object { $_.path })
        $expected = if ($id -eq 'Tansr.Sdk') { @('lib/netstandard2.0/Tansr.Sdk.dll', 'lib/net10.0/Tansr.Sdk.dll') } else { @('lib/net48/Tansr.Sdk.Windows.dll', 'lib/net10.0-windows7.0/Tansr.Sdk.Windows.dll') }
        if (@(Compare-Object $expected $dlls).Count -ne 0) { throw "Unexpected SDK target assets: $id ($dlls)" }
        if (-not $names.Contains('README.md')) { throw "Missing package README: $id" }
        $noticeRecords = @()
        foreach ($noticeName in @('LICENSE', 'NOTICE')) {
            $noticeEntry = $archive.GetEntry($noticeName)
            if (-not $noticeEntry) {
                if ($RequireNotices) { throw "Missing package notice: $id/$noticeName" }
                continue
            }
            $stream = $noticeEntry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $noticeHash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
                finally { $sha.Dispose() }
            } finally { $stream.Dispose() }
            if ($RequireNotices) {
                $original = Join-Path (Split-Path -Parent $PSScriptRoot) $noticeName
                if ($noticeEntry.Length -eq 0 -or $noticeHash -cne (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant()) {
                    throw "Package notice differs from the candidate source: $id/$noticeName"
                }
            }
            $noticeRecords += @{ path = $noticeName; bytes = $noticeEntry.Length; sha256 = $noticeHash }
        }
        if ($id -eq 'Tansr.Sdk.Windows' -and -not $names.Contains('buildTransitive/net48/Tansr.Sdk.Windows.targets')) { throw 'Missing net48 native asset integration.' }
        $dependencies = @($xml.SelectNodes("//*[local-name()='dependency']") | ForEach-Object { @{ id = $_.id; version = $_.version } })
        $records += @{ id = $id; version = $Version; sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $path).Length; entries = $entries; dependencies = $dependencies; license = 'MIT'; notices = $noticeRecords }
    }
    finally { $archive.Dispose() }
}
$dependencyRecords = @()
if ($RestoredPackageDirectory) {
    $restoreRoot = (Resolve-Path -LiteralPath $RestoredPackageDirectory).Path
    foreach ($nuspec in Get-ChildItem -LiteralPath $restoreRoot -Filter '*.nuspec' -File -Recurse) {
        [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
        $metadata = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        $license = $metadata.SelectSingleNode("*[local-name()='license']")
        $root = $nuspec.Directory.FullName
        if (-not $license -or -not $license.InnerText) {
            if (-not $metadata.licenseUrl) { throw "Dependency has no license evidence: $($metadata.id)" }
            $licenseEvidence = @{ type = 'legacy-url'; value = [string]$metadata.licenseUrl; review = 'Original terms must be reviewed before redistribution; no SPDX license inferred.' }
        }
        else { $licenseEvidence = @{ type = $license.type; value = $license.InnerText } }
        if ($license -and $license.type -eq 'file') {
            $licensePath = [IO.Path]::GetFullPath((Join-Path $root $license.InnerText))
            if (-not $licensePath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw 'Dependency license file is missing or outside package.' }
            $licenseEvidence.sha256 = (Get-FileHash -LiteralPath $licensePath).Hash.ToLowerInvariant()
        }
        $native = @(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object { $_.FullName -match '[\\/]runtimes[\\/].*[\\/]native[\\/]' } | ForEach-Object {
            @{ path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
        })
        $dependencyRecords += @{ id = $metadata.id; version = $metadata.version; developmentDependency = ($metadata.developmentDependency -eq 'true'); license = $licenseEvidence; nativeAssets = $native; nuspecSha256 = (Get-FileHash -LiteralPath $nuspec.FullName).Hash.ToLowerInvariant() }
    }
    if ($dependencyRecords.Count -eq 0) { throw 'No restored package metadata was found.' }
}
$auditJson = @{
    outcome = 'passed'; packages = $records; restoredDependencies = $dependencyRecords
    supportedConsumerRid = 'win-x64'; measuredAt = [DateTime]::UtcNow.ToString('o'); requireNotices = [bool]$RequireNotices
    boundary = 'Package content and declared dependency-license inventory, not a substitute for distributor notice obligations, signature verification or runtime evidence for other RIDs.'
} | ConvertTo-Json -Depth 10
$receiptStream = [IO.File]::Open([IO.Path]::GetFullPath($OutputFile), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $writer = [IO.StreamWriter]::new($receiptStream, [Text.UTF8Encoding]::new($false))
    try { $writer.WriteLine($auditJson) }
    finally { $writer.Dispose() }
}
finally { $receiptStream.Dispose() }
