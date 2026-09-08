DO $$
BEGIN
CREATE OR REPLACE FUNCTION tracked_changes_testextension.haircolordescriptor_deleted()
    RETURNS trigger AS
$BODY$
BEGIN
    INSERT INTO tracked_changes_edfi.descriptor(olddescriptorid, oldcodevalue, oldnamespace, id, discriminator, changeversion)
    SELECT OLD.HairColorDescriptorId, b.codevalue, b.namespace, b.id, 'testextension.HairColorDescriptor', nextval('changes.ChangeVersionSequence')
    FROM edfi.descriptor b WHERE old.HairColorDescriptorId = b.descriptorid ;

    RETURN NULL;
END;
$BODY$ LANGUAGE plpgsql;

IF NOT EXISTS(SELECT 1 FROM information_schema.triggers WHERE trigger_name = 'trackdeletes' AND event_object_schema = 'testextension' AND event_object_table = 'haircolordescriptor') THEN
CREATE TRIGGER TrackDeletes AFTER DELETE ON testextension.haircolordescriptor 
    FOR EACH ROW EXECUTE PROCEDURE tracked_changes_testextension.haircolordescriptor_deleted();
END IF;

END
$$;
