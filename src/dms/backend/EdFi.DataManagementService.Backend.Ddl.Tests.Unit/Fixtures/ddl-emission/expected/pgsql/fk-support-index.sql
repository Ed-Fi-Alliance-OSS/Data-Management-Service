CREATE SCHEMA IF NOT EXISTS "edfi";

CREATE TABLE IF NOT EXISTS "edfi"."Enrollment"
(
    "DocumentId" bigint NOT NULL,
    "EnrollmentId" integer NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_Enrollment" PRIMARY KEY ("DocumentId")
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
        WHERE conname = 'FK_Enrollment_School'
        AND conrelid = to_regclass('"edfi"."Enrollment"')
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

CREATE INDEX IF NOT EXISTS "IX_Enrollment_SchoolId" ON "edfi"."Enrollment" ("SchoolId");

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_Enrollment_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
    _stampedContentLastModifiedAt timestamp with time zone;
    _stampedDocumentUuid uuid;
    _stampedIdentityVersion bigint;
    _stampedIdentityLastModifiedAt timestamp with time zone;
    _stampedCreatedAt timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."EnrollmentId" IS DISTINCT FROM NEW."EnrollmentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN
        SELECT "ContentVersion", "ContentLastModifiedAt", "DocumentUuid", "IdentityVersion", "IdentityLastModifiedAt", "CreatedAt"
        INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt, _stampedDocumentUuid, _stampedIdentityVersion, _stampedIdentityLastModifiedAt, _stampedCreatedAt
        FROM "dms"."Document"
        WHERE "DocumentId" = NEW."DocumentId";
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := _stampedContentLastModifiedAt;
        NEW."DocumentUuid" := _stampedDocumentUuid;
        NEW."IdentityVersion" := _stampedIdentityVersion;
        NEW."IdentityLastModifiedAt" := _stampedIdentityLastModifiedAt;
        NEW."CreatedAt" := _stampedCreatedAt;
    ELSIF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId"
        RETURNING "ContentVersion", "ContentLastModifiedAt" INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt;
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := _stampedContentLastModifiedAt;
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."EnrollmentId" IS DISTINCT FROM NEW."EnrollmentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId"
        RETURNING "IdentityVersion", "IdentityLastModifiedAt" INTO STRICT _stampedIdentityVersion, _stampedIdentityLastModifiedAt;
        NEW."IdentityVersion" := _stampedIdentityVersion;
        NEW."IdentityLastModifiedAt" := _stampedIdentityLastModifiedAt;
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Enrollment_Stamp" ON "edfi"."Enrollment";
CREATE TRIGGER "TR_Enrollment_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."Enrollment"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_Enrollment_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
    _stampedContentLastModifiedAt timestamp with time zone;
    _stampedDocumentUuid uuid;
    _stampedIdentityVersion bigint;
    _stampedIdentityLastModifiedAt timestamp with time zone;
    _stampedCreatedAt timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN
        SELECT "ContentVersion", "ContentLastModifiedAt", "DocumentUuid", "IdentityVersion", "IdentityLastModifiedAt", "CreatedAt"
        INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt, _stampedDocumentUuid, _stampedIdentityVersion, _stampedIdentityLastModifiedAt, _stampedCreatedAt
        FROM "dms"."Document"
        WHERE "DocumentId" = NEW."DocumentId";
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := _stampedContentLastModifiedAt;
        NEW."DocumentUuid" := _stampedDocumentUuid;
        NEW."IdentityVersion" := _stampedIdentityVersion;
        NEW."IdentityLastModifiedAt" := _stampedIdentityLastModifiedAt;
        NEW."CreatedAt" := _stampedCreatedAt;
    ELSIF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId"
        RETURNING "ContentVersion", "ContentLastModifiedAt" INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt;
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := _stampedContentLastModifiedAt;
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId"
        RETURNING "IdentityVersion", "IdentityLastModifiedAt" INTO STRICT _stampedIdentityVersion, _stampedIdentityLastModifiedAt;
        NEW."IdentityVersion" := _stampedIdentityVersion;
        NEW."IdentityLastModifiedAt" := _stampedIdentityLastModifiedAt;
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_Stamp" ON "edfi"."School";
CREATE TRIGGER "TR_School_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

