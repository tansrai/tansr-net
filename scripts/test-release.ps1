#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$CorePackage,
    [Parameter(Mandatory = $true)][string]$WindowsPackage,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9A-Za-z.+-]+$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$CoreSha256,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$WindowsSha256,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$CertificateFingerprint,
    [switch]$RequireSigned,
    [switch]$RequireNotices,
    [ValidateRange(1, 600)][int]$VerifyTimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
if ([bool]$CoreSha256 -ne [bool]$WindowsSha256) { throw 'Provide both expected package SHA256 values, or neither.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Release evidence directory already exists; use a new directory.' }
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$target = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $target | Out-Null
$candidate = Join-Path $target 'packages'
New-Item -ItemType Directory -Path $candidate | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$manifest = [ordered]@{
    startedAt = [DateTime]::UtcNow.ToString('o'); outcome = 'incomplete'; readiness = 'unverified'
    version = $Version; requireSigned = [bool]$RequireSigned; requireNotices = [bool]$RequireNotices
    expectedHashesProvided = [bool]$CoreSha256; certificateFingerprint = $CertificateFingerprint
    signerPinRequested = [bool]$CertificateFingerprint; signerPinned = $false
    contentAuditPassed = $false; signaturesVerified = $false
    noticesVerified = $false; releaseReady = $false; published = $false; packages = @(); failure = $null
    source = (& git -C (Split-Path -Parent $PSScriptRoot) rev-parse HEAD | Out-String).Trim()
    checkerSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    dotnet = $dotnet
    boundary = 'Local package content and signature checks only. No signing, certificate creation, publication, channel ownership, publisher authorization or CI is performed. Without a fingerprint, a valid trust chain does not establish an approved publisher. Hashes describe these exact bytes, not future replacements.'
    references = @(
        'https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify',
        'https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu3004',
        'https://github.com/NuGet/Home/wiki/Package-Signatures-Technical-Details'
    )
}
$heldFiles = [Collections.Generic.List[IO.FileStream]]::new()
function Get-StreamSha256([IO.Stream]$Stream) {
    $Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash($Stream)).ToLowerInvariant() }
    finally { $sha.Dispose(); $Stream.Position = 0 }
}
function Invoke-DotnetCheck([string]$Name, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $target
    $start.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not start dotnet verification.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($VerifyTimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        $out | Set-Content -LiteralPath (Join-Path $target "$Name.stdout.log") -Encoding utf8
        $err | Set-Content -LiteralPath (Join-Path $target "$Name.stderr.log") -Encoding utf8
        return @{ executable = $dotnet; arguments = $Arguments; exitCode = $process.ExitCode; timedOut = $timedOut; stdout = $out; stderr = $err }
    }
    finally { $process.Dispose() }
}
try {
    # An explicit empty source list avoids inheriting feed credentials. Verification still
    # uses the host's normal code-signing/timestamp trust roots and revocation policy.
    '<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $target 'NuGet.Config') -Encoding utf8
    $sdk = Invoke-DotnetCheck 'dotnet-version' @('--version')
    if ($sdk.timedOut -or $sdk.exitCode -ne 0) { throw 'Could not determine the dotnet SDK version.' }
    $manifest.dotnetVersion = $sdk.stdout.Trim()
    foreach ($packageInput in @(
        @{ id = 'Tansr.Sdk'; path = $CorePackage; expectedSha256 = $CoreSha256 },
        @{ id = 'Tansr.Sdk.Windows'; path = $WindowsPackage; expectedSha256 = $WindowsSha256 }
    )) {
        $path = (Resolve-Path -LiteralPath $packageInput.path).Path
        # Hold both the source and checked copy read-only through auditing and verification.
        # Before/after hashes also detect changes on systems without mandatory file sharing.
        $source = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $heldFiles.Add($source)
        $hash = Get-StreamSha256 $source
        $record = [ordered]@{
            id = $packageInput.id; version = $Version; sourcePath = $path; bytes = $source.Length; sha256 = $hash
            expectedSha256 = $packageInput.expectedSha256; packagePath = $null; signaturePresent = $null
            signatureStatus = 'unverified'; verification = $null; unchanged = $false
        }
        $manifest.packages += $record
        if ($packageInput.expectedSha256 -and $hash -cne $packageInput.expectedSha256.ToLowerInvariant()) { throw "Expected package SHA256 mismatch: $($packageInput.id)" }
        $copyPath = Join-Path $candidate "$($packageInput.id).$Version.nupkg"
        $copy = [IO.File]::Open($copyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $source.CopyTo($copy) } finally { $copy.Dispose(); $source.Position = 0 }
        $copy = [IO.File]::Open($copyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $heldFiles.Add($copy)
        if ((Get-StreamSha256 $copy) -cne $hash) { throw "Copied package SHA256 mismatch: $($packageInput.id)" }
        $record.packagePath = $copyPath
        $archive = [IO.Compression.ZipArchive]::new($copy, [IO.Compression.ZipArchiveMode]::Read, $true)
        try { $record.signaturePresent = $null -ne $archive.GetEntry('.signature.p7s') }
        finally { $archive.Dispose(); $copy.Position = 0 }
    }
    if (@(Get-ChildItem -LiteralPath $candidate -File).Count -ne 2) { throw 'Expected exactly the two SDK product packages.' }
    & (Join-Path $PSScriptRoot 'audit-packages.ps1') -PackageDirectory $candidate -OutputFile (Join-Path $target 'package-audit.json') -Version $Version -RequireNotices:$RequireNotices
    $manifest.contentAuditPassed = $true
    $manifest.noticesVerified = [bool]$RequireNotices
    foreach ($record in $manifest.packages) {
        $arguments = @('nuget', 'verify', $record.packagePath, '--all', '--verbosity', 'normal', '--configfile', (Join-Path $target 'NuGet.Config'))
        if ($CertificateFingerprint) { $arguments += @('--certificate-fingerprint', $CertificateFingerprint) }
        $verification = Invoke-DotnetCheck ($record.id + '-verify') $arguments
        $record.verification = @{ executable = $verification.executable; arguments = $arguments; exitCode = $verification.exitCode; timedOut = $verification.timedOut }
        $diagnostics = $verification.stdout + "`n" + $verification.stderr
        $codes = @([regex]::Matches($diagnostics, '\bNU\d{4}\b') | ForEach-Object { $_.Value } | Select-Object -Unique)
        $record.verification.diagnosticCodes = $codes
        if ($verification.timedOut) { $record.signatureStatus = 'verification-unavailable' }
        elseif ($record.signaturePresent -and $verification.exitCode -eq 0) { $record.signatureStatus = 'verified' }
        elseif (-not $record.signaturePresent -and $verification.exitCode -ne 0 -and $codes -contains 'NU3004' -and @($codes | Where-Object { $_ -ne 'NU3004' }).Count -eq 0) { $record.signatureStatus = 'unsigned' }
        elseif ($record.signaturePresent) { $record.signatureStatus = 'signature-verification-failed' }
        else { $record.signatureStatus = 'verification-unavailable' }
    }
    foreach ($record in $manifest.packages) {
        $record.unchanged = (Get-FileHash -LiteralPath $record.sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $record.sha256 -and
            (Get-FileHash -LiteralPath $record.packagePath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $record.sha256
        if (-not $record.unchanged) { throw "Package changed during verification: $($record.id)" }
    }
    $failed = @($manifest.packages | Where-Object { $_.signatureStatus -notin @('verified', 'unsigned') })
    if ($failed.Count -gt 0) { $manifest.readiness = 'signature-verification-failed'; throw 'One or more package signatures could not be verified. See the original dotnet verification logs.' }
    $manifest.signaturesVerified = @($manifest.packages | Where-Object { $_.signatureStatus -ne 'verified' }).Count -eq 0
    $manifest.signerPinned = [bool]$CertificateFingerprint -and $manifest.signaturesVerified
    $manifest.readiness = if ($manifest.signaturesVerified) { 'verified' } else { 'unsigned' }
    if ($RequireSigned -and -not $manifest.signaturesVerified) { throw 'RequireSigned rejected unsigned SDK packages.' }
    if ($CertificateFingerprint -and -not $manifest.signerPinned) { throw 'The requested certificate fingerprint was not verified for both SDK packages.' }
    $manifest.outcome = 'passed'
}
catch {
    $manifest.outcome = 'failed'
    $manifest.failure = $_.Exception.Message
    throw
}
finally {
    foreach ($file in $heldFiles) { $file.Dispose() }
    $manifest.finishedAt = [DateTime]::UtcNow.ToString('o')
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8
}
