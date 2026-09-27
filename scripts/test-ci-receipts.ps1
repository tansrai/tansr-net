#requires -Version 7.0
$ErrorActionPreference = 'Stop'

# Load only the real receipt reader, never execute CI initialization, dotnet or packaging.
$source = Join-Path $PSScriptRoot 'test-ci.ps1'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'CI script does not parse.' }
foreach ($name in @('Hash-File', 'Read-TestResult')) {
    $functions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($functions.Count -ne 1) { throw "Expected one original helper: $name" }
    . ([scriptblock]::Create($functions[0].Extent.Text))
}
function Save-Manifest { }

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('tansr-ci-receipts-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$files = [Collections.Generic.List[string]]::new()
$passed = 0
$cases = @(
    @{ name = 'completed-positive'; outcomes = @('Passed', 'Passed'); total = '2'; executed = '2'; passed = '2'; failed = '0'; notExecuted = '0'; summary = 'Completed'; accepts = $true },
    @{ name = 'passed-positive'; outcomes = @('Passed'); total = '1'; executed = '1'; passed = '1'; failed = '0'; notExecuted = '0'; summary = 'Passed'; accepts = $true },
    @{ name = 'missing-results'; outcomes = @(); total = '1'; executed = '1'; passed = '1'; failed = '0'; notExecuted = '0'; summary = 'Completed'; accepts = $false },
    @{ name = 'count-mismatch'; outcomes = @('Passed'); total = '2'; executed = '2'; passed = '2'; failed = '0'; notExecuted = '0'; summary = 'Completed'; accepts = $false },
    @{ name = 'failed-result-hidden'; outcomes = @('Failed'); total = '1'; executed = '1'; passed = '1'; failed = '0'; notExecuted = '0'; summary = 'Completed'; accepts = $false },
    @{ name = 'failed-consistent'; outcomes = @('Passed', 'Failed'); total = '2'; executed = '2'; passed = '1'; failed = '1'; notExecuted = '0'; summary = 'Failed'; accepts = $false },
    @{ name = 'not-executed'; outcomes = @('NotExecuted'); total = '1'; executed = '0'; passed = '0'; failed = '0'; notExecuted = '1'; summary = 'Completed'; accepts = $false },
    @{ name = 'zero-cases'; outcomes = @(); total = '0'; executed = '0'; passed = '0'; failed = '0'; notExecuted = '0'; summary = 'Completed'; accepts = $false },
    @{ name = 'contradictory-failed-counter'; outcomes = @('Passed'); total = '1'; executed = '1'; passed = '1'; failed = '1'; notExecuted = '0'; summary = 'Completed'; accepts = $false },
    @{ name = 'missing-counter'; outcomes = @('Passed'); total = '1'; executed = '1'; passed = '1'; failed = ''; notExecuted = '0'; summary = 'Completed'; accepts = $false },
    @{ name = 'negative-counter'; outcomes = @('Passed'); total = '-1'; executed = '1'; passed = '1'; failed = '0'; notExecuted = '0'; summary = 'Completed'; accepts = $false },
    @{ name = 'aborted-summary'; outcomes = @('Passed'); total = '1'; executed = '1'; passed = '1'; failed = '0'; notExecuted = '0'; summary = 'Aborted'; accepts = $false }
)
try {
    foreach ($case in $cases) {
        $path = Join-Path $temporaryRoot ($case.name + '.trx'); $files.Add($path)
        $rows = @(); $index = 0
        foreach ($outcome in $case.outcomes) { $rows += '<UnitTestResult testName="case' + (++$index) + '" outcome="' + $outcome + '" />' }
        $counters = '<Counters'
        foreach ($key in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
            if ($case[$key] -ne '') { $counters += ' ' + $key + '="' + $case[$key] + '"' }
        }
        $counters += ' />'
        $xml = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>' + ($rows -join '') +
            '</Results><ResultSummary outcome="' + $case.summary + '">' + $counters + '</ResultSummary></TestRun>'
        [IO.File]::WriteAllText($path, $xml)
        $manifest = @{ tests = @() }; $accepted = $true
        try { Read-TestResult $case.name $path } catch { $accepted = $false }
        if ($accepted -ne $case.accepts) { throw "Incorrect acceptance of $($case.name): $accepted" }
        if ($accepted -and ($manifest.tests.Count -ne 1 -or -not $manifest.tests[0].resultsConsistent)) { throw 'Successful receipt was not recorded consistently.' }
        $passed++
        Write-Output "PASS $($case.name)"
    }

    # A failing test process still retains its evidence, but invalid counters cannot become success.
    $manifest = @{ tests = @() }
    Read-TestResult 'missing-results-record-only' (Join-Path $temporaryRoot 'missing-results.trx') -RecordOnly
    if ($manifest.tests.Count -ne 1 -or $manifest.tests[0].resultsConsistent -or $manifest.tests[0].resultCount -ne 0) { throw 'RecordOnly concealed the missing actual results.' }
    $passed++; Write-Output 'PASS record-only-retains-inconsistency'
    $manifest = @{ tests = @() }
    Read-TestResult 'failed-record-only' (Join-Path $temporaryRoot 'failed-consistent.trx') -RecordOnly
    if ($manifest.tests.Count -ne 1 -or -not $manifest.tests[0].resultsConsistent -or $manifest.tests[0].failed -ne 1 -or $manifest.tests[0].nonPassed.Count -ne 1) { throw 'RecordOnly lost the actual failure.' }
    $passed++; Write-Output 'PASS record-only-retains-failure'
}
finally {
    # Only files created by this invocation; never recursively delete another CI/consumer directory.
    foreach ($path in $files) { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
    [IO.Directory]::Delete($temporaryRoot)
}
Write-Output "CI receipt regression: $passed passed; temporary fixtures removed."
