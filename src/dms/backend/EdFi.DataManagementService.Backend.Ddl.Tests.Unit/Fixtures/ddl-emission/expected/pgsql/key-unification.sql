CREATE SCHEMA IF NOT EXISTS "edfi";

CREATE TABLE IF NOT EXISTS "edfi"."CourseRegistration"
(
    "DocumentId" bigint NOT NULL,
    "CourseOffering_DocumentId" bigint NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "CourseOffering_SchoolId" integer GENERATED ALWAYS AS (CASE WHEN "CourseOffering_DocumentId" IS NULL THEN NULL ELSE "SchoolId_Unified" END) STORED,
    "CourseOffering_LocalCourseCode" varchar(60) NOT NULL,
    "School_SchoolId" integer GENERATED ALWAYS AS (CASE WHEN "School_DocumentId" IS NULL THEN NULL ELSE "SchoolId_Unified" END) STORED,
    "RegistrationDate" date NOT NULL,
    "SchoolId_Unified" integer NOT NULL,
    CONSTRAINT "PK_CourseRegistration" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_CourseRegistration_CourseOffering'
        AND conrelid = to_regclass('"edfi"."CourseRegistration"')
    )
    THEN
        ALTER TABLE "edfi"."CourseRegistration"
        ADD CONSTRAINT "FK_CourseRegistration_CourseOffering"
        FOREIGN KEY ("CourseOffering_DocumentId")
        REFERENCES "edfi"."CourseOffering" ("DocumentId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_CourseRegistration_School'
        AND conrelid = to_regclass('"edfi"."CourseRegistration"')
    )
    THEN
        ALTER TABLE "edfi"."CourseRegistration"
        ADD CONSTRAINT "FK_CourseRegistration_School"
        FOREIGN KEY ("School_DocumentId")
        REFERENCES "edfi"."School" ("DocumentId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_CourseRegistration_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."CourseOffering_DocumentId" IS DISTINCT FROM NEW."CourseOffering_DocumentId" OR OLD."School_DocumentId" IS DISTINCT FROM NEW."School_DocumentId" OR OLD."CourseOffering_LocalCourseCode" IS DISTINCT FROM NEW."CourseOffering_LocalCourseCode" OR OLD."RegistrationDate" IS DISTINCT FROM NEW."RegistrationDate" OR OLD."SchoolId_Unified" IS DISTINCT FROM NEW."SchoolId_Unified") THEN
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
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId_Unified" IS DISTINCT FROM NEW."SchoolId_Unified" OR OLD."CourseOffering_LocalCourseCode" IS DISTINCT FROM NEW."CourseOffering_LocalCourseCode" OR OLD."RegistrationDate" IS DISTINCT FROM NEW."RegistrationDate") THEN
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_CourseRegistration_Stamp" ON "edfi"."CourseRegistration";
CREATE TRIGGER "TR_CourseRegistration_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."CourseRegistration"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_CourseRegistration_Stamp"();

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

