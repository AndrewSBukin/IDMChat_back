using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V18PinnedMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "Messages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PinnedAt",
                table: "Messages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PinnedByUserId",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Conversation_Pinned",
                table: "Messages",
                columns: new[] { "ConversationId", "IsPinned", "PinnedAt" },
                filter: "[IsPinned] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_PinnedByUserId",
                table: "Messages",
                column: "PinnedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Users_PinnedByUserId",
                table: "Messages",
                column: "PinnedByUserId",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Users_PinnedByUserId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_Conversation_Pinned",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_PinnedByUserId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PinnedAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PinnedByUserId",
                table: "Messages");
        }
    }
}
