using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.OrderApprovals.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialApprovalsM5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "approvals");

            migrationBuilder.CreateTable(
                name: "Requests",
                schema: "approvals",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedRevision = table.Column<long>(type: "bigint", nullable: false),
                    SubmittedVersion = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => new { x.OrderId, x.SubmittedRevision });
                    table.CheckConstraint("CK_ApprovalRequest", "[OrderId] <> '00000000-0000-0000-0000-000000000000' AND [SubmittedRevision] > 1 AND [CreatedBy] <> '00000000-0000-0000-0000-000000000000' AND [SubmittedBy] <> '00000000-0000-0000-0000-000000000000' AND DATALENGTH([SubmittedVersion]) = 12");
                });

            migrationBuilder.CreateTable(
                name: "Decisions",
                schema: "approvals",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedRevision = table.Column<long>(type: "bigint", nullable: false),
                    ResultRevision = table.Column<long>(type: "bigint", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Approved = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decisions", x => new { x.OrderId, x.SubmittedRevision });
                    table.CheckConstraint("CK_ApprovalDecision", "[SubmittedRevision] > 1 AND [ResultRevision] > [SubmittedRevision] AND [ResultRevision] - [SubmittedRevision] = 1 AND [DecidedBy] <> '00000000-0000-0000-0000-000000000000' AND (([Approved] = 1 AND [Reason] IS NULL) OR ([Approved] = 0 AND [Reason] IS NOT NULL AND LEN(LTRIM(RTRIM([Reason]))) > 0))");
                    table.ForeignKey(
                        name: "FK_Decisions_Requests_OrderId_SubmittedRevision",
                        columns: x => new { x.OrderId, x.SubmittedRevision },
                        principalSchema: "approvals",
                        principalTable: "Requests",
                        principalColumns: new[] { "OrderId", "SubmittedRevision" },
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Decisions",
                schema: "approvals");

            migrationBuilder.DropTable(
                name: "Requests",
                schema: "approvals");
        }
    }
}
