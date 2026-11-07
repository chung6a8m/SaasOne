using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.Tenancy.Migrations.Platform
{
    /// <inheritdoc />
    public partial class ResourceOperationsM3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_Operation",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_Version",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.AddColumn<long>(
                name: "AuthorizationGeneration",
                schema: "platform",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PurgedAt",
                schema: "platform",
                table: "Tenants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAt",
                schema: "platform",
                table: "Tenants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details",
                schema: "platform",
                table: "OperationalAudit",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RecoveryAuthorities",
                schema: "platform",
                columns: table => new
                {
                    Actor = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CanChangePlan = table.Column<bool>(type: "bit", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RegisteredBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryAuthorities", x => x.Actor);
                });

            migrationBuilder.CreateTable(
                name: "ResourceOperations",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SourcePlan = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    RequestedPlan = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    SelectedPlan = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    Checkpoint = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerHost = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedRegistryVersion = table.Column<long>(type: "bigint", nullable: false),
                    GateVersion = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedAcl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    AllowApprovalUpgrade = table.Column<bool>(type: "bit", nullable: false),
                    ApprovalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceOperations", x => x.Id);
                    table.CheckConstraint("CK_ResourceOperation", "[Kind] IN (3,5,7) AND [Fence] > 0 AND [ExpectedRegistryVersion] > 0 AND [AuthorizationGeneration] > 0 AND [Checkpoint] BETWEEN 0 AND 8");
                    table.ForeignKey(
                        name: "FK_ResourceOperations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_Operation",
                schema: "platform",
                table: "Tenants",
                sql: "([Operation] = 0 AND [OperationId] IS NULL AND [OwnerId] IS NULL) OR ([Operation] BETWEEN 1 AND 7 AND [OperationId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_Version",
                schema: "platform",
                table: "Tenants",
                sql: "[RegistryVersion] > 0 AND [Fence] > 0 AND [AuthorizationGeneration] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceOperations_TenantId_StartedAt",
                schema: "platform",
                table: "ResourceOperations",
                columns: new[] { "TenantId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecoveryAuthorities",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "ResourceOperations",
                schema: "platform");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_Operation",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_Version",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuthorizationGeneration",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PurgedAt",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "RetiredAt",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Details",
                schema: "platform",
                table: "OperationalAudit");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_Operation",
                schema: "platform",
                table: "Tenants",
                sql: "([Operation] = 0 AND [OperationId] IS NULL AND [OwnerId] IS NULL) OR ([Operation] BETWEEN 1 AND 6 AND [OperationId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_Version",
                schema: "platform",
                table: "Tenants",
                sql: "[RegistryVersion] > 0 AND [Fence] > 0");
        }
    }
}
