CREATE SCHEMA IF NOT EXISTS "edfi";

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId")
);

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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
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
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$$.schoolId=' || NEW."SchoolId"::text), NEW."DocumentId", 1);
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_ReferentialIdentity" ON "edfi"."School";
CREATE TRIGGER "TR_School_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_CourseRegistration_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId_Unified" IS DISTINCT FROM NEW."SchoolId_Unified" OR OLD."CourseOffering_LocalCourseCode" IS DISTINCT FROM NEW."CourseOffering_LocalCourseCode" OR OLD."RegistrationDate" IS DISTINCT FROM NEW."RegistrationDate") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_CourseRegistration_Stamp" ON "edfi"."CourseRegistration";
CREATE TRIGGER "TR_CourseRegistration_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."CourseRegistration"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_CourseRegistration_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_CourseRegistration_ReferentialIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."SchoolId_Unified" IS DISTINCT FROM NEW."SchoolId_Unified" OR OLD."CourseOffering_LocalCourseCode" IS DISTINCT FROM NEW."CourseOffering_LocalCourseCode" OR OLD."RegistrationDate" IS DISTINCT FROM NEW."RegistrationDate") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 2;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiCourseRegistration' || '$$.courseOfferingReference.schoolId=' || NEW."CourseOffering_SchoolId"::text || '#' || '$$.courseOfferingReference.localCourseCode=' || NEW."CourseOffering_LocalCourseCode"::text || '#' || '$$.schoolReference.schoolId=' || NEW."School_SchoolId"::text || '#' || '$$.registrationDate=' || NEW."RegistrationDate"::text), NEW."DocumentId", 2);
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_CourseRegistration_ReferentialIdentity" ON "edfi"."CourseRegistration";
CREATE TRIGGER "TR_CourseRegistration_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."CourseRegistration"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_CourseRegistration_ReferentialIdentity"();

