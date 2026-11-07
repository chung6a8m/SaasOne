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
# Also parse the operator UI without invoking a privileged action.
$tokens = $null; $errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'operations-console.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Operator console syntax is invalid.' }
Write-Host 'PASS: seven evidence-validator scenarios and private console syntax.'
return $cases.Count
