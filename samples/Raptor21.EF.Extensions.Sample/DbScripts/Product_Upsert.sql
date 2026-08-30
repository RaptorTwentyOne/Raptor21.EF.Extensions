CREATE OR ALTER PROCEDURE dbo.Product_Upsert
    @Sku   varchar(32),
    @Name  varchar(128),
    @Price decimal(18,2),
    @Id    int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.Product
       SET Name = @Name, Price = @Price, UpdatedUtc = GETUTCDATE(), @Id = Id
     WHERE Sku = @Sku;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO dbo.Product (Sku, Name, Price, UpdatedUtc)
        VALUES (@Sku, @Name, @Price, GETUTCDATE());

        SET @Id = CAST(SCOPE_IDENTITY() AS int);
        RETURN 1;   -- inserted
    END

    RETURN 0;       -- updated
END
