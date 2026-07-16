using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V21_3AddExternalChatMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalChatMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalChatId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalChatMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalChatMappings_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalChatMappings_ConversationId",
                table: "ExternalChatMappings",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalChatMappings_ExternalChatId",
                table: "ExternalChatMappings",
                column: "ExternalChatId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalChatMappings");
        }
    }
}
