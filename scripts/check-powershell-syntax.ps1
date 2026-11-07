# Parses every PowerShell script without executing setup, SQL, Docker or business code.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$files = @(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.ps1' -File -Recurse)
if ($files.Count -eq 0) { throw 'No PowerShell scripts found.' }
$failures = 0
foreach ($file in $files) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    foreach ($parseError in @($errors)) {
        $failures++
        Write-Host ('{0}:{1}:{2}: {3}' -f $file.FullName, $parseError.Extent.StartLineNumber, $parseError.Extent.StartColumnNumber, $parseError.Message)
    }
}
if ($failures -gt 0) {
    Write-Host ('FAIL: {0} PowerShell parser error(s) in {1} script(s).' -f $failures, $files.Count)
    exit 1
}
Write-Host ('PASS: {0} PowerShell scripts parsed with no errors.' -f $files.Count)
exit 0
