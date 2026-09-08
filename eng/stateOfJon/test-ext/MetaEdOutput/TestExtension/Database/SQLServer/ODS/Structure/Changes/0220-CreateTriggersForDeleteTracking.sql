DROP TRIGGER IF EXISTS [testextension].[testextension_HairColorDescriptor_TR_DeleteTracking]
GO

CREATE TRIGGER [testextension].[testextension_HairColorDescriptor_TR_DeleteTracking] ON [testextension].[HairColorDescriptor] AFTER DELETE AS
BEGIN
    IF @@rowcount = 0 
        RETURN

    SET NOCOUNT ON

    INSERT INTO [tracked_changes_edfi].[Descriptor](OldDescriptorId, OldCodeValue, OldNamespace, Id, Discriminator, ChangeVersion)
    SELECT  d.HairColorDescriptorId, b.CodeValue, b.Namespace, b.Id, 'testextension.HairColorDescriptor', (NEXT VALUE FOR [changes].[ChangeVersionSequence])
    FROM    deleted d
            INNER JOIN edfi.Descriptor b ON d.HairColorDescriptorId = b.DescriptorId
END
GO

ALTER TABLE [testextension].[HairColorDescriptor] ENABLE TRIGGER [testextension_HairColorDescriptor_TR_DeleteTracking]
GO


