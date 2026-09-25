-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1437: one row per recurring job schedule. A schedule is identified by (TenantId, ScheduleType)
-- whether or not it is enabled, so its Id stays stable across disable and re-enable. The key columns use
-- a binary collation so that matching is exact and case-sensitive, as on PostgreSQL. The payload check
-- accepts a JSON object after any JSON whitespace prefix, which is what PostgreSQL's jsonb_typeof accepts.
IF OBJECT_ID('dmscs.JobSchedule', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.JobSchedule (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        TenantId BIGINT NULL,
        ScheduleType NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL,
        JobType NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL,
        PayloadVersion SMALLINT NOT NULL,
        Payload NVARCHAR(4000) NOT NULL,
        IntervalMinutes INT NOT NULL,
        Enabled BIT NOT NULL,
        NextRunAt DATETIME2 NOT NULL,
        LastEnqueuedOccurrence DATETIME2 NULL,
        LeaseOwner NVARCHAR(200) NULL,
        LeaseExpiresAt DATETIME2 NULL,
        FencingToken BIGINT NOT NULL DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_JobSchedule' AND parent_object_id = OBJECT_ID('dmscs.JobSchedule'))
BEGIN
    ALTER TABLE dmscs.JobSchedule ADD CONSTRAINT PK_JobSchedule PRIMARY KEY (Id);
END;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_JobSchedule_Tenant')
BEGIN
    ALTER TABLE dmscs.JobSchedule ADD CONSTRAINT FK_JobSchedule_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_JobSchedule_Payload_Object' AND parent_object_id = OBJECT_ID('dmscs.JobSchedule'))
BEGIN
    ALTER TABLE dmscs.JobSchedule ADD CONSTRAINT CK_JobSchedule_Payload_Object CHECK (
        ISJSON(Payload) = 1
        AND SUBSTRING(Payload, PATINDEX(N'%[^ ' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%', Payload), 1) = N'{'
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_JobSchedule_IntervalMinutes' AND parent_object_id = OBJECT_ID('dmscs.JobSchedule'))
BEGIN
    ALTER TABLE dmscs.JobSchedule ADD CONSTRAINT CK_JobSchedule_IntervalMinutes CHECK (IntervalMinutes BETWEEN 1 AND 527040);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_JobSchedule_Tenant_Type' AND object_id = OBJECT_ID('dmscs.JobSchedule'))
    CREATE UNIQUE INDEX UX_JobSchedule_Tenant_Type ON dmscs.JobSchedule (TenantId, ScheduleType) WHERE TenantId IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_JobSchedule_SingleTenant_Type' AND object_id = OBJECT_ID('dmscs.JobSchedule'))
    CREATE UNIQUE INDEX UX_JobSchedule_SingleTenant_Type ON dmscs.JobSchedule (ScheduleType) WHERE TenantId IS NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSchedule_Due' AND object_id = OBJECT_ID('dmscs.JobSchedule'))
    CREATE INDEX IX_JobSchedule_Due ON dmscs.JobSchedule (Enabled, NextRunAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSchedule_TenantId' AND object_id = OBJECT_ID('dmscs.JobSchedule'))
    CREATE INDEX IX_JobSchedule_TenantId ON dmscs.JobSchedule (TenantId);
