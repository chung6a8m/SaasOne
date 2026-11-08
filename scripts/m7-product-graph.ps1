# Pure validation: inputs must come from restored assets, native metadata and actual publish files.
function Assert-M7ProductInventory {
    param([AllowEmptyCollection()][string[]]$Libraries, [AllowEmptyCollection()][string[]]$Features, [AllowEmptyCollection()][string[]]$Assemblies)
    if ($Libraries.Count -eq 0 -or $Features.Count -eq 0 -or $Assemblies.Count -eq 0) { throw 'product_inventory_empty' }
    foreach ($name in @($Libraries) + @($Features) + @($Assemblies)) {
        if ([string]::IsNullOrWhiteSpace($name)) { throw 'product_inventory_invalid' }
        if ($name -match '(?i)(^|[/\\])SaaS\.(Orders|OrderApprovals|Web|Theme\.App)([./\\]|$)') { throw 'product_business_dependency_forbidden' }
    }
    foreach ($name in @('SaaS.Tenancy', 'SaaS.Abstractions', 'SaaS.Workspace.Theme')) {
        if (@($Libraries | Where-Object { ($_ -split '/')[0] -ieq $name }).Count -ne 1) { throw 'product_platform_closure_missing' }
    }
    foreach ($name in @('SaaS.Workspace.Web', 'SaaS.Workspace.Theme')) {
        if ($Features -cnotcontains $name -or $Assemblies -cnotcontains $name) { throw 'product_host_or_theme_missing' }
    }
}
