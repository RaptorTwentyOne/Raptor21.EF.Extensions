CREATE OR ALTER PROCEDURE dbo.Product_List
    @Take int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Take) Id, Sku, Name, Price, UpdatedUtc
      FROM dbo.Product
     ORDER BY Id;

    DECLARE @Total int = (SELECT COUNT(*) FROM dbo.Product);
    RETURN @Total;
END
