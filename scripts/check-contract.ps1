[CmdletBinding()]
param([string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Get-Content -LiteralPath (Join-Path $taskRoot 'contract/manifest.json') -Raw | ConvertFrom-Json
if ($manifest.format -ne 'tansr-net-contract-lock-v1' -or $manifest.protocol -ne 'sdk2-ext-v1' -or
    $manifest.terminalServices -ne 'not-frozen-not-implemented') { throw 'Unknown contract lock format.' }

function Resolve-ContractPath([string]$root, [string]$relative) {
    $absoluteRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($relative)) { throw 'Contract paths must be relative.' }
    $path = [IO.Path]::GetFullPath((Join-Path $absoluteRoot $relative))
    if (-not $path.StartsWith($absoluteRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Contract path escapes its repository.'
    }
    return $path
}

$checked = 0
foreach ($entry in $manifest.files) {
    if ($entry.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid SHA256 in contract lock.' }
    $snapshot = Resolve-ContractPath $taskRoot $entry.snapshot
    $actual = (Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $entry.sha256 -or (Get-Item -LiteralPath $snapshot).Length -ne $entry.bytes) {
        throw ('Pinned contract changed: ' + $entry.snapshot)
    }
    if ($SourceRoot -and $entry.source) {
        $source = Resolve-ContractPath $SourceRoot $entry.source
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) {
            throw ('Upstream contract changed; review before updating: ' + $entry.source)
        }
    }
    $checked++
}

$schema = Get-Content -LiteralPath (Join-Path $taskRoot 'contract/sdk2-ext-v1.schema.json') -Raw | ConvertFrom-Json
if ($schema.definitions.PSObject.Properties.Count -eq 0) { throw 'Pinned schema definitions are missing.' }
$publicContract = Get-Content -LiteralPath (Join-Path $taskRoot 'src/Tansr.Sdk/Protocol/WireContract.cs') -Raw
$schemaLock = @($manifest.files | Where-Object { $_.snapshot -eq 'contract/sdk2-ext-v1.schema.json' })
if ($schemaLock.Count -ne 1 -or -not $publicContract.Contains('"' + $schemaLock[0].sha256 + '"')) {
    throw 'Public schema fingerprint disagrees with the contract lock.'
}
Write-Output ('Contract verified: ' + $checked + ' files; sdk2-ext-v1; source ' + $manifest.sourceRevision)
