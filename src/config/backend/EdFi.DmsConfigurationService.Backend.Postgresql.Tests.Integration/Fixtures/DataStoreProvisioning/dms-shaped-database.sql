-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- A hand-built DMS-shaped PostgreSQL database for the DMS-1438 Step 3.1 integration tests: the dms
-- singleton tables with the generated DDL's column shapes, one resource project (edfi) with its
-- tracked_changes companion, the SECURITY DEFINER enqueue function owned by edfi_dms_enqueue_owner with
-- EXECUTE revoked from PUBLIC, and exactly the grants the core DDL emitter makes to that role. The role
-- must exist before this script runs (the test helper ensures it). Everything else is owned by the
-- connected administrator, so the delivered ownership and privilege gates pass on this database and every
-- drift the tests inject is the only violation. The committed canonical inventory and hash beside this
-- file were produced from it by the delivered PowerShell module (see README.md).

CREATE SCHEMA "dms";
CREATE SCHEMA "edfi";
CREATE SCHEMA "tracked_changes_edfi";

CREATE SEQUENCE "dms"."ChangeVersionSequence" START WITH 1;

CREATE TABLE "dms"."DataStoreIdentity"
(
    "DataStoreIdentitySingletonId" smallint NOT NULL,
    "SourceIdentity" uuid NOT NULL,
    CONSTRAINT "PK_DataStoreIdentity" PRIMARY KEY ("DataStoreIdentitySingletonId"),
    CONSTRAINT "CK_DataStoreIdentity_Singleton" CHECK ("DataStoreIdentitySingletonId" = 1)
);

CREATE TABLE "dms"."EffectiveSchema"
(
    "EffectiveSchemaSingletonId" smallint NOT NULL,
    "ApiSchemaFormatVersion" varchar(64) NOT NULL,
    "EffectiveSchemaHash" varchar(64) NOT NULL,
    "ResourceKeyCount" smallint NOT NULL,
    "ResourceKeySeedHash" bytea NOT NULL,
    "AppliedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_EffectiveSchema" PRIMARY KEY ("EffectiveSchemaSingletonId"),
    CONSTRAINT "CK_EffectiveSchema_Singleton" CHECK ("EffectiveSchemaSingletonId" = 1),
    CONSTRAINT "CK_EffectiveSchema_ApiSchemaFormatVersion_NotBlank" CHECK (btrim("ApiSchemaFormatVersion") <> ''),
    CONSTRAINT "CK_EffectiveSchema_ResourceKeySeedHash_Length" CHECK (octet_length("ResourceKeySeedHash") = 32),
    CONSTRAINT "UX_EffectiveSchema_EffectiveSchemaHash" UNIQUE ("EffectiveSchemaHash")
);

CREATE TABLE "dms"."ResourceKey"
(
    "ResourceKeyId" smallint NOT NULL,
    "ProjectName" varchar(256) NOT NULL,
    "ResourceName" varchar(256) NOT NULL,
    "ResourceVersion" varchar(32) NOT NULL,
    CONSTRAINT "PK_ResourceKey" PRIMARY KEY ("ResourceKeyId")
);

CREATE TABLE "dms"."Document"
(
    "DocumentId" bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    "DocumentUuid" uuid NOT NULL,
    "ResourceKeyId" smallint NOT NULL,
    "ContentVersion" bigint NOT NULL DEFAULT nextval('"dms"."ChangeVersionSequence"'),
    CONSTRAINT "PK_Document" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "FK_Document_ResourceKey" FOREIGN KEY ("ResourceKeyId") REFERENCES "dms"."ResourceKey" ("ResourceKeyId")
);

CREATE INDEX "IX_Document_ResourceKeyId" ON "dms"."Document" ("ResourceKeyId");

CREATE TABLE "dms"."DocumentCache"
(
    "DocumentId" bigint NOT NULL,
    "DocumentUuid" uuid NOT NULL,
    "DocumentJson" jsonb NOT NULL,
    CONSTRAINT "PK_DocumentCache" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "CK_DocumentCache_JsonObject" CHECK (jsonb_typeof("DocumentJson") = 'object')
);

CREATE TABLE "dms"."DocumentCacheState"
(
    "StateId" smallint NOT NULL,
    "ProjectionLifecycleState" varchar(16) NOT NULL,
    "CacheAheadRecoveryRequired" boolean NOT NULL,
    CONSTRAINT "PK_DocumentCacheState" PRIMARY KEY ("StateId"),
    CONSTRAINT "CK_DocumentCacheState_Singleton" CHECK ("StateId" = 1)
);

CREATE TABLE "dms"."DocumentProjectionWork"
(
    "DocumentId" bigint NOT NULL,
    "RequiredContentVersion" bigint NOT NULL,
    "FirstEnqueuedAt" timestamp with time zone NOT NULL,
    "LastEnqueuedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_DocumentProjectionWork" PRIMARY KEY ("DocumentId")
);

CREATE FUNCTION "dms"."GetMaxChangeVersion"() RETURNS bigint AS
$GetMaxChangeVersion$
DECLARE
    result bigint;
BEGIN
    SELECT last_value FROM "dms"."ChangeVersionSequence" INTO result;
    RETURN result;
END
$GetMaxChangeVersion$ LANGUAGE plpgsql;

CREATE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $func$
BEGIN
    RETURN NULL;
END;
$func$;
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() FROM PUBLIC;
ALTER FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() OWNER TO "edfi_dms_enqueue_owner";

CREATE TRIGGER "TR_Document_EnqueueProjectionInsert"
    AFTER INSERT ON "dms"."Document"
    REFERENCING NEW TABLE AS new_rows
    FOR EACH STATEMENT
    EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"();

GRANT USAGE ON SCHEMA "dms" TO "edfi_dms_enqueue_owner";
GRANT SELECT ON TABLE "dms"."DocumentCacheState" TO "edfi_dms_enqueue_owner";
GRANT SELECT, INSERT, UPDATE ON TABLE "dms"."DocumentProjectionWork" TO "edfi_dms_enqueue_owner";

CREATE TYPE "edfi"."ReferenceKind" AS ENUM ('Primary', 'Secondary');

CREATE TABLE "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "SchoolId" bigint NOT NULL,
    "NameOfInstitution" varchar(75) NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId"),
    CONSTRAINT "UX_School_SchoolId" UNIQUE ("SchoolId")
);

CREATE VIEW "edfi"."SchoolSummary" AS
    SELECT "SchoolId", "NameOfInstitution" FROM "edfi"."School";

CREATE TABLE "tracked_changes_edfi"."School"
(
    "ChangeVersion" bigint NOT NULL,
    "SchoolId" bigint NOT NULL,
    CONSTRAINT "PK_tracked_changes_edfi_School" PRIMARY KEY ("ChangeVersion")
);

INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity")
VALUES (1, '6f1c9a52-2d3e-4b7a-9c8d-1e2f3a4b5c6d');

INSERT INTO "dms"."EffectiveSchema" ("EffectiveSchemaSingletonId", "ApiSchemaFormatVersion", "EffectiveSchemaHash", "ResourceKeyCount", "ResourceKeySeedHash")
VALUES (1, '1.0.0', 'abababababababababababababababababababababababababababababababab', 42, decode('0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20', 'hex'));

INSERT INTO "dms"."DocumentCacheState" ("StateId", "ProjectionLifecycleState", "CacheAheadRecoveryRequired")
VALUES (1, 'Tracking', false);

INSERT INTO "dms"."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
VALUES (1, 'Ed-Fi', 'School', '5.2.0'), (2, 'Ed-Fi', 'GradeLevelDescriptor', '5.2.0'), (3, 'Ed-Fi', 'SchoolYearType', '5.2.0');

-- Only descriptor and school-year documents: the Populated predicate is false until a test adds a School document.
INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
VALUES ('0f3b2c1d-4e5f-4a6b-8c7d-9e0f1a2b3c4d', 2), ('1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d', 3);
