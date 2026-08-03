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
        IF _stored_hash IS NOT NULL AND _stored_hash <> 'afd5034a0630ba03994e2a9fc99b4802906af1958be0a488a2214af863f2056f' THEN
            RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database has ''%'' but expected ''%''', _stored_hash, 'afd5034a0630ba03994e2a9fc99b4802906af1958be0a488a2214af863f2056f';
        END IF;
    END IF;
END $$;

-- ==========================================================
-- Phase 1: Schemas
-- ==========================================================

CREATE SCHEMA IF NOT EXISTS "dms";

-- ==========================================================
-- Phase 3: Sequences
-- ==========================================================

CREATE SEQUENCE IF NOT EXISTS "dms"."ChangeVersionSequence" START WITH 1;

CREATE SEQUENCE IF NOT EXISTS "dms"."CollectionItemIdSequence" START WITH 1;

CREATE SEQUENCE IF NOT EXISTS "dms"."DocumentIdSequence" START WITH 1;

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

-- ==========================================================
-- Phase 5: Tables (PK/UNIQUE/CHECK only, no cross-table FKs)
-- ==========================================================

CREATE TABLE IF NOT EXISTS "dms"."Descriptor"
(
    "DocumentId" bigint NOT NULL DEFAULT nextval('"dms"."DocumentIdSequence"'),
    "Namespace" varchar(255) NOT NULL,
    "CodeValue" varchar(50) NOT NULL,
    "ShortDescription" varchar(75) NOT NULL,
    "Description" varchar(1024) NULL,
    "EffectiveBeginDate" date NULL,
    "EffectiveEndDate" date NULL,
    "Discriminator" varchar(128) NOT NULL,
    "Uri" varchar(306) NOT NULL,
    "UriLowered" varchar(612) GENERATED ALWAYS AS (lower("Uri")) STORED,
    "DocumentUuid" uuid NOT NULL DEFAULT gen_random_uuid(),
    "IdentityVersion" bigint NOT NULL DEFAULT 0,
    "IdentityLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "CreatedByOwnershipTokenId" smallint NULL,
    "ResourceKeyId" smallint NULL,
    "ContentVersion" bigint NOT NULL DEFAULT 0,
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_Descriptor" PRIMARY KEY ("DocumentId")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'UX_Descriptor_DocumentUuid'
        AND conrelid = to_regclass('"dms"."Descriptor"')
    )
    THEN
        ALTER TABLE "dms"."Descriptor"
        ADD CONSTRAINT "UX_Descriptor_DocumentUuid" UNIQUE ("DocumentUuid");
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'UX_Descriptor_UriLowered_Discriminator'
        AND conrelid = to_regclass('"dms"."Descriptor"')
    )
    THEN
        ALTER TABLE "dms"."Descriptor"
        ADD CONSTRAINT "UX_Descriptor_UriLowered_Discriminator" UNIQUE ("UriLowered", "Discriminator");
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
        UPDATE "dms"."Descriptor" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now(), "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE r."DocumentId" = NEW."DocumentId";
    ELSIF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Descriptor" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE r."DocumentId" = NEW."DocumentId";
    ELSIF TG_OP = 'DELETE' THEN
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

CREATE SCHEMA IF NOT EXISTS "aligned";
CREATE SCHEMA IF NOT EXISTS "edfi";
CREATE SCHEMA IF NOT EXISTS "tracked_changes_edfi";

CREATE TABLE IF NOT EXISTS "edfi"."ParentResource"
(
    "DocumentId" bigint NOT NULL DEFAULT nextval('"dms"."DocumentIdSequence"'),
    "ContentLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "ContentVersion" bigint NOT NULL DEFAULT 0,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "CreatedByOwnershipTokenId" smallint NULL,
    "DocumentUuid" uuid NOT NULL DEFAULT gen_random_uuid(),
    "IdentityLastModifiedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "IdentityVersion" bigint NOT NULL DEFAULT 0,
    "ParentResourceId" integer NOT NULL,
    CONSTRAINT "PK_ParentResource" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_ParentResource_DocumentUuid" UNIQUE ("DocumentUuid"),
    CONSTRAINT "UX_ParentResource_NK" UNIQUE ("ParentResourceId")
);

CREATE TABLE IF NOT EXISTS "edfi"."ParentResourceParent"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "Ordinal" integer NOT NULL,
    "ParentResource_DocumentId" bigint NOT NULL,
    "ParentCode" varchar(30) NULL,
    "ParentName" varchar(100) NULL,
    CONSTRAINT "PK_ParentResourceParent" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_ParentResourceParent_CollectionItemId_ParentResou_aa2e84db9f" UNIQUE ("CollectionItemId", "ParentResource_DocumentId"),
    CONSTRAINT "UX_ParentResourceParent_Ordinal_ParentResource_DocumentId" UNIQUE ("ParentResource_DocumentId", "Ordinal"),
    CONSTRAINT "UX_ParentResourceParent_ParentCode_ParentResource_DocumentId" UNIQUE ("ParentResource_DocumentId", "ParentCode")
);

CREATE TABLE IF NOT EXISTS "aligned"."ParentResourceExtensionParent"
(
    "BaseCollectionItemId" bigint NOT NULL,
    "ParentResource_DocumentId" bigint NOT NULL,
    "AlignedHiddenScalar" varchar(100) NULL,
    "AlignedVisibleScalar" varchar(100) NULL,
    CONSTRAINT "PK_ParentResourceExtensionParent" PRIMARY KEY ("BaseCollectionItemId"),
    CONSTRAINT "UX_ParentResourceExtensionParent_BaseCollectionItemI_8cab8c84ed" UNIQUE ("BaseCollectionItemId", "ParentResource_DocumentId")
);

CREATE TABLE IF NOT EXISTS "aligned"."ParentResourceExtensionParentChildren"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "BaseCollectionItemId" bigint NOT NULL,
    "Ordinal" integer NOT NULL,
    "ParentResource_DocumentId" bigint NOT NULL,
    "ChildCode" varchar(30) NULL,
    "ChildValue" varchar(100) NULL,
    CONSTRAINT "PK_ParentResourceExtensionParentChildren" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_ParentResourceExtensionParentChildren_BaseCollect_1f410f72fe" UNIQUE ("BaseCollectionItemId", "Ordinal"),
    CONSTRAINT "UX_ParentResourceExtensionParentChildren_BaseCollect_57b37c41ae" UNIQUE ("BaseCollectionItemId", "ChildCode"),
    CONSTRAINT "UX_ParentResourceExtensionParentChildren_CollectionI_1c2d406775" UNIQUE ("CollectionItemId", "ParentResource_DocumentId")
);

CREATE TABLE IF NOT EXISTS "aligned"."ParentResourceExtensionParentChildrenExtensionChildren"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "Ordinal" integer NOT NULL,
    "ParentCollectionItemId" bigint NOT NULL,
    "ParentResource_DocumentId" bigint NOT NULL,
    "ExtensionChildCode" varchar(30) NULL,
    "ExtensionChildValue" varchar(100) NULL,
    CONSTRAINT "PK_ParentResourceExtensionParentChildrenExtensionChildren" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_ParentResourceExtensionParentChildrenExtensionChi_75cba104d8" UNIQUE ("ParentCollectionItemId", "Ordinal"),
    CONSTRAINT "UX_ParentResourceExtensionParentChildrenExtensionChi_8cefc64873" UNIQUE ("ParentCollectionItemId", "ExtensionChildCode")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."ParentResource"
(
    "OldParentResourceId" integer NOT NULL,
    "NewParentResourceId" integer NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_ParentResource" PRIMARY KEY ("ChangeVersion")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ParentResourceParent_ParentResource'
        AND conrelid = to_regclass('"edfi"."ParentResourceParent"')
    )
    THEN
        ALTER TABLE "edfi"."ParentResourceParent"
        ADD CONSTRAINT "FK_ParentResourceParent_ParentResource"
        FOREIGN KEY ("ParentResource_DocumentId")
        REFERENCES "edfi"."ParentResource" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ParentResourceExtensionParent_ParentResourceParent'
        AND conrelid = to_regclass('"aligned"."ParentResourceExtensionParent"')
    )
    THEN
        ALTER TABLE "aligned"."ParentResourceExtensionParent"
        ADD CONSTRAINT "FK_ParentResourceExtensionParent_ParentResourceParent"
        FOREIGN KEY ("BaseCollectionItemId", "ParentResource_DocumentId")
        REFERENCES "edfi"."ParentResourceParent" ("CollectionItemId", "ParentResource_DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ParentResourceExtensionParentChildren_ParentResou_bcaa263b83'
        AND conrelid = to_regclass('"aligned"."ParentResourceExtensionParentChildren"')
    )
    THEN
        ALTER TABLE "aligned"."ParentResourceExtensionParentChildren"
        ADD CONSTRAINT "FK_ParentResourceExtensionParentChildren_ParentResou_bcaa263b83"
        FOREIGN KEY ("BaseCollectionItemId", "ParentResource_DocumentId")
        REFERENCES "aligned"."ParentResourceExtensionParent" ("BaseCollectionItemId", "ParentResource_DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ParentResourceExtensionParentChildrenExtensionChi_af5562519b'
        AND conrelid = to_regclass('"aligned"."ParentResourceExtensionParentChildrenExtensionChildren"')
    )
    THEN
        ALTER TABLE "aligned"."ParentResourceExtensionParentChildrenExtensionChildren"
        ADD CONSTRAINT "FK_ParentResourceExtensionParentChildrenExtensionChi_af5562519b"
        FOREIGN KEY ("ParentCollectionItemId", "ParentResource_DocumentId")
        REFERENCES "aligned"."ParentResourceExtensionParentChildren" ("CollectionItemId", "ParentResource_DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_ParentResourceExtensionParentChildren_BaseCollect_984953c4a5" ON "aligned"."ParentResourceExtensionParentChildren" ("BaseCollectionItemId", "ParentResource_DocumentId");

CREATE INDEX IF NOT EXISTS "IX_ParentResourceExtensionParentChildrenExtensionChi_372c0cacd4" ON "aligned"."ParentResourceExtensionParentChildrenExtensionChildren" ("ParentCollectionItemId", "ParentResource_DocumentId");

CREATE INDEX IF NOT EXISTS "IX_ParentResource_ContentVersion" ON "edfi"."ParentResource" ("ContentVersion");

CREATE OR REPLACE FUNCTION "aligned"."TF_TR_ParentResourceExtensionParent_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "edfi"."ParentResource" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE r."DocumentId" = OLD."ParentResource_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."BaseCollectionItemId" IS DISTINCT FROM NEW."BaseCollectionItemId" OR OLD."ParentResource_DocumentId" IS DISTINCT FROM NEW."ParentResource_DocumentId" OR OLD."AlignedHiddenScalar" IS DISTINCT FROM NEW."AlignedHiddenScalar" OR OLD."AlignedVisibleScalar" IS DISTINCT FROM NEW."AlignedVisibleScalar") THEN
        RETURN NEW;
    END IF;
    UPDATE "edfi"."ParentResource" r
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE r."DocumentId" = NEW."ParentResource_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ParentResourceExtensionParent_Stamp" ON "aligned"."ParentResourceExtensionParent";
CREATE TRIGGER "TR_ParentResourceExtensionParent_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "aligned"."ParentResourceExtensionParent"
FOR EACH ROW
EXECUTE FUNCTION "aligned"."TF_TR_ParentResourceExtensionParent_Stamp"();

CREATE OR REPLACE FUNCTION "aligned"."TF_TR_ParentResourceExtensionParentChildren_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "edfi"."ParentResource" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE r."DocumentId" = OLD."ParentResource_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."CollectionItemId" IS DISTINCT FROM NEW."CollectionItemId" OR OLD."BaseCollectionItemId" IS DISTINCT FROM NEW."BaseCollectionItemId" OR OLD."Ordinal" IS DISTINCT FROM NEW."Ordinal" OR OLD."ParentResource_DocumentId" IS DISTINCT FROM NEW."ParentResource_DocumentId" OR OLD."ChildCode" IS DISTINCT FROM NEW."ChildCode" OR OLD."ChildValue" IS DISTINCT FROM NEW."ChildValue") THEN
        RETURN NEW;
    END IF;
    UPDATE "edfi"."ParentResource" r
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE r."DocumentId" = NEW."ParentResource_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ParentResourceExtensionParentChildren_Stamp" ON "aligned"."ParentResourceExtensionParentChildren";
CREATE TRIGGER "TR_ParentResourceExtensionParentChildren_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "aligned"."ParentResourceExtensionParentChildren"
FOR EACH ROW
EXECUTE FUNCTION "aligned"."TF_TR_ParentResourceExtensionParentChildren_Stamp"();

CREATE OR REPLACE FUNCTION "aligned"."TF_TR_ParentResourceExtensionParentChildrenExtension_7747cf507c"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "edfi"."ParentResource" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE r."DocumentId" = OLD."ParentResource_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."CollectionItemId" IS DISTINCT FROM NEW."CollectionItemId" OR OLD."Ordinal" IS DISTINCT FROM NEW."Ordinal" OR OLD."ParentCollectionItemId" IS DISTINCT FROM NEW."ParentCollectionItemId" OR OLD."ParentResource_DocumentId" IS DISTINCT FROM NEW."ParentResource_DocumentId" OR OLD."ExtensionChildCode" IS DISTINCT FROM NEW."ExtensionChildCode" OR OLD."ExtensionChildValue" IS DISTINCT FROM NEW."ExtensionChildValue") THEN
        RETURN NEW;
    END IF;
    UPDATE "edfi"."ParentResource" r
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE r."DocumentId" = NEW."ParentResource_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ParentResourceExtensionParentChildrenExtensionChildren_Stamp" ON "aligned"."ParentResourceExtensionParentChildrenExtensionChildren";
CREATE TRIGGER "TR_ParentResourceExtensionParentChildrenExtensionChildren_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "aligned"."ParentResourceExtensionParentChildrenExtensionChildren"
FOR EACH ROW
EXECUTE FUNCTION "aligned"."TF_TR_ParentResourceExtensionParentChildrenExtension_7747cf507c"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_ParentResource_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        _stampedContentVersion := nextval('"dms"."ChangeVersionSequence"');
        INSERT INTO "tracked_changes_edfi"."ParentResource" (
            "OldParentResourceId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."ParentResourceId",
            OLD."DocumentUuid",
            _stampedContentVersion;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."ParentResourceId" IS DISTINCT FROM NEW."ParentResourceId") THEN
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
    IF TG_OP = 'UPDATE' AND (OLD."ParentResourceId" IS DISTINCT FROM NEW."ParentResourceId") THEN
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
        INSERT INTO "tracked_changes_edfi"."ParentResource" (
            "OldParentResourceId",
            "NewParentResourceId",
            "Id",
            "ChangeVersion"
        )
        SELECT
            OLD."ParentResourceId",
            NEW."ParentResourceId",
            NEW."DocumentUuid",
            _stampedContentVersion;
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ParentResource_Stamp" ON "edfi"."ParentResource";
CREATE TRIGGER "TR_ParentResource_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."ParentResource"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_ParentResource_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_ParentResourceParent_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "edfi"."ParentResource" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE r."DocumentId" = OLD."ParentResource_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."CollectionItemId" IS DISTINCT FROM NEW."CollectionItemId" OR OLD."Ordinal" IS DISTINCT FROM NEW."Ordinal" OR OLD."ParentResource_DocumentId" IS DISTINCT FROM NEW."ParentResource_DocumentId" OR OLD."ParentCode" IS DISTINCT FROM NEW."ParentCode" OR OLD."ParentName" IS DISTINCT FROM NEW."ParentName") THEN
        RETURN NEW;
    END IF;
    UPDATE "edfi"."ParentResource" r
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE r."DocumentId" = NEW."ParentResource_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ParentResourceParent_Stamp" ON "edfi"."ParentResourceParent";
CREATE TRIGGER "TR_ParentResourceParent_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."ParentResourceParent"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_ParentResourceParent_Stamp"();

-- ==========================================================
-- Phase 7: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- ResourceKey seed inserts (insert-if-missing)
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (1, 'Ed-Fi', 'ParentResource', '1.0.0')
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
            (1::smallint, 'Ed-Fi', 'ParentResource', '1.0.0')
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
                    (1::smallint, 'Ed-Fi', 'ParentResource', '1.0.0')
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
VALUES (1, '1.0.0', 'afd5034a0630ba03994e2a9fc99b4802906af1958be0a488a2214af863f2056f', 1, '\xAA4516A2188A393B97F346BD6483E8A82E57AB430F5377D00B6409E307A812DC'::bytea)
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
        IF _stored_hash <> '\xAA4516A2188A393B97F346BD6483E8A82E57AB430F5377D00B6409E307A812DC'::bytea THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored % but expected %', encode(_stored_hash, 'hex'), encode('\xAA4516A2188A393B97F346BD6483E8A82E57AB430F5377D00B6409E307A812DC'::bytea, 'hex');
        END IF;
    END IF;
END $$;

-- SchemaComponent seed inserts (insert-if-missing)
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('afd5034a0630ba03994e2a9fc99b4802906af1958be0a488a2214af863f2056f', 'aligned', 'Aligned', '1.0.0', true)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('afd5034a0630ba03994e2a9fc99b4802906af1958be0a488a2214af863f2056f', 'ed-fi', 'Ed-Fi', '1.0.0', false)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;

-- SchemaComponent exact-match validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_names text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."SchemaComponent" WHERE "EffectiveSchemaHash" = 'afd5034a0630ba03994e2a9fc99b4802906af1958be0a488a2214af863f2056f';
    IF _actual_count <> 2 THEN
        RAISE EXCEPTION 'dms.SchemaComponent count mismatch: expected 2, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."SchemaComponent" sc
    WHERE sc."EffectiveSchemaHash" = 'afd5034a0630ba03994e2a9fc99b4802906af1958be0a488a2214af863f2056f'
    AND NOT EXISTS (
        SELECT 1 FROM (VALUES
            ('aligned', 'Aligned', '1.0.0', true),
            ('ed-fi', 'Ed-Fi', '1.0.0', false)
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
            WHERE sc."EffectiveSchemaHash" = 'afd5034a0630ba03994e2a9fc99b4802906af1958be0a488a2214af863f2056f'
            AND NOT EXISTS (
                SELECT 1 FROM (VALUES
                    ('aligned', 'Aligned', '1.0.0', true),
                    ('ed-fi', 'Ed-Fi', '1.0.0', false)
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
