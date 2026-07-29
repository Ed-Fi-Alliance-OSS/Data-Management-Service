-- ==========================================================
-- Phase 0: Bounded Provisioning Guards
-- ==========================================================

-- Preflight: validate EffectiveSchema hash compatibility
DO $$
DECLARE
    _stored_hash text;
BEGIN
    IF to_regclass('"dms"."EffectiveSchema"') IS NOT NULL THEN
        SELECT "EffectiveSchemaHash" INTO _stored_hash FROM "dms"."EffectiveSchema"
        WHERE "EffectiveSchemaSingletonId" = 1;
        IF _stored_hash IS NOT NULL AND _stored_hash <> 'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0' THEN
            RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database has ''%'' but expected ''%''', _stored_hash, 'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0';
        END IF;
    END IF;
END $$;

-- Preflight: protect completed DocumentCache mutable singleton state before mutation
DO $$
DECLARE
    _stored_hash text;
    _source_identity uuid;
    _lifecycle_state text;
    _cache_ahead_recovery_required boolean;
BEGIN
    IF to_regclass('"dms"."EffectiveSchema"') IS NOT NULL THEN
        SELECT "EffectiveSchemaHash" INTO _stored_hash FROM "dms"."EffectiveSchema"
        WHERE "EffectiveSchemaSingletonId" = 1;
        IF _stored_hash = 'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0' THEN
            IF to_regclass('"dms"."DataStoreIdentity"') IS NULL THEN
                RAISE EXCEPTION 'Completed dms.EffectiveSchema hash matches this DDL, but dms.DataStoreIdentity is missing. Drop and recreate the database before re-provisioning.';
            END IF;

            SELECT "SourceIdentity" INTO _source_identity FROM "dms"."DataStoreIdentity"
            WHERE "DataStoreIdentitySingletonId" = 1;
            IF _source_identity IS NULL THEN
                RAISE EXCEPTION 'Completed dms.EffectiveSchema hash matches this DDL, but dms.DataStoreIdentity singleton row is missing. Drop and recreate the database before re-provisioning.';
            END IF;
            IF _source_identity = '00000000-0000-0000-0000-000000000000'::uuid THEN
                RAISE EXCEPTION 'dms.DataStoreIdentity.SourceIdentity must not be the zero UUID. Drop and recreate the database before re-provisioning.';
            END IF;

            IF to_regclass('"dms"."DocumentCacheState"') IS NULL THEN
                RAISE EXCEPTION 'Completed dms.EffectiveSchema hash matches this DDL, but dms.DocumentCacheState is missing. Drop and recreate the database before re-provisioning.';
            END IF;

            SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired" INTO _lifecycle_state, _cache_ahead_recovery_required
            FROM "dms"."DocumentCacheState"
            WHERE "StateId" = 1;
            IF _lifecycle_state IS NULL THEN
                RAISE EXCEPTION 'Completed dms.EffectiveSchema hash matches this DDL, but dms.DocumentCacheState singleton row is missing. Drop and recreate the database before re-provisioning.';
            END IF;
            IF _lifecycle_state NOT IN ('Disabled', 'Resetting', 'Rebuilding', 'Tracking') THEN
                RAISE EXCEPTION 'dms.DocumentCacheState.ProjectionLifecycleState has unsupported value % during provisioning preflight.', _lifecycle_state;
            END IF;
            IF _cache_ahead_recovery_required IS NULL THEN
                RAISE EXCEPTION 'dms.DocumentCacheState.CacheAheadRecoveryRequired must not be null during provisioning preflight.';
            END IF;
        END IF;
    END IF;
END $$;

-- Preflight: reject known legacy DocumentCache artifacts before mutation
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dms'
        AND table_name = 'DocumentCache'
        AND column_name = 'Etag'
    ) THEN
        RAISE EXCEPTION 'Known legacy artifact dms.DocumentCache.Etag was found. Drop and recreate the database before provisioning E18 DocumentCache schema.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_constraint constraint_info
        WHERE constraint_info.conname = 'UX_DocumentCache_DocumentUuid'
        AND constraint_info.conrelid = to_regclass('"dms"."DocumentCache"')
    ) OR to_regclass('"dms"."UX_DocumentCache_DocumentUuid"') IS NOT NULL THEN
        RAISE EXCEPTION 'Known legacy artifact UX_DocumentCache_DocumentUuid was found. Drop and recreate the database before provisioning E18 DocumentCache schema.';
    END IF;

    IF to_regclass('"dms"."IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt"') IS NOT NULL THEN
        RAISE EXCEPTION 'Known legacy artifact IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt was found. Drop and recreate the database before provisioning E18 DocumentCache schema.';
    END IF;
END $$;

-- Preflight: validate PostgreSQL enqueue-owner prerequisites
DO $$
DECLARE
    _owner_role oid := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
    _session_role oid;
    _session_is_superuser boolean;
    _session_can_create_role boolean;
    _has_required_direct_membership boolean;
BEGIN
    SELECT oid, rolsuper, rolcreaterole
    INTO _session_role, _session_is_superuser, _session_can_create_role
    FROM pg_catalog.pg_roles
    WHERE rolname = SESSION_USER;

    IF _owner_role IS NULL THEN
        IF NOT COALESCE(_session_is_superuser OR _session_can_create_role, false) THEN
            RAISE EXCEPTION 'PostgreSQL provisioning principal must be SUPERUSER or CREATEROLE to create edfi_dms_enqueue_owner before provisioning.';
        END IF;
        RETURN;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_roles owner_role
        WHERE owner_role.oid = _owner_role
        AND (owner_role.rolcanlogin OR owner_role.rolinherit OR owner_role.rolsuper OR owner_role.rolcreatedb OR owner_role.rolcreaterole OR owner_role.rolreplication OR owner_role.rolbypassrls)
    ) THEN
        RAISE EXCEPTION 'PostgreSQL role edfi_dms_enqueue_owner exists but is not locked down as NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS. Drop or repair the role before provisioning.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members membership
        WHERE membership.member = _owner_role
        AND (membership.admin_option OR membership.inherit_option OR membership.set_option)
    ) THEN
        RAISE EXCEPTION 'PostgreSQL role edfi_dms_enqueue_owner must not hold outgoing privilege-bearing memberships before provisioning.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members membership
        WHERE membership.roleid = _owner_role
        AND membership.member = _session_role
        AND NOT (membership.admin_option AND NOT membership.inherit_option AND NOT membership.set_option AND COALESCE(_session_can_create_role, false))
        AND (membership.admin_option OR membership.inherit_option OR NOT membership.set_option)
    ) THEN
        RAISE EXCEPTION 'PostgreSQL provisioning principal has an unsafe direct membership in edfi_dms_enqueue_owner; required options are SET TRUE, INHERIT FALSE, ADMIN FALSE.';
    END IF;

    _has_required_direct_membership := EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members membership
        WHERE membership.roleid = _owner_role
        AND membership.member = _session_role
        AND NOT membership.admin_option
        AND NOT membership.inherit_option
        AND membership.set_option
    );

    IF NOT COALESCE(_session_is_superuser, false)
    AND NOT _has_required_direct_membership THEN
        RAISE EXCEPTION 'PostgreSQL provisioning principal must have direct SET TRUE, INHERIT FALSE, ADMIN FALSE membership in existing edfi_dms_enqueue_owner before provisioning.';
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

CREATE TABLE IF NOT EXISTS "dms"."DataStoreIdentity"
(
    "DataStoreIdentitySingletonId" smallint NOT NULL,
    "SourceIdentity" uuid NOT NULL,
    CONSTRAINT "PK_DataStoreIdentity" PRIMARY KEY ("DataStoreIdentitySingletonId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_DataStoreIdentity_Singleton'
        AND conrelid = to_regclass('"dms"."DataStoreIdentity"')
    )
    THEN
        ALTER TABLE "dms"."DataStoreIdentity"
        ADD CONSTRAINT "CK_DataStoreIdentity_Singleton" CHECK ("DataStoreIdentitySingletonId" = 1);
    END IF;
END $$;

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
    "ContentVersion" bigint NOT NULL,
    "StreamEtag" varchar(64) NOT NULL,
    "LastModifiedAt" timestamp with time zone NOT NULL,
    "DocumentJson" jsonb NOT NULL,
    "ComputedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_DocumentCache" PRIMARY KEY ("DocumentId")
);

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

CREATE TABLE IF NOT EXISTS "dms"."DocumentCacheState"
(
    "StateId" smallint NOT NULL,
    "ProjectionLifecycleState" varchar(16) NOT NULL,
    "CacheAheadRecoveryRequired" boolean NOT NULL,
    CONSTRAINT "PK_DocumentCacheState" PRIMARY KEY ("StateId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_DocumentCacheState_Singleton'
        AND conrelid = to_regclass('"dms"."DocumentCacheState"')
    )
    THEN
        ALTER TABLE "dms"."DocumentCacheState"
        ADD CONSTRAINT "CK_DocumentCacheState_Singleton" CHECK ("StateId" = 1);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_DocumentCacheState_Lifecycle'
        AND conrelid = to_regclass('"dms"."DocumentCacheState"')
    )
    THEN
        ALTER TABLE "dms"."DocumentCacheState"
        ADD CONSTRAINT "CK_DocumentCacheState_Lifecycle" CHECK ("ProjectionLifecycleState" IN ('Disabled', 'Resetting', 'Rebuilding', 'Tracking'));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS "dms"."DocumentProjectionWork"
(
    "DocumentId" bigint NOT NULL,
    "RequiredContentVersion" bigint NOT NULL,
    "FirstEnqueuedAt" timestamp with time zone NOT NULL,
    "LastEnqueuedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_DocumentProjectionWork" PRIMARY KEY ("DocumentId")
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
        WHERE conname = 'FK_DocumentProjectionWork_Document'
        AND conrelid = to_regclass('"dms"."DocumentProjectionWork"')
    )
    THEN
        ALTER TABLE "dms"."DocumentProjectionWork"
        ADD CONSTRAINT "FK_DocumentProjectionWork_Document"
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

CREATE INDEX IF NOT EXISTS "IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId" ON "dms"."DocumentProjectionWork" ("FirstEnqueuedAt", "DocumentId");

-- ==========================================================
-- Phase 8: Triggers
-- ==========================================================

CREATE OR REPLACE FUNCTION "dms"."TF_Descriptor_Stamp_Document"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP IN ('INSERT', 'UPDATE') THEN
        IF NOT EXISTS (
            SELECT 1
            FROM "dms"."Document"
            WHERE "DocumentId" = NEW."DocumentId"
                AND "ResourceKeyId" = NEW."ResourceKeyId"
        ) THEN
            RAISE EXCEPTION 'dms.Descriptor.ResourceKeyId % diverges from the owning dms.Document row for DocumentId %', NEW."ResourceKeyId", NEW."DocumentId";
        END IF;
    END IF;
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
        INSERT INTO "tracked_changes_edfi"."Descriptor" (
            "Discriminator",
            "OldNamespace",
            "OldCodeValue",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."Discriminator",
            OLD."Namespace",
            OLD."CodeValue",
            doc."DocumentUuid",
            doc."ContentVersion"
        FROM "dms"."Document" doc
        WHERE doc."DocumentId" = OLD."DocumentId";
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

DO $$
DECLARE
    _owner_role oid := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
    _session_role oid;
BEGIN
    SELECT oid INTO _session_role
    FROM pg_catalog.pg_roles
    WHERE rolname = SESSION_USER;

    IF _owner_role IS NOT NULL AND EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members membership
        WHERE membership.roleid = _owner_role
        AND membership.member = _session_role
        AND NOT membership.admin_option
        AND NOT membership.inherit_option
        AND membership.set_option
    ) THEN
        EXECUTE 'GRANT USAGE ON SCHEMA "dms" TO "edfi_dms_enqueue_owner"';
        EXECUTE 'GRANT CREATE ON SCHEMA "dms" TO "edfi_dms_enqueue_owner"';
        EXECUTE 'SET ROLE "edfi_dms_enqueue_owner"';
    END IF;
END $$;

CREATE OR REPLACE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $func$
DECLARE
    _lifecycle_state text;
    _enqueued_at timestamp with time zone;
BEGIN
    SELECT "ProjectionLifecycleState" INTO _lifecycle_state
    FROM "dms"."DocumentCacheState"
    WHERE "StateId" = 1;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'dms.DocumentCacheState singleton row is missing or unreadable for projection enqueue.';
    END IF;

    IF _lifecycle_state NOT IN ('Disabled', 'Resetting', 'Rebuilding', 'Tracking') THEN
        RAISE EXCEPTION 'dms.DocumentCacheState.ProjectionLifecycleState has unsupported value % for projection enqueue.', _lifecycle_state;
    END IF;

    IF _lifecycle_state = 'Disabled' THEN
        RETURN NULL;
    END IF;

    _enqueued_at := statement_timestamp();

    INSERT INTO "dms"."DocumentProjectionWork" AS work (
        "DocumentId",
        "RequiredContentVersion",
        "FirstEnqueuedAt",
        "LastEnqueuedAt"
    )
    SELECT "DocumentId", "ContentVersion", _enqueued_at, _enqueued_at
    FROM new_rows
    ON CONFLICT ("DocumentId") DO UPDATE
    SET "RequiredContentVersion" = EXCLUDED."RequiredContentVersion",
        "LastEnqueuedAt" = EXCLUDED."LastEnqueuedAt"
    WHERE work."RequiredContentVersion" < EXCLUDED."RequiredContentVersion";

    RETURN NULL;
END;
$func$;

CREATE OR REPLACE FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $func$
DECLARE
    _lifecycle_state text;
    _enqueued_at timestamp with time zone;
BEGIN
    SELECT "ProjectionLifecycleState" INTO _lifecycle_state
    FROM "dms"."DocumentCacheState"
    WHERE "StateId" = 1;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'dms.DocumentCacheState singleton row is missing or unreadable for projection enqueue.';
    END IF;

    IF _lifecycle_state NOT IN ('Disabled', 'Resetting', 'Rebuilding', 'Tracking') THEN
        RAISE EXCEPTION 'dms.DocumentCacheState.ProjectionLifecycleState has unsupported value % for projection enqueue.', _lifecycle_state;
    END IF;

    IF _lifecycle_state = 'Disabled' THEN
        RETURN NULL;
    END IF;

    _enqueued_at := statement_timestamp();

    INSERT INTO "dms"."DocumentProjectionWork" AS work (
        "DocumentId",
        "RequiredContentVersion",
        "FirstEnqueuedAt",
        "LastEnqueuedAt"
    )
    SELECT n."DocumentId", n."ContentVersion", _enqueued_at, _enqueued_at
    FROM new_rows n
    INNER JOIN old_rows o ON o."DocumentId" = n."DocumentId"
    WHERE n."ContentVersion" <> o."ContentVersion"
    ON CONFLICT ("DocumentId") DO UPDATE
    SET "RequiredContentVersion" = EXCLUDED."RequiredContentVersion",
        "LastEnqueuedAt" = EXCLUDED."LastEnqueuedAt"
    WHERE work."RequiredContentVersion" < EXCLUDED."RequiredContentVersion";

    RETURN NULL;
END;
$func$;

GRANT EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() TO SESSION_USER;
GRANT EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() TO SESSION_USER;
RESET ROLE;

DO $$
BEGIN
    IF pg_catalog.to_regrole('edfi_dms_enqueue_owner') IS NOT NULL THEN
        EXECUTE 'REVOKE CREATE ON SCHEMA "dms" FROM "edfi_dms_enqueue_owner"';
    END IF;
END $$;

DROP TRIGGER IF EXISTS "TR_Document_EnqueueProjectionInsert" ON "dms"."Document";
CREATE TRIGGER "TR_Document_EnqueueProjectionInsert"
    AFTER INSERT ON "dms"."Document"
    REFERENCING NEW TABLE AS new_rows
    FOR EACH STATEMENT
    EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"();

DROP TRIGGER IF EXISTS "TR_Document_EnqueueProjectionUpdate" ON "dms"."Document";
CREATE TRIGGER "TR_Document_EnqueueProjectionUpdate"
    AFTER UPDATE ON "dms"."Document"
    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
    FOR EACH STATEMENT
    EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"();

CREATE OR REPLACE FUNCTION "dms"."TF_DocumentCache_ValidateDocumentUuid"()
RETURNS TRIGGER AS $func$
DECLARE
    _canonical_document_uuid uuid;
BEGIN
    SELECT "DocumentUuid" INTO _canonical_document_uuid
    FROM "dms"."Document"
    WHERE "DocumentId" = NEW."DocumentId";

    IF _canonical_document_uuid IS NOT NULL AND NEW."DocumentUuid" <> _canonical_document_uuid THEN
        RAISE EXCEPTION 'dms.DocumentCache.DocumentUuid diverges from the owning dms.Document row for DocumentId %', NEW."DocumentId";
    END IF;

    RETURN NEW;
END;
$func$ LANGUAGE plpgsql SECURITY INVOKER;

DROP TRIGGER IF EXISTS "TR_DocumentCache_ValidateDocumentUuid" ON "dms"."DocumentCache";
CREATE TRIGGER "TR_DocumentCache_ValidateDocumentUuid"
    BEFORE INSERT OR UPDATE ON "dms"."DocumentCache"
    FOR EACH ROW
    EXECUTE FUNCTION "dms"."TF_DocumentCache_ValidateDocumentUuid"();

-- ==========================================================
-- Phase 9: Security and Grants
-- ==========================================================

DO $$
DECLARE
    _owner_role oid := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
    _session_role oid;
    _session_is_superuser boolean;
    _session_can_create_role boolean;
    _created_owner_role boolean := false;
BEGIN
    SELECT oid, rolsuper, rolcreaterole
    INTO _session_role, _session_is_superuser, _session_can_create_role
    FROM pg_catalog.pg_roles
    WHERE rolname = SESSION_USER;

    IF _owner_role IS NULL THEN
        IF NOT COALESCE(_session_is_superuser OR _session_can_create_role, false) THEN
            RAISE EXCEPTION 'PostgreSQL provisioning principal must be SUPERUSER or CREATEROLE to create edfi_dms_enqueue_owner before provisioning.';
        END IF;
        CREATE ROLE "edfi_dms_enqueue_owner" WITH NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
        _owner_role := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
        _created_owner_role := true;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles owner_role WHERE owner_role.oid = _owner_role
    AND (owner_role.rolcanlogin OR owner_role.rolinherit OR owner_role.rolsuper OR owner_role.rolcreatedb OR owner_role.rolcreaterole OR owner_role.rolreplication OR owner_role.rolbypassrls)) THEN
        RAISE EXCEPTION 'PostgreSQL role edfi_dms_enqueue_owner exists but is not locked down as NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS. Drop or repair the role before provisioning.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members membership
        WHERE membership.member = _owner_role
        AND (membership.admin_option OR membership.inherit_option OR membership.set_option)
    ) THEN
        RAISE EXCEPTION 'PostgreSQL role edfi_dms_enqueue_owner must not hold outgoing privilege-bearing memberships.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members membership
        WHERE membership.roleid = _owner_role
        AND membership.member = _session_role
        AND NOT (membership.admin_option AND NOT membership.inherit_option AND NOT membership.set_option AND COALESCE(_session_can_create_role, false))
        AND (membership.admin_option OR membership.inherit_option OR NOT membership.set_option)
    ) THEN
        RAISE EXCEPTION 'PostgreSQL provisioning principal has an unsafe direct membership in edfi_dms_enqueue_owner; required options are SET TRUE, INHERIT FALSE, ADMIN FALSE.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members membership
        WHERE membership.roleid = _owner_role
        AND membership.member = _session_role
        AND NOT membership.admin_option
        AND NOT membership.inherit_option
        AND membership.set_option
    ) THEN
        IF NOT COALESCE(_session_is_superuser OR (_created_owner_role AND _session_can_create_role), false) THEN
            RAISE EXCEPTION 'PostgreSQL provisioning principal must have direct SET TRUE, INHERIT FALSE, ADMIN FALSE membership in existing edfi_dms_enqueue_owner before provisioning.';
        END IF;
        GRANT "edfi_dms_enqueue_owner" TO SESSION_USER WITH SET TRUE, INHERIT FALSE, ADMIN FALSE;
    END IF;
END $$;

DO $$
DECLARE
    _owner_role oid := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
BEGIN
    IF _owner_role IS NOT NULL AND EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc p
        INNER JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'dms'
        AND p.proname IN ('TF_Document_EnqueueProjectionInsert', 'TF_Document_EnqueueProjectionUpdate')
        AND p.proowner <> _owner_role
    ) THEN
        EXECUTE 'GRANT CREATE ON SCHEMA "dms" TO "edfi_dms_enqueue_owner"';
        BEGIN
            EXECUTE 'ALTER FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() OWNER TO "edfi_dms_enqueue_owner"';
            EXECUTE 'ALTER FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() OWNER TO "edfi_dms_enqueue_owner"';
        EXCEPTION WHEN OTHERS THEN
            EXECUTE 'REVOKE CREATE ON SCHEMA "dms" FROM "edfi_dms_enqueue_owner"';
            RAISE;
        END;
        EXECUTE 'REVOKE CREATE ON SCHEMA "dms" FROM "edfi_dms_enqueue_owner"';
    END IF;
END $$;

GRANT USAGE ON SCHEMA "dms" TO "edfi_dms_enqueue_owner";
SET ROLE "edfi_dms_enqueue_owner";
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() FROM SESSION_USER;
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() FROM SESSION_USER;
RESET ROLE;
REVOKE INSERT, UPDATE, DELETE ON TABLE "dms"."DocumentProjectionWork" FROM PUBLIC;

GRANT SELECT ON TABLE "dms"."DocumentCacheState" TO "edfi_dms_enqueue_owner";
GRANT SELECT, INSERT, UPDATE ON TABLE "dms"."DocumentProjectionWork" TO "edfi_dms_enqueue_owner";

CREATE SCHEMA IF NOT EXISTS "edfi";
CREATE SCHEMA IF NOT EXISTS "tracked_changes_edfi";

CREATE TABLE IF NOT EXISTS "edfi"."ProfileRootOnlyMergeItem"
(
    "DocumentId" bigint NOT NULL,
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "ContentVersion" bigint NOT NULL DEFAULT 0,
    "PrimarySchoolTypeDescriptor_DescriptorId_Present" boolean NULL,
    "PrimarySchoolTypeDescriptor_Unified_DescriptorId" bigint NULL,
    "SecondarySchoolTypeDescriptor_DescriptorId_Present" boolean NULL,
    "StudentReference_DocumentId" bigint NULL,
    "StudentReference_StudentUniqueId" varchar(32) NULL,
    "PrimarySchoolTypeDescriptor_DescriptorId" bigint GENERATED ALWAYS AS (CASE WHEN "PrimarySchoolTypeDescriptor_DescriptorId_Present" IS NULL THEN NULL ELSE "PrimarySchoolTypeDescriptor_Unified_DescriptorId" END) STORED,
    "SecondarySchoolTypeDescriptor_DescriptorId" bigint GENERATED ALWAYS AS (CASE WHEN "SecondarySchoolTypeDescriptor_DescriptorId_Present" IS NULL THEN NULL ELSE "PrimarySchoolTypeDescriptor_Unified_DescriptorId" END) STORED,
    "DisplayName" varchar(100) NULL,
    "ProfileRootOnlyMergeItemId" integer NOT NULL,
    "ProfileScopeClearableText" varchar(100) NULL,
    "ProfileScopePreservedText" varchar(100) NULL,
    CONSTRAINT "PK_ProfileRootOnlyMergeItem" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_ProfileRootOnlyMergeItem_NK" UNIQUE ("ProfileRootOnlyMergeItemId"),
    CONSTRAINT "CK_ProfileRootOnlyMergeItem_StudentReference_AllNone" CHECK (("StudentReference_DocumentId" IS NULL AND "StudentReference_StudentUniqueId" IS NULL) OR ("StudentReference_DocumentId" IS NOT NULL AND "StudentReference_StudentUniqueId" IS NOT NULL)),
    CONSTRAINT "CK_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescrip_61d9cbded7" CHECK ("PrimarySchoolTypeDescriptor_DescriptorId_Present" IS NULL OR "PrimarySchoolTypeDescriptor_DescriptorId_Present" = true),
    CONSTRAINT "CK_ProfileRootOnlyMergeItem_SecondarySchoolTypeDescr_f2dede0083" CHECK ("SecondarySchoolTypeDescriptor_DescriptorId_Present" IS NULL OR "SecondarySchoolTypeDescriptor_DescriptorId_Present" = true)
);

CREATE TABLE IF NOT EXISTS "edfi"."Student"
(
    "DocumentId" bigint NOT NULL,
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "ContentVersion" bigint NOT NULL DEFAULT 0,
    "FirstName" varchar(75) NOT NULL,
    "StudentUniqueId" varchar(32) NOT NULL,
    CONSTRAINT "PK_Student" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_Student_NK" UNIQUE ("StudentUniqueId"),
    CONSTRAINT "UX_Student_RefKey" UNIQUE ("StudentUniqueId", "DocumentId")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."Descriptor"
(
    "OldNamespace" varchar(255) NOT NULL,
    "NewNamespace" varchar(255) NULL,
    "OldCodeValue" varchar(50) NOT NULL,
    "NewCodeValue" varchar(50) NULL,
    "Discriminator" varchar(128) NOT NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_Descriptor" PRIMARY KEY ("ChangeVersion")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."ProfileRootOnlyMergeItem"
(
    "OldProfileRootOnlyMergeItemId" integer NOT NULL,
    "NewProfileRootOnlyMergeItemId" integer NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_ProfileRootOnlyMergeItem" PRIMARY KEY ("ChangeVersion")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."Student"
(
    "OldStudentUniqueId" varchar(32) NOT NULL,
    "NewStudentUniqueId" varchar(32) NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_Student" PRIMARY KEY ("ChangeVersion")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ProfileRootOnlyMergeItem_Document'
        AND conrelid = to_regclass('"edfi"."ProfileRootOnlyMergeItem"')
    )
    THEN
        ALTER TABLE "edfi"."ProfileRootOnlyMergeItem"
        ADD CONSTRAINT "FK_ProfileRootOnlyMergeItem_Document"
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
        WHERE conname = 'FK_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescriptor_Unified'
        AND conrelid = to_regclass('"edfi"."ProfileRootOnlyMergeItem"')
    )
    THEN
        ALTER TABLE "edfi"."ProfileRootOnlyMergeItem"
        ADD CONSTRAINT "FK_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescriptor_Unified"
        FOREIGN KEY ("PrimarySchoolTypeDescriptor_Unified_DescriptorId")
        REFERENCES "dms"."Descriptor" ("DocumentId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ProfileRootOnlyMergeItem_StudentReference_RefKey'
        AND conrelid = to_regclass('"edfi"."ProfileRootOnlyMergeItem"')
    )
    THEN
        ALTER TABLE "edfi"."ProfileRootOnlyMergeItem"
        ADD CONSTRAINT "FK_ProfileRootOnlyMergeItem_StudentReference_RefKey"
        FOREIGN KEY ("StudentReference_StudentUniqueId", "StudentReference_DocumentId")
        REFERENCES "edfi"."Student" ("StudentUniqueId", "DocumentId")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_Student_Document'
        AND conrelid = to_regclass('"edfi"."Student"')
    )
    THEN
        ALTER TABLE "edfi"."Student"
        ADD CONSTRAINT "FK_Student_Document"
        FOREIGN KEY ("DocumentId")
        REFERENCES "dms"."Document" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_Descriptor_Discriminator_ContentVersion" ON "dms"."Descriptor" ("Discriminator", "ContentVersion");

CREATE INDEX IF NOT EXISTS "IX_ProfileRootOnlyMergeItem_ContentVersion" ON "edfi"."ProfileRootOnlyMergeItem" ("ContentVersion");

CREATE INDEX IF NOT EXISTS "IX_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescrip_5313ae7036" ON "edfi"."ProfileRootOnlyMergeItem" ("PrimarySchoolTypeDescriptor_Unified_DescriptorId");

CREATE INDEX IF NOT EXISTS "IX_ProfileRootOnlyMergeItem_StudentReference_Student_706047d2af" ON "edfi"."ProfileRootOnlyMergeItem" ("StudentReference_StudentUniqueId", "StudentReference_DocumentId");

CREATE INDEX IF NOT EXISTS "IX_Student_ContentVersion" ON "edfi"."Student" ("ContentVersion");

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_ProfileRootOnlyMergeItem_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."ProfileRootOnlyMergeItemId" IS DISTINCT FROM NEW."ProfileRootOnlyMergeItemId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiProfileRootOnlyMergeItem' || '$.profileRootOnlyMergeItemId=' || NEW."ProfileRootOnlyMergeItemId"::text), NEW."DocumentId", 1);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ProfileRootOnlyMergeItem_ReferentialIdentity" ON "edfi"."ProfileRootOnlyMergeItem";
CREATE TRIGGER "TR_ProfileRootOnlyMergeItem_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."ProfileRootOnlyMergeItem"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_ProfileRootOnlyMergeItem_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_ProfileRootOnlyMergeItem_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
    _stampedContentLastModifiedAt timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        INSERT INTO "tracked_changes_edfi"."ProfileRootOnlyMergeItem" (
            "OldProfileRootOnlyMergeItemId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."ProfileRootOnlyMergeItemId",
            doc."DocumentUuid",
            doc."ContentVersion"
        FROM "dms"."Document" doc
        WHERE doc."DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."PrimarySchoolTypeDescriptor_DescriptorId_Present" IS DISTINCT FROM NEW."PrimarySchoolTypeDescriptor_DescriptorId_Present" OR OLD."PrimarySchoolTypeDescriptor_Unified_DescriptorId" IS DISTINCT FROM NEW."PrimarySchoolTypeDescriptor_Unified_DescriptorId" OR OLD."SecondarySchoolTypeDescriptor_DescriptorId_Present" IS DISTINCT FROM NEW."SecondarySchoolTypeDescriptor_DescriptorId_Present" OR OLD."StudentReference_DocumentId" IS DISTINCT FROM NEW."StudentReference_DocumentId" OR OLD."StudentReference_StudentUniqueId" IS DISTINCT FROM NEW."StudentReference_StudentUniqueId" OR OLD."DisplayName" IS DISTINCT FROM NEW."DisplayName" OR OLD."ProfileRootOnlyMergeItemId" IS DISTINCT FROM NEW."ProfileRootOnlyMergeItemId" OR OLD."ProfileScopeClearableText" IS DISTINCT FROM NEW."ProfileScopeClearableText" OR OLD."ProfileScopePreservedText" IS DISTINCT FROM NEW."ProfileScopePreservedText") THEN
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
    IF TG_OP = 'UPDATE' AND (OLD."ProfileRootOnlyMergeItemId" IS DISTINCT FROM NEW."ProfileRootOnlyMergeItemId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
        INSERT INTO "tracked_changes_edfi"."ProfileRootOnlyMergeItem" (
            "OldProfileRootOnlyMergeItemId",
            "NewProfileRootOnlyMergeItemId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."ProfileRootOnlyMergeItemId",
            NEW."ProfileRootOnlyMergeItemId",
            doc."DocumentUuid",
            _stampedContentVersion
        FROM "dms"."Document" doc
        WHERE doc."DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ProfileRootOnlyMergeItem_Stamp" ON "edfi"."ProfileRootOnlyMergeItem";
CREATE TRIGGER "TR_ProfileRootOnlyMergeItem_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."ProfileRootOnlyMergeItem"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_ProfileRootOnlyMergeItem_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_Student_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 3;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiStudent' || '$.studentUniqueId=' || NEW."StudentUniqueId"::text), NEW."DocumentId", 3);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Student_ReferentialIdentity" ON "edfi"."Student";
CREATE TRIGGER "TR_Student_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."Student"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_Student_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_Student_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
    _stampedContentLastModifiedAt timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        INSERT INTO "tracked_changes_edfi"."Student" (
            "OldStudentUniqueId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."StudentUniqueId",
            doc."DocumentUuid",
            doc."ContentVersion"
        FROM "dms"."Document" doc
        WHERE doc."DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."FirstName" IS DISTINCT FROM NEW."FirstName" OR OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId") THEN
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
    IF TG_OP = 'UPDATE' AND (OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
        INSERT INTO "tracked_changes_edfi"."Student" (
            "OldStudentUniqueId",
            "NewStudentUniqueId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."StudentUniqueId",
            NEW."StudentUniqueId",
            doc."DocumentUuid",
            _stampedContentVersion
        FROM "dms"."Document" doc
        WHERE doc."DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Student_Stamp" ON "edfi"."Student";
CREATE TRIGGER "TR_Student_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."Student"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_Student_Stamp"();

-- ==========================================================
-- Phase 7: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- DataStoreIdentity singleton insert-if-missing
INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity")
VALUES (1, gen_random_uuid())
ON CONFLICT ("DataStoreIdentitySingletonId") DO NOTHING;

-- DocumentCacheState singleton insert-if-missing
INSERT INTO "dms"."DocumentCacheState" ("StateId", "ProjectionLifecycleState", "CacheAheadRecoveryRequired")
VALUES (1, 'Disabled', false)
ON CONFLICT ("StateId") DO NOTHING;

-- ResourceKey seed inserts (insert-if-missing)
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (1, 'Ed-Fi', 'ProfileRootOnlyMergeItem', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (2, 'Ed-Fi', 'SchoolTypeDescriptor', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (3, 'Ed-Fi', 'Student', '5.0.0')
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
            (1::smallint, 'Ed-Fi', 'ProfileRootOnlyMergeItem', '5.0.0'),
            (2::smallint, 'Ed-Fi', 'SchoolTypeDescriptor', '5.0.0'),
            (3::smallint, 'Ed-Fi', 'Student', '5.0.0')
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
                    (1::smallint, 'Ed-Fi', 'ProfileRootOnlyMergeItem', '5.0.0'),
                    (2::smallint, 'Ed-Fi', 'SchoolTypeDescriptor', '5.0.0'),
                    (3::smallint, 'Ed-Fi', 'Student', '5.0.0')
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
VALUES (1, '1.0.0', 'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0', 3, '\xA72B6D85B70E1147001E9503787A2F7173C575DDBED1DFD867AAFECC5ACD13D9'::bytea)
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
        IF _stored_hash <> '\xA72B6D85B70E1147001E9503787A2F7173C575DDBED1DFD867AAFECC5ACD13D9'::bytea THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored % but expected %', encode(_stored_hash, 'hex'), encode('\xA72B6D85B70E1147001E9503787A2F7173C575DDBED1DFD867AAFECC5ACD13D9'::bytea, 'hex');
        END IF;
    END IF;
END $$;

-- SchemaComponent seed inserts (insert-if-missing)
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0', 'ed-fi', 'Ed-Fi', '5.0.0', false)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;

-- SchemaComponent exact-match validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_names text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."SchemaComponent" WHERE "EffectiveSchemaHash" = 'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0';
    IF _actual_count <> 1 THEN
        RAISE EXCEPTION 'dms.SchemaComponent count mismatch: expected 1, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."SchemaComponent" sc
    WHERE sc."EffectiveSchemaHash" = 'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0'
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
            WHERE sc."EffectiveSchemaHash" = 'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0'
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
