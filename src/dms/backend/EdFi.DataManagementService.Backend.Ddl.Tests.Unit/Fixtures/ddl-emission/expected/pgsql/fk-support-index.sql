CREATE SCHEMA IF NOT EXISTS "edfi";

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."Enrollment"
(
    "DocumentId" bigint NOT NULL,
    "EnrollmentId" integer NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_Enrollment" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_Enrollment_School'
        AND conrelid = to_regclass('edfi.Enrollment')
    )
    THEN
        ALTER TABLE "edfi"."Enrollment"
        ADD CONSTRAINT "FK_Enrollment_School"
        FOREIGN KEY ("SchoolId")
        REFERENCES "edfi"."School" ("SchoolId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "edfi"."IX_Enrollment_SchoolId" ON "edfi"."Enrollment" ("SchoolId");

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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_Enrollment_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    IF TG_OP = 'UPDATE' AND (OLD."EnrollmentId" IS DISTINCT FROM NEW."EnrollmentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Enrollment_Stamp" ON "edfi"."Enrollment";
CREATE TRIGGER "TR_Enrollment_Stamp"
BEFORE INSERT OR UPDATE ON "edfi"."Enrollment"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_Enrollment_Stamp"();

