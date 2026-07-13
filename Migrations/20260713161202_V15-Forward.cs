using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V15Forward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsForwarded",
                table: "Messages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalSenderId",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OriginalSenderId",
                table: "Messages",
                column: "OriginalSenderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Users_OriginalSenderId",
                table: "Messages",
                column: "OriginalSenderId",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Users_OriginalSenderId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_OriginalSenderId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsForwarded",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "OriginalSenderId",
                table: "Messages");
        }
    }
}
