IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'auth')
    EXEC('CREATE SCHEMA [auth]');

IF OBJECT_ID(N'edfi.StaffEducationOrganizationAssignmentAssociation', N'U') IS NULL
CREATE TABLE [edfi].[StaffEducationOrganizationAssignmentAssociation]
(
    [DocumentId] bigint NOT NULL,
    [Staff_DocumentId] bigint NOT NULL,
    [EducationOrganization_EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_StaffEducationOrganizationAssignmentAssociation] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.StaffEducationOrganizationEmploymentAssociation', N'U') IS NULL
CREATE TABLE [edfi].[StaffEducationOrganizationEmploymentAssociation]
(
    [DocumentId] bigint NOT NULL,
    [Staff_DocumentId] bigint NOT NULL,
    [EducationOrganization_EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_StaffEducationOrganizationEmploymentAssociation] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.StudentContactAssociation', N'U') IS NULL
CREATE TABLE [edfi].[StudentContactAssociation]
(
    [DocumentId] bigint NOT NULL,
    [Student_DocumentId] bigint NOT NULL,
    [Contact_DocumentId] bigint NOT NULL,
    CONSTRAINT [PK_StudentContactAssociation] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.StudentEducationOrganizationResponsibilityAssociation', N'U') IS NULL
CREATE TABLE [edfi].[StudentEducationOrganizationResponsibilityAssociation]
(
    [DocumentId] bigint NOT NULL,
    [Student_DocumentId] bigint NOT NULL,
    [EducationOrganization_EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_StudentEducationOrganizationResponsibilityAssociation] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.StudentSchoolAssociation', N'U') IS NULL
CREATE TABLE [edfi].[StudentSchoolAssociation]
(
    [DocumentId] bigint NOT NULL,
    [Student_DocumentId] bigint NOT NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_StudentSchoolAssociation] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'auth.EducationOrganizationIdToEducationOrganizationId', N'U') IS NULL
CREATE TABLE [auth].[EducationOrganizationIdToEducationOrganizationId]
(
    [SourceEducationOrganizationId] bigint NOT NULL,
    [TargetEducationOrganizationId] bigint NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdToEducationOrganizationId] PRIMARY KEY CLUSTERED ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'auth' AND t.name = N'EducationOrganizationIdToEducationOrganizationId' AND i.name = N'IX_EducationOrganizationIdToEducationOrganizationId_Target'
)
CREATE INDEX [IX_EducationOrganizationIdToEducationOrganizationId_Target] ON [auth].[EducationOrganizationIdToEducationOrganizationId] ([TargetEducationOrganizationId]) INCLUDE ([SourceEducationOrganizationId]);

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToContactDocumentId] AS
SELECT DISTINCT
    edOrg.[SourceEducationOrganizationId],
    sca.[Contact_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [edfi].[StudentSchoolAssociation] ssa ON edOrg.[TargetEducationOrganizationId] = ssa.[SchoolId]
INNER JOIN [edfi].[StudentContactAssociation] sca ON ssa.[Student_DocumentId] = sca.[Student_DocumentId]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToStaffDocumentId] AS
SELECT DISTINCT
    edOrg.[SourceEducationOrganizationId],
    seoaa.[Staff_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [edfi].[StaffEducationOrganizationAssignmentAssociation] seoaa ON edOrg.[TargetEducationOrganizationId] = seoaa.[EducationOrganization_EducationOrganizationId]
UNION
SELECT DISTINCT
    edOrg.[SourceEducationOrganizationId],
    seoea.[Staff_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [edfi].[StaffEducationOrganizationEmploymentAssociation] seoea ON edOrg.[TargetEducationOrganizationId] = seoea.[EducationOrganization_EducationOrganizationId]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToStudentDocumentId] AS
SELECT DISTINCT
    edOrg.[SourceEducationOrganizationId],
    ssa.[Student_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [edfi].[StudentSchoolAssociation] ssa ON edOrg.[TargetEducationOrganizationId] = ssa.[SchoolId]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToStudentDocumentIdThroughResponsibility] AS
SELECT DISTINCT
    edOrg.[SourceEducationOrganizationId],
    seora.[Student_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [edfi].[StudentEducationOrganizationResponsibilityAssociation] seora ON edOrg.[TargetEducationOrganizationId] = seora.[EducationOrganization_EducationOrganizationId]
;

