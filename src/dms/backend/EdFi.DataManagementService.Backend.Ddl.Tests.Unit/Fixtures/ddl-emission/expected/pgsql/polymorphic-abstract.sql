CREATE SCHEMA IF NOT EXISTS "edfi";

CREATE TABLE IF NOT EXISTS "edfi"."LocalEducationAgency"
(
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    CONSTRAINT "PK_LocalEducationAgency" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."EducationOrganizationIdentity"
(
    "DocumentId" bigint NOT NULL,
    "DocumentUuid" uuid NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    "Discriminator" varchar(50) NOT NULL,
    CONSTRAINT "PK_EducationOrganizationIdentity" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_LocalEducationAgency_EducationOrganizationIdentity'
        AND conrelid = to_regclass('"edfi"."LocalEducationAgency"')
    )
    THEN
        ALTER TABLE "edfi"."LocalEducationAgency"
        ADD CONSTRAINT "FK_LocalEducationAgency_EducationOrganizationIdentity"
        FOREIGN KEY ("DocumentId")
        REFERENCES "edfi"."EducationOrganizationIdentity" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_School_EducationOrganizationIdentity'
        AND conrelid = to_regclass('"edfi"."School"')
    )
    THEN
        ALTER TABLE "edfi"."School"
        ADD CONSTRAINT "FK_School_EducationOrganizationIdentity"
        FOREIGN KEY ("DocumentId")
        REFERENCES "edfi"."EducationOrganizationIdentity" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_EducationOrganizationIdentity_Document'
        AND conrelid = to_regclass('"edfi"."EducationOrganizationIdentity"')
    )
    THEN
        ALTER TABLE "edfi"."EducationOrganizationIdentity"
        ADD CONSTRAINT "FK_EducationOrganizationIdentity_Document"
        FOREIGN KEY ("DocumentId")
        REFERENCES "dms"."Document" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE OR REPLACE VIEW "edfi"."EducationOrganization_View" AS
SELECT "DocumentId" AS "DocumentId", "EducationOrganizationId" AS "EducationOrganizationId", 'Ed-Fi:School'::varchar(50) AS "Discriminator"
FROM "edfi"."School"
UNION ALL
SELECT "DocumentId" AS "DocumentId", "EducationOrganizationId" AS "EducationOrganizationId", 'Ed-Fi:LocalEducationAgency'::varchar(50) AS "Discriminator"
FROM "edfi"."LocalEducationAgency"
;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AbstractIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        INSERT INTO "edfi"."EducationOrganizationIdentity" ("DocumentId", "DocumentUuid", "EducationOrganizationId", "Discriminator")
        VALUES (NEW."DocumentId", NEW."DocumentUuid", NEW."EducationOrganizationId", 'Ed-Fi:LocalEducationAgency')
        ON CONFLICT ("DocumentId")
        DO UPDATE SET "EducationOrganizationId" = EXCLUDED."EducationOrganizationId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_LocalEducationAgency_AbstractIdentity" ON "edfi"."LocalEducationAgency";
CREATE TRIGGER "TR_LocalEducationAgency_AbstractIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."LocalEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AbstractIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
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
    IF TG_OP = 'UPDATE' AND (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_LocalEducationAgency_Stamp" ON "edfi"."LocalEducationAgency";
CREATE TRIGGER "TR_LocalEducationAgency_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."LocalEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_AbstractIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        INSERT INTO "edfi"."EducationOrganizationIdentity" ("DocumentId", "DocumentUuid", "EducationOrganizationId", "Discriminator")
        VALUES (NEW."DocumentId", NEW."DocumentUuid", NEW."EducationOrganizationId", 'Ed-Fi:School')
        ON CONFLICT ("DocumentId")
        DO UPDATE SET "EducationOrganizationId" = EXCLUDED."EducationOrganizationId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_AbstractIdentity" ON "edfi"."School";
CREATE TRIGGER "TR_School_AbstractIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_AbstractIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
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
    IF TG_OP = 'UPDATE' AND (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
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

