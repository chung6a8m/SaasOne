using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.Orders.Migrations
{
    /// <inheritdoc />
    public partial class OrdersInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "Orders",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CustomerName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    ApprovalRequired = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_OrderState", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [CreatedBy] <> '00000000-0000-0000-0000-000000000000' AND [Revision] > 0 AND [Amount] >= 0 AND [Status] IN (1,2,3,4) AND ([Status] NOT IN (2,3) OR [ApprovalRequired] = 1)");
                });

            migrationBuilder.CreateTable(
                name: "Receipts",
                schema: "orders",
                columns: table => new
                {
                    PartitionHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Action = table.Column<int>(type: "int", nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultRevision = table.Column<long>(type: "bigint", nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => x.PartitionHash);
                    table.CheckConstraint("CK_OrderReceipt", "[TenantId] <> '00000000-0000-0000-0000-000000000000' AND [ActorId] <> '00000000-0000-0000-0000-000000000000' AND [Action] IN (1,2,3,4,5) AND [ResultRevision] > 0 AND [ExpiresAt] > [CompletedAt]");
                    table.ForeignKey(
                        name: "FK_Receipts_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Revisions",
                schema: "orders",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ApprovalRequired = table.Column<bool>(type: "bit", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Revisions", x => new { x.OrderId, x.Revision });
                    table.CheckConstraint("CK_OrderRevision", "[Revision] > 0 AND [ActorId] <> '00000000-0000-0000-0000-000000000000' AND [Action] IN (1,2,3,4,5) AND [Status] IN (1,2,3,4) AND ([Status] NOT IN (2,3) OR [ApprovalRequired] = 1)");
                    table.ForeignKey(
                        name: "FK_Revisions_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedAt_Id",
                schema: "orders",
                table: "Orders",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedBy_CreatedAt_Id",
                schema: "orders",
                table: "Orders",
                columns: new[] { "CreatedBy", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Number",
                schema: "orders",
                table: "Orders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_ExpiresAt",
                schema: "orders",
                table: "Receipts",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_OrderId",
                schema: "orders",
                table: "Receipts",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Receipts",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "Revisions",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "Orders",
                schema: "orders");
        }
    }
}
