using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V22AddFullTextIndexToMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF NOT EXISTS (SELECT * FROM sys.fulltext_catalogs WHERE name = 'ChatFullTextCatalog') " +
                                 "CREATE FULLTEXT CATALOG ChatFullTextCatalog AS DEFAULT;", suppressTransaction: true);

            migrationBuilder.Sql("CREATE FULLTEXT INDEX ON [Messages]([Text] LANGUAGE 1049) " +
                                 "KEY INDEX PK_Messages " +
                                 "ON ChatFullTextCatalog WITH CHANGE_TRACKING AUTO;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FULLTEXT INDEX ON [Messages];", suppressTransaction: true);
        }
    }
}
