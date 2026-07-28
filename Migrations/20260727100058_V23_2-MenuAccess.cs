using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V23_2MenuAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clubs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Idm = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CityGmt = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clubs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultSectionKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClubLandingSectionKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserProfiles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserProfiles_Sections_ClubLandingSectionKey",
                        column: x => x.ClubLandingSectionKey,
                        principalTable: "Sections",
                        principalColumn: "Key");
                    table.ForeignKey(
                        name: "FK_UserProfiles_Sections_DefaultSectionKey",
                        column: x => x.DefaultSectionKey,
                        principalTable: "Sections",
                        principalColumn: "Key");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_ClubLandingSectionKey",
                table: "UserProfiles",
                column: "ClubLandingSectionKey");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_DefaultSectionKey",
                table: "UserProfiles",
                column: "DefaultSectionKey");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_RoleId",
                table: "UserProfiles",
                column: "RoleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Clubs");

            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
