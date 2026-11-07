using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaaS.Tenancy.Migrations.Platform
{
    /// <inheritdoc />
    public partial class IdentityAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.CheckConstraint("CK_IdentityAccount_Generation", "[Generation] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Clients",
                schema: "identity",
                columns: table => new
                {
                    ClientId = table.Column<string>(type: "varchar(96)", unicode: false, maxLength: 96, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShellName = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RedirectUri = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LogoutRedirectUri = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.ClientId);
                    table.ForeignKey(
                        name: "FK_Clients_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Installations",
                schema: "identity",
                columns: table => new
                {
                    NativePrefix = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Issuer = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FeatureHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MigrationHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installations", x => x.NativePrefix);
                });

            migrationBuilder.CreateTable(
                name: "TenantSecurity",
                schema: "identity",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSecurity", x => x.TenantId);
                    table.CheckConstraint("CK_IdentityTenant_Generation", "[Generation] > 0");
                    table.ForeignKey(
                        name: "FK_TenantSecurity_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                schema: "identity",
                columns: table => new
                {
                    GlobalUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => new { x.GlobalUserId, x.TenantId });
                    table.CheckConstraint("CK_IdentityMembership_Generation", "[Generation] > 0");
                    table.ForeignKey(
                        name: "FK_Memberships_Accounts_GlobalUserId",
                        column: x => x.GlobalUserId,
                        principalSchema: "identity",
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Memberships_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GlobalUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Subject = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    NativeSecurityStamp = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AccountGeneration = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdleExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Accounts_GlobalUserId",
                        column: x => x.GlobalUserId,
                        principalSchema: "identity",
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subjects",
                schema: "identity",
                columns: table => new
                {
                    Issuer = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Subject = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    GlobalUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subjects", x => new { x.Issuer, x.Subject });
                    table.ForeignKey(
                        name: "FK_Subjects_Accounts_GlobalUserId",
                        column: x => x.GlobalUserId,
                        principalSchema: "identity",
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Families",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GlobalUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Issuer = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Subject = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ClientId = table.Column<string>(type: "varchar(96)", unicode: false, maxLength: 96, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AuthorizationId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AccountGeneration = table.Column<long>(type: "bigint", nullable: false),
                    TenantGeneration = table.Column<long>(type: "bigint", nullable: false),
                    RegistryVersion = table.Column<long>(type: "bigint", nullable: false),
                    MembershipGeneration = table.Column<long>(type: "bigint", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubscriptionExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RefreshIdleExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RefreshAbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Families", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Families_Accounts_GlobalUserId",
                        column: x => x.GlobalUserId,
                        principalSchema: "identity",
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Families_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "identity",
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Families_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefreshUses",
                schema: "identity",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshUses", x => x.TokenHash);
                    table.ForeignKey(
                        name: "FK_RefreshUses_Families_FamilyId",
                        column: x => x.FamilyId,
                        principalSchema: "identity",
                        principalTable: "Families",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_TenantId",
                schema: "identity",
                table: "Clients",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Families_AuthorizationId",
                schema: "identity",
                table: "Families",
                column: "AuthorizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Families_GlobalUserId_TenantId",
                schema: "identity",
                table: "Families",
                columns: new[] { "GlobalUserId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Families_SessionId",
                schema: "identity",
                table: "Families",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Families_TenantId",
                schema: "identity",
                table: "Families",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_TenantId",
                schema: "identity",
                table: "Memberships",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshUses_FamilyId",
                schema: "identity",
                table: "RefreshUses",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_GlobalUserId_TenantId",
                schema: "identity",
                table: "Sessions",
                columns: new[] { "GlobalUserId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Subjects_GlobalUserId",
                schema: "identity",
                table: "Subjects",
                column: "GlobalUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Clients",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Installations",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Memberships",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "RefreshUses",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Subjects",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "TenantSecurity",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Families",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "identity");
        }
    }
}
