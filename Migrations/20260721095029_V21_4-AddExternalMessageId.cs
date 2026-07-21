using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V21_4AddExternalMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExternalIdmId",
                table: "Messages",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalIdmId",
                table: "Messages");
        }
    }
}
