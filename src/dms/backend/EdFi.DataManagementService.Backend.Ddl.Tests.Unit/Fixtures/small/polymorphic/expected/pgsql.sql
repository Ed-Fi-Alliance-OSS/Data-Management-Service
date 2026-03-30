-- ==========================================================
-- Phase 0: Preflight (fail fast on schema hash mismatch)
-- ==========================================================

-- Preflight: fail fast if database is provisioned for a different schema hash
DO $$
DECLARE
    _stored_hash text;
BEGIN
    IF to_regclass('"dms"."EffectiveSchema"') IS NOT NULL THEN
        SELECT "EffectiveSchemaHash" INTO _stored_hash FROM "dms"."EffectiveSchema"
        WHERE "EffectiveSchemaSingletonId" = 1;
        IF _stored_hash IS NOT NULL AND _stored_hash <> 'aed2277170a95207e1cf932e9e24744cd2b944999e8b42a1fa0c8678eda2ab50' THEN
            RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database has ''%'' but expected ''%''', _stored_hash, 'aed2277170a95207e1cf932e9e24744cd2b944999e8b42a1fa0c8678eda2ab50';
        END IF;
    END IF;
END $$;

-- ==========================================================
-- Phase 1: Schemas
-- ==========================================================

CREATE SCHEMA IF NOT EXISTS "dms";

-- ==========================================================
-- Phase 2: Extensions
-- ==========================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ==========================================================
-- Phase 3: Sequences
-- ==========================================================

CREATE SEQUENCE IF NOT EXISTS "dms"."ChangeVersionSequence" START WITH 1;

CREATE SEQUENCE IF NOT EXISTS "dms"."CollectionItemIdSequence" START WITH 1;

-- ==========================================================
-- Phase 4: Functions and Types
-- ==========================================================

CREATE OR REPLACE FUNCTION "dms"."throw_error"(code text, msg text)
RETURNS integer
LANGUAGE plpgsql
AS $throw_error$
BEGIN
    RAISE EXCEPTION '%', msg USING ERRCODE = code;
END
$throw_error$;

CREATE OR REPLACE FUNCTION "dms"."uuidv5"(namespace_uuid uuid, name_text text)
RETURNS uuid
LANGUAGE plpgsql
IMMUTABLE STRICT PARALLEL SAFE
AS $uuidv5$
DECLARE
    hash bytea;
BEGIN
    hash := digest(
        decode(replace(namespace_uuid::text, '-', ''), 'hex')
        || convert_to(name_text, 'UTF8'),
        'sha1'
    );
    hash := set_byte(hash, 6, (get_byte(hash, 6) & x'0f'::int) | x'50'::int);
    hash := set_byte(hash, 8, (get_byte(hash, 8) & x'3f'::int) | x'80'::int);
    RETURN encode(substring(hash from 1 for 16), 'hex')::uuid;
END
$uuidv5$;

-- ==========================================================
-- Phase 5: Tables (PK/UNIQUE/CHECK only, no cross-table FKs)
-- ==========================================================

CREATE TABLE IF NOT EXISTS "dms"."Descriptor"
(
    "DocumentId" bigint NOT NULL,
    "Namespace" varchar(255) NOT NULL,
    "CodeValue" varchar(50) NOT NULL,
    "ShortDescription" varchar(75) NOT NULL,
    "Description" varchar(1024) NULL,
    "EffectiveBeginDate" date NULL,
    "EffectiveEndDate" date NULL,
    "Discriminator" varchar(128) NOT NULL,
    "Uri" varchar(306) NOT NULL,
    CONSTRAINT "PK_Descriptor" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'UX_Descriptor_Uri_Discriminator'
        AND conrelid = to_regclass('"dms"."Descriptor"')
    )
    THEN
        ALTER TABLE "dms"."Descriptor"
        ADD CONSTRAINT "UX_Descriptor_Uri_Discriminator" UNIQUE ("Uri", "Discriminator");
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS "dms"."Document"
(
    "DocumentId" bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    "DocumentUuid" uuid NOT NULL,
    "ResourceKeyId" smallint NOT NULL,
    "ContentVersion" bigint NOT NULL DEFAULT nextval('"dms"."ChangeVersionSequence"'),
    "IdentityVersion" bigint NOT NULL DEFAULT nextval('"dms"."ChangeVersionSequence"'),
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "IdentityLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_Document" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'UX_Document_DocumentUuid'
        AND conrelid = to_regclass('"dms"."Document"')
    )
    THEN
        ALTER TABLE "dms"."Document"
        ADD CONSTRAINT "UX_Document_DocumentUuid" UNIQUE ("DocumentUuid");
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS "dms"."DocumentCache"
(
    "DocumentId" bigint NOT NULL,
    "DocumentUuid" uuid NOT NULL,
    "ProjectName" varchar(256) NOT NULL,
    "ResourceName" varchar(256) NOT NULL,
    "ResourceVersion" varchar(32) NOT NULL,
    "Etag" varchar(64) NOT NULL,
    "LastModifiedAt" timestamp with time zone NOT NULL,
    "DocumentJson" jsonb NOT NULL,
    "ComputedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_DocumentCache" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'UX_DocumentCache_DocumentUuid'
        AND conrelid = to_regclass('"dms"."DocumentCache"')
    )
    THEN
        ALTER TABLE "dms"."DocumentCache"
        ADD CONSTRAINT "UX_DocumentCache_DocumentUuid" UNIQUE ("DocumentUuid");
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_DocumentCache_JsonObject'
        AND conrelid = to_regclass('"dms"."DocumentCache"')
    )
    THEN
        ALTER TABLE "dms"."DocumentCache"
        ADD CONSTRAINT "CK_DocumentCache_JsonObject" CHECK (jsonb_typeof("DocumentJson") = 'object');
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS "dms"."DocumentChangeEvent"
(
    "ChangeVersion" bigint NOT NULL,
    "DocumentId" bigint NOT NULL,
    "ResourceKeyId" smallint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_DocumentChangeEvent" PRIMARY KEY ("ChangeVersion", "DocumentId")
);

CREATE TABLE IF NOT EXISTS "dms"."EffectiveSchema"
(
    "EffectiveSchemaSingletonId" smallint NOT NULL,
    "ApiSchemaFormatVersion" varchar(64) NOT NULL,
    "EffectiveSchemaHash" varchar(64) NOT NULL,
    "ResourceKeyCount" smallint NOT NULL,
    "ResourceKeySeedHash" bytea NOT NULL,
    "AppliedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_EffectiveSchema" PRIMARY KEY ("EffectiveSchemaSingletonId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_EffectiveSchema_Singleton'
        AND conrelid = to_regclass('"dms"."EffectiveSchema"')
    )
    THEN
        ALTER TABLE "dms"."EffectiveSchema"
        ADD CONSTRAINT "CK_EffectiveSchema_Singleton" CHECK ("EffectiveSchemaSingletonId" = 1);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_EffectiveSchema_ApiSchemaFormatVersion_NotBlank'
        AND conrelid = to_regclass('"dms"."EffectiveSchema"')
    )
    THEN
        ALTER TABLE "dms"."EffectiveSchema"
        ADD CONSTRAINT "CK_EffectiveSchema_ApiSchemaFormatVersion_NotBlank" CHECK (btrim("ApiSchemaFormatVersion") <> '');
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_EffectiveSchema_ResourceKeySeedHash_Length'
        AND conrelid = to_regclass('"dms"."EffectiveSchema"')
    )
    THEN
        ALTER TABLE "dms"."EffectiveSchema"
        ADD CONSTRAINT "CK_EffectiveSchema_ResourceKeySeedHash_Length" CHECK (octet_length("ResourceKeySeedHash") = 32);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'UX_EffectiveSchema_EffectiveSchemaHash'
        AND conrelid = to_regclass('"dms"."EffectiveSchema"')
    )
    THEN
        ALTER TABLE "dms"."EffectiveSchema"
        ADD CONSTRAINT "UX_EffectiveSchema_EffectiveSchemaHash" UNIQUE ("EffectiveSchemaHash");
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS "dms"."ReferentialIdentity"
(
    "ReferentialId" uuid NOT NULL,
    "DocumentId" bigint NOT NULL,
    "ResourceKeyId" smallint NOT NULL,
    CONSTRAINT "PK_ReferentialIdentity" PRIMARY KEY ("ReferentialId")
);

CREATE TABLE IF NOT EXISTS "dms"."ResourceKey"
(
    "ResourceKeyId" smallint NOT NULL,
    "ProjectName" varchar(256) NOT NULL,
    "ResourceName" varchar(256) NOT NULL,
    "ResourceVersion" varchar(32) NOT NULL,
    CONSTRAINT "PK_ResourceKey" PRIMARY KEY ("ResourceKeyId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'UX_ResourceKey_ProjectName_ResourceName'
        AND conrelid = to_regclass('"dms"."ResourceKey"')
    )
    THEN
        ALTER TABLE "dms"."ResourceKey"
        ADD CONSTRAINT "UX_ResourceKey_ProjectName_ResourceName" UNIQUE ("ProjectName", "ResourceName");
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS "dms"."SchemaComponent"
(
    "EffectiveSchemaHash" varchar(64) NOT NULL,
    "ProjectEndpointName" varchar(128) NOT NULL,
    "ProjectName" varchar(256) NOT NULL,
    "ProjectVersion" varchar(32) NOT NULL,
    "IsExtensionProject" boolean NOT NULL,
    CONSTRAINT "PK_SchemaComponent" PRIMARY KEY ("EffectiveSchemaHash", "ProjectEndpointName")
);

-- ==========================================================
-- Phase 6: Foreign Keys
-- ==========================================================

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_Descriptor_Document'
        AND conrelid = to_regclass('"dms"."Descriptor"')
    )
    THEN
        ALTER TABLE "dms"."Descriptor"
        ADD CONSTRAINT "FK_Descriptor_Document"
        FOREIGN KEY ("DocumentId")
        REFERENCES "dms"."Document" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_Document_ResourceKey'
        AND conrelid = to_regclass('"dms"."Document"')
    )
    THEN
        ALTER TABLE "dms"."Document"
        ADD CONSTRAINT "FK_Document_ResourceKey"
        FOREIGN KEY ("ResourceKeyId")
        REFERENCES "dms"."ResourceKey" ("ResourceKeyId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_DocumentCache_Document'
        AND conrelid = to_regclass('"dms"."DocumentCache"')
    )
    THEN
        ALTER TABLE "dms"."DocumentCache"
        ADD CONSTRAINT "FK_DocumentCache_Document"
        FOREIGN KEY ("DocumentId")
        REFERENCES "dms"."Document" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_DocumentChangeEvent_Document'
        AND conrelid = to_regclass('"dms"."DocumentChangeEvent"')
    )
    THEN
        ALTER TABLE "dms"."DocumentChangeEvent"
        ADD CONSTRAINT "FK_DocumentChangeEvent_Document"
        FOREIGN KEY ("DocumentId")
        REFERENCES "dms"."Document" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_DocumentChangeEvent_ResourceKey'
        AND conrelid = to_regclass('"dms"."DocumentChangeEvent"')
    )
    THEN
        ALTER TABLE "dms"."DocumentChangeEvent"
        ADD CONSTRAINT "FK_DocumentChangeEvent_ResourceKey"
        FOREIGN KEY ("ResourceKeyId")
        REFERENCES "dms"."ResourceKey" ("ResourceKeyId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ReferentialIdentity_Document'
        AND conrelid = to_regclass('"dms"."ReferentialIdentity"')
    )
    THEN
        ALTER TABLE "dms"."ReferentialIdentity"
        ADD CONSTRAINT "FK_ReferentialIdentity_Document"
        FOREIGN KEY ("DocumentId")
        REFERENCES "dms"."Document" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ReferentialIdentity_ResourceKey'
        AND conrelid = to_regclass('"dms"."ReferentialIdentity"')
    )
    THEN
        ALTER TABLE "dms"."ReferentialIdentity"
        ADD CONSTRAINT "FK_ReferentialIdentity_ResourceKey"
        FOREIGN KEY ("ResourceKeyId")
        REFERENCES "dms"."ResourceKey" ("ResourceKeyId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchemaComponent_EffectiveSchemaHash'
        AND conrelid = to_regclass('"dms"."SchemaComponent"')
    )
    THEN
        ALTER TABLE "dms"."SchemaComponent"
        ADD CONSTRAINT "FK_SchemaComponent_EffectiveSchemaHash"
        FOREIGN KEY ("EffectiveSchemaHash")
        REFERENCES "dms"."EffectiveSchema" ("EffectiveSchemaHash")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

-- ==========================================================
-- Phase 7: Indexes
-- ==========================================================

CREATE INDEX IF NOT EXISTS "IX_Descriptor_Uri_Discriminator" ON "dms"."Descriptor" ("Uri", "Discriminator");

CREATE INDEX IF NOT EXISTS "IX_Document_ResourceKeyId_DocumentId" ON "dms"."Document" ("ResourceKeyId", "DocumentId");

CREATE INDEX IF NOT EXISTS "IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt" ON "dms"."DocumentCache" ("ProjectName", "ResourceName", "LastModifiedAt", "DocumentId");

CREATE INDEX IF NOT EXISTS "IX_DocumentChangeEvent_DocumentId" ON "dms"."DocumentChangeEvent" ("DocumentId");

CREATE INDEX IF NOT EXISTS "IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion" ON "dms"."DocumentChangeEvent" ("ResourceKeyId", "ChangeVersion", "DocumentId");

CREATE INDEX IF NOT EXISTS "IX_ReferentialIdentity_DocumentId" ON "dms"."ReferentialIdentity" ("DocumentId");

-- ==========================================================
-- Phase 8: Triggers
-- ==========================================================

CREATE OR REPLACE FUNCTION "dms"."TF_Document_Journal"()
RETURNS TRIGGER AS $func$
BEGIN
    INSERT INTO "dms"."DocumentChangeEvent" ("ChangeVersion", "DocumentId", "ResourceKeyId", "CreatedAt")
    VALUES (NEW."ContentVersion", NEW."DocumentId", NEW."ResourceKeyId", now());
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Document_Journal" ON "dms"."Document";
CREATE TRIGGER "TR_Document_Journal"
    AFTER INSERT OR UPDATE OF "ContentVersion" ON "dms"."Document"
    FOR EACH ROW
    EXECUTE FUNCTION "dms"."TF_Document_Journal"();

CREATE SCHEMA IF NOT EXISTS "edfi";
CREATE SCHEMA IF NOT EXISTS "auth";

CREATE TABLE IF NOT EXISTS "edfi"."LocalEducationAgency"
(
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    "LocalEducationAgencyId" integer NOT NULL,
    CONSTRAINT "PK_LocalEducationAgency" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_LocalEducationAgency_NK" UNIQUE ("LocalEducationAgencyId")
);

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_School_NK" UNIQUE ("SchoolId")
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
    "Discriminator" varchar(256) NOT NULL,
    CONSTRAINT "PK_EducationOrganizationIdentity" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_EducationOrganizationIdentity_NK" UNIQUE ("EducationOrganizationId"),
    CONSTRAINT "UX_EducationOrganizationIdentity_RefKey" UNIQUE ("DocumentId", "EducationOrganizationId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_LocalEducationAgency_Document'
        AND conrelid = to_regclass('"edfi"."LocalEducationAgency"')
    )
    THEN
        ALTER TABLE "edfi"."LocalEducationAgency"
        ADD CONSTRAINT "FK_LocalEducationAgency_Document"
        FOREIGN KEY ("DocumentId")
        REFERENCES "dms"."Document" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_School_Document'
        AND conrelid = to_regclass('"edfi"."School"')
    )
    THEN
        ALTER TABLE "edfi"."School"
        ADD CONSTRAINT "FK_School_Document"
        FOREIGN KEY ("DocumentId")
        REFERENCES "dms"."Document" ("DocumentId")
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
SELECT "DocumentId" AS "DocumentId", "LocalEducationAgencyId" AS "EducationOrganizationId", 'Ed-Fi:LocalEducationAgency'::varchar(256) AS "Discriminator"
FROM "edfi"."LocalEducationAgency"
UNION ALL
SELECT "DocumentId" AS "DocumentId", "SchoolId" AS "EducationOrganizationId", 'Ed-Fi:School'::varchar(256) AS "Discriminator"
FROM "edfi"."School"
;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AbstractIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."LocalEducationAgencyId" IS DISTINCT FROM NEW."LocalEducationAgencyId") THEN
        INSERT INTO "edfi"."EducationOrganizationIdentity" ("DocumentId", "EducationOrganizationId", "Discriminator")
        VALUES (NEW."DocumentId", NEW."LocalEducationAgencyId", 'Ed-Fi:LocalEducationAgency')
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
    WHERE "SourceEducationOrganizationId" = OLD."LocalEducationAgencyId" AND "TargetEducationOrganizationId" = OLD."LocalEducationAgencyId";
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
    VALUES (NEW."LocalEducationAgencyId", NEW."LocalEducationAgencyId");
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_LocalEducationAgency_AuthHierarchy_Insert" ON "edfi"."LocalEducationAgency";
CREATE TRIGGER "TR_LocalEducationAgency_AuthHierarchy_Insert"
    AFTER INSERT ON "edfi"."LocalEducationAgency"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AuthHierarchy_Insert"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."LocalEducationAgencyId" IS DISTINCT FROM NEW."LocalEducationAgencyId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 2;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiLocalEducationAgency' || '$.localEducationAgencyId=' || NEW."LocalEducationAgencyId"::text), NEW."DocumentId", 2);
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiEducationOrganization' || '$.educationOrganizationId=' || NEW."LocalEducationAgencyId"::text), NEW."DocumentId", 1);
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
    IF TG_OP = 'UPDATE' AND (OLD."LocalEducationAgencyId" IS DISTINCT FROM NEW."LocalEducationAgencyId") THEN
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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_AbstractIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        INSERT INTO "edfi"."EducationOrganizationIdentity" ("DocumentId", "EducationOrganizationId", "Discriminator")
        VALUES (NEW."DocumentId", NEW."SchoolId", 'Ed-Fi:School')
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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_AuthHierarchy_Delete"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
    WHERE "SourceEducationOrganizationId" = OLD."SchoolId" AND "TargetEducationOrganizationId" = OLD."SchoolId";
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_AuthHierarchy_Delete" ON "edfi"."School";
CREATE TRIGGER "TR_School_AuthHierarchy_Delete"
    AFTER DELETE ON "edfi"."School"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_TR_School_AuthHierarchy_Delete"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_AuthHierarchy_Insert"()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
    VALUES (NEW."SchoolId", NEW."SchoolId");
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_AuthHierarchy_Insert" ON "edfi"."School";
CREATE TRIGGER "TR_School_AuthHierarchy_Insert"
    AFTER INSERT ON "edfi"."School"
    FOR EACH ROW
    EXECUTE FUNCTION "edfi"."TF_TR_School_AuthHierarchy_Insert"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 3;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$.schoolId=' || NEW."SchoolId"::text), NEW."DocumentId", 3);
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiEducationOrganization' || '$.educationOrganizationId=' || NEW."SchoolId"::text), NEW."DocumentId", 1);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_ReferentialIdentity" ON "edfi"."School";
CREATE TRIGGER "TR_School_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
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
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_Stamp" ON "edfi"."School";
CREATE TRIGGER "TR_School_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

-- ==========================================================
-- Phase 7: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- ResourceKey seed inserts (insert-if-missing)
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (1, 'Ed-Fi', 'EducationOrganization', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (2, 'Ed-Fi', 'LocalEducationAgency', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (3, 'Ed-Fi', 'School', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;

-- ResourceKey full-table validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_ids text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."ResourceKey";
    IF _actual_count <> 3 THEN
        RAISE EXCEPTION 'dms.ResourceKey count mismatch: expected 3, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."ResourceKey" rk
    WHERE NOT EXISTS (
        SELECT 1 FROM (VALUES
            (1::smallint, 'Ed-Fi', 'EducationOrganization', '5.0.0'),
            (2::smallint, 'Ed-Fi', 'LocalEducationAgency', '5.0.0'),
            (3::smallint, 'Ed-Fi', 'School', '5.0.0')
        ) AS expected("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
        WHERE expected."ResourceKeyId" = rk."ResourceKeyId"
        AND expected."ProjectName" = rk."ProjectName"
        AND expected."ResourceName" = rk."ResourceName"
        AND expected."ResourceVersion" = rk."ResourceVersion"
    );
    IF _mismatched_count > 0 THEN
        SELECT string_agg(sub.id, ', ' ORDER BY sub.id_num) INTO _mismatched_ids
        FROM (
            SELECT rk."ResourceKeyId"::text AS id, rk."ResourceKeyId" AS id_num
            FROM "dms"."ResourceKey" rk
            WHERE NOT EXISTS (
                SELECT 1 FROM (VALUES
                    (1::smallint, 'Ed-Fi', 'EducationOrganization', '5.0.0'),
                    (2::smallint, 'Ed-Fi', 'LocalEducationAgency', '5.0.0'),
                    (3::smallint, 'Ed-Fi', 'School', '5.0.0')
                ) AS expected("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
                WHERE expected."ResourceKeyId" = rk."ResourceKeyId"
                AND expected."ProjectName" = rk."ProjectName"
                AND expected."ResourceName" = rk."ResourceName"
                AND expected."ResourceVersion" = rk."ResourceVersion"
            )
            ORDER BY rk."ResourceKeyId"
            LIMIT 10
        ) sub;
        RAISE EXCEPTION 'dms.ResourceKey contents mismatch: % unexpected or modified rows (ResourceKeyIds: %). Run ddl provision for detailed row-level diff.', _mismatched_count, _mismatched_ids;
    END IF;
END $$;

-- EffectiveSchema singleton insert-if-missing
INSERT INTO "dms"."EffectiveSchema" ("EffectiveSchemaSingletonId", "ApiSchemaFormatVersion", "EffectiveSchemaHash", "ResourceKeyCount", "ResourceKeySeedHash")
VALUES (1, '1.0.0', 'aed2277170a95207e1cf932e9e24744cd2b944999e8b42a1fa0c8678eda2ab50', 3, '\x13390DEE0A99E1FF56E7F39CB8F5B43BEDA834078EA87A4975FE84B5710F6252'::bytea)
ON CONFLICT ("EffectiveSchemaSingletonId") DO NOTHING;

-- EffectiveSchema validation (ApiSchemaFormatVersion + ResourceKeyCount + ResourceKeySeedHash)
DO $$
DECLARE
    _stored_api_schema_format_version text;
    _stored_count smallint;
    _stored_hash bytea;
BEGIN
    SELECT "ApiSchemaFormatVersion", "ResourceKeyCount", "ResourceKeySeedHash" INTO _stored_api_schema_format_version, _stored_count, _stored_hash
    FROM "dms"."EffectiveSchema"
    WHERE "EffectiveSchemaSingletonId" = 1;
    IF _stored_count IS NOT NULL THEN
        IF _stored_api_schema_format_version IS NULL OR btrim(_stored_api_schema_format_version) = '' THEN
            RAISE EXCEPTION 'dms.EffectiveSchema.ApiSchemaFormatVersion must not be empty.';
        END IF;
        IF _stored_count <> 3 THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeyCount mismatch: expected 3, found %', _stored_count;
        END IF;
        IF _stored_hash <> '\x13390DEE0A99E1FF56E7F39CB8F5B43BEDA834078EA87A4975FE84B5710F6252'::bytea THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored % but expected %', encode(_stored_hash, 'hex'), encode('\x13390DEE0A99E1FF56E7F39CB8F5B43BEDA834078EA87A4975FE84B5710F6252'::bytea, 'hex');
        END IF;
    END IF;
END $$;

-- SchemaComponent seed inserts (insert-if-missing)
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('aed2277170a95207e1cf932e9e24744cd2b944999e8b42a1fa0c8678eda2ab50', 'ed-fi', 'Ed-Fi', '5.0.0', false)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;

-- SchemaComponent exact-match validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_names text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."SchemaComponent" WHERE "EffectiveSchemaHash" = 'aed2277170a95207e1cf932e9e24744cd2b944999e8b42a1fa0c8678eda2ab50';
    IF _actual_count <> 1 THEN
        RAISE EXCEPTION 'dms.SchemaComponent count mismatch: expected 1, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."SchemaComponent" sc
    WHERE sc."EffectiveSchemaHash" = 'aed2277170a95207e1cf932e9e24744cd2b944999e8b42a1fa0c8678eda2ab50'
    AND NOT EXISTS (
        SELECT 1 FROM (VALUES
            ('ed-fi', 'Ed-Fi', '5.0.0', false)
        ) AS expected("ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
        WHERE expected."ProjectEndpointName" = sc."ProjectEndpointName"
        AND expected."ProjectName" = sc."ProjectName"
        AND expected."ProjectVersion" = sc."ProjectVersion"
        AND expected."IsExtensionProject" = sc."IsExtensionProject"
    );
    IF _mismatched_count > 0 THEN
        SELECT string_agg(sub.name, ', ' ORDER BY sub.name) INTO _mismatched_names
        FROM (
            SELECT sc."ProjectEndpointName" AS name
            FROM "dms"."SchemaComponent" sc
            WHERE sc."EffectiveSchemaHash" = 'aed2277170a95207e1cf932e9e24744cd2b944999e8b42a1fa0c8678eda2ab50'
            AND NOT EXISTS (
                SELECT 1 FROM (VALUES
                    ('ed-fi', 'Ed-Fi', '5.0.0', false)
                ) AS expected("ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
                WHERE expected."ProjectEndpointName" = sc."ProjectEndpointName"
                AND expected."ProjectName" = sc."ProjectName"
                AND expected."ProjectVersion" = sc."ProjectVersion"
                AND expected."IsExtensionProject" = sc."IsExtensionProject"
            )
            ORDER BY sc."ProjectEndpointName"
            LIMIT 10
        ) sub;
        RAISE EXCEPTION 'dms.SchemaComponent contents mismatch: % unexpected or modified rows (ProjectEndpointNames: %). Run ddl provision for detailed row-level diff.', _mismatched_count, _mismatched_names;
    END IF;
END $$;

