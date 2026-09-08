IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'auth')
    EXEC('CREATE SCHEMA [auth]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'tracked_changes_edfi')
    EXEC('CREATE SCHEMA [tracked_changes_edfi]');

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
    [SchoolId_Unified] int NOT NULL,
    CONSTRAINT [PK_StudentSchoolAssociation] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'auth.EducationOrganizationIdToEducationOrganizationId', N'U') IS NULL
CREATE TABLE [auth].[EducationOrganizationIdToEducationOrganizationId]
(
    [SourceEducationOrganizationId] bigint NOT NULL,
    [TargetEducationOrganizationId] bigint NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdToEducationOrganizationId] PRIMARY KEY CLUSTERED ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
);

IF OBJECT_ID(N'tracked_changes_edfi.StaffEducationOrganizationAssignmentAssociation', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[StaffEducationOrganizationAssignmentAssociation]
(
    [OldEducationOrganization_EducationOrganizationId] int NOT NULL,
    [NewEducationOrganization_EducationOrganizationId] int NULL,
    [OldStaff_DocumentId] bigint NOT NULL,
    [NewStaff_DocumentId] bigint NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_StaffEducationOrganizationAssignmentAssociation_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_StaffEducationOrganizationAssignmentAssociation] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.StaffEducationOrganizationEmploymentAssociation', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[StaffEducationOrganizationEmploymentAssociation]
(
    [OldEducationOrganization_EducationOrganizationId] int NOT NULL,
    [NewEducationOrganization_EducationOrganizationId] int NULL,
    [OldStaff_DocumentId] bigint NOT NULL,
    [NewStaff_DocumentId] bigint NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_StaffEducationOrganizationEmploymentAssociation_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_StaffEducationOrganizationEmploymentAssociation] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.StudentContactAssociation', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[StudentContactAssociation]
(
    [OldStudent_DocumentId] bigint NOT NULL,
    [NewStudent_DocumentId] bigint NULL,
    [OldContact_DocumentId] bigint NOT NULL,
    [NewContact_DocumentId] bigint NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_StudentContactAssociation_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_StudentContactAssociation] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.StudentEducationOrganizationResponsibilityAssociation', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[StudentEducationOrganizationResponsibilityAssociation]
(
    [OldEducationOrganization_EducationOrganizationId] int NOT NULL,
    [NewEducationOrganization_EducationOrganizationId] int NULL,
    [OldStudent_DocumentId] bigint NOT NULL,
    [NewStudent_DocumentId] bigint NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_StudentEducationOrganizationResponsibilityAssociation_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_StudentEducationOrganizationResponsibilityAssociation] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.StudentSchoolAssociation', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[StudentSchoolAssociation]
(
    [OldSchoolId_Unified] int NOT NULL,
    [NewSchoolId_Unified] int NULL,
    [OldStudent_DocumentId] bigint NOT NULL,
    [NewStudent_DocumentId] bigint NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_StudentSchoolAssociation_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_StudentSchoolAssociation] PRIMARY KEY CLUSTERED ([ChangeVersion])
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
INNER JOIN [edfi].[StudentSchoolAssociation] ssa ON edOrg.[TargetEducationOrganizationId] = ssa.[SchoolId_Unified]
INNER JOIN [edfi].[StudentContactAssociation] sca ON ssa.[Student_DocumentId] = sca.[Student_DocumentId]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToStaffDocumentId] AS
SELECT
    edOrg.[SourceEducationOrganizationId],
    seoaa.[Staff_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [edfi].[StaffEducationOrganizationAssignmentAssociation] seoaa ON edOrg.[TargetEducationOrganizationId] = seoaa.[EducationOrganization_EducationOrganizationId]
UNION
SELECT
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
INNER JOIN [edfi].[StudentSchoolAssociation] ssa ON edOrg.[TargetEducationOrganizationId] = ssa.[SchoolId_Unified]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToStudentDocumentIdThroughResponsibility] AS
SELECT DISTINCT
    edOrg.[SourceEducationOrganizationId],
    seora.[Student_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [edfi].[StudentEducationOrganizationResponsibilityAssociation] seora ON edOrg.[TargetEducationOrganizationId] = seora.[EducationOrganization_EducationOrganizationId]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToContactDocumentIdIncludingDeletes] AS
SELECT
    edOrgToContact.[SourceEducationOrganizationId],
    edOrgToContact.[Contact_DocumentId]
FROM [auth].[EducationOrganizationIdToContactDocumentId] edOrgToContact
UNION
SELECT
    edOrg.[SourceEducationOrganizationId],
    sca_tc.[OldContact_DocumentId] AS [Contact_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [edfi].[StudentSchoolAssociation] ssa ON edOrg.[TargetEducationOrganizationId] = ssa.[SchoolId_Unified]
INNER JOIN [tracked_changes_edfi].[StudentContactAssociation] sca_tc ON ssa.[Student_DocumentId] = sca_tc.[OldStudent_DocumentId]
UNION
SELECT
    edOrg.[SourceEducationOrganizationId],
    sca.[Contact_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [tracked_changes_edfi].[StudentSchoolAssociation] ssa_tc ON edOrg.[TargetEducationOrganizationId] = ssa_tc.[OldSchoolId_Unified]
INNER JOIN [edfi].[StudentContactAssociation] sca ON ssa_tc.[OldStudent_DocumentId] = sca.[Student_DocumentId]
UNION
SELECT
    edOrg.[SourceEducationOrganizationId],
    sca_tc.[OldContact_DocumentId] AS [Contact_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [tracked_changes_edfi].[StudentSchoolAssociation] ssa_tc ON edOrg.[TargetEducationOrganizationId] = ssa_tc.[OldSchoolId_Unified]
INNER JOIN [tracked_changes_edfi].[StudentContactAssociation] sca_tc ON ssa_tc.[OldStudent_DocumentId] = sca_tc.[OldStudent_DocumentId]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToStaffDocumentIdIncludingDeletes] AS
SELECT
    edOrgToStaff.[SourceEducationOrganizationId],
    edOrgToStaff.[Staff_DocumentId]
FROM [auth].[EducationOrganizationIdToStaffDocumentId] edOrgToStaff
UNION
SELECT
    edOrg.[SourceEducationOrganizationId],
    seoaa_tc.[OldStaff_DocumentId] AS [Staff_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [tracked_changes_edfi].[StaffEducationOrganizationAssignmentAssociation] seoaa_tc ON edOrg.[TargetEducationOrganizationId] = seoaa_tc.[OldEducationOrganization_EducationOrganizationId]
UNION
SELECT
    edOrg.[SourceEducationOrganizationId],
    seoea_tc.[OldStaff_DocumentId] AS [Staff_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [tracked_changes_edfi].[StaffEducationOrganizationEmploymentAssociation] seoea_tc ON edOrg.[TargetEducationOrganizationId] = seoea_tc.[OldEducationOrganization_EducationOrganizationId]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToStudentDocumentIdDeletedResponsibility] AS
SELECT
    edOrgToStudentResp.[SourceEducationOrganizationId],
    edOrgToStudentResp.[Student_DocumentId]
FROM [auth].[EducationOrganizationIdToStudentDocumentIdThroughResponsibility] edOrgToStudentResp
UNION
SELECT
    edOrg.[SourceEducationOrganizationId],
    seora_tc.[OldStudent_DocumentId] AS [Student_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [tracked_changes_edfi].[StudentEducationOrganizationResponsibilityAssociation] seora_tc ON edOrg.[TargetEducationOrganizationId] = seora_tc.[OldEducationOrganization_EducationOrganizationId]
;

GO
CREATE OR ALTER VIEW [auth].[EducationOrganizationIdToStudentDocumentIdIncludingDeletes] AS
SELECT
    edOrgToStudent.[SourceEducationOrganizationId],
    edOrgToStudent.[Student_DocumentId]
FROM [auth].[EducationOrganizationIdToStudentDocumentId] edOrgToStudent
UNION
SELECT
    edOrg.[SourceEducationOrganizationId],
    ssa_tc.[OldStudent_DocumentId] AS [Student_DocumentId]
FROM [auth].[EducationOrganizationIdToEducationOrganizationId] edOrg
INNER JOIN [tracked_changes_edfi].[StudentSchoolAssociation] ssa_tc ON edOrg.[TargetEducationOrganizationId] = ssa_tc.[OldSchoolId_Unified]
;

