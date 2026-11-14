-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1439 step 1.3d: storage only; no managed records or handlers are activated.
-- The exact canonical endpoint JSON is written by the repository serializer. Its digest is
-- a bounded, conservative reservation key, never identity proof: admission must compare every
-- descriptor field against the operation-bound snapshot. No endpoint field is truncated.
-- Pin immutability, legal transitions and same-tenant link verification belong to the model
-- and fenced repository transactions. Job IDs deliberately have no FK: retention removes jobs.

-- Pin string bounds derive from existing validators and the 16 MiB manifest envelope;
-- see catalog-name-uniqueness-rollout.md. Large Unicode values are bounded by UTF-8 bytes.
-- Replay builds an empty sibling from this one canonical DDL definition. It never repairs
-- an existing table or index by name; incompatible objects require explicit operator repair.

BEGIN;
DO $migration$
DECLARE
    definition constant text := $definition$
CREATE TABLE dmscs."DataStoreManage" (
    "Id" INT GENERATED ALWAYS AS IDENTITY,
    "TenantId" BIGINT NULL,
    "Name" VARCHAR(46) NOT NULL,
    "NameComparisonKey" TEXT COLLATE "C" GENERATED ALWAYS AS (
        translate(
            btrim(
                "Name" COLLATE "C",
                chr(9) || chr(10) || chr(11) || chr(12) || chr(13) || chr(32)
                || chr(133) || chr(160) || chr(5760)
                || chr(8192) || chr(8193) || chr(8194) || chr(8195) || chr(8196)
                || chr(8197) || chr(8198) || chr(8199) || chr(8200) || chr(8201) || chr(8202)
                || chr(8232) || chr(8233) || chr(8239) || chr(8287) || chr(12288)
            ),
            'ABCDEFGHIJKLMNOPQRSTUVWXYZ',
            'abcdefghijklmnopqrstuvwxyz'
        )
    ) STORED,
    "Template" VARCHAR(16) COLLATE "C" NOT NULL,
    "Status" VARCHAR(32) COLLATE "C" NOT NULL,
    "DataStoreId" INT NULL,
    "HistoricalDataStoreId" INT NULL,
    "HistoricalDataStoreName" VARCHAR(46) NULL,
    "TargetDatabaseName" VARCHAR(63) COLLATE "C" NOT NULL,
    "TargetDatabaseNameKey" VARCHAR(63) COLLATE "C" GENERATED ALWAYS AS
        (translate("TargetDatabaseName", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')) STORED,
    "ExpectedSourceIdentity" UUID NOT NULL,
    "EndpointDescriptor" TEXT NOT NULL,
    "EndpointDigest" BYTEA GENERATED ALWAYS AS (public.digest("EndpointDescriptor", 'sha256')) STORED,
    "CreateJobId" VARCHAR(150) NOT NULL,
    "DeleteJobId" VARCHAR(150) NULL,
    "LastAttempt" INT NOT NULL DEFAULT 0,
    "LastFailureCategory" VARCHAR(64) COLLATE "C" NULL,
    "LastFailureProvisioningCode" VARCHAR(64) COLLATE "C" NULL,
    "LastFailureJobCode" VARCHAR(64) COLLATE "C" NULL,
    "CreatedAt" TIMESTAMP NOT NULL,
    "UpdatedAt" TIMESTAMP NOT NULL,
    "DeletedAt" TIMESTAMP NULL,
    "LastRefreshed" TIMESTAMP NOT NULL,
    "LastModifiedDate" TIMESTAMP NOT NULL,
    "PinnedContractVersion" INT NULL,
    "PinnedProvider" VARCHAR(10) NULL,
    "PinnedPackageId" VARCHAR(100) NULL,
    "PinnedPackageVersion" TEXT NULL,
    "PinnedPackageSha256" VARCHAR(64) NULL,
    "PinnedArtifactSha256" VARCHAR(64) NULL,
    "PinnedTemplateKind" VARCHAR(9) NULL,
    "PinnedDataStandardVersion" VARCHAR(100) NULL,
    "PinnedEffectiveSchemaHash" VARCHAR(64) NULL,
    "PinnedInventorySha256" VARCHAR(64) NULL,
    "PinnedApiSchemaFormatVersion" TEXT NULL,
    "PinnedRelationalMappingVersion" TEXT NULL,
    CONSTRAINT "PK_DataStoreManage" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_DataStoreManage_Tenant" FOREIGN KEY ("TenantId") REFERENCES dmscs."Tenant" ("Id") ON DELETE NO ACTION,
    CONSTRAINT "FK_DataStoreManage_DataStore" FOREIGN KEY ("DataStoreId") REFERENCES dmscs."DataStore" ("Id") ON DELETE NO ACTION,
    CONSTRAINT "CK_DataStoreManage_Name" CHECK (length("NameComparisonKey") > 0),
    CONSTRAINT "CK_DataStoreManage_Template" CHECK ("Template" IN ('Minimal', 'Sample')),
    CONSTRAINT "CK_DataStoreManage_Status" CHECK ("Status" IN ('PendingCreate', 'CreateInProgress', 'CreateFailed', 'CreateError', 'Created',
        'PendingDelete', 'DeleteInProgress', 'DeleteFailed', 'DeleteError', 'Deleted')),
    CONSTRAINT "CK_DataStoreManage_TargetDatabaseName" CHECK ("TargetDatabaseName" ~ '^[A-Za-z][A-Za-z0-9_]{0,62}$'),
    CONSTRAINT "CK_DataStoreManage_ExpectedSourceIdentity" CHECK ("ExpectedSourceIdentity" <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT "CK_DataStoreManage_EndpointDescriptor" CHECK (
        COALESCE(jsonb_typeof("EndpointDescriptor"::jsonb) = 'array'
            AND "EndpointDescriptor"::jsonb -> 0 = '1'::jsonb
            AND ("EndpointDescriptor"::jsonb ->> 1) COLLATE "C" IN ('postgresql', 'mssql'), FALSE)),
    CONSTRAINT "CK_DataStoreManage_LastAttempt" CHECK ("LastAttempt" >= 0),
    CONSTRAINT "CK_DataStoreManage_LastFailure" CHECK (
        ("LastFailureCategory" IS NULL AND "LastFailureProvisioningCode" IS NULL AND "LastFailureJobCode" IS NULL)
        OR ("LastFailureCategory" IS NOT NULL AND "LastFailureCategory" IN ('InvalidRequest', 'NotReady', 'Transient', 'Permanent', 'OperatorInspectionRequired')
            AND (("LastFailureProvisioningCode" IS NOT NULL AND length("LastFailureProvisioningCode") > 0 AND "LastFailureJobCode" IS NULL)
                OR ("LastFailureProvisioningCode" IS NULL AND "LastFailureJobCode" IS NOT NULL AND length("LastFailureJobCode") > 0)))),
    CONSTRAINT "CK_DataStoreManage_JobIds" CHECK (length("CreateJobId") > 0 AND ("DeleteJobId" IS NULL OR length("DeleteJobId") > 0)),
    CONSTRAINT "CK_DataStoreManage_ArtifactPinBounds" CHECK (
        ("PinnedPackageVersion" IS NULL OR octet_length("PinnedPackageVersion") <= 16777216)
        AND ("PinnedApiSchemaFormatVersion" IS NULL OR octet_length("PinnedApiSchemaFormatVersion") <= 16777216)
        AND ("PinnedRelationalMappingVersion" IS NULL OR octet_length("PinnedRelationalMappingVersion") <= 16777216)),
    CONSTRAINT "CK_DataStoreManage_ArtifactPin" CHECK (
        ("PinnedContractVersion" IS NULL AND "PinnedProvider" IS NULL
            AND "PinnedPackageId" IS NULL AND "PinnedPackageVersion" IS NULL
            AND "PinnedPackageSha256" IS NULL AND "PinnedArtifactSha256" IS NULL
            AND "PinnedTemplateKind" IS NULL AND "PinnedDataStandardVersion" IS NULL
            AND "PinnedEffectiveSchemaHash" IS NULL AND "PinnedInventorySha256" IS NULL
            AND "PinnedApiSchemaFormatVersion" IS NULL AND "PinnedRelationalMappingVersion" IS NULL)
        OR ("PinnedContractVersion" IS NOT NULL AND "PinnedProvider" IS NOT NULL
            AND "PinnedPackageId" IS NOT NULL AND "PinnedPackageVersion" IS NOT NULL
            AND "PinnedPackageSha256" IS NOT NULL AND "PinnedArtifactSha256" IS NOT NULL
            AND "PinnedTemplateKind" IS NOT NULL AND "PinnedDataStandardVersion" IS NOT NULL
            AND "PinnedEffectiveSchemaHash" IS NOT NULL AND "PinnedInventorySha256" IS NOT NULL
            AND "PinnedApiSchemaFormatVersion" IS NOT NULL AND "PinnedRelationalMappingVersion" IS NOT NULL))
);

CREATE UNIQUE INDEX "UX_DataStoreManage_TenantId_Name" ON dmscs."DataStoreManage" ("TenantId", "NameComparisonKey") NULLS NOT DISTINCT WHERE "Status" <> 'Deleted';
CREATE UNIQUE INDEX "UX_DataStoreManage_ActiveTarget" ON dmscs."DataStoreManage" ("EndpointDigest", "TargetDatabaseNameKey") WHERE "Status" <> 'Deleted';
CREATE UNIQUE INDEX "UX_DataStoreManage_DataStoreId" ON dmscs."DataStoreManage" ("DataStoreId") WHERE "DataStoreId" IS NOT NULL;
CREATE INDEX "IX_DataStoreManage_TenantId" ON dmscs."DataStoreManage" ("TenantId");
$definition$;
    actual_table oid;
    expected_table oid;
    incompatible boolean;
BEGIN
    IF to_regclass('dmscs."DataStoreManage_0041_Expected"') IS NOT NULL THEN
        RAISE EXCEPTION 'DataStoreManage replay is incompatible with reserved DataStoreManage_0041_Expected object; inspect and repair explicitly before retrying.';
    END IF;
    actual_table := to_regclass('dmscs."DataStoreManage"');
    IF actual_table IS NULL THEN
        EXECUTE definition;
        RETURN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = actual_table AND relkind = 'r') THEN
        RAISE EXCEPTION 'DataStoreManage object is incompatible; inspect and repair explicitly before retrying.';
    END IF;

    LOCK TABLE dmscs."DataStoreManage" IN ACCESS EXCLUSIVE MODE;
    -- Durability and inheritance are table semantics, not column/index properties.
    -- Recheck under the lock so concurrent DDL cannot change the admitted table shape.
    IF NOT EXISTS (
        SELECT 1 FROM pg_class
        WHERE oid = actual_table AND relkind = 'r' AND relpersistence = 'p' AND NOT relispartition
    ) OR EXISTS (
        SELECT 1 FROM pg_inherits WHERE inhrelid = actual_table OR inhparent = actual_table
    ) THEN
        RAISE EXCEPTION 'DataStoreManage table persistence or inheritance is incompatible; inspect and repair explicitly before retrying.';
    END IF;
    EXECUTE replace(definition, 'DataStoreManage', 'DataStoreManage_0041_Expected');
    expected_table := 'dmscs."DataStoreManage_0041_Expected"'::regclass;

    -- Compare server-parsed column definitions, not a separate hand-maintained type matrix.
    -- Identity configuration is compared; consumed sequence values are deliberately excluded.
    WITH columns AS (
        SELECT a.attrelid, jsonb_build_array(
            a.attnum, a.attname, a.atttypid, a.atttypmod, a.attnotnull, a.attcollation,
            a.attidentity, a.attgenerated, pg_get_expr(d.adbin, d.adrelid),
            seq.seqtypid, seq.seqstart, seq.seqincrement, seq.seqmin, seq.seqmax,
            seq.seqcache, seq.seqcycle) AS shape
        FROM pg_attribute a
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        LEFT JOIN pg_sequence seq ON seq.seqrelid = pg_get_serial_sequence(a.attrelid::regclass::text, a.attname)::regclass
        WHERE a.attrelid IN (actual_table, expected_table) AND a.attnum > 0 AND NOT a.attisdropped
    )
    SELECT EXISTS (
        (SELECT shape FROM columns WHERE attrelid = actual_table
         EXCEPT SELECT shape FROM columns WHERE attrelid = expected_table)
        UNION ALL
        (SELECT shape FROM columns WHERE attrelid = expected_table
         EXCEPT SELECT shape FROM columns WHERE attrelid = actual_table)
    ) INTO incompatible;
    IF incompatible THEN
        RAISE EXCEPTION 'DataStoreManage table definition is incompatible; inspect and repair explicitly before retrying.';
    END IF;

    -- Identifier normalization is limited to expected object names. Expressions and literal
    -- values are compared unchanged; JSONB equality is independent of database collation.
    WITH constraints AS (
        SELECT c.conrelid,
            CASE WHEN c.conrelid = expected_table
                THEN replace(c.conname, 'DataStoreManage_0041_Expected', 'DataStoreManage')
                ELSE c.conname END AS name,
            jsonb_build_array(c.contype, c.condeferrable, c.condeferred, c.convalidated,
                c.connoinherit, COALESCE((to_jsonb(c)->>'conenforced')::boolean, true),
                pg_get_constraintdef(c.oid, true)) AS shape
        FROM pg_constraint c
        WHERE c.conrelid IN (actual_table, expected_table) AND c.contype <> 'n'
    )
    SELECT EXISTS (
        SELECT 1 FROM constraints e
        LEFT JOIN constraints a ON a.conrelid = actual_table AND a.name COLLATE "C" = e.name COLLATE "C"
        WHERE e.conrelid = expected_table AND (a.name IS NULL OR a.shape IS DISTINCT FROM e.shape)
    ) INTO incompatible;
    IF incompatible THEN
        RAISE EXCEPTION 'DataStoreManage approved constraint is missing or incompatible; inspect and repair explicitly before retrying.';
    END IF;

    WITH indexes AS (
        SELECT i.indrelid,
            CASE WHEN i.indrelid = expected_table
                THEN replace(c.relname, 'DataStoreManage_0041_Expected', 'DataStoreManage')
                ELSE c.relname END AS name,
            jsonb_build_array(c.relam, i.indisunique, i.indisprimary, i.indisexclusion,
                i.indimmediate, i.indisvalid, i.indisready, i.indislive, i.indnullsnotdistinct,
                i.indnatts, i.indnkeyatts, i.indkey::text, i.indcollation::text,
                i.indclass::text, i.indoption::text,
                pg_get_expr(i.indexprs, i.indrelid), pg_get_expr(i.indpred, i.indrelid)) AS shape
        FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
        WHERE i.indrelid IN (actual_table, expected_table)
    )
    SELECT EXISTS (
        SELECT 1 FROM indexes e
        LEFT JOIN indexes a ON a.indrelid = actual_table AND a.name COLLATE "C" = e.name COLLATE "C"
        WHERE e.indrelid = expected_table AND (a.name IS NULL OR a.shape IS DISTINCT FROM e.shape)
    ) INTO incompatible;
    IF incompatible THEN
        RAISE EXCEPTION 'DataStoreManage approved index is missing or incompatible; inspect and repair explicitly before retrying.';
    END IF;

    DROP TABLE dmscs."DataStoreManage_0041_Expected";
END $migration$;
COMMIT;
