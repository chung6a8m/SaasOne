using System;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace SaaS.Tenancy.Migrations.Tenant;

public partial class ApplicationProfileM3 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "ApplicationProfile", schema: "tenancy", columns: table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            Plan = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
            ManifestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
            Version = table.Column<int>(type: "int", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ApplicationProfile", x => x.Id);
            table.CheckConstraint("CK_ApplicationProfile", "[Id] = 1 AND [Version] > 0 AND [TenantId] <> '00000000-0000-0000-0000-000000000000'");
        });
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("ApplicationProfile", "tenancy");
}
