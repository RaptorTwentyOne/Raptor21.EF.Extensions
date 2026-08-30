using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raptor21.EF.Extensions.Sample.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Product",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Sku = table.Column<string>(type: "varchar(32)", nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Product", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Product_Sku",
                table: "Product",
                column: "Sku",
                unique: true);

            migrationBuilder.Sql("CREATE OR ALTER PROCEDURE dbo.Product_GetBySku\n    @Sku varchar(32)\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    SELECT Id, Sku, Name, Price, UpdatedUtc\n      FROM dbo.Product\n     WHERE Sku = @Sku;\nEND\n");

            migrationBuilder.Sql("CREATE OR ALTER PROCEDURE dbo.Product_List\n    @Take int\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    SELECT TOP (@Take) Id, Sku, Name, Price, UpdatedUtc\n      FROM dbo.Product\n     ORDER BY Id;\n\n    DECLARE @Total int = (SELECT COUNT(*) FROM dbo.Product);\n    RETURN @Total;\nEND\n");

            migrationBuilder.Sql("CREATE OR ALTER PROCEDURE dbo.Product_TouchStamp\n    @Id int\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    UPDATE dbo.Product\n       SET UpdatedUtc = GETUTCDATE()\n     WHERE Id = @Id;\nEND\n");

            migrationBuilder.Sql("CREATE OR ALTER PROCEDURE dbo.Product_Upsert\n    @Sku   varchar(32),\n    @Name  varchar(128),\n    @Price decimal(18,2),\n    @Id    int OUTPUT\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    UPDATE dbo.Product\n       SET Name = @Name, Price = @Price, UpdatedUtc = GETUTCDATE(), @Id = Id\n     WHERE Sku = @Sku;\n\n    IF @@ROWCOUNT = 0\n    BEGIN\n        INSERT INTO dbo.Product (Sku, Name, Price, UpdatedUtc)\n        VALUES (@Sku, @Name, @Price, GETUTCDATE());\n\n        SET @Id = CAST(SCOPE_IDENTITY() AS int);\n        RETURN 1;   -- inserted\n    END\n\n    RETURN 0;       -- updated\nEND\n");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Product");

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_GetBySku];");

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_List];");

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_TouchStamp];");

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_Upsert];");
        }
    }
}
