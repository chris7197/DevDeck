CREATE OR ALTER PROCEDURE [dbo].[spExampleUpdate]
    @ItemID int,
    @NewValue int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @NewValue < 0
        THROW 50001, 'Value must not be negative.', 1;
    BEGIN TRANSACTION;
    UPDATE dbo.Items SET Quantity = @NewValue WHERE ID = @ItemID;
    IF @@ROWCOUNT = 0
    BEGIN
        ROLLBACK TRANSACTION;
        RETURN;
    END;
    INSERT INTO dbo.ItemAudit (ItemID, NewValue, UpdatedUtc)
    VALUES (@ItemID, @NewValue, SYSUTCDATETIME());
    COMMIT TRANSACTION;
END;
GO
