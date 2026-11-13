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

BEGIN;

-- A committed migration whose journal write was lost can be replayed.
CREATE TABLE IF NOT EXISTS dmscs."DataStoreManage" (
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
    "PinnedProvider" TEXT NULL,
    "PinnedPackageId" TEXT NULL,
    "PinnedPackageVersion" TEXT NULL,
    "PinnedPackageSha256" TEXT NULL,
    "PinnedArtifactSha256" TEXT NULL,
    "PinnedTemplateKind" TEXT NULL,
    "PinnedDataStandardVersion" TEXT NULL,
    "PinnedEffectiveSchemaHash" TEXT NULL,
    "PinnedInventorySha256" TEXT NULL,
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

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DataStoreManage_TenantId_Name" ON dmscs."DataStoreManage" ("TenantId", "NameComparisonKey") NULLS NOT DISTINCT WHERE "Status" <> 'Deleted';

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DataStoreManage_ActiveTarget" ON dmscs."DataStoreManage" ("EndpointDigest", "TargetDatabaseNameKey") WHERE "Status" <> 'Deleted';

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DataStoreManage_DataStoreId" ON dmscs."DataStoreManage" ("DataStoreId") WHERE "DataStoreId" IS NOT NULL;

CREATE INDEX IF NOT EXISTS "IX_DataStoreManage_TenantId" ON dmscs."DataStoreManage" ("TenantId");

COMMIT;
