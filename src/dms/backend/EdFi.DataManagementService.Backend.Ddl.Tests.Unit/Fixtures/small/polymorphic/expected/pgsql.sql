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
        IF _stored_hash IS NOT NULL AND _stored_hash <> '2621a6197de1865649a847eea39217759c1eab0225f2d152b1f4de7f7279e041' THEN
            RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database has ''%'' but expected ''%''', _stored_hash, '2621a6197de1865649a847eea39217759c1eab0225f2d152b1f4de7f7279e041';
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
        IF _stored_hash = '2621a6197de1865649a847eea39217759c1eab0225f2d152b1f4de7f7279e041' THEN
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
        RAISE EXCEPTION 'dms.DocumentCacheState.ProjectionLifecycleState has unsupported value for projection enqueue.';
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
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() FROM PUBLIC;

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
        RAISE EXCEPTION 'dms.DocumentCacheState.ProjectionLifecycleState has unsupported value for projection enqueue.';
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
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() FROM PUBLIC;

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
        BEGIN
            CREATE ROLE "edfi_dms_enqueue_owner" WITH NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            _owner_role := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
            _created_owner_role := true;
        EXCEPTION
            WHEN duplicate_object OR unique_violation THEN
                _owner_role := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
                _created_owner_role := true;
        END;
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
CREATE SCHEMA IF NOT EXISTS "auth";
CREATE SCHEMA IF NOT EXISTS "tracked_changes_edfi";

CREATE TABLE IF NOT EXISTS "edfi"."LocalEducationAgency"
(
    "DocumentId" bigint NOT NULL,
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "ContentVersion" bigint NOT NULL DEFAULT 0,
    "EducationOrganizationId" integer NOT NULL,
    "LocalEducationAgencyId" integer NOT NULL,
    CONSTRAINT "PK_LocalEducationAgency" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_LocalEducationAgency_NK" UNIQUE ("LocalEducationAgencyId")
);

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "ContentVersion" bigint NOT NULL DEFAULT 0,
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

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."LocalEducationAgency"
(
    "OldLocalEducationAgencyId" integer NOT NULL,
    "NewLocalEducationAgencyId" integer NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_LocalEducationAgency" PRIMARY KEY ("ChangeVersion")
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

CREATE TABLE IF NOT EXISTS "edfi"."EducationOrganizationIdentity"
(
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    "Discriminator" varchar(256) NOT NULL,
    CONSTRAINT "PK_EducationOrganizationIdentity" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_EducationOrganizationIdentity_NK" UNIQUE ("EducationOrganizationId"),
    CONSTRAINT "UX_EducationOrganizationIdentity_RefKey" UNIQUE ("EducationOrganizationId", "DocumentId")
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

CREATE INDEX IF NOT EXISTS "IX_LocalEducationAgency_ContentVersion" ON "edfi"."LocalEducationAgency" ("ContentVersion");

CREATE INDEX IF NOT EXISTS "IX_School_ContentVersion" ON "edfi"."School" ("ContentVersion");

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
AFTER INSERT OR UPDATE ON "edfi"."LocalEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
    _stampedContentLastModifiedAt timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        INSERT INTO "tracked_changes_edfi"."LocalEducationAgency" (
            "OldLocalEducationAgencyId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."LocalEducationAgencyId",
            doc."DocumentUuid",
            doc."ContentVersion"
        FROM "dms"."Document" doc
        WHERE doc."DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId" OR OLD."LocalEducationAgencyId" IS DISTINCT FROM NEW."LocalEducationAgencyId") THEN
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
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
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
-- Phase 10: Seed Data (insert-if-missing + validation)
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
VALUES (1, '1.0.0', '2621a6197de1865649a847eea39217759c1eab0225f2d152b1f4de7f7279e041', 3, '\x13390DEE0A99E1FF56E7F39CB8F5B43BEDA834078EA87A4975FE84B5710F6252'::bytea)
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
VALUES ('2621a6197de1865649a847eea39217759c1eab0225f2d152b1f4de7f7279e041', 'ed-fi', 'Ed-Fi', '5.0.0', false)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;

-- SchemaComponent exact-match validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_names text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."SchemaComponent" WHERE "EffectiveSchemaHash" = '2621a6197de1865649a847eea39217759c1eab0225f2d152b1f4de7f7279e041';
    IF _actual_count <> 1 THEN
        RAISE EXCEPTION 'dms.SchemaComponent count mismatch: expected 1, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."SchemaComponent" sc
    WHERE sc."EffectiveSchemaHash" = '2621a6197de1865649a847eea39217759c1eab0225f2d152b1f4de7f7279e041'
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
            WHERE sc."EffectiveSchemaHash" = '2621a6197de1865649a847eea39217759c1eab0225f2d152b1f4de7f7279e041'
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
