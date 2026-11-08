[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $out = Join-Path $root 'artifacts/m7/product'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    [void](New-Item $out -ItemType Directory -Force)
    foreach ($path in @('samples/Portal/packages.lock.json', 'samples/Portal.Theme/packages.lock.json')) {
        if (-not (Test-Path $path)) { throw "A committed NuGet lock is required: $path" }
    }
    & dotnet restore samples/Portal/SaaS.Portal.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Independent product locked restore failed.' }
    & dotnet publish samples/Portal/SaaS.Portal.csproj -c Release --no-restore -o "$out/publish"
    if ($LASTEXITCODE -ne 0) { throw 'Independent product publish failed.' }
    $forbidden = '(?i)SaaS\.(Orders|OrderApprovals|Web)([./\\-]|$)'
    $assets = Get-Content samples/Portal/obj/project.assets.json -Raw | ConvertFrom-Json -AsHashtable
    $deps = Get-Content "$out/publish/SaaS.Portal.deps.json" -Raw | ConvertFrom-Json -AsHashtable
    foreach ($graph in @($assets.libraries, $deps.libraries)) {
        if ($graph.Count -eq 0) { throw 'Empty resolved dependency inventory.' }
        foreach ($name in $graph.Keys) { if ($name -match $forbidden) { throw "Forbidden product dependency: $name" } }
    }
    $files = @(Get-ChildItem "$out/publish" -Recurse -File)
    if ($files.Count -eq 0) { throw 'Empty publish artifact.' }
    foreach ($file in $files) { if ($file.Name -match $forbidden) { throw "Forbidden published assembly: $($file.Name)" } }
    $raw = & dotnet "$out/publish/SaaS.Portal.dll" --feature-graph
    if ($LASTEXITCODE -ne 0) { throw 'Native product feature inventory failed.' }
    $features = @($raw | ConvertFrom-Json)
    if ($features.Count -eq 0 -or 'SaaS.Theme.Portal' -notin @($features.Id)) { throw 'Missing own native theme.' }
    foreach ($feature in $features) {
        foreach ($name in @($feature.Id, $feature.ExtensionId) + @($feature.Dependencies)) {
            if ($name -match $forbidden) { throw "Forbidden native product feature: $name" }
        }
    }
    $raw | Set-Content "$out/features.json" -Encoding utf8
    $manifest = @($files | Sort-Object FullName | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath("$out/publish", $_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $report = [ordered]@{ schemaVersion=1; sourceSha=(& git rev-parse HEAD).Trim(); status='PASS'; acceptance='NOT_ASSESSED'; packageCount=$assets.libraries.Count; featureCount=$features.Count; files=$manifest }
    $report | ConvertTo-Json -Depth 10 | Set-Content "$out/report.json" -Encoding utf8
    Write-Host 'PASS: independent product locked publish, package, native feature and artifact inventories. Runtime SQL smoke is a separate mandatory gate.'
} finally { Pop-Location }
