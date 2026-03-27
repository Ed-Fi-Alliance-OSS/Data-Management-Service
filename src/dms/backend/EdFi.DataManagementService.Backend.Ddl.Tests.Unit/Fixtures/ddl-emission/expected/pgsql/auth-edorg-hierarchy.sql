CREATE SCHEMA IF NOT EXISTS "edfi";
CREATE SCHEMA IF NOT EXISTS "auth";

CREATE TABLE IF NOT EXISTS "edfi"."LocalEducationAgency"
(
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    "StateEducationAgency_EducationOrganizationId" integer NULL,
    CONSTRAINT "PK_LocalEducationAgency" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."StateEducationAgency"
(
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    CONSTRAINT "PK_StateEducationAgency" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "auth"."EducationOrganizationIdToEducationOrganizationId"
(
    "SourceEducationOrganizationId" bigint NOT NULL,
    "TargetEducationOrganizationId" bigint NOT NULL,
    CONSTRAINT "PK_EducationOrganizationIdToEducationOrganizationId" PRIMARY KEY ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
);

CREATE TABLE IF NOT EXISTS "edfi"."EducationOrganizationIdentity"
(
    "DocumentId" bigint NOT NULL,
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
        WHERE conname = 'FK_StateEducationAgency_EducationOrganizationIdentity'
        AND conrelid = to_regclass('"edfi"."StateEducationAgency"')
    )
    THEN
        ALTER TABLE "edfi"."StateEducationAgency"
        ADD CONSTRAINT "FK_StateEducationAgency_EducationOrganizationIdentity"
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

CREATE INDEX IF NOT EXISTS "IX_EducationOrganizationIdToEducationOrganizationId_Target" ON "auth"."EducationOrganizationIdToEducationOrganizationId" ("TargetEducationOrganizationId") INCLUDE ("SourceEducationOrganizationId");

CREATE OR REPLACE VIEW "edfi"."EducationOrganization_View" AS
SELECT "DocumentId" AS "DocumentId", "EducationOrganizationId" AS "EducationOrganizationId", 'Ed-Fi:LocalEducationAgency'::varchar(50) AS "Discriminator"
FROM "edfi"."LocalEducationAgency"
UNION ALL
SELECT "DocumentId" AS "DocumentId", "EducationOrganizationId" AS "EducationOrganizationId", 'Ed-Fi:StateEducationAgency'::varchar(50) AS "Discriminator"
FROM "edfi"."StateEducationAgency"
;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AbstractIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        INSERT INTO "edfi"."EducationOrganizationIdentity" ("DocumentId", "EducationOrganizationId", "Discriminator")
        VALUES (NEW."DocumentId", NEW."EducationOrganizationId", 'Ed-Fi:LocalEducationAgency')
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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AuthHierarchy_Delete"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE ("SourceEducationOrganizationId", "TargetEducationOrganizationId") IN (
        SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
        FROM (
            SELECT tuples."SourceEducationOrganizationId"
            FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
            WHERE tuples."TargetEducationOrganizationId" = OLD."StateEducationAgency_EducationOrganizationId"
                AND OLD."StateEducationAgency_EducationOrganizationId" IS NOT NULL
        ) AS sources
        CROSS JOIN
        (
            SELECT tuples."TargetEducationOrganizationId"
            FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
            WHERE tuples."SourceEducationOrganizationId" = OLD."EducationOrganizationId"
        ) AS targets
    );

    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE "SourceEducationOrganizationId" = OLD."EducationOrganizationId" AND "TargetEducationOrganizationId" = OLD."EducationOrganizationId";
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_LocalEducationAgency_AuthHierarchy_Delete" ON "edfi"."LocalEducationAgency";
CREATE TRIGGER "TR_LocalEducationAgency_AuthHierarchy_Delete"
    AFTER DELETE ON "edfi"."LocalEducationAgency"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AuthHierarchy_Delete"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AuthHierarchy_Insert"()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    VALUES (NEW."EducationOrganizationId", NEW."EducationOrganizationId");

    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
    FROM (
        SELECT tuples."SourceEducationOrganizationId"
        FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
        WHERE tuples."TargetEducationOrganizationId" = NEW."StateEducationAgency_EducationOrganizationId"
            AND NEW."StateEducationAgency_EducationOrganizationId" IS NOT NULL
    ) AS sources
    CROSS JOIN
    (
        SELECT tuples."TargetEducationOrganizationId"
        FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
        WHERE tuples."SourceEducationOrganizationId" = NEW."EducationOrganizationId"
    ) AS targets;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_LocalEducationAgency_AuthHierarchy_Insert" ON "edfi"."LocalEducationAgency";
CREATE TRIGGER "TR_LocalEducationAgency_AuthHierarchy_Insert"
    AFTER INSERT ON "edfi"."LocalEducationAgency"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AuthHierarchy_Insert"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AuthHierarchy_Update"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE ("SourceEducationOrganizationId", "TargetEducationOrganizationId") IN (
        SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
        FROM (
            SELECT tuples."SourceEducationOrganizationId"
            FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
            WHERE tuples."TargetEducationOrganizationId" = OLD."StateEducationAgency_EducationOrganizationId"
                AND OLD."StateEducationAgency_EducationOrganizationId" IS NOT NULL
                AND (NEW."StateEducationAgency_EducationOrganizationId" IS NULL OR OLD."StateEducationAgency_EducationOrganizationId" <> NEW."StateEducationAgency_EducationOrganizationId")

            EXCEPT

            SELECT tuples."SourceEducationOrganizationId"
            FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
            WHERE tuples."TargetEducationOrganizationId" = NEW."StateEducationAgency_EducationOrganizationId"
        ) AS sources
        CROSS JOIN
        (
            SELECT tuples."TargetEducationOrganizationId"
            FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
            WHERE tuples."SourceEducationOrganizationId" = NEW."EducationOrganizationId"
        ) AS targets
    );

    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
    FROM (
        SELECT tuples."SourceEducationOrganizationId"
        FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
        WHERE tuples."TargetEducationOrganizationId" = NEW."StateEducationAgency_EducationOrganizationId"
            AND ((OLD."StateEducationAgency_EducationOrganizationId" IS NULL AND NEW."StateEducationAgency_EducationOrganizationId" IS NOT NULL) OR OLD."StateEducationAgency_EducationOrganizationId" <> NEW."StateEducationAgency_EducationOrganizationId")
    ) AS sources
    CROSS JOIN
    (
        SELECT tuples."TargetEducationOrganizationId"
        FROM "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
        WHERE tuples."SourceEducationOrganizationId" = NEW."EducationOrganizationId"
    ) AS targets
    ON CONFLICT ("SourceEducationOrganizationId", "TargetEducationOrganizationId") DO NOTHING;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_LocalEducationAgency_AuthHierarchy_Update" ON "edfi"."LocalEducationAgency";
CREATE TRIGGER "TR_LocalEducationAgency_AuthHierarchy_Update"
    AFTER UPDATE ON "edfi"."LocalEducationAgency"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AuthHierarchy_Update"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 2;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiLocalEducationAgency' || '$$.educationOrganizationId=' || NEW."EducationOrganizationId"::text), NEW."DocumentId", 2);
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiEducationOrganization' || '$$.educationOrganizationId=' || NEW."EducationOrganizationId"::text), NEW."DocumentId", 1);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_LocalEducationAgency_ReferentialIdentity" ON "edfi"."LocalEducationAgency";
CREATE TRIGGER "TR_LocalEducationAgency_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."LocalEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_Stamp"()
RETURNS TRIGGER AS $func$
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
    IF TG_OP = 'UPDATE' AND (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_LocalEducationAgency_Stamp" ON "edfi"."LocalEducationAgency";
CREATE TRIGGER "TR_LocalEducationAgency_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."LocalEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StateEducationAgency_AbstractIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        INSERT INTO "edfi"."EducationOrganizationIdentity" ("DocumentId", "EducationOrganizationId", "Discriminator")
        VALUES (NEW."DocumentId", NEW."EducationOrganizationId", 'Ed-Fi:StateEducationAgency')
        ON CONFLICT ("DocumentId")
        DO UPDATE SET "EducationOrganizationId" = EXCLUDED."EducationOrganizationId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StateEducationAgency_AbstractIdentity" ON "edfi"."StateEducationAgency";
CREATE TRIGGER "TR_StateEducationAgency_AbstractIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."StateEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_StateEducationAgency_AbstractIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StateEducationAgency_AuthHierarchy_Delete"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE "SourceEducationOrganizationId" = OLD."EducationOrganizationId" AND "TargetEducationOrganizationId" = OLD."EducationOrganizationId";
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StateEducationAgency_AuthHierarchy_Delete" ON "edfi"."StateEducationAgency";
CREATE TRIGGER "TR_StateEducationAgency_AuthHierarchy_Delete"
    AFTER DELETE ON "edfi"."StateEducationAgency"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_TR_StateEducationAgency_AuthHierarchy_Delete"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StateEducationAgency_AuthHierarchy_Insert"()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    VALUES (NEW."EducationOrganizationId", NEW."EducationOrganizationId");
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StateEducationAgency_AuthHierarchy_Insert" ON "edfi"."StateEducationAgency";
CREATE TRIGGER "TR_StateEducationAgency_AuthHierarchy_Insert"
    AFTER INSERT ON "edfi"."StateEducationAgency"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_TR_StateEducationAgency_AuthHierarchy_Insert"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StateEducationAgency_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 3;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiStateEducationAgency' || '$$.educationOrganizationId=' || NEW."EducationOrganizationId"::text), NEW."DocumentId", 3);
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiEducationOrganization' || '$$.educationOrganizationId=' || NEW."EducationOrganizationId"::text), NEW."DocumentId", 1);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StateEducationAgency_ReferentialIdentity" ON "edfi"."StateEducationAgency";
CREATE TRIGGER "TR_StateEducationAgency_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."StateEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_StateEducationAgency_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StateEducationAgency_Stamp"()
RETURNS TRIGGER AS $func$
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
    IF TG_OP = 'UPDATE' AND (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StateEducationAgency_Stamp" ON "edfi"."StateEducationAgency";
CREATE TRIGGER "TR_StateEducationAgency_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."StateEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_StateEducationAgency_Stamp"();

