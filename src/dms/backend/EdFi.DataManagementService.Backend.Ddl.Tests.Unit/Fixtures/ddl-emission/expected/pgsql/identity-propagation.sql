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
    "SchoolId" integer NOT NULL,
    "StudentUniqueId" varchar(32) NOT NULL,
    "EntryDate" date NOT NULL,
    CONSTRAINT "PK_StudentSchoolAssociation" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_StudentSchoolAssociation_School'
        AND conrelid = to_regclass('edfi.StudentSchoolAssociation')
    )
    THEN
        ALTER TABLE "edfi"."StudentSchoolAssociation"
        ADD CONSTRAINT "FK_StudentSchoolAssociation_School"
        FOREIGN KEY ("SchoolId")
        REFERENCES "edfi"."School" ("SchoolId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_Stamp" ON "edfi"."School";
CREATE TRIGGER "TR_School_Stamp"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "dms"."ReferentialIdentity"
    WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
    INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
    VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$$.schoolId=' || NEW."SchoolId"::text), NEW."DocumentId", 1);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_ReferentialIdentity" ON "edfi"."School";
CREATE TRIGGER "TR_School_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId" OR OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId" OR OLD."EntryDate" IS DISTINCT FROM NEW."EntryDate") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StudentSchoolAssociation_Stamp" ON "edfi"."StudentSchoolAssociation";
CREATE TRIGGER "TR_StudentSchoolAssociation_Stamp"
BEFORE INSERT OR UPDATE ON "edfi"."StudentSchoolAssociation"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_ReferentialIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "dms"."ReferentialIdentity"
    WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 2;
    INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
    VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiStudentSchoolAssociation' || '$$.schoolReference.schoolId=' || NEW."SchoolId"::text || '#' || '$$.studentReference.studentUniqueId=' || NEW."StudentUniqueId"::text || '#' || '$$.entryDate=' || NEW."EntryDate"::text), NEW."DocumentId", 2);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StudentSchoolAssociation_ReferentialIdentity" ON "edfi"."StudentSchoolAssociation";
CREATE TRIGGER "TR_StudentSchoolAssociation_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."StudentSchoolAssociation"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_ReferentialIdentity"();

