[CmdletBinding()]
param([string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Get-Content -LiteralPath (Join-Path $taskRoot 'contract/manifest.json') -Raw | ConvertFrom-Json
if ($manifest.format -ne 'tansr-net-contract-lock-v1' -or $manifest.protocol -ne 'sdk2-ext-v1' -or
    $manifest.terminalServices -ne 'separately-pinned-preview') { throw 'Unknown contract lock format.' }

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

# UAPI-01: the unified /api route table is generated from the vendored api-manifest; the generated
# module must embed the manifest file fingerprint, revision and schemaHash recorded in the lock.
$api = $manifest.apiManifest
if (-not $api -or $api.contract -ne 'unified-v1' -or $api.format -ne 'tansr-api-manifest-v1') { throw 'Contract lock lacks the unified-v1 api-manifest block.' }
$apiLock = @($manifest.files | Where-Object { $_.snapshot -eq 'contract/api-manifest.json' })
if ($apiLock.Count -ne 1) { throw 'Contract lock must pin contract/api-manifest.json exactly once.' }
# Explicit UTF-8: Windows PowerShell 5.1 otherwise decodes the file with the ANSI code page and the parse fails.
$apiManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'contract/api-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($apiManifest.revision -ne $api.revision -or $apiManifest.schemaHash -cne $api.schemaHash) { throw 'api-manifest revision/schemaHash disagree with the contract lock.' }
$generated = Get-Content -LiteralPath (Join-Path $taskRoot $api.generated) -Raw
if (-not $generated.Contains('"' + $apiLock[0].sha256 + '"') -or -not $generated.Contains('"' + $api.schemaHash + '"') -or
    -not $generated.Contains('ManifestRevision = ' + $api.revision + ';')) {
    throw 'Generated ApiRoutes fingerprint disagrees with the contract lock; run node scripts/generate-api-routes.mjs.'
}
Write-Output ('Contract verified: ' + $checked + ' files; sdk2-ext-v1; source ' + $manifest.sourceRevision + '; api-manifest revision ' + $api.revision)
