CREATE OR ALTER PROCEDURE dbo.Product_GetBySku
    @Sku varchar(32)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, Sku, Name, Price, UpdatedUtc
      FROM dbo.Product
     WHERE Sku = @Sku;
END
