using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MTPlayer.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAndroidUpdatePush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AndroidDownloadUrl",
                table: "member_pushes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AndroidVersion",
                table: "member_pushes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForceAndroidUpdate",
                table: "member_pushes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Message",
                table: "member_pushes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AndroidDownloadUrl",
                table: "member_pushes");

            migrationBuilder.DropColumn(
                name: "AndroidVersion",
                table: "member_pushes");

            migrationBuilder.DropColumn(
                name: "ForceAndroidUpdate",
                table: "member_pushes");

            migrationBuilder.DropColumn(
                name: "Message",
                table: "member_pushes");
        }
    }
}
