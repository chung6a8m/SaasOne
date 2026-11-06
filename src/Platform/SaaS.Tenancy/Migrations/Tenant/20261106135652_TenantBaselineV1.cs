using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.Tenancy.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class TenantBaselineV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenancy");

            migrationBuilder.CreateTable(
                name: "Baseline",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    SeedVersion = table.Column<int>(type: "int", nullable: false),
                    Recipe = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    FeatureHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OrchardMigrationHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Baseline", x => x.Id);
                    table.CheckConstraint("CK_TenantBaseline_Singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "Identity",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseNonce = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InitialOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Identity", x => x.Id);
                    table.CheckConstraint("CK_TenantIdentity_Nonempty", "[TenantId] <> '00000000-0000-0000-0000-000000000000' AND [DatabaseNonce] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_TenantIdentity_Singleton", "[Id] = 1");
                });
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Baseline",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "Identity",
                schema: "tenancy");
        }
    }
}
