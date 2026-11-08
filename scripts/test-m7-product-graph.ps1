[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/m7-product-graph.ps1"
$libraries = @('SaaS.Tenancy/1.0.0', 'SaaS.Abstractions/1.0.0', 'SaaS.Workspace.Theme/1.0.0', 'Safe.Bridge/1.0.0')
$features = @('Application.Default', 'SaaS.Workspace.Web', 'SaaS.Workspace.Theme')
$assemblies = @('SaaS.Workspace.Web', 'SaaS.Workspace.Theme', 'SaaS.Tenancy', 'SaaS.Abstractions')
Assert-M7ProductInventory $libraries $features $assemblies
$cases = @(
    @{ l = @(); f = $features; a = $assemblies },
    @{ l = $libraries; f = @(); a = $assemblies },
    @{ l = $libraries; f = $features; a = @() },
    @{ l = $libraries + 'SaaS.Orders/1.0.0'; f = $features; a = $assemblies },
    @{ l = $libraries + 'SaaS.OrderApprovals/1.0.0'; f = $features; a = $assemblies },
    @{ l = $libraries; f = $features + 'SaaS.OrderApprovals.Decisions'; a = $assemblies },
    @{ l = $libraries; f = $features; a = $assemblies + 'SaaS.Orders' },
    @{ l = $libraries + 'SaaS.Web/1.0.0'; f = $features; a = $assemblies },
    @{ l = $libraries; f = @('Application.Default'); a = $assemblies },
    @{ l = @('SaaS.Workspace.Theme/1.0.0'); f = $features; a = $assemblies }
)
foreach ($case in $cases) {
    $rejected = $false
    try { Assert-M7ProductInventory $case.l $case.f $case.a } catch { $rejected = $true }
    if (-not $rejected) { throw 'Product closure negative case was accepted.' }
}
Write-Host 'PASS: product closure positive case and 10 negative cases.'
exit 0
