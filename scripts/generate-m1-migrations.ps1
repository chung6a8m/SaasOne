[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $project = 'src/Platform/SaaS.Tenancy'
    if (Test-Path "$project/Migrations") { throw 'Initial generation is one-shot. Use explicit reviewed incremental migrations after M1.' }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    & dotnet restore SaasOne.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    foreach ($stream in @('Platform', 'Tenant')) {
        & dotnet ef migrations add "${stream}BaselineV1" --project $project --context "${stream}DbContext" --output-dir "Migrations/$stream" --namespace "SaaS.Tenancy.Migrations.$stream"
        if ($LASTEXITCODE -ne 0) { throw "Migration generation failed: $stream" }
    }
    # EF records the trigger metadata but does not generate the trigger body.
    # Add it to the owning initial migration, after its tables have been created.
    $migration = @(Get-ChildItem "$project/Migrations/Tenant/*_TenantBaselineV1.cs")
    if ($migration.Count -ne 1) { throw 'Expected exactly one tenant baseline migration.' }
    $text = Get-Content $migration[0].FullName -Raw
    $down = $text.IndexOf('protected override void Down(')
    $closing = $text.LastIndexOf('        }', $down)
    if ($down -lt 0 -or $closing -lt 0) { throw 'Unexpected EF migration format.' }
    $trigger = @'
            migrationBuilder.Sql("""
                CREATE TRIGGER [tenancy].[ImmutableTenantDatabaseIdentity]
                ON [tenancy].[Identity]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'tenant_identity_immutable', 1;
                END
                """);
'@
    $text = $text.Insert($closing, $trigger + [Environment]::NewLine)
    Set-Content $migration[0].FullName $text -Encoding utf8NoBOM -NoNewline
    & dotnet build SaasOne.slnx --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Generated migration build failed.' }
    foreach ($context in @('PlatformDbContext', 'TenantDbContext')) {
        & dotnet ef migrations has-pending-model-changes --project $project --context $context
        if ($LASTEXITCODE -ne 0) { throw "Snapshot/model mismatch: $context" }
    }
} finally { Pop-Location }
