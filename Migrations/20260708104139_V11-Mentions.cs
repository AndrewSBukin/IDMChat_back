using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V11Mentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MessageMention_Messages_MessageId",
                table: "MessageMention");

            migrationBuilder.DropForeignKey(
                name: "FK_MessageMention_Users_UserId",
                table: "MessageMention");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MessageMention",
                table: "MessageMention");

            migrationBuilder.RenameTable(
                name: "MessageMention",
                newName: "MessageMentions");

            migrationBuilder.RenameIndex(
                name: "IX_MessageMention_UserId",
                table: "MessageMentions",
                newName: "IX_MessageMentions_UserId");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "MessageMentions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MessageMentions",
                table: "MessageMentions",
                columns: new[] { "MessageId", "UserId" });

            migrationBuilder.AddForeignKey(
                name: "FK_MessageMentions_Messages_MessageId",
                table: "MessageMentions",
                column: "MessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageMentions_Users_UserId",
                table: "MessageMentions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MessageMentions_Messages_MessageId",
                table: "MessageMentions");

            migrationBuilder.DropForeignKey(
                name: "FK_MessageMentions_Users_UserId",
                table: "MessageMentions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MessageMentions",
                table: "MessageMentions");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "MessageMentions");

            migrationBuilder.RenameTable(
                name: "MessageMentions",
                newName: "MessageMention");

            migrationBuilder.RenameIndex(
                name: "IX_MessageMentions_UserId",
                table: "MessageMention",
                newName: "IX_MessageMention_UserId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MessageMention",
                table: "MessageMention",
                columns: new[] { "MessageId", "UserId" });

            migrationBuilder.AddForeignKey(
                name: "FK_MessageMention_Messages_MessageId",
                table: "MessageMention",
                column: "MessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageMention_Users_UserId",
                table: "MessageMention",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
