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
        IF _stored_hash IS NOT NULL AND _stored_hash <> '1acb653e8acf6278a49130d19b48f13cdd9cf750dca58d5e73dde85f88985169' THEN
            RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database has ''%'' but expected ''%''', _stored_hash, '1acb653e8acf6278a49130d19b48f13cdd9cf750dca58d5e73dde85f88985169';
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
CREATE SCHEMA IF NOT EXISTS "auth";

CREATE TABLE IF NOT EXISTS "edfi"."KeyUnifiedResource"
(
    "DocumentId" bigint NOT NULL,
    "StudentUniqueId_Unified" varchar(32) NOT NULL,
    "ResourceAReference_DocumentId" bigint NOT NULL,
    "ResourceAReference_ResourceAId" varchar(64) NOT NULL,
    "ResourceAReference_StudentUniqueId" varchar(32) GENERATED ALWAYS AS (CASE WHEN "ResourceAReference_DocumentId" IS NULL THEN NULL ELSE "StudentUniqueId_Unified" END) STORED,
    "ResourceBReference_DocumentId" bigint NOT NULL,
    "ResourceBReference_ResourceBId" varchar(64) NOT NULL,
    "ResourceBReference_StudentUniqueId" varchar(32) GENERATED ALWAYS AS (CASE WHEN "ResourceBReference_DocumentId" IS NULL THEN NULL ELSE "StudentUniqueId_Unified" END) STORED,
    "KeyUnifiedResourceId" varchar(64) NOT NULL,
    CONSTRAINT "PK_KeyUnifiedResource" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_KeyUnifiedResource_NK" UNIQUE ("KeyUnifiedResourceId", "ResourceAReference_DocumentId", "ResourceBReference_DocumentId"),
    CONSTRAINT "CK_KeyUnifiedResource_ResourceAReference_AllNone" CHECK (("ResourceAReference_DocumentId" IS NULL AND "ResourceAReference_ResourceAId" IS NULL AND "ResourceAReference_StudentUniqueId" IS NULL) OR ("ResourceAReference_DocumentId" IS NOT NULL AND "ResourceAReference_ResourceAId" IS NOT NULL AND "ResourceAReference_StudentUniqueId" IS NOT NULL)),
    CONSTRAINT "CK_KeyUnifiedResource_ResourceBReference_AllNone" CHECK (("ResourceBReference_DocumentId" IS NULL AND "ResourceBReference_ResourceBId" IS NULL AND "ResourceBReference_StudentUniqueId" IS NULL) OR ("ResourceBReference_DocumentId" IS NOT NULL AND "ResourceBReference_ResourceBId" IS NOT NULL AND "ResourceBReference_StudentUniqueId" IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS "edfi"."ResourceA"
(
    "DocumentId" bigint NOT NULL,
    "StudentReference_DocumentId" bigint NOT NULL,
    "StudentReference_StudentUniqueId" varchar(32) NOT NULL,
    "ResourceAId" varchar(64) NOT NULL,
    CONSTRAINT "PK_ResourceA" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_ResourceA_NK" UNIQUE ("ResourceAId", "StudentReference_DocumentId"),
    CONSTRAINT "UX_ResourceA_RefKey" UNIQUE ("DocumentId", "ResourceAId", "StudentReference_StudentUniqueId"),
    CONSTRAINT "CK_ResourceA_StudentReference_AllNone" CHECK (("StudentReference_DocumentId" IS NULL AND "StudentReference_StudentUniqueId" IS NULL) OR ("StudentReference_DocumentId" IS NOT NULL AND "StudentReference_StudentUniqueId" IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS "edfi"."ResourceB"
(
    "DocumentId" bigint NOT NULL,
    "StudentReference_DocumentId" bigint NOT NULL,
    "StudentReference_StudentUniqueId" varchar(32) NOT NULL,
    "ResourceBId" varchar(64) NOT NULL,
    CONSTRAINT "PK_ResourceB" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_ResourceB_NK" UNIQUE ("ResourceBId", "StudentReference_DocumentId"),
    CONSTRAINT "UX_ResourceB_RefKey" UNIQUE ("DocumentId", "ResourceBId", "StudentReference_StudentUniqueId"),
    CONSTRAINT "CK_ResourceB_StudentReference_AllNone" CHECK (("StudentReference_DocumentId" IS NULL AND "StudentReference_StudentUniqueId" IS NULL) OR ("StudentReference_DocumentId" IS NOT NULL AND "StudentReference_StudentUniqueId" IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    "NameOfInstitution" varchar(75) NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_School_NK" UNIQUE ("SchoolId"),
    CONSTRAINT "UX_School_RefKey" UNIQUE ("DocumentId", "SchoolId")
);

CREATE TABLE IF NOT EXISTS "edfi"."Student"
(
    "DocumentId" bigint NOT NULL,
    "FirstName" varchar(75) NOT NULL,
    "StudentUniqueId" varchar(32) NOT NULL,
    CONSTRAINT "PK_Student" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_Student_NK" UNIQUE ("StudentUniqueId"),
    CONSTRAINT "UX_Student_RefKey" UNIQUE ("DocumentId", "StudentUniqueId")
);

CREATE TABLE IF NOT EXISTS "edfi"."StudentSchoolAssociation"
(
    "DocumentId" bigint NOT NULL,
    "SchoolReference_DocumentId" bigint NOT NULL,
    "SchoolReference_SchoolId" integer NOT NULL,
    "StudentUniqueId" varchar(32) NOT NULL,
    CONSTRAINT "PK_StudentSchoolAssociation" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_StudentSchoolAssociation_NK" UNIQUE ("StudentUniqueId", "SchoolReference_DocumentId"),
    CONSTRAINT "CK_StudentSchoolAssociation_SchoolReference_AllNone" CHECK (("SchoolReference_DocumentId" IS NULL AND "SchoolReference_SchoolId" IS NULL) OR ("SchoolReference_DocumentId" IS NOT NULL AND "SchoolReference_SchoolId" IS NOT NULL))
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
        WHERE conname = 'FK_KeyUnifiedResource_Document'
        AND conrelid = to_regclass('"edfi"."KeyUnifiedResource"')
    )
    THEN
        ALTER TABLE "edfi"."KeyUnifiedResource"
        ADD CONSTRAINT "FK_KeyUnifiedResource_Document"
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
        WHERE conname = 'FK_KeyUnifiedResource_ResourceAReference_RefKey'
        AND conrelid = to_regclass('"edfi"."KeyUnifiedResource"')
    )
    THEN
        ALTER TABLE "edfi"."KeyUnifiedResource"
        ADD CONSTRAINT "FK_KeyUnifiedResource_ResourceAReference_RefKey"
        FOREIGN KEY ("ResourceAReference_DocumentId", "ResourceAReference_ResourceAId", "StudentUniqueId_Unified")
        REFERENCES "edfi"."ResourceA" ("DocumentId", "ResourceAId", "StudentReference_StudentUniqueId")
        ON DELETE NO ACTION
        ON UPDATE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_KeyUnifiedResource_ResourceBReference_RefKey'
        AND conrelid = to_regclass('"edfi"."KeyUnifiedResource"')
    )
    THEN
        ALTER TABLE "edfi"."KeyUnifiedResource"
        ADD CONSTRAINT "FK_KeyUnifiedResource_ResourceBReference_RefKey"
        FOREIGN KEY ("ResourceBReference_DocumentId", "ResourceBReference_ResourceBId", "StudentUniqueId_Unified")
        REFERENCES "edfi"."ResourceB" ("DocumentId", "ResourceBId", "StudentReference_StudentUniqueId")
        ON DELETE NO ACTION
        ON UPDATE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ResourceA_Document'
        AND conrelid = to_regclass('"edfi"."ResourceA"')
    )
    THEN
        ALTER TABLE "edfi"."ResourceA"
        ADD CONSTRAINT "FK_ResourceA_Document"
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
        WHERE conname = 'FK_ResourceA_StudentReference_RefKey'
        AND conrelid = to_regclass('"edfi"."ResourceA"')
    )
    THEN
        ALTER TABLE "edfi"."ResourceA"
        ADD CONSTRAINT "FK_ResourceA_StudentReference_RefKey"
        FOREIGN KEY ("StudentReference_DocumentId", "StudentReference_StudentUniqueId")
        REFERENCES "edfi"."Student" ("DocumentId", "StudentUniqueId")
        ON DELETE NO ACTION
        ON UPDATE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_ResourceB_Document'
        AND conrelid = to_regclass('"edfi"."ResourceB"')
    )
    THEN
        ALTER TABLE "edfi"."ResourceB"
        ADD CONSTRAINT "FK_ResourceB_Document"
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
        WHERE conname = 'FK_ResourceB_StudentReference_RefKey'
        AND conrelid = to_regclass('"edfi"."ResourceB"')
    )
    THEN
        ALTER TABLE "edfi"."ResourceB"
        ADD CONSTRAINT "FK_ResourceB_StudentReference_RefKey"
        FOREIGN KEY ("StudentReference_DocumentId", "StudentReference_StudentUniqueId")
        REFERENCES "edfi"."Student" ("DocumentId", "StudentUniqueId")
        ON DELETE NO ACTION
        ON UPDATE CASCADE;
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

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_StudentSchoolAssociation_Document'
        AND conrelid = to_regclass('"edfi"."StudentSchoolAssociation"')
    )
    THEN
        ALTER TABLE "edfi"."StudentSchoolAssociation"
        ADD CONSTRAINT "FK_StudentSchoolAssociation_Document"
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
        WHERE conname = 'FK_StudentSchoolAssociation_SchoolReference_RefKey'
        AND conrelid = to_regclass('"edfi"."StudentSchoolAssociation"')
    )
    THEN
        ALTER TABLE "edfi"."StudentSchoolAssociation"
        ADD CONSTRAINT "FK_StudentSchoolAssociation_SchoolReference_RefKey"
        FOREIGN KEY ("SchoolReference_DocumentId", "SchoolReference_SchoolId")
        REFERENCES "edfi"."School" ("DocumentId", "SchoolId")
        ON DELETE NO ACTION
        ON UPDATE CASCADE;
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

CREATE INDEX IF NOT EXISTS "IX_KeyUnifiedResource_ResourceAReference_DocumentId__1b15ae82f2" ON "edfi"."KeyUnifiedResource" ("ResourceAReference_DocumentId", "ResourceAReference_ResourceAId", "StudentUniqueId_Unified");

CREATE INDEX IF NOT EXISTS "IX_KeyUnifiedResource_ResourceBReference_DocumentId__9ec2040deb" ON "edfi"."KeyUnifiedResource" ("ResourceBReference_DocumentId", "ResourceBReference_ResourceBId", "StudentUniqueId_Unified");

CREATE INDEX IF NOT EXISTS "IX_ResourceA_StudentReference_DocumentId_StudentRefe_661e48fb55" ON "edfi"."ResourceA" ("StudentReference_DocumentId", "StudentReference_StudentUniqueId");

CREATE INDEX IF NOT EXISTS "IX_ResourceB_StudentReference_DocumentId_StudentRefe_59f7d306df" ON "edfi"."ResourceB" ("StudentReference_DocumentId", "StudentReference_StudentUniqueId");

CREATE INDEX IF NOT EXISTS "IX_StudentSchoolAssociation_SchoolReference_Document_73243293fa" ON "edfi"."StudentSchoolAssociation" ("SchoolReference_DocumentId", "SchoolReference_SchoolId");

CREATE OR REPLACE VIEW "edfi"."EducationOrganization_View" AS
SELECT "DocumentId" AS "DocumentId", "SchoolId" AS "EducationOrganizationId", 'Ed-Fi:School'::varchar(256) AS "Discriminator"
FROM "edfi"."School"
;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_KeyUnifiedResource_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."KeyUnifiedResourceId" IS DISTINCT FROM NEW."KeyUnifiedResourceId" OR OLD."ResourceAReference_ResourceAId" IS DISTINCT FROM NEW."ResourceAReference_ResourceAId" OR OLD."StudentUniqueId_Unified" IS DISTINCT FROM NEW."StudentUniqueId_Unified" OR OLD."ResourceBReference_ResourceBId" IS DISTINCT FROM NEW."ResourceBReference_ResourceBId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 2;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiKeyUnifiedResource' || '$.keyUnifiedResourceId=' || NEW."KeyUnifiedResourceId"::text || '#' || '$.resourceAReference.resourceAId=' || NEW."ResourceAReference_ResourceAId"::text || '#' || '$.resourceAReference.studentUniqueId=' || NEW."ResourceAReference_StudentUniqueId"::text || '#' || '$.resourceBReference.resourceBId=' || NEW."ResourceBReference_ResourceBId"::text || '#' || '$.resourceBReference.studentUniqueId=' || NEW."ResourceBReference_StudentUniqueId"::text), NEW."DocumentId", 2);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_KeyUnifiedResource_ReferentialIdentity" ON "edfi"."KeyUnifiedResource";
CREATE TRIGGER "TR_KeyUnifiedResource_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."KeyUnifiedResource"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_KeyUnifiedResource_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_KeyUnifiedResource_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."StudentUniqueId_Unified" IS DISTINCT FROM NEW."StudentUniqueId_Unified" OR OLD."ResourceAReference_DocumentId" IS DISTINCT FROM NEW."ResourceAReference_DocumentId" OR OLD."ResourceAReference_ResourceAId" IS DISTINCT FROM NEW."ResourceAReference_ResourceAId" OR OLD."ResourceBReference_DocumentId" IS DISTINCT FROM NEW."ResourceBReference_DocumentId" OR OLD."ResourceBReference_ResourceBId" IS DISTINCT FROM NEW."ResourceBReference_ResourceBId" OR OLD."KeyUnifiedResourceId" IS DISTINCT FROM NEW."KeyUnifiedResourceId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."KeyUnifiedResourceId" IS DISTINCT FROM NEW."KeyUnifiedResourceId" OR OLD."ResourceAReference_ResourceAId" IS DISTINCT FROM NEW."ResourceAReference_ResourceAId" OR OLD."StudentUniqueId_Unified" IS DISTINCT FROM NEW."StudentUniqueId_Unified" OR OLD."ResourceBReference_ResourceBId" IS DISTINCT FROM NEW."ResourceBReference_ResourceBId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_KeyUnifiedResource_Stamp" ON "edfi"."KeyUnifiedResource";
CREATE TRIGGER "TR_KeyUnifiedResource_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."KeyUnifiedResource"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_KeyUnifiedResource_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_ResourceA_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."ResourceAId" IS DISTINCT FROM NEW."ResourceAId" OR OLD."StudentReference_StudentUniqueId" IS DISTINCT FROM NEW."StudentReference_StudentUniqueId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 3;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiResourceA' || '$.resourceAId=' || NEW."ResourceAId"::text || '#' || '$.studentReference.studentUniqueId=' || NEW."StudentReference_StudentUniqueId"::text), NEW."DocumentId", 3);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ResourceA_ReferentialIdentity" ON "edfi"."ResourceA";
CREATE TRIGGER "TR_ResourceA_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."ResourceA"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_ResourceA_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_ResourceA_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."StudentReference_DocumentId" IS DISTINCT FROM NEW."StudentReference_DocumentId" OR OLD."StudentReference_StudentUniqueId" IS DISTINCT FROM NEW."StudentReference_StudentUniqueId" OR OLD."ResourceAId" IS DISTINCT FROM NEW."ResourceAId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."ResourceAId" IS DISTINCT FROM NEW."ResourceAId" OR OLD."StudentReference_StudentUniqueId" IS DISTINCT FROM NEW."StudentReference_StudentUniqueId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ResourceA_Stamp" ON "edfi"."ResourceA";
CREATE TRIGGER "TR_ResourceA_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."ResourceA"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_ResourceA_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_ResourceB_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."ResourceBId" IS DISTINCT FROM NEW."ResourceBId" OR OLD."StudentReference_StudentUniqueId" IS DISTINCT FROM NEW."StudentReference_StudentUniqueId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 4;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiResourceB' || '$.resourceBId=' || NEW."ResourceBId"::text || '#' || '$.studentReference.studentUniqueId=' || NEW."StudentReference_StudentUniqueId"::text), NEW."DocumentId", 4);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ResourceB_ReferentialIdentity" ON "edfi"."ResourceB";
CREATE TRIGGER "TR_ResourceB_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."ResourceB"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_ResourceB_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_ResourceB_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."StudentReference_DocumentId" IS DISTINCT FROM NEW."StudentReference_DocumentId" OR OLD."StudentReference_StudentUniqueId" IS DISTINCT FROM NEW."StudentReference_StudentUniqueId" OR OLD."ResourceBId" IS DISTINCT FROM NEW."ResourceBId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."ResourceBId" IS DISTINCT FROM NEW."ResourceBId" OR OLD."StudentReference_StudentUniqueId" IS DISTINCT FROM NEW."StudentReference_StudentUniqueId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_ResourceB_Stamp" ON "edfi"."ResourceB";
CREATE TRIGGER "TR_ResourceB_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."ResourceB"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_ResourceB_Stamp"();

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
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 5;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$.schoolId=' || NEW."SchoolId"::text), NEW."DocumentId", 5);
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
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId" OR OLD."NameOfInstitution" IS DISTINCT FROM NEW."NameOfInstitution" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_Student_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 6;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiStudent' || '$.studentUniqueId=' || NEW."StudentUniqueId"::text), NEW."DocumentId", 6);
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
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."FirstName" IS DISTINCT FROM NEW."FirstName" OR OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_Student_Stamp" ON "edfi"."Student";
CREATE TRIGGER "TR_Student_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."Student"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_Student_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId" OR OLD."SchoolReference_SchoolId" IS DISTINCT FROM NEW."SchoolReference_SchoolId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 7;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiStudentSchoolAssociation' || '$.studentUniqueId=' || NEW."StudentUniqueId"::text || '#' || '$.schoolReference.schoolId=' || NEW."SchoolReference_SchoolId"::text), NEW."DocumentId", 7);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StudentSchoolAssociation_ReferentialIdentity" ON "edfi"."StudentSchoolAssociation";
CREATE TRIGGER "TR_StudentSchoolAssociation_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."StudentSchoolAssociation"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."SchoolReference_DocumentId" IS DISTINCT FROM NEW."SchoolReference_DocumentId" OR OLD."SchoolReference_SchoolId" IS DISTINCT FROM NEW."SchoolReference_SchoolId" OR OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."StudentUniqueId" IS DISTINCT FROM NEW."StudentUniqueId" OR OLD."SchoolReference_SchoolId" IS DISTINCT FROM NEW."SchoolReference_SchoolId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_StudentSchoolAssociation_Stamp" ON "edfi"."StudentSchoolAssociation";
CREATE TRIGGER "TR_StudentSchoolAssociation_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."StudentSchoolAssociation"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_StudentSchoolAssociation_Stamp"();

-- ==========================================================
-- Phase 7: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- ResourceKey seed inserts (insert-if-missing)
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (1, 'Ed-Fi', 'EducationOrganization', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (2, 'Ed-Fi', 'KeyUnifiedResource', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (3, 'Ed-Fi', 'ResourceA', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (4, 'Ed-Fi', 'ResourceB', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (5, 'Ed-Fi', 'School', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (6, 'Ed-Fi', 'Student', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;
INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (7, 'Ed-Fi', 'StudentSchoolAssociation', '5.0.0')
ON CONFLICT ("ResourceKeyId") DO NOTHING;

-- ResourceKey full-table validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_ids text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."ResourceKey";
    IF _actual_count <> 7 THEN
        RAISE EXCEPTION 'dms.ResourceKey count mismatch: expected 7, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."ResourceKey" rk
    WHERE NOT EXISTS (
        SELECT 1 FROM (VALUES
            (1::smallint, 'Ed-Fi', 'EducationOrganization', '5.0.0'),
            (2::smallint, 'Ed-Fi', 'KeyUnifiedResource', '5.0.0'),
            (3::smallint, 'Ed-Fi', 'ResourceA', '5.0.0'),
            (4::smallint, 'Ed-Fi', 'ResourceB', '5.0.0'),
            (5::smallint, 'Ed-Fi', 'School', '5.0.0'),
            (6::smallint, 'Ed-Fi', 'Student', '5.0.0'),
            (7::smallint, 'Ed-Fi', 'StudentSchoolAssociation', '5.0.0')
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
                    (2::smallint, 'Ed-Fi', 'KeyUnifiedResource', '5.0.0'),
                    (3::smallint, 'Ed-Fi', 'ResourceA', '5.0.0'),
                    (4::smallint, 'Ed-Fi', 'ResourceB', '5.0.0'),
                    (5::smallint, 'Ed-Fi', 'School', '5.0.0'),
                    (6::smallint, 'Ed-Fi', 'Student', '5.0.0'),
                    (7::smallint, 'Ed-Fi', 'StudentSchoolAssociation', '5.0.0')
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
VALUES (1, '1.0.0', '1acb653e8acf6278a49130d19b48f13cdd9cf750dca58d5e73dde85f88985169', 7, '\x32EF5794D29AE47B77649821D903B3E816550F32464099919350A9C7ADF96920'::bytea)
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
        IF _stored_count <> 7 THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeyCount mismatch: expected 7, found %', _stored_count;
        END IF;
        IF _stored_hash <> '\x32EF5794D29AE47B77649821D903B3E816550F32464099919350A9C7ADF96920'::bytea THEN
            RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored % but expected %', encode(_stored_hash, 'hex'), encode('\x32EF5794D29AE47B77649821D903B3E816550F32464099919350A9C7ADF96920'::bytea, 'hex');
        END IF;
    END IF;
END $$;

-- SchemaComponent seed inserts (insert-if-missing)
INSERT INTO "dms"."SchemaComponent" ("EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")
VALUES ('1acb653e8acf6278a49130d19b48f13cdd9cf750dca58d5e73dde85f88985169', 'ed-fi', 'Ed-Fi', '5.0.0', false)
ON CONFLICT ("EffectiveSchemaHash", "ProjectEndpointName") DO NOTHING;

-- SchemaComponent exact-match validation (count + content)
DO $$
DECLARE
    _actual_count integer;
    _mismatched_count integer;
    _mismatched_names text;
BEGIN
    SELECT COUNT(*) INTO _actual_count FROM "dms"."SchemaComponent" WHERE "EffectiveSchemaHash" = '1acb653e8acf6278a49130d19b48f13cdd9cf750dca58d5e73dde85f88985169';
    IF _actual_count <> 1 THEN
        RAISE EXCEPTION 'dms.SchemaComponent count mismatch: expected 1, found %', _actual_count;
    END IF;

    SELECT COUNT(*) INTO _mismatched_count
    FROM "dms"."SchemaComponent" sc
    WHERE sc."EffectiveSchemaHash" = '1acb653e8acf6278a49130d19b48f13cdd9cf750dca58d5e73dde85f88985169'
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
            WHERE sc."EffectiveSchemaHash" = '1acb653e8acf6278a49130d19b48f13cdd9cf750dca58d5e73dde85f88985169'
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

