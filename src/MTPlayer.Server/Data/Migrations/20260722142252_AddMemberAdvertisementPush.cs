using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MTPlayer.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberAdvertisementPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreRollAdvertisementJson",
                table: "member_pushes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartupAdvertisementJson",
                table: "member_pushes",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreRollAdvertisementJson",
                table: "member_pushes");

            migrationBuilder.DropColumn(
                name: "StartupAdvertisementJson",
                table: "member_pushes");
        }
    }
}
