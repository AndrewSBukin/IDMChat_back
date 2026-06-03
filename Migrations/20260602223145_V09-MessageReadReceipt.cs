using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V09MessageReadReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.Sql(@"
        -- Удаляем первичный ключ
        ALTER TABLE [MessageReadReceipts] DROP CONSTRAINT [PK_MessageReadReceipts];
        
        -- Удаляем Identity
        ALTER TABLE [MessageReadReceipts] ALTER COLUMN [MessageId] bigint NOT NULL;
        
        -- Восстанавливаем первичный ключ
        ALTER TABLE [MessageReadReceipts] ADD CONSTRAINT [PK_MessageReadReceipts] PRIMARY KEY ([MessageId], [UserId]);
    ");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_MessageReadReceipts",
                table: "MessageReadReceipts");

            migrationBuilder.AlterColumn<long>(
                name: "MessageId",
                table: "MessageReadReceipts",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MessageReadReceipts",
                table: "MessageReadReceipts",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageReadReceipts_MessageId_UserId",
                table: "MessageReadReceipts",
                columns: new[] { "MessageId", "UserId" });
        }
    }
}
