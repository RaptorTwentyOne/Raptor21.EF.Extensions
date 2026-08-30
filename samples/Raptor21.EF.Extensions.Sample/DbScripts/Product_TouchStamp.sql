CREATE OR ALTER PROCEDURE dbo.Product_TouchStamp
    @Id int
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.Product
       SET UpdatedUtc = GETUTCDATE()
     WHERE Id = @Id;
END
