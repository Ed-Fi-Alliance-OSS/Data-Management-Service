CREATE SCHEMA [edfi];

CREATE TABLE [edfi].[School] (
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

CREATE TABLE [edfi].[LocalEducationAgency] (
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_LocalEducationAgency] PRIMARY KEY ([DocumentId])
);

CREATE TABLE [edfi].[EducationOrganizationIdentity] (
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    [Discriminator] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdentity] PRIMARY KEY ([DocumentId])
);

ALTER TABLE [edfi].[School] ADD CONSTRAINT [FK_School_EducationOrganizationIdentity] FOREIGN KEY ([DocumentId]) REFERENCES [edfi].[EducationOrganizationIdentity] ([DocumentId]) ON DELETE CASCADE;

ALTER TABLE [edfi].[LocalEducationAgency] ADD CONSTRAINT [FK_LocalEducationAgency_EducationOrganizationIdentity] FOREIGN KEY ([DocumentId]) REFERENCES [edfi].[EducationOrganizationIdentity] ([DocumentId]) ON DELETE CASCADE;

CREATE OR ALTER VIEW [edfi].[EducationOrganization] AS
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(N'School' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[School]
UNION ALL
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(N'LocalEducationAgency' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[LocalEducationAgency]
;

