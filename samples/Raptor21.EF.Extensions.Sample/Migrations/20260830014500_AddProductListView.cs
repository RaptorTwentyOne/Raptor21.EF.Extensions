using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raptor21.EF.Extensions.Sample.Migrations
{
    /// <inheritdoc />
    public partial class AddProductListView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE OR ALTER PROCEDURE dbo.Product_ListView\n    @Take int\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    -- The column order below is the MODEL's, not this file's choice. ProductListRow's members are\n    -- generated in snapshot order - primary key first, then alphabetical; ProductListResult is keyless,\n    -- so plain alphabetical - and StoredProcedureValidator compares result columns POSITIONALLY while\n    -- the generated reader looks them up BY NAME. Reorder this SELECT to read more naturally and the\n    -- app fails at startup with a result-column name mismatch, not with a wrong value.\n    --\n    -- One thing no model can check: the server reports the nullability of the EXPRESSION, not of the\n    -- column. Wrap a column in ISNULL, or reach it through an outer join or a UNION, and a NOT NULL\n    -- column arrives nullable. That one stays the procedure author's to keep.\n    SELECT TOP (@Take)\n           p.Id,\n           p.Name,\n           p.Price,\n           p.Sku\n      FROM dbo.Product AS p\n     ORDER BY p.Id;\nEND\n");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_ListView];");
        }
    }
}
