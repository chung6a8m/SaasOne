using Microsoft.EntityFrameworkCore.Migrations;
namespace SaaS.Tenancy.Migrations.Platform;

public partial class ReleaseMaintenanceM6 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_ResourceOperation", schema: "platform", table: "ResourceOperations");
        migrationBuilder.AddCheckConstraint(name: "CK_ResourceOperation", schema: "platform", table: "ResourceOperations",
            sql: "[Kind] IN (3,4,5,7) AND [Fence] > 0 AND [ExpectedRegistryVersion] > 0 AND [AuthorizationGeneration] > 0 AND [Checkpoint] BETWEEN 0 AND 8");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not erase audited release/backup operations to make an old application start.
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [platform].[ResourceOperations] WHERE [Kind] = 4) THROW 51000, 'release_history_requires_forward_fix', 1;");
        migrationBuilder.DropCheckConstraint(name: "CK_ResourceOperation", schema: "platform", table: "ResourceOperations");
        migrationBuilder.AddCheckConstraint(name: "CK_ResourceOperation", schema: "platform", table: "ResourceOperations",
            sql: "[Kind] IN (3,5,7) AND [Fence] > 0 AND [ExpectedRegistryVersion] > 0 AND [AuthorizationGeneration] > 0 AND [Checkpoint] BETWEEN 0 AND 8");
    }
}
