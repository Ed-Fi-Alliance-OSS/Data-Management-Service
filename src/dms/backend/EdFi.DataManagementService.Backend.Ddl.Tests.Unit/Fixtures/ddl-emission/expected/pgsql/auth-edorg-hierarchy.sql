-- ==========================================================
-- Phase 1: Schemas
-- ==========================================================

CREATE SCHEMA IF NOT EXISTS "auth";

-- ==========================================================
-- Phase 2: Tables
-- ==========================================================

CREATE TABLE IF NOT EXISTS "auth"."EducationOrganizationIdToEducationOrganizationId"
(
    "SourceEducationOrganizationId" bigint NOT NULL,
    "TargetEducationOrganizationId" bigint NOT NULL,
    CONSTRAINT "PK_EducationOrganizationIdToEducationOrganizationId" PRIMARY KEY ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
);

-- ==========================================================
-- Phase 3: Indexes
-- ==========================================================

CREATE INDEX IF NOT EXISTS "IX_EducationOrganizationIdToEducationOrganizationId_Target" ON "auth"."EducationOrganizationIdToEducationOrganizationId" ("TargetEducationOrganizationId") INCLUDE ("SourceEducationOrganizationId");

-- ==========================================================
-- Phase 4: Triggers
-- ==========================================================

CREATE OR REPLACE FUNCTION "edfi"."TF_LocalEducationAgency_AuthHierarchy_Delete"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE ("SourceEducationOrganizationId", "TargetEducationOrganizationId") IN (
        SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
        FROM (
            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."EducationServiceCenter" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = OLD."EducationServiceCenter_DocumentId"
                AND OLD."EducationServiceCenter_DocumentId" IS NOT NULL

            UNION

            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."LocalEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = OLD."ParentLocalEducationAgency_DocumentId"
                AND OLD."ParentLocalEducationAgency_DocumentId" IS NOT NULL

            UNION

            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."StateEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = OLD."StateEducationAgency_DocumentId"
                AND OLD."StateEducationAgency_DocumentId" IS NOT NULL
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
    EXECUTE FUNCTION "edfi"."TF_LocalEducationAgency_AuthHierarchy_Delete"();

CREATE OR REPLACE FUNCTION "edfi"."TF_LocalEducationAgency_AuthHierarchy_Insert"()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    VALUES (NEW."EducationOrganizationId", NEW."EducationOrganizationId");

    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
    FROM (
        SELECT tuples."SourceEducationOrganizationId"
        FROM "edfi"."EducationServiceCenter" AS parent
            INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
        WHERE parent."DocumentId" = NEW."EducationServiceCenter_DocumentId"
            AND NEW."EducationServiceCenter_DocumentId" IS NOT NULL

        UNION

        SELECT tuples."SourceEducationOrganizationId"
        FROM "edfi"."LocalEducationAgency" AS parent
            INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
        WHERE parent."DocumentId" = NEW."ParentLocalEducationAgency_DocumentId"
            AND NEW."ParentLocalEducationAgency_DocumentId" IS NOT NULL

        UNION

        SELECT tuples."SourceEducationOrganizationId"
        FROM "edfi"."StateEducationAgency" AS parent
            INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
        WHERE parent."DocumentId" = NEW."StateEducationAgency_DocumentId"
            AND NEW."StateEducationAgency_DocumentId" IS NOT NULL
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
    EXECUTE FUNCTION "edfi"."TF_LocalEducationAgency_AuthHierarchy_Insert"();

CREATE OR REPLACE FUNCTION "edfi"."TF_LocalEducationAgency_AuthHierarchy_Update"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE ("SourceEducationOrganizationId", "TargetEducationOrganizationId") IN (
        SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
        FROM (
            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."EducationServiceCenter" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = OLD."EducationServiceCenter_DocumentId"
                AND OLD."EducationServiceCenter_DocumentId" IS NOT NULL
                AND (NEW."EducationServiceCenter_DocumentId" IS NULL OR OLD."EducationServiceCenter_DocumentId" <> NEW."EducationServiceCenter_DocumentId")

            UNION

            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."LocalEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = OLD."ParentLocalEducationAgency_DocumentId"
                AND OLD."ParentLocalEducationAgency_DocumentId" IS NOT NULL
                AND (NEW."ParentLocalEducationAgency_DocumentId" IS NULL OR OLD."ParentLocalEducationAgency_DocumentId" <> NEW."ParentLocalEducationAgency_DocumentId")

            UNION

            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."StateEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = OLD."StateEducationAgency_DocumentId"
                AND OLD."StateEducationAgency_DocumentId" IS NOT NULL
                AND (NEW."StateEducationAgency_DocumentId" IS NULL OR OLD."StateEducationAgency_DocumentId" <> NEW."StateEducationAgency_DocumentId")

            EXCEPT

            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."EducationServiceCenter" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = NEW."EducationServiceCenter_DocumentId"

            EXCEPT

            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."LocalEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = NEW."ParentLocalEducationAgency_DocumentId"

            EXCEPT

            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."StateEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = NEW."StateEducationAgency_DocumentId"
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
        FROM "edfi"."EducationServiceCenter" AS parent
            INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
        WHERE parent."DocumentId" = NEW."EducationServiceCenter_DocumentId"
            AND ((OLD."EducationServiceCenter_DocumentId" IS NULL AND NEW."EducationServiceCenter_DocumentId" IS NOT NULL) OR OLD."EducationServiceCenter_DocumentId" <> NEW."EducationServiceCenter_DocumentId")

        UNION

        SELECT tuples."SourceEducationOrganizationId"
        FROM "edfi"."LocalEducationAgency" AS parent
            INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
        WHERE parent."DocumentId" = NEW."ParentLocalEducationAgency_DocumentId"
            AND ((OLD."ParentLocalEducationAgency_DocumentId" IS NULL AND NEW."ParentLocalEducationAgency_DocumentId" IS NOT NULL) OR OLD."ParentLocalEducationAgency_DocumentId" <> NEW."ParentLocalEducationAgency_DocumentId")

        UNION

        SELECT tuples."SourceEducationOrganizationId"
        FROM "edfi"."StateEducationAgency" AS parent
            INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
        WHERE parent."DocumentId" = NEW."StateEducationAgency_DocumentId"
            AND ((OLD."StateEducationAgency_DocumentId" IS NULL AND NEW."StateEducationAgency_DocumentId" IS NOT NULL) OR OLD."StateEducationAgency_DocumentId" <> NEW."StateEducationAgency_DocumentId")
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
    EXECUTE FUNCTION "edfi"."TF_LocalEducationAgency_AuthHierarchy_Update"();

CREATE OR REPLACE FUNCTION "edfi"."TF_School_AuthHierarchy_Delete"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE ("SourceEducationOrganizationId", "TargetEducationOrganizationId") IN (
        SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
        FROM (
            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."LocalEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = OLD."LocalEducationAgency_DocumentId"
                AND OLD."LocalEducationAgency_DocumentId" IS NOT NULL
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

DROP TRIGGER IF EXISTS "TR_School_AuthHierarchy_Delete" ON "edfi"."School";
CREATE TRIGGER "TR_School_AuthHierarchy_Delete"
    AFTER DELETE ON "edfi"."School"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_School_AuthHierarchy_Delete"();

CREATE OR REPLACE FUNCTION "edfi"."TF_School_AuthHierarchy_Insert"()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    VALUES (NEW."EducationOrganizationId", NEW."EducationOrganizationId");

    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
    FROM (
        SELECT tuples."SourceEducationOrganizationId"
        FROM "edfi"."LocalEducationAgency" AS parent
            INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
        WHERE parent."DocumentId" = NEW."LocalEducationAgency_DocumentId"
            AND NEW."LocalEducationAgency_DocumentId" IS NOT NULL
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

DROP TRIGGER IF EXISTS "TR_School_AuthHierarchy_Insert" ON "edfi"."School";
CREATE TRIGGER "TR_School_AuthHierarchy_Insert"
    AFTER INSERT ON "edfi"."School"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_School_AuthHierarchy_Insert"();

CREATE OR REPLACE FUNCTION "edfi"."TF_School_AuthHierarchy_Update"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE ("SourceEducationOrganizationId", "TargetEducationOrganizationId") IN (
        SELECT sources."SourceEducationOrganizationId", targets."TargetEducationOrganizationId"
        FROM (
            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."LocalEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = OLD."LocalEducationAgency_DocumentId"
                AND OLD."LocalEducationAgency_DocumentId" IS NOT NULL
                AND (NEW."LocalEducationAgency_DocumentId" IS NULL OR OLD."LocalEducationAgency_DocumentId" <> NEW."LocalEducationAgency_DocumentId")

            EXCEPT

            SELECT tuples."SourceEducationOrganizationId"
            FROM "edfi"."LocalEducationAgency" AS parent
                INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                    ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
            WHERE parent."DocumentId" = NEW."LocalEducationAgency_DocumentId"
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
        FROM "edfi"."LocalEducationAgency" AS parent
            INNER JOIN "auth"."EducationOrganizationIdToEducationOrganizationId" AS tuples
                ON parent."EducationOrganizationId" = tuples."TargetEducationOrganizationId"
        WHERE parent."DocumentId" = NEW."LocalEducationAgency_DocumentId"
            AND ((OLD."LocalEducationAgency_DocumentId" IS NULL AND NEW."LocalEducationAgency_DocumentId" IS NOT NULL) OR OLD."LocalEducationAgency_DocumentId" <> NEW."LocalEducationAgency_DocumentId")
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

DROP TRIGGER IF EXISTS "TR_School_AuthHierarchy_Update" ON "edfi"."School";
CREATE TRIGGER "TR_School_AuthHierarchy_Update"
    AFTER UPDATE ON "edfi"."School"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_School_AuthHierarchy_Update"();

CREATE OR REPLACE FUNCTION "edfi"."TF_StateEducationAgency_AuthHierarchy_Delete"()
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
    EXECUTE FUNCTION "edfi"."TF_StateEducationAgency_AuthHierarchy_Delete"();

CREATE OR REPLACE FUNCTION "edfi"."TF_StateEducationAgency_AuthHierarchy_Insert"()
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
    EXECUTE FUNCTION "edfi"."TF_StateEducationAgency_AuthHierarchy_Insert"();

