using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V20AddIdmFieldsToUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IdmUserId",
                table: "Users",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDisplayNameCustom",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_IdmUserId",
                table: "Users",
                column: "IdmUserId",
                unique: true,
                filter: "[IdmUserId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_IdmUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IdmUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsDisplayNameCustom",
                table: "Users");
        }
    }
}
