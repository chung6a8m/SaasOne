using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.Auditing.Migrations
{
    /// <inheritdoc />
    public partial class BusinessDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangesJson",
                schema: "audit",
                table: "Events",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "audit",
                table: "Events",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResourceId",
                schema: "audit",
                table: "Events",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceType",
                schema: "audit",
                table: "Events",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangesJson",
                schema: "audit",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "audit",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ResourceId",
                schema: "audit",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ResourceType",
                schema: "audit",
                table: "Events");
        }
    }
}
