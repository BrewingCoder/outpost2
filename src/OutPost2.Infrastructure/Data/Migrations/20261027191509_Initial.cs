using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OutPost2.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubUserId = table.Column<long>(type: "bigint", nullable: false),
                    GitHubLogin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AvatarUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSignInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Instance = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AuthKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CredentialRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HomeAccountId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsDefaultForProvider = table.Column<bool>(type: "boolean", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHealthyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Capabilities = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_connections_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connections_UserId_Label",
                table: "connections",
                columns: new[] { "UserId", "Label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_connections_UserId_Provider",
                table: "connections",
                columns: new[] { "UserId", "Provider" },
                unique: true,
                filter: "\"IsDefaultForProvider\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_connections_UserId_Provider_Instance_HomeAccountId",
                table: "connections",
                columns: new[] { "UserId", "Provider", "Instance", "HomeAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_GitHubUserId",
                table: "users",
                column: "GitHubUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connections");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
