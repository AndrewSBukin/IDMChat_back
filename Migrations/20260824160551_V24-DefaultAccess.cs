using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V24DefaultAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdmRoleMap",
                columns: table => new
                {
                    IdmRole = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RoleCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DefaultSectionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ClubLandingSectionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdmRoleMap", x => x.IdmRole);
                });

            migrationBuilder.CreateTable(
                name: "LimitKeys",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LimitKeys", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdmRoleMap");

            migrationBuilder.DropTable(
                name: "LimitKeys");
        }
    }
}
