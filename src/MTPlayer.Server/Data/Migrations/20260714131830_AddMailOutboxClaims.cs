using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MTPlayer.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailOutboxClaims : Migration
    {
        private static readonly string[] StatusClaimedColumns = ["Status", "ClaimedAtUtc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                table: "mail_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAtUtc",
                table: "mail_outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_outbox_Status_ClaimedAtUtc",
                table: "mail_outbox",
                columns: StatusClaimedColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mail_outbox_Status_ClaimedAtUtc",
                table: "mail_outbox");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "mail_outbox");

            migrationBuilder.DropColumn(
                name: "ClaimedAtUtc",
                table: "mail_outbox");
        }
    }
}
