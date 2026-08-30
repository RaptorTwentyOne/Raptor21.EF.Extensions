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
            // This migration was scaffolded before StoredProcedureDiff learned to wrap, and hand-edited
            // afterwards to say what it would say today. The wrap is not cosmetic: CREATE OR ALTER
            // PROCEDURE must be the first statement in its batch, and `dotnet ef migrations script
            // --idempotent` puts `IF NOT EXISTS (SELECT ... FROM __EFMigrationsHistory ...) BEGIN` in
            // front of every command, so the bare script that used to stand here produced a deployment
            // script SQL Server rejects. EXEC compiles its argument as a batch of its own, which
            // satisfies the rule. Down needs no such treatment: DROP PROCEDURE carries no first-in-batch
            // rule and is already legal inside that BEGIN ... END.
            //
            // Nothing rewrites a migration once it is committed, so the library fix reaches only
            // migrations scaffolded after it. A project that already has some must re-scaffold or
            // hand-edit them exactly as this file was: double every single quote (hence MODEL''s below,
            // which reads back as MODEL's) and wrap the whole script in ONE EXEC(N'...'). Never a
            // '...' + '...' concatenation - each piece is typed on its own and so truncates at 4,000
            // characters, and a truncated procedure body deploys rather than failing.
            migrationBuilder.Sql("EXEC(N'CREATE OR ALTER PROCEDURE dbo.Product_ListView\n    @Take int\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    -- The column order below is the MODEL''s, not this file''s choice. ProductListRow''s members are\n    -- generated in snapshot order - primary key first, then alphabetical; ProductListResult is keyless,\n    -- so plain alphabetical - and StoredProcedureValidator compares result columns POSITIONALLY while\n    -- the generated reader looks them up BY NAME. Reorder this SELECT to read more naturally and the\n    -- app fails at startup with a result-column name mismatch, not with a wrong value.\n    --\n    -- One thing no model can check: the server reports the nullability of the EXPRESSION, not of the\n    -- column. Wrap a column in ISNULL, or reach it through an outer join or a UNION, and a NOT NULL\n    -- column arrives nullable. That one stays the procedure author''s to keep.\n    SELECT TOP (@Take)\n           p.Id,\n           p.Name,\n           p.Price,\n           p.Sku\n      FROM dbo.Product AS p\n     ORDER BY p.Id;\nEND');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[Product_ListView];");
        }
    }
}
