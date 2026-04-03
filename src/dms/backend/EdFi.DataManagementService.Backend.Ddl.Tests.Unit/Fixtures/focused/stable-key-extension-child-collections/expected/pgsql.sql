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
        IF _stored_hash IS NOT NULL AND _stored_hash <> '60037ca0d1357cab3f3c898dba14e939e3b12f2d01fcc7cb62364733958827ea' THEN
            RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database has ''%'' but expected ''%''', _stored_hash, '60037ca0d1357cab3f3c898dba14e939e3b12f2d01fcc7cb62364733958827ea';
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
CREATE SCHEMA IF NOT EXISTS "sample";

CREATE TABLE IF NOT EXISTS "edfi"."Program"
(
    "DocumentId" bigint NOT NULL,
    "ProgramName" varchar(20) NOT NULL,
    CONSTRAINT "PK_Program" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_Program_NK" UNIQUE ("ProgramName"),
    CONSTRAINT "UX_Program_RefKey" UNIQUE ("DocumentId", "ProgramName")
);

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_School_NK" UNIQUE ("SchoolId")
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolExtension"
(
    "DocumentId" bigint NOT NULL,
    "CampusCode" varchar(20) NULL,
    CONSTRAINT "PK_SchoolExtension" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolExtensionAddress"
(
    "BaseCollectionItemId" bigint NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "Zone" varchar(20) NULL,
    CONSTRAINT "PK_SchoolExtensionAddress" PRIMARY KEY ("BaseCollectionItemId"),
    CONSTRAINT "UX_SchoolExtensionAddress_BaseCollectionItemId_Schoo_5f03dcfbaa" UNIQUE ("BaseCollectionItemId", "School_DocumentId")
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolExtensionIntervention"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "Ordinal" integer NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "InterventionCode" varchar(20) NULL,
    CONSTRAINT "PK_SchoolExtensionIntervention" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_SchoolExtensionIntervention_CollectionItemId_Scho_86f658fa71" UNIQUE ("CollectionItemId", "School_DocumentId"),
    CONSTRAINT "UX_SchoolExtensionIntervention_InterventionCode_Scho_dd07b5d573" UNIQUE ("School_DocumentId", "InterventionCode"),
    CONSTRAINT "UX_SchoolExtensionIntervention_Ordinal_School_DocumentId" UNIQUE ("School_DocumentId", "Ordinal")
);

CREATE TABLE IF NOT EXISTS "edfi"."SchoolAddress"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "Ordinal" integer NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "City" varchar(30) NULL,
    CONSTRAINT "PK_SchoolAddress" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_SchoolAddress_City_School_DocumentId" UNIQUE ("School_DocumentId", "City"),
    CONSTRAINT "UX_SchoolAddress_CollectionItemId_School_DocumentId" UNIQUE ("CollectionItemId", "School_DocumentId"),
    CONSTRAINT "UX_SchoolAddress_Ordinal_School_DocumentId" UNIQUE ("School_DocumentId", "Ordinal")
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolExtensionAddressSponsorReference"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "BaseCollectionItemId" bigint NOT NULL,
    "Ordinal" integer NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "Program_DocumentId" bigint NULL,
    "Program_ProgramName" varchar(20) NULL,
    CONSTRAINT "PK_SchoolExtensionAddressSponsorReference" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_SchoolExtensionAddressSponsorReference_BaseCollec_320d72732a" UNIQUE ("BaseCollectionItemId", "Program_DocumentId"),
    CONSTRAINT "UX_SchoolExtensionAddressSponsorReference_BaseCollec_62da5d3142" UNIQUE ("BaseCollectionItemId", "Ordinal"),
    CONSTRAINT "CK_SchoolExtensionAddressSponsorReference_Program_AllNone" CHECK (("Program_DocumentId" IS NULL AND "Program_ProgramName" IS NULL) OR ("Program_DocumentId" IS NOT NULL AND "Program_ProgramName" IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolExtensionInterventionVisit"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "Ordinal" integer NOT NULL,
    "ParentCollectionItemId" bigint NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "VisitCode" varchar(20) NULL,
    CONSTRAINT "PK_SchoolExtensionInterventionVisit" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_SchoolExtensionInterventionVisit_Ordinal_ParentCo_9a9989371a" UNIQUE ("ParentCollectionItemId", "Ordinal"),
    CONSTRAINT "UX_SchoolExtensionInterventionVisit_ParentCollection_57090fc344" UNIQUE ("ParentCollectionItemId", "VisitCode")
);

CREATE TABLE IF NOT EXISTS "edfi"."SchoolAddressPeriod"
(
    "CollectionItemId" bigint NOT NULL DEFAULT nextval('"dms"."CollectionItemIdSequence"'),
    "Ordinal" integer NOT NULL,
    "ParentCollectionItemId" bigint NOT NULL,
    "School_DocumentId" bigint NOT NULL,
    "PeriodName" varchar(30) NULL,
    CONSTRAINT "PK_SchoolAddressPeriod" PRIMARY KEY ("CollectionItemId"),
    CONSTRAINT "UX_SchoolAddressPeriod_Ordinal_ParentCollectionItemId" UNIQUE ("ParentCollectionItemId", "Ordinal"),
    CONSTRAINT "UX_SchoolAddressPeriod_ParentCollectionItemId_PeriodName" UNIQUE ("ParentCollectionItemId", "PeriodName")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_Program_Document'
        AND conrelid = to_regclass('"edfi"."Program"')
    )
    THEN
        ALTER TABLE "edfi"."Program"
        ADD CONSTRAINT "FK_Program_Document"
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
        WHERE conname = 'FK_SchoolExtensionIntervention_SchoolExtension'
        AND conrelid = to_regclass('"sample"."SchoolExtensionIntervention"')
    )
    THEN
        ALTER TABLE "sample"."SchoolExtensionIntervention"
        ADD CONSTRAINT "FK_SchoolExtensionIntervention_SchoolExtension"
        FOREIGN KEY ("School_DocumentId")
        REFERENCES "sample"."SchoolExtension" ("DocumentId")
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

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolExtensionAddressSponsorReference_Program_RefKey'
        AND conrelid = to_regclass('"sample"."SchoolExtensionAddressSponsorReference"')
    )
    THEN
        ALTER TABLE "sample"."SchoolExtensionAddressSponsorReference"
        ADD CONSTRAINT "FK_SchoolExtensionAddressSponsorReference_Program_RefKey"
        FOREIGN KEY ("Program_DocumentId", "Program_ProgramName")
        REFERENCES "edfi"."Program" ("DocumentId", "ProgramName")
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolExtensionAddressSponsorReference_SchoolExte_6ed3e7d5fe'
        AND conrelid = to_regclass('"sample"."SchoolExtensionAddressSponsorReference"')
    )
    THEN
        ALTER TABLE "sample"."SchoolExtensionAddressSponsorReference"
        ADD CONSTRAINT "FK_SchoolExtensionAddressSponsorReference_SchoolExte_6ed3e7d5fe"
        FOREIGN KEY ("BaseCollectionItemId", "School_DocumentId")
        REFERENCES "sample"."SchoolExtensionAddress" ("BaseCollectionItemId", "School_DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolExtensionInterventionVisit_SchoolExtensionIntervention'
        AND conrelid = to_regclass('"sample"."SchoolExtensionInterventionVisit"')
    )
    THEN
        ALTER TABLE "sample"."SchoolExtensionInterventionVisit"
        ADD CONSTRAINT "FK_SchoolExtensionInterventionVisit_SchoolExtensionIntervention"
        FOREIGN KEY ("ParentCollectionItemId", "School_DocumentId")
        REFERENCES "sample"."SchoolExtensionIntervention" ("CollectionItemId", "School_DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolAddressPeriod_SchoolAddress'
        AND conrelid = to_regclass('"edfi"."SchoolAddressPeriod"')
    )
    THEN
        ALTER TABLE "edfi"."SchoolAddressPeriod"
        ADD CONSTRAINT "FK_SchoolAddressPeriod_SchoolAddress"
        FOREIGN KEY ("ParentCollectionItemId", "School_DocumentId")
        REFERENCES "edfi"."SchoolAddress" ("CollectionItemId", "School_DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_SchoolAddressPeriod_ParentCollectionItemId_School_DocumentId" ON "edfi"."SchoolAddressPeriod" ("ParentCollectionItemId", "School_DocumentId");

CREATE INDEX IF NOT EXISTS "IX_SchoolExtensionAddressSponsorReference_BaseCollec_246d3bdfd0" ON "sample"."SchoolExtensionAddressSponsorReference" ("BaseCollectionItemId", "School_DocumentId");

CREATE INDEX IF NOT EXISTS "IX_SchoolExtensionAddressSponsorReference_Program_Do_1aadb68520" ON "sample"."SchoolExtensionAddressSponsorReference" ("Program_DocumentId", "Program_ProgramName");

CREATE INDEX IF NOT EXISTS "IX_SchoolExtensionInterventionVisit_ParentCollection_03f54ece78" ON "sample"."SchoolExtensionInterventionVisit" ("ParentCollectionItemId", "School_DocumentId");

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_Program_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."ProgramName" IS DISTINCT FROM NEW."ProgramName") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiProgram' || '$.programName=' || NEW."ProgramName"::text), NEW."DocumentId", 1);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Program_ReferentialIdentity" ON "edfi"."Program";
CREATE TRIGGER "TR_Program_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."Program"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_Program_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_Program_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."ProgramName" IS DISTINCT FROM NEW."ProgramName") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."ProgramName" IS DISTINCT FROM NEW."ProgramName") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Program_Stamp" ON "edfi"."Program";
CREATE TRIGGER "TR_Program_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."Program"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_Program_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 2;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$.schoolId=' || NEW."SchoolId"::text), NEW."DocumentId", 2);
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
    IF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."School_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."CollectionItemId" IS DISTINCT FROM NEW."CollectionItemId" OR OLD."Ordinal" IS DISTINCT FROM NEW."Ordinal" OR OLD."School_DocumentId" IS DISTINCT FROM NEW."School_DocumentId" OR OLD."City" IS DISTINCT FROM NEW."City") THEN
        RETURN NEW;
    END IF;
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."School_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."SchoolAddress"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddressPeriod_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."School_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."CollectionItemId" IS DISTINCT FROM NEW."CollectionItemId" OR OLD."Ordinal" IS DISTINCT FROM NEW."Ordinal" OR OLD."ParentCollectionItemId" IS DISTINCT FROM NEW."ParentCollectionItemId" OR OLD."School_DocumentId" IS DISTINCT FROM NEW."School_DocumentId" OR OLD."PeriodName" IS DISTINCT FROM NEW."PeriodName") THEN
        RETURN NEW;
    END IF;
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."School_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolAddressPeriod_Stamp" ON "edfi"."SchoolAddressPeriod";
CREATE TRIGGER "TR_SchoolAddressPeriod_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."SchoolAddressPeriod"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddressPeriod_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."CampusCode" IS DISTINCT FROM NEW."CampusCode") THEN
        RETURN NEW;
    END IF;
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolExtension_Stamp" ON "sample"."SchoolExtension";
CREATE TRIGGER "TR_SchoolExtension_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "sample"."SchoolExtension"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtensionAddress_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."School_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."BaseCollectionItemId" IS DISTINCT FROM NEW."BaseCollectionItemId" OR OLD."School_DocumentId" IS DISTINCT FROM NEW."School_DocumentId" OR OLD."Zone" IS DISTINCT FROM NEW."Zone") THEN
        RETURN NEW;
    END IF;
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."School_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolExtensionAddress_Stamp" ON "sample"."SchoolExtensionAddress";
CREATE TRIGGER "TR_SchoolExtensionAddress_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "sample"."SchoolExtensionAddress"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtensionAddress_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtensionAddressSponsorReference_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."School_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."CollectionItemId" IS DISTINCT FROM NEW."CollectionItemId" OR OLD."BaseCollectionItemId" IS DISTINCT FROM NEW."BaseCollectionItemId" OR OLD."Ordinal" IS DISTINCT FROM NEW."Ordinal" OR OLD."School_DocumentId" IS DISTINCT FROM NEW."School_DocumentId" OR OLD."Program_DocumentId" IS DISTINCT FROM NEW."Program_DocumentId" OR OLD."Program_ProgramName" IS DISTINCT FROM NEW."Program_ProgramName") THEN
        RETURN NEW;
    END IF;
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."School_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolExtensionAddressSponsorReference_Stamp" ON "sample"."SchoolExtensionAddressSponsorReference";
CREATE TRIGGER "TR_SchoolExtensionAddressSponsorReference_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "sample"."SchoolExtensionAddressSponsorReference"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtensionAddressSponsorReference_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtensionIntervention_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."School_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."CollectionItemId" IS DISTINCT FROM NEW."CollectionItemId" OR OLD."Ordinal" IS DISTINCT FROM NEW."Ordinal" OR OLD."School_DocumentId" IS DISTINCT FROM NEW."School_DocumentId" OR OLD."InterventionCode" IS DISTINCT FROM NEW."InterventionCode") THEN
        RETURN NEW;
    END IF;
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."School_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolExtensionIntervention_Stamp" ON "sample"."SchoolExtensionIntervention";
CREATE TRIGGER "TR_SchoolExtensionIntervention_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "sample"."SchoolExtensionIntervention"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtensionIntervention_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtensionInterventionVisit_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."School_DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."CollectionItemId" IS DISTINCT FROM NEW."CollectionItemId" OR OLD."Ordinal" IS DISTINCT FROM NEW."Ordinal" OR OLD."ParentCollectionItemId" IS DISTINCT FROM NEW."ParentCollectionItemId" OR OLD."School_DocumentId" IS DISTINCT FROM NEW."School_DocumentId" OR OLD."VisitCode" IS DISTINCT FROM NEW."VisitCode") THEN
        RETURN NEW;
    END IF;
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."School_DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolExtensionInterventionVisit_Stamp" ON "sample"."SchoolExtensionInterventionVisit";
CREATE TRIGGER "TR_SchoolExtensionInterventionVisit_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "sample"."SchoolExtensionInterventionVisit"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtensionInterventionVisit_Stamp"();

-- ==========================================================
-- Phase 7: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- ResourceKey seed inserts (insert-if-missing)
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (1, 'Ed-Fi', 'Program', '1.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (2, 'Ed-Fi', 'School', '1.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;

-- ResourceKey full-table validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_ids text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."ResourceKey";
    IF _actual_count <> 2 THEN
        RAISE EXCEPTION 'dms.ResourceKey count mismatch: expected 2, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."ResourceKey" rk
    WHERE NOT EXISTS (
        SELECT 1 FROM (VALUES
            (1::smallint, 'Ed-Fi', 'Program', '1.0.0'),
            (2::smallint, 'Ed-Fi', 'School', '1.0.0')
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
                    (1::smallint, 'Ed-Fi', 'Program', '1.0.0'),
                    (2::smallint, 'Ed-Fi', 'School', '1.0.0')
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
VALUES (1, '1.0.0', '60037ca0d1357cab3f3c898dba14e939e3b12f2d01fcc7cb62364733958827ea', 2, '\x3F4100845FD234C31689525E02A344E042EC3E11C81EF4405143942137997C00'::bytea)
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
        IF _stored_count <> 2 THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeyCount mismatch: expected 2, found %', _stored_count;
        END IF;
        IF _stored_hash <> '\x3F4100845FD234C31689525E02A344E042EC3E11C81EF4405143942137997C00'::bytea THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored % but expected %', encode(_stored_hash, 'hex'), encode('\x3F4100845FD234C31689525E02A344E042EC3E11C81EF4405143942137997C00'::bytea, 'hex');
        END IF;
    END IF;
END $$;

-- SchemaComponent seed inserts (insert-if-missing)
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('60037ca0d1357cab3f3c898dba14e939e3b12f2d01fcc7cb62364733958827ea', 'ed-fi', 'Ed-Fi', '1.0.0', false)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('60037ca0d1357cab3f3c898dba14e939e3b12f2d01fcc7cb62364733958827ea', 'sample', 'Sample', '1.0.0', true)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;

-- SchemaComponent exact-match validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_names text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."SchemaComponent" WHERE "EffectiveSchemaHash" = '60037ca0d1357cab3f3c898dba14e939e3b12f2d01fcc7cb62364733958827ea';
    IF _actual_count <> 2 THEN
        RAISE EXCEPTION 'dms.SchemaComponent count mismatch: expected 2, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."SchemaComponent" sc
    WHERE sc."EffectiveSchemaHash" = '60037ca0d1357cab3f3c898dba14e939e3b12f2d01fcc7cb62364733958827ea'
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
            WHERE sc."EffectiveSchemaHash" = '60037ca0d1357cab3f3c898dba14e939e3b12f2d01fcc7cb62364733958827ea'
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

