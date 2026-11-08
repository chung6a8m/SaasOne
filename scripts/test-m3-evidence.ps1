[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'm3-evidence-inventory.ps1')
$required = @('Acceptance.Web', 'Acceptance.Sql')
$a = [pscustomobject]@{ testName = 'Acceptance.Web'; outcome = 'Passed' }
$b = [pscustomobject]@{ testName = 'Acceptance.Sql'; outcome = 'Passed' }
$cases = @(
    [pscustomobject]@{ name = 'complete'; results = @($a, $b); valid = $true },
    [pscustomobject]@{ name = 'zero'; results = @(); valid = $false },
    [pscustomobject]@{ name = 'missing'; results = @($a); valid = $false },
    [pscustomobject]@{ name = 'duplicate'; results = @($a, $a, $b); valid = $false },
    [pscustomobject]@{ name = 'skipped'; results = @($a, [pscustomobject]@{ testName = 'Acceptance.Sql'; outcome = 'NotExecuted' }); valid = $false },
    [pscustomobject]@{ name = 'failed'; results = @($a, [pscustomobject]@{ testName = 'Acceptance.Sql'; outcome = 'Failed' }); valid = $false },
    [pscustomobject]@{ name = 'substituted'; results = @($a, [pscustomobject]@{ testName = 'Unrelated.Sql'; outcome = 'Passed' }); valid = $false }
)
foreach ($case in $cases) {
    $actual = Get-M3EvidenceInventory -Results $case.results -Required $required
    if ($actual.valid -ne $case.valid) { throw ('Evidence validator regression: ' + $case.name) }
}
# Regression: dotnet test may emit sibling TRX files such as net10.0[1].trx.
# Windows PowerShell interprets [1] as a wildcard unless the reader uses -LiteralPath.
$trxRoot = Join-Path ([IO.Path]::GetTempPath()) ('saas-m3-trx-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($trxRoot) | Out-Null
try {
    foreach ($suffix in @('Normal', 'Bracketed')) {
        $fileName = if ($suffix -eq 'Normal') { 'unit.trx' } else { 'unit[1].trx' }
        $xml = '<TestRun><Results>' +
            ('<UnitTestResult testName="SaaS.UnitTests.M4PolicyTests.{0}" outcome="Passed" />' -f $suffix) +
            ('<UnitTestResult testName="SaaS.UnitTests.M5PolicyTests.{0}" outcome="Passed" />' -f $suffix) +
            '</Results></TestRun>'
        [IO.File]::WriteAllText((Join-Path $trxRoot $fileName), $xml)
    }
    foreach ($milestone in @('M4','M5')) {
        $found = @(Get-M3PolicyTrxResults -ResultsDirectory $trxRoot -TestNamePattern ('^SaaS\.UnitTests\.' + $milestone))
        $names = @($found | ForEach-Object { [string]$_.testName } | Sort-Object -Unique)
        if ($found.Count -ne 2 -or $names.Count -ne 2 -or
            @($found | Where-Object { $_.outcome -cne 'Passed' }).Count -ne 0) {
            throw "TRX literal-path evidence regression: $milestone"
        }
    }
} finally {
    Remove-Item -LiteralPath $trxRoot -Recurse -Force
}
# Also parse the operator UI without invoking a privileged action.
$tokens = $null; $errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'operations-console.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Operator console syntax is invalid.' }
Write-Host 'PASS: seven evidence-validator scenarios and private console syntax.'
return $cases.Count
