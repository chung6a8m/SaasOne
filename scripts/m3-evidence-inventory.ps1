# Pure evidence validation shared by the SQL gate and its negative self-tests.
function Get-M3EvidenceInventory {
    [CmdletBinding()]
    param([AllowEmptyCollection()][object[]]$Results, [AllowEmptyCollection()][string[]]$Required)
    if ($Required.Count -lt 1 -or @($Required | Sort-Object -Unique).Count -ne $Required.Count) { throw 'Invalid mandatory-case manifest.' }
    $inventory = @($Required | ForEach-Object {
        $name = $_
        $found = @($Results | Where-Object { $_.testName -ceq $name })
        $outcome = if ($found.Count -eq 0) { 'Missing' } elseif ($found.Count -ne 1) { 'Duplicate' } else { [string]$found[0].outcome }
        [ordered]@{ name = $name; discovered = $found.Count; outcome = $outcome }
    })
    $unexpected = @($Results | Where-Object { $Required -cnotcontains [string]$_.testName } | ForEach-Object { [string]$_.testName })
    [pscustomobject]@{
        valid = $Results.Count -eq $Required.Count -and $unexpected.Count -eq 0 -and @($inventory | Where-Object { $_.outcome -cne 'Passed' }).Count -eq 0
        mandatoryCases = $inventory
        missingMandatoryCases = @($inventory | Where-Object { $_.discovered -eq 0 } | ForEach-Object { $_.name })
        unexpectedCases = $unexpected
    }
}
