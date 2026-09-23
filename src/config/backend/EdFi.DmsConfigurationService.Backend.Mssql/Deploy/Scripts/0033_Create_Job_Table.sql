-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1437: durable background jobs. Id is the FIFO key; JobId is the public identifier. Every active row
-- carries NextAttemptAt, its eligibility time, so a claim is an ordered walk of IX_Job_Claim. A scheduled
-- occurrence is identified by (SourceScheduleId, ScheduledOccurrence); manual jobs leave both NULL.
-- JobId, JobType, and Status use a binary collation so that matching, the status check, and the filtered
-- index predicates are exact and case-sensitive, as on PostgreSQL.
IF OBJECT_ID('dmscs.Job', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.Job (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        JobId NVARCHAR(150) COLLATE Latin1_General_BIN2 NOT NULL,
        TenantId BIGINT NULL,
        JobType NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL,
        PayloadVersion SMALLINT NOT NULL,
        Payload NVARCHAR(4000) NOT NULL,
        SourceScheduleId BIGINT NULL,
        ScheduledOccurrence DATETIME2 NULL,
        Status NVARCHAR(20) COLLATE Latin1_General_BIN2 NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        FinishedAt DATETIME2 NULL,
        NextAttemptAt DATETIME2 NULL,
        LeaseExpiresAt DATETIME2 NULL,
        ErrorMessage NVARCHAR(1000) NULL,
        AttemptCount INT NOT NULL DEFAULT 0,
        LeaseOwner NVARCHAR(200) NULL,
        FencingToken BIGINT NOT NULL DEFAULT 0,
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_Job' AND parent_object_id = OBJECT_ID('dmscs.Job'))
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT PK_Job PRIMARY KEY (Id);
END;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UX_Job_JobId' AND parent_object_id = OBJECT_ID('dmscs.Job'))
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT UX_Job_JobId UNIQUE (JobId);
END;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Job_Tenant')
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT FK_Job_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Job_JobSchedule')
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT FK_Job_JobSchedule FOREIGN KEY (SourceScheduleId) REFERENCES dmscs.JobSchedule(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Job_Payload_Object' AND parent_object_id = OBJECT_ID('dmscs.Job'))
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT CK_Job_Payload_Object CHECK (
        ISJSON(Payload) = 1
        AND SUBSTRING(Payload, PATINDEX(N'%[^ ' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%', Payload), 1) = N'{'
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Job_Occurrence_Pairing' AND parent_object_id = OBJECT_ID('dmscs.Job'))
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT CK_Job_Occurrence_Pairing CHECK (
        (SourceScheduleId IS NULL AND ScheduledOccurrence IS NULL)
        OR (SourceScheduleId IS NOT NULL AND ScheduledOccurrence IS NOT NULL)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Job_Status' AND parent_object_id = OBJECT_ID('dmscs.Job'))
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT CK_Job_Status CHECK (Status IN (N'Pending', N'InProgress', N'Completed', N'Error'));
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Job_AttemptCount' AND parent_object_id = OBJECT_ID('dmscs.Job'))
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT CK_Job_AttemptCount CHECK (AttemptCount >= 0);
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Job_NextAttemptAt_Active' AND parent_object_id = OBJECT_ID('dmscs.Job'))
BEGIN
    ALTER TABLE dmscs.Job ADD CONSTRAINT CK_Job_NextAttemptAt_Active CHECK (Status IN (N'Completed', N'Error') OR NextAttemptAt IS NOT NULL);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Job_SourceScheduleId_ScheduledOccurrence' AND object_id = OBJECT_ID('dmscs.Job'))
    CREATE UNIQUE INDEX UX_Job_SourceScheduleId_ScheduledOccurrence ON dmscs.Job (SourceScheduleId, ScheduledOccurrence)
        WHERE SourceScheduleId IS NOT NULL AND ScheduledOccurrence IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Job_Claim' AND object_id = OBJECT_ID('dmscs.Job'))
    CREATE INDEX IX_Job_Claim ON dmscs.Job (NextAttemptAt, Id) INCLUDE (Status, LeaseExpiresAt, AttemptCount)
        WHERE Status IN (N'Pending', N'InProgress');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Job_Retention' AND object_id = OBJECT_ID('dmscs.Job'))
    CREATE INDEX IX_Job_Retention ON dmscs.Job (Status, FinishedAt) WHERE Status IN (N'Completed', N'Error');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Job_TenantId' AND object_id = OBJECT_ID('dmscs.Job'))
    CREATE INDEX IX_Job_TenantId ON dmscs.Job (TenantId);
