CREATE SCHEMA IF NOT EXISTS "edfi";

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."StudentSchoolAssociation"
(
    "DocumentId" bigint NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "SchoolId" integer NOT NULL,
    "StudentUniqueId" varchar(32) NOT NULL,
    "EntryDate" date NOT NULL,
    "EntryTimestamp" timestamp with time zone NOT NULL,
    "IsActive" boolean NOT NULL,
    CONSTRAINT "PK_StudentSchoolAssociation" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_StudentSchoolAssociation_School'
        AND conrelid = to_regclass('"edfi"."StudentSchoolAssociation"')
    )
    THEN
        ALTER TABLE "edfi"."StudentSchoolAssociation"
        ADD CONSTRAINT "FK_StudentSchoolAssociation_School"
        FOREIGN KEY ("School_DocumentId")
        REFERENCES "edfi"."School" ("DocumentId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN
        _stampedContentVersion := nextval('"dms"."ChangeVersionSequence"');
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := now();
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
        NEW."CreatedAt" := now();
    ELSIF TG_OP = 'UPDATE' THEN
        _stampedContentVersion := nextval('"dms"."ChangeVersionSequence"');
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := now();
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_Stamp" ON "edfi"."School";
CREATE TRIGGER "TR_School_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."School_DocumentId" IS DISTINCT FROM NEW."School_DocumentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId" OR OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId" OR OLD."EntryDate" IS DISTINCT FROM NEW."EntryDate" OR OLD."EntryTimestamp" IS DISTINCT FROM NEW."EntryTimestamp" OR OLD."IsActive" IS DISTINCT FROM NEW."IsActive") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN
        _stampedContentVersion := nextval('"dms"."ChangeVersionSequence"');
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := now();
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
        NEW."CreatedAt" := now();
    ELSIF TG_OP = 'UPDATE' THEN
        _stampedContentVersion := nextval('"dms"."ChangeVersionSequence"');
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := now();
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId" OR OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId" OR OLD."EntryDate" IS DISTINCT FROM NEW."EntryDate" OR OLD."EntryTimestamp" IS DISTINCT FROM NEW."EntryTimestamp" OR OLD."IsActive" IS DISTINCT FROM NEW."IsActive") THEN
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StudentSchoolAssociation_Stamp" ON "edfi"."StudentSchoolAssociation";
CREATE TRIGGER "TR_StudentSchoolAssociation_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."StudentSchoolAssociation"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_Stamp"();

