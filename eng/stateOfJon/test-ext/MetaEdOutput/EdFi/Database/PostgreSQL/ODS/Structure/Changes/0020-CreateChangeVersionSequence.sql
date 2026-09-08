CREATE SEQUENCE IF NOT EXISTS changes.ChangeVersionSequence START WITH 1;

CREATE OR REPLACE FUNCTION changes.updateChangeVersion()
    RETURNS trigger AS
$BODY$
BEGIN
    new.ChangeVersion := nextval('changes.ChangeVersionSequence');

    -- Only update LastModifiedDate if it was not modified by the triggering update
    IF new.LastModifiedDate IS NOT DISTINCT FROM old.LastModifiedDate THEN
        new.LastModifiedDate := NOW();
    END IF;

    RETURN new;
END;
$BODY$ LANGUAGE plpgsql;
