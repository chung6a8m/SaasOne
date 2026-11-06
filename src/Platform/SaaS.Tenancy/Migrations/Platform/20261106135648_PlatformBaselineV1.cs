using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.Tenancy.Migrations.Platform
{
    /// <inheritdoc />
    public partial class PlatformBaselineV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "Bootstrap",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Completed = table.Column<bool>(type: "bit", nullable: false),
                    BaselineVersion = table.Column<int>(type: "int", nullable: false),
                    OperationHost = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bootstrap", x => x.Id);
                    table.CheckConstraint("CK_Bootstrap_Singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "OperationalAudit",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RegistryVersion = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DatabaseName = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    DatabaseNonce = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuntimeSecret = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    MigratorSecret = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Plan = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    Subscription = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    RegistryVersion = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    SecurityRevalidationRequired = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.CheckConstraint("CK_Tenants_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_Tenants_Operation", "([Operation] = 0 AND [OperationId] IS NULL AND [OwnerId] IS NULL) OR ([Operation] BETWEEN 1 AND 6 AND [OperationId] IS NOT NULL)");
                    table.CheckConstraint("CK_Tenants_Subscription", "[Subscription] IN (1,2,3)");
                    table.CheckConstraint("CK_Tenants_Version", "[RegistryVersion] > 0 AND [Fence] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Operations",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Checkpoint = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerHost = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    Failed = table.Column<bool>(type: "bit", nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Operations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAudit_TenantId_OperationId_OccurredAt",
                schema: "platform",
                table: "OperationalAudit",
                columns: new[] { "TenantId", "OperationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_TenantId",
                schema: "platform",
                table: "Operations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_DatabaseName",
                schema: "platform",
                table: "Tenants",
                column: "DatabaseName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                schema: "platform",
                table: "Tenants",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bootstrap",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "OperationalAudit",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Operations",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Tenants",
                schema: "platform");
        }
    }
}
