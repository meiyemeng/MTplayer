using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace MTPlayer.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipAndLoginLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLoginAtUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastLoginCity",
                table: "users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastLoginIp",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MembershipExpiresAtUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MembershipLevel",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "free");

            migrationBuilder.CreateTable(
                name: "member_pushes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MinimumMembershipLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "member"),
                    ConfigurationSourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    LiveSourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_pushes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_member_pushes_Enabled_MinimumMembershipLevel",
                table: "member_pushes",
                columns: new[] { "Enabled", "MinimumMembershipLevel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_pushes");

            migrationBuilder.DropColumn(
                name: "LastLoginAtUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastLoginCity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastLoginIp",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MembershipExpiresAtUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MembershipLevel",
                table: "users");
        }
    }
}
#pragma warning restore CA1861
