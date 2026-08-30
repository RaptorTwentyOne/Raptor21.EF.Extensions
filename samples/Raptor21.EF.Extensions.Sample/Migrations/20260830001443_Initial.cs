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

            // Scaffolded before StoredProcedureDiff learned to wrap, and hand-edited afterwards to say what it
            // would say today. CREATE OR ALTER PROCEDURE has to be the first statement in its batch and is
            // first in none of the scripts EF writes - `migrations script --idempotent` puts
            // IF NOT EXISTS (...) BEGIN in front of every command, and even the plain script opens a
            // BEGIN TRANSACTION - so the bare scripts that used to stand here produced a deployment script SQL
            // Server rejects. EXEC compiles its argument as a batch of its own, which satisfies the rule. The
            // text below is exactly what StoredProcedureScript.WrapInExec produces: one N'...' literal, every
            // single quote doubled (none of these four bodies contains one), trailing terminator trimmed.
            //
            // The model snapshot still records these scripts unwrapped, and that is right rather than an
            // oversight: the differ compares procedure bodies, while the wrap belongs only to the SQL a
            // migration runs. Wrapping the snapshot too would make every one of these read as changed.
            migrationBuilder.Sql("EXEC(N'CREATE OR ALTER PROCEDURE dbo.Product_GetBySku\n    @Sku varchar(32)\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    SELECT Id, Sku, Name, Price, UpdatedUtc\n      FROM dbo.Product\n     WHERE Sku = @Sku;\nEND');");

            migrationBuilder.Sql("EXEC(N'CREATE OR ALTER PROCEDURE dbo.Product_List\n    @Take int\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    SELECT TOP (@Take) Id, Sku, Name, Price, UpdatedUtc\n      FROM dbo.Product\n     ORDER BY Id;\n\n    DECLARE @Total int = (SELECT COUNT(*) FROM dbo.Product);\n    RETURN @Total;\nEND');");

            migrationBuilder.Sql("EXEC(N'CREATE OR ALTER PROCEDURE dbo.Product_TouchStamp\n    @Id int\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    UPDATE dbo.Product\n       SET UpdatedUtc = GETUTCDATE()\n     WHERE Id = @Id;\nEND');");

            migrationBuilder.Sql("EXEC(N'CREATE OR ALTER PROCEDURE dbo.Product_Upsert\n    @Sku   varchar(32),\n    @Name  varchar(128),\n    @Price decimal(18,2),\n    @Id    int OUTPUT\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    UPDATE dbo.Product\n       SET Name = @Name, Price = @Price, UpdatedUtc = GETUTCDATE(), @Id = Id\n     WHERE Sku = @Sku;\n\n    IF @@ROWCOUNT = 0\n    BEGIN\n        INSERT INTO dbo.Product (Sku, Name, Price, UpdatedUtc)\n        VALUES (@Sku, @Name, @Price, GETUTCDATE());\n\n        SET @Id = CAST(SCOPE_IDENTITY() AS int);\n        RETURN 1;   -- inserted\n    END\n\n    RETURN 0;       -- updated\nEND');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Product");

            // The drops stay bare deliberately. DROP PROCEDURE carries no first-in-batch rule, is already legal
            // inside EF's IF NOT EXISTS ... BEGIN ... END, and wrapping it would only hide what the rollback
            // does behind a string literal.
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_GetBySku];");

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_List];");

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_TouchStamp];");

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_Upsert];");
        }
    }
}
