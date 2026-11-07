using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.Tenancy.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class TenantWriteGateM3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WriteGate",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WriteGate", x => x.Id);
                    table.CheckConstraint("CK_TenantWriteGate", "[Id] = 1 AND [Version] > 0 AND [Fence] > 0 AND [TenantId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000'");
                });
            // Existing databases acquire a CLOSED gate. Upgrade never silently grants write readiness.
            migrationBuilder.Sql("""
                INSERT INTO [tenancy].[WriteGate] ([Id], [TenantId], [IsOpen], [Version], [OperationId], [Fence])
                SELECT 1, [TenantId], 0, 1, [InitialOperationId], 1 FROM [tenancy].[Identity] WHERE [Id] = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WriteGate",
                schema: "tenancy");
        }
    }
}
