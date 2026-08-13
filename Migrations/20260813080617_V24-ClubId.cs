using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IDMChat.Migrations
{
    /// <inheritdoc />
    public partial class V24ClubId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            // 2. Создаем временную таблицу, где у ID нет свойства IDENTITY
            migrationBuilder.Sql(@"
                CREATE TABLE [dbo].[Clubs_Tmp] (
                    [Id]       INT           NOT NULL,
                    [Code]     NVARCHAR(50) NOT NULL,
                    [Idm]      NVARCHAR(24) NOT NULL,
                    [Name]     NVARCHAR(100) NOT NULL,
                    [CityName] NVARCHAR(100) NOT NULL,
                    [CityGmt]  INT           NOT NULL,
                    CONSTRAINT [PK_Clubs_Tmp] PRIMARY KEY CLUSTERED ([Id] ASC)
                );
            ");

            // 3. Переносим все ваши данные из старой таблицы в новую
            migrationBuilder.Sql("INSERT INTO [dbo].[Clubs_Tmp] (Id, Code, Idm, Name, CityName, CityGmt) SELECT Id, Code, Idm, Name, CityName, CityGmt FROM [dbo].[Clubs];");

            // 4. Удаляем старую таблицу, которая содержала автоинкремент
            migrationBuilder.Sql("DROP TABLE [dbo].[Clubs];");

            // 5. Переименовываем временную таблицу в системное имя Clubs
            migrationBuilder.Sql("EXECUTE sp_rename N'[dbo].[Clubs_Tmp]', N'Clubs';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE [dbo].[Clubs_Tmp] (
                    [Id]       INT IDENTITY (1, 1) NOT NULL,
                    [Code]     NVARCHAR(50) NOT NULL,
                    [Idm]      NVARCHAR(24) NOT NULL,
                    [Name]     NVARCHAR(100) NOT NULL,
                    [CityName] NVARCHAR(100) NOT NULL,
                    [CityGmt]  INT           NOT NULL,
                    CONSTRAINT [PK_Clubs_Tmp] PRIMARY KEY CLUSTERED ([Id] ASC)
                );
            ");

            migrationBuilder.Sql("SET IDENTITY_INSERT [dbo].[Clubs_Tmp] ON;");
            migrationBuilder.Sql("INSERT INTO [dbo].[Clubs_Tmp] (Id, Code, Idm, Name, CityName, CityGmt) SELECT Id, Code, Idm, Name, CityName, CityGmt FROM [dbo].[Clubs];");
            migrationBuilder.Sql("SET IDENTITY_INSERT [dbo].[Clubs_Tmp] OFF;");

            migrationBuilder.Sql("DROP TABLE [dbo].[Clubs];");
            migrationBuilder.Sql("EXECUTE sp_rename N'[dbo].[Clubs_Tmp]', N'Clubs';");
        }
    }
}
