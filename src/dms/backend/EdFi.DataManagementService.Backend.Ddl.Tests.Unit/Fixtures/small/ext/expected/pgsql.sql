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
        IF _stored_hash IS NOT NULL AND _stored_hash <> '6761abd4b566a068ae28b56dba70794382a1d951b7f320c7683bd42285f4a7ba' THEN
            RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database has ''%'' but expected ''%''', _stored_hash, '6761abd4b566a068ae28b56dba70794382a1d951b7f320c7683bd42285f4a7ba';
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

CREATE OR REPLACE FUNCTION "dms"."GetMaxChangeVersion"() RETURNS bigint AS
$GetMaxChangeVersion$
DECLARE
    result bigint;
BEGIN
    SELECT last_value FROM "dms"."ChangeVersionSequence" INTO result;
    RETURN result;
END
$GetMaxChangeVersion$ LANGUAGE plpgsql;

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
    "ResourceKeyId" smallint NOT NULL,
    "Namespace" varchar(255) NOT NULL,
    "CodeValue" varchar(50) NOT NULL,
    "ShortDescription" varchar(75) NOT NULL,
    "Description" varchar(1024) NULL,
    "EffectiveBeginDate" date NULL,
    "EffectiveEndDate" date NULL,
    "Discriminator" varchar(128) NOT NULL,
    "Uri" varchar(306) NOT NULL,
    "ContentVersion" bigint NOT NULL DEFAULT 0,
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
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
    "CreatedByOwnershipTokenId" smallint NULL,
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
    "ContentVersion" bigint NOT NULL,
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
    CONSTRAINT "PK_ReferentialIdentity" PRIMARY KEY ("ReferentialId"),
    CONSTRAINT "UX_ReferentialIdentity_DocumentId_ResourceKeyId" UNIQUE ("DocumentId", "ResourceKeyId")
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
        WHERE conname = 'FK_Descriptor_ResourceKey'
        AND conrelid = to_regclass('"dms"."Descriptor"')
    )
    THEN
        ALTER TABLE "dms"."Descriptor"
        ADD CONSTRAINT "FK_Descriptor_ResourceKey"
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

CREATE INDEX IF NOT EXISTS "IX_Descriptor_ResourceKeyId_DocumentId" ON "dms"."Descriptor" ("ResourceKeyId", "DocumentId");

CREATE INDEX IF NOT EXISTS "IX_Document_CreatedByOwnershipTokenId" ON "dms"."Document" ("CreatedByOwnershipTokenId");

CREATE INDEX IF NOT EXISTS "IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt" ON "dms"."DocumentCache" ("ProjectName", "ResourceName", "LastModifiedAt", "DocumentId");

-- ==========================================================
-- Phase 8: Triggers
-- ==========================================================

CREATE OR REPLACE FUNCTION "dms"."TF_Descriptor_Stamp_Document"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NOT (OLD."Namespace" IS DISTINCT FROM NEW."Namespace" OR OLD."CodeValue" IS DISTINCT FROM NEW."CodeValue" OR OLD."ShortDescription" IS DISTINCT FROM NEW."ShortDescription" OR OLD."Description" IS DISTINCT FROM NEW."Description" OR OLD."EffectiveBeginDate" IS DISTINCT FROM NEW."EffectiveBeginDate" OR OLD."EffectiveEndDate" IS DISTINCT FROM NEW."EffectiveEndDate" OR OLD."Discriminator" IS DISTINCT FROM NEW."Discriminator" OR OLD."Uri" IS DISTINCT FROM NEW."Uri") THEN
            RETURN NEW;
        END IF;
    END IF;
    IF TG_OP = 'INSERT' THEN
        WITH stamped AS (
            SELECT "DocumentId", "ContentVersion", "ContentLastModifiedAt"
            FROM "dms"."Document"
            WHERE "DocumentId" = NEW."DocumentId"
        )
        UPDATE "dms"."Descriptor" r
        SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
        FROM stamped
        WHERE r."DocumentId" = stamped."DocumentId";
    ELSIF TG_OP = 'UPDATE' THEN
        WITH stamped AS (
            UPDATE "dms"."Document"
            SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
            WHERE "DocumentId" = NEW."DocumentId"
            RETURNING "DocumentId", "ContentVersion", "ContentLastModifiedAt"
        )
        UPDATE "dms"."Descriptor" r
        SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
        FROM stamped
        WHERE r."DocumentId" = stamped."DocumentId";
    ELSIF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Descriptor_Stamp_Document" ON "dms"."Descriptor";
CREATE TRIGGER "TR_Descriptor_Stamp_Document"
    AFTER INSERT OR UPDATE OR DELETE ON "dms"."Descriptor"
    FOR EACH ROW
    EXECUTE FUNCTION "dms"."TF_Descriptor_Stamp_Document"();

CREATE SCHEMA IF NOT EXISTS "edfi";
CREATE SCHEMA IF NOT EXISTS "sample";
CREATE SCHEMA IF NOT EXISTS "tracked_changes_edfi";

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "ContentVersion" bigint NOT NULL DEFAULT 0,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_School_NK" UNIQUE ("SchoolId")
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolExtension"
(
    "DocumentId" bigint NOT NULL,
    "ExtensionData" varchar(200) NULL,
    CONSTRAINT "PK_SchoolExtension" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolExtensionAddress"
(
    "BaseCollectionItemId" bigint NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "AddressExtData" varchar(100) NULL,
    CONSTRAINT "PK_SchoolExtensionAddress" PRIMARY KEY ("BaseCollectionItemId")
);

CREATE TABLE IF NOT EXISTS "edfi"."SchoolAddress"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "Ordinal" integer NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "Street" varchar(100) NOT NULL,
    CONSTRAINT "PK_SchoolAddress" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_SchoolAddress_CollectionItemId_School_DocumentId" UNIQUE ("CollectionItemId", "School_DocumentId"),
    CONSTRAINT "UX_SchoolAddress_Ordinal_School_DocumentId" UNIQUE ("School_DocumentId", "Ordinal")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."School"
(
    "OldSchoolId" integer NOT NULL,
    "NewSchoolId" integer NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_School" PRIMARY KEY ("ChangeVersion")
);

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
        WHERE conname = 'FK_SchoolExtension_School'
        AND conrelid = to_regclass('"sample"."SchoolExtension"')
    )
    THEN
        ALTER TABLE "sample"."SchoolExtension"
        ADD CONSTRAINT "FK_SchoolExtension_School"
        FOREIGN KEY ("DocumentId")
        REFERENCES "edfi"."School" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolExtensionAddress_SchoolAddress'
        AND conrelid = to_regclass('"sample"."SchoolExtensionAddress"')
    )
    THEN
        ALTER TABLE "sample"."SchoolExtensionAddress"
        ADD CONSTRAINT "FK_SchoolExtensionAddress_SchoolAddress"
        FOREIGN KEY ("BaseCollectionItemId", "School_DocumentId")
        REFERENCES "edfi"."SchoolAddress" ("CollectionItemId", "School_DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolAddress_School'
        AND conrelid = to_regclass('"edfi"."SchoolAddress"')
    )
    THEN
        ALTER TABLE "edfi"."SchoolAddress"
        ADD CONSTRAINT "FK_SchoolAddress_School"
        FOREIGN KEY ("School_DocumentId")
        REFERENCES "edfi"."School" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_School_ContentVersion" ON "edfi"."School" ("ContentVersion");

CREATE INDEX IF NOT EXISTS "IX_SchoolExtensionAddress_BaseCollectionItemId_Schoo_8db7cde2b1" ON "sample"."SchoolExtensionAddress" ("BaseCollectionItemId", "School_DocumentId");

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$.schoolId=' || NEW."SchoolId"::text), NEW."DocumentId", 1);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_ReferentialIdentity" ON "edfi"."School";
CREATE TRIGGER "TR_School_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
    _stampedContentLastModifiedAt timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        INSERT INTO "tracked_changes_edfi"."School" (
            "OldSchoolId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."SchoolId",
            doc."DocumentUuid",
            doc."ContentVersion"
        FROM "dms"."Document" doc
        WHERE doc."DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN
        SELECT "ContentVersion", "ContentLastModifiedAt"
        INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt
        FROM "dms"."Document"
        WHERE "DocumentId" = NEW."DocumentId";
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := _stampedContentLastModifiedAt;
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
        WHERE "DocumentId" = NEW."DocumentId";
        INSERT INTO "tracked_changes_edfi"."School" (
            "OldSchoolId",
            "NewSchoolId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."SchoolId",
            NEW."SchoolId",
            doc."DocumentUuid",
            _stampedContentVersion
        FROM "dms"."Document" doc
        WHERE doc."DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_Stamp" ON "edfi"."School";
CREATE TRIGGER "TR_School_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_ins"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT newtab."School_DocumentId" AS "DocumentId"
        FROM newtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_upd"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT n."School_DocumentId" AS "DocumentId"
        FROM newtab n
        LEFT JOIN oldtab o ON o."CollectionItemId" = n."CollectionItemId"
        WHERE o."CollectionItemId" IS NULL OR n."CollectionItemId" IS DISTINCT FROM o."CollectionItemId" OR n."Ordinal" IS DISTINCT FROM o."Ordinal" OR n."School_DocumentId" IS DISTINCT FROM o."School_DocumentId" OR n."Street" IS DISTINCT FROM o."Street"
        UNION
        SELECT o."School_DocumentId" AS "DocumentId"
        FROM oldtab o
        LEFT JOIN newtab n ON n."CollectionItemId" = o."CollectionItemId"
        WHERE n."CollectionItemId" IS NULL OR n."CollectionItemId" IS DISTINCT FROM o."CollectionItemId" OR n."Ordinal" IS DISTINCT FROM o."Ordinal" OR n."School_DocumentId" IS DISTINCT FROM o."School_DocumentId" OR n."Street" IS DISTINCT FROM o."Street"
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_del"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT oldtab."School_DocumentId" AS "DocumentId"
        FROM oldtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp" ON "edfi"."SchoolAddress";
DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp_ins" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp_ins"
AFTER INSERT ON "edfi"."SchoolAddress"
REFERENCING NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_ins"();

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp_upd" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp_upd"
AFTER UPDATE ON "edfi"."SchoolAddress"
REFERENCING OLD TABLE AS oldtab NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_upd"();

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp_del" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp_del"
AFTER DELETE ON "edfi"."SchoolAddress"
REFERENCING OLD TABLE AS oldtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_del"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp_ins"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT newtab."DocumentId" AS "DocumentId"
        FROM newtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp_upd"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT n."DocumentId" AS "DocumentId"
        FROM newtab n
        LEFT JOIN oldtab o ON o."DocumentId" = n."DocumentId"
        WHERE o."DocumentId" IS NULL OR n."DocumentId" IS DISTINCT FROM o."DocumentId" OR n."ExtensionData" IS DISTINCT FROM o."ExtensionData"
        UNION
        SELECT o."DocumentId" AS "DocumentId"
        FROM oldtab o
        LEFT JOIN newtab n ON n."DocumentId" = o."DocumentId"
        WHERE n."DocumentId" IS NULL OR n."DocumentId" IS DISTINCT FROM o."DocumentId" OR n."ExtensionData" IS DISTINCT FROM o."ExtensionData"
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp_del"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT oldtab."DocumentId" AS "DocumentId"
        FROM oldtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolExtension_Stamp" ON "sample"."SchoolExtension";
DROP TRIGGER IF EXISTS "TR_SchoolExtension_Stamp_ins" ON "sample"."SchoolExtension";
CREATE TRIGGER "TR_SchoolExtension_Stamp_ins"
AFTER INSERT ON "sample"."SchoolExtension"
REFERENCING NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp_ins"();

DROP TRIGGER IF EXISTS "TR_SchoolExtension_Stamp_upd" ON "sample"."SchoolExtension";
CREATE TRIGGER "TR_SchoolExtension_Stamp_upd"
AFTER UPDATE ON "sample"."SchoolExtension"
REFERENCING OLD TABLE AS oldtab NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp_upd"();

DROP TRIGGER IF EXISTS "TR_SchoolExtension_Stamp_del" ON "sample"."SchoolExtension";
CREATE TRIGGER "TR_SchoolExtension_Stamp_del"
AFTER DELETE ON "sample"."SchoolExtension"
REFERENCING OLD TABLE AS oldtab
FOR EACH STATEMENT
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp_del"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtensionAddress_Stamp_ins"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT newtab."School_DocumentId" AS "DocumentId"
        FROM newtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtensionAddress_Stamp_upd"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT n."School_DocumentId" AS "DocumentId"
        FROM newtab n
        LEFT JOIN oldtab o ON o."BaseCollectionItemId" = n."BaseCollectionItemId"
        WHERE o."BaseCollectionItemId" IS NULL OR n."BaseCollectionItemId" IS DISTINCT FROM o."BaseCollectionItemId" OR n."School_DocumentId" IS DISTINCT FROM o."School_DocumentId" OR n."AddressExtData" IS DISTINCT FROM o."AddressExtData"
        UNION
        SELECT o."School_DocumentId" AS "DocumentId"
        FROM oldtab o
        LEFT JOIN newtab n ON n."BaseCollectionItemId" = o."BaseCollectionItemId"
        WHERE n."BaseCollectionItemId" IS NULL OR n."BaseCollectionItemId" IS DISTINCT FROM o."BaseCollectionItemId" OR n."School_DocumentId" IS DISTINCT FROM o."School_DocumentId" OR n."AddressExtData" IS DISTINCT FROM o."AddressExtData"
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtensionAddress_Stamp_del"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT oldtab."School_DocumentId" AS "DocumentId"
        FROM oldtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolExtensionAddress_Stamp" ON "sample"."SchoolExtensionAddress";
DROP TRIGGER IF EXISTS "TR_SchoolExtensionAddress_Stamp_ins" ON "sample"."SchoolExtensionAddress";
CREATE TRIGGER "TR_SchoolExtensionAddress_Stamp_ins"
AFTER INSERT ON "sample"."SchoolExtensionAddress"
REFERENCING NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtensionAddress_Stamp_ins"();

DROP TRIGGER IF EXISTS "TR_SchoolExtensionAddress_Stamp_upd" ON "sample"."SchoolExtensionAddress";
CREATE TRIGGER "TR_SchoolExtensionAddress_Stamp_upd"
AFTER UPDATE ON "sample"."SchoolExtensionAddress"
REFERENCING OLD TABLE AS oldtab NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtensionAddress_Stamp_upd"();

DROP TRIGGER IF EXISTS "TR_SchoolExtensionAddress_Stamp_del" ON "sample"."SchoolExtensionAddress";
CREATE TRIGGER "TR_SchoolExtensionAddress_Stamp_del"
AFTER DELETE ON "sample"."SchoolExtensionAddress"
REFERENCING OLD TABLE AS oldtab
FOR EACH STATEMENT
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtensionAddress_Stamp_del"();

-- ==========================================================
-- Phase 7: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- ResourceKey seed inserts (insert-if-missing)
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (1, 'Ed-Fi', 'School', '1.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;

-- ResourceKey full-table validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_ids text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."ResourceKey";
    IF _actual_count <> 1 THEN
        RAISE EXCEPTION 'dms.ResourceKey count mismatch: expected 1, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."ResourceKey" rk
    WHERE NOT EXISTS (
        SELECT 1 FROM (VALUES
            (1::smallint, 'Ed-Fi', 'School', '1.0.0')
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
                    (1::smallint, 'Ed-Fi', 'School', '1.0.0')
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
VALUES (1, '1.0.0', '6761abd4b566a068ae28b56dba70794382a1d951b7f320c7683bd42285f4a7ba', 1, '\x732A553A326B7F67D4706E056DDE684CAF2DFFDF8EFFE0DB2FE2420AA3CFA168'::bytea)
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
        IF _stored_count <> 1 THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeyCount mismatch: expected 1, found %', _stored_count;
        END IF;
        IF _stored_hash <> '\x732A553A326B7F67D4706E056DDE684CAF2DFFDF8EFFE0DB2FE2420AA3CFA168'::bytea THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored % but expected %', encode(_stored_hash, 'hex'), encode('\x732A553A326B7F67D4706E056DDE684CAF2DFFDF8EFFE0DB2FE2420AA3CFA168'::bytea, 'hex');
        END IF;
    END IF;
END $$;

-- SchemaComponent seed inserts (insert-if-missing)
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('6761abd4b566a068ae28b56dba70794382a1d951b7f320c7683bd42285f4a7ba', 'ed-fi', 'Ed-Fi', '1.0.0', false)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('6761abd4b566a068ae28b56dba70794382a1d951b7f320c7683bd42285f4a7ba', 'sample', 'Sample', '1.0.0', true)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;

-- SchemaComponent exact-match validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_names text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."SchemaComponent" WHERE "EffectiveSchemaHash" = '6761abd4b566a068ae28b56dba70794382a1d951b7f320c7683bd42285f4a7ba';
    IF _actual_count <> 2 THEN
        RAISE EXCEPTION 'dms.SchemaComponent count mismatch: expected 2, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."SchemaComponent" sc
    WHERE sc."EffectiveSchemaHash" = '6761abd4b566a068ae28b56dba70794382a1d951b7f320c7683bd42285f4a7ba'
    AND NOT EXISTS (
        SELECT 1 FROM (VALUES
            ('ed-fi', 'Ed-Fi', '1.0.0', false),
            ('sample', 'Sample', '1.0.0', true)
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
            WHERE sc."EffectiveSchemaHash" = '6761abd4b566a068ae28b56dba70794382a1d951b7f320c7683bd42285f4a7ba'
            AND NOT EXISTS (
                SELECT 1 FROM (VALUES
                    ('ed-fi', 'Ed-Fi', '1.0.0', false),
                    ('sample', 'Sample', '1.0.0', true)
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
