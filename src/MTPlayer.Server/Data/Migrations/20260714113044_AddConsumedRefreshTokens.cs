using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MTPlayer.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConsumedRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consumed_refresh_tokens",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consumed_refresh_tokens", x => x.TokenHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_consumed_refresh_tokens_ExpiresAtUtc",
                table: "consumed_refresh_tokens",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_consumed_refresh_tokens_SessionId",
                table: "consumed_refresh_tokens",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consumed_refresh_tokens");
        }
    }
}
