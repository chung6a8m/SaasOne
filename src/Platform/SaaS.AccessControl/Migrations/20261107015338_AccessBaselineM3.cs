using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.AccessControl.Migrations
{
    /// <inheritdoc />
    public partial class AccessBaselineM3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.CreateTable(
                name: "History",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecurityGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AclHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "State",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecurityGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AppliedOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AclHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_State", x => x.Id);
                    table.CheckConstraint("CK_AccessState", "[Id] = 1 AND [Revision] > 0 AND [SecurityGeneration] > 0 AND [TenantId] <> '00000000-0000-0000-0000-000000000000' AND [AppliedOperationId] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "UserGrants",
                schema: "access",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Effect = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGrants", x => new { x.UserId, x.Permission });
                    table.CheckConstraint("CK_UserGrant_Effect", "[Effect] IN (0,1,2)");
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                schema: "access",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_Assignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "access",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleGrants",
                schema: "access",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Effect = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleGrants", x => new { x.RoleId, x.Permission });
                    table.CheckConstraint("CK_RoleGrant_Effect", "[Effect] IN (0,1,2)");
                    table.ForeignKey(
                        name: "FK_RoleGrants_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "access",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_RoleId",
                schema: "access",
                table: "Assignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_History_TenantId_SecurityGeneration_Revision",
                schema: "access",
                table: "History",
                columns: new[] { "TenantId", "SecurityGeneration", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                schema: "access",
                table: "Roles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assignments",
                schema: "access");

            migrationBuilder.DropTable(
                name: "History",
                schema: "access");

            migrationBuilder.DropTable(
                name: "RoleGrants",
                schema: "access");

            migrationBuilder.DropTable(
                name: "State",
                schema: "access");

            migrationBuilder.DropTable(
                name: "UserGrants",
                schema: "access");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "access");
        }
    }
}
