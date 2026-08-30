CREATE OR ALTER PROCEDURE dbo.Product_ListView
    @Take int
AS
BEGIN
    SET NOCOUNT ON;

    -- The column order below is the MODEL's, not this file's choice. ProductListRow's members are
    -- generated in snapshot order - primary key first, then alphabetical; ProductListResult is keyless,
    -- so plain alphabetical - and StoredProcedureValidator compares result columns POSITIONALLY while
    -- the generated reader looks them up BY NAME. Reorder this SELECT to read more naturally and the
    -- app fails at startup with a result-column name mismatch, not with a wrong value.
    --
    -- One thing no model can check: the server reports the nullability of the EXPRESSION, not of the
    -- column. Wrap a column in ISNULL, or reach it through an outer join or a UNION, and a NOT NULL
    -- column arrives nullable. That one stays the procedure author's to keep.
    SELECT TOP (@Take)
           p.Id,
           p.Name,
           p.Price,
           p.Sku
      FROM dbo.Product AS p
     ORDER BY p.Id;
END
