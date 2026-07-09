# Change Queries

## Purpose

The Change Queries feature allows client systems to retrieve data that has changed since a specified version number. This keeps client systems in sync with the DMS without requiring them to pull the complete dataset.

Unlike Change Data Capture (CDC), this feature does **not** store the old and new values for every write. Instead, it stores only the current representation and its version number, similar to [SQL Server Change Tracking](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/track-data-changes-sql-server). This means downstream systems need special handling to maintain consistency, as described throughout this document.

Refer to the official documentation in the following links:
- [Using the Changed Record Queries](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries)
- [Changed Record Queries](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries)

## How Change Queries Currently Work in ODS

The feature introduces a global version counter using a sequence object:

```sql
CREATE SEQUENCE [changes].[ChangeVersionSequence] AS [bigint]
```

Each top-level resource table gets a `ChangeVersion` column:

```sql
CREATE TABLE [edfi].[Grade](
    -- Some columns omitted for brevity ...
    [ChangeVersion] [bigint] NOT NULL
);

ALTER TABLE [edfi].[Grade] ADD  CONSTRAINT [Grade_DF_ChangeVersion]  DEFAULT (NEXT VALUE FOR [changes].[ChangeVersionSequence]) FOR [ChangeVersion]
```

Each top-level resource table gets an accompanying `tracked_changes_<schema>.<resource>` table, where key changes and deletes get logged:

```sql
CREATE TABLE [tracked_changes_edfi].[Grade] (
  OldBeginDate [DATE] NOT NULL, 
  OldGradeTypeDescriptorId [INT] NOT NULL, 
  OldGradeTypeDescriptorNamespace [NVARCHAR](255) NOT NULL, 
  OldGradeTypeDescriptorCodeValue [NVARCHAR](50) NOT NULL, 
  OldGradingPeriodDescriptorId [INT] NOT NULL, 
  OldGradingPeriodDescriptorNamespace [NVARCHAR](255) NOT NULL, 
  OldGradingPeriodDescriptorCodeValue [NVARCHAR](50) NOT NULL, 
  OldGradingPeriodName [NVARCHAR](60) NOT NULL, 
  OldGradingPeriodSchoolYear [SMALLINT] NOT NULL, 
  OldLocalCourseCode [NVARCHAR](60) NOT NULL, 
  OldSchoolId [BIGINT] NOT NULL, 
  OldSchoolYear [SMALLINT] NOT NULL, 
  OldSectionIdentifier [NVARCHAR](255) NOT NULL, 
  OldSessionName [NVARCHAR](60) NOT NULL, 
  OldStudentUSI [INT] NOT NULL, 
  OldStudentUniqueId [NVARCHAR](32) NOT NULL, 
  
  NewBeginDate [DATE] NULL, 
  NewGradeTypeDescriptorId [INT] NULL, 
  NewGradeTypeDescriptorNamespace [NVARCHAR](255) NULL, 
  NewGradeTypeDescriptorCodeValue [NVARCHAR](50) NULL, 
  NewGradingPeriodDescriptorId [INT] NULL, 
  NewGradingPeriodDescriptorNamespace [NVARCHAR](255) NULL, 
  NewGradingPeriodDescriptorCodeValue [NVARCHAR](50) NULL, 
  NewGradingPeriodName [NVARCHAR](60) NULL, 
  NewGradingPeriodSchoolYear [SMALLINT] NULL, 
  NewLocalCourseCode [NVARCHAR](60) NULL, 
  NewSchoolId [BIGINT] NULL, 
  NewSchoolYear [SMALLINT] NULL, 
  NewSectionIdentifier [NVARCHAR](255) NULL, 
  NewSessionName [NVARCHAR](60) NULL, 
  NewStudentUSI [INT] NULL, 
  NewStudentUniqueId [NVARCHAR](32) NULL, 

  Id uniqueidentifier NOT NULL, 
  ChangeVersion bigint NOT NULL, 
  Discriminator [NVARCHAR](128) NULL, 
  CreateDate DateTime2 NOT NULL DEFAULT (getutcdate()), 
  CONSTRAINT PK_Grade PRIMARY KEY CLUSTERED (ChangeVersion)
)
```

### Change tracking

Whenever resources change through the API or through cascading identity changes, triggers update the `ChangeVersion` column with the latest sequence value. If the update modifies the identifying values, the triggers log it in the `tracked_changes` table:

```sql
CREATE TRIGGER [edfi].[edfi_Grade_TR_UpdateChangeVersion] ON [edfi].[Grade] AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE u
    SET 
        ChangeVersion = NEXT VALUE FOR [changes].[ChangeVersionSequence],
        LastModifiedDate = 
            CASE 
                WHEN i.LastModifiedDate = d.LastModifiedDate THEN GETUTCDATE()
                ELSE i.LastModifiedDate
            END
    FROM [edfi].[Grade] u
    INNER JOIN inserted i ON i.Id = u.Id
    INNER JOIN deleted d ON d.Id = u.Id;

    -- Handle key changes
    INSERT INTO tracked_changes_edfi.Grade(
        OldBeginDate, OldGradeTypeDescriptorId, OldGradeTypeDescriptorNamespace, OldGradeTypeDescriptorCodeValue, OldGradingPeriodDescriptorId, 
        OldGradingPeriodDescriptorNamespace, OldGradingPeriodDescriptorCodeValue, OldGradingPeriodName, OldGradingPeriodSchoolYear, OldLocalCourseCode, 
        OldSchoolId, OldSchoolYear, OldSectionIdentifier, OldSessionName, OldStudentUSI, OldStudentUniqueId, 

        NewBeginDate, NewGradeTypeDescriptorId, NewGradeTypeDescriptorNamespace, NewGradeTypeDescriptorCodeValue, NewGradingPeriodDescriptorId, 
        NewGradingPeriodDescriptorNamespace, NewGradingPeriodDescriptorCodeValue, NewGradingPeriodName, NewGradingPeriodSchoolYear, NewLocalCourseCode, 
        NewSchoolId, NewSchoolYear, NewSectionIdentifier, NewSessionName, NewStudentUSI, NewStudentUniqueId, 

        Id, 
        ChangeVersion)
    SELECT
        d.BeginDate, d.GradeTypeDescriptorId, dj0.Namespace, dj0.CodeValue, d.GradingPeriodDescriptorId, 
        dj1.Namespace, dj1.CodeValue, d.GradingPeriodName, d.GradingPeriodSchoolYear, d.LocalCourseCode, 
        d.SchoolId, d.SchoolYear, d.SectionIdentifier, d.SessionName, d.StudentUSI, 
        dj2.StudentUniqueId, 
        
        i.BeginDate, i.GradeTypeDescriptorId, ij0.Namespace, ij0.CodeValue, 
        i.GradingPeriodDescriptorId, ij1.Namespace, ij1.CodeValue, i.GradingPeriodName, i.GradingPeriodSchoolYear, 
        i.LocalCourseCode, i.SchoolId, i.SchoolYear, i.SectionIdentifier, i.SessionName, 
        i.StudentUSI, ij2.StudentUniqueId, 
        
        d.Id, 
        (NEXT VALUE FOR [changes].[ChangeVersionSequence])
    FROM deleted d INNER JOIN inserted i ON d.Id = i.Id
        INNER JOIN edfi.Descriptor dj0 ON d.GradeTypeDescriptorId = dj0.DescriptorId
        INNER JOIN edfi.Descriptor dj1 ON d.GradingPeriodDescriptorId = dj1.DescriptorId
        INNER JOIN edfi.Student dj2 ON d.StudentUSI = dj2.StudentUSI
        INNER JOIN edfi.Descriptor ij0 ON i.GradeTypeDescriptorId = ij0.DescriptorId
        INNER JOIN edfi.Descriptor ij1 ON i.GradingPeriodDescriptorId = ij1.DescriptorId
        INNER JOIN edfi.Student ij2 ON i.StudentUSI = ij2.StudentUSI
    WHERE
        d.BeginDate <> i.BeginDate OR d.GradeTypeDescriptorId <> i.GradeTypeDescriptorId OR d.GradingPeriodDescriptorId <> i.GradingPeriodDescriptorId OR d.GradingPeriodName <> i.GradingPeriodName OR d.GradingPeriodSchoolYear <> i.GradingPeriodSchoolYear OR d.LocalCourseCode <> i.LocalCourseCode OR d.SchoolId <> i.SchoolId OR d.SchoolYear <> i.SchoolYear OR d.SectionIdentifier <> i.SectionIdentifier OR d.SessionName <> i.SessionName OR d.StudentUSI <> i.StudentUSI;
END
```

Child tables that reference resources whose identity allows updates must also update the parent's `ChangeVersion` when a cascading identity change happens. This is handled in [0230-CreateIndirectUpdateCascadeTriggers.sql](https://github.com/Ed-Fi-Alliance-OSS/MetaEd-js/blob/main/packages/metaed-plugin-edfi-ods-changequery-sqlserver/src/generator/templates/indirectUpdateCascadeTrigger.hbs). These *indirect* triggers are only emitted for child tables that directly or indirectly reference a resource whose identity can change.

The feature introduces a `/keyChanges` endpoint for each resource and descriptor. The endpoint returns entries whose identifying values changed in the given window, backed by the corresponding `tracked_changes` table. The endpoint also supports the `minChangeVersion`, `maxChangeVersion`, `limit`, `offset`, and `totalCount` parameters:

```http
GET /data/v3/ed-fi/grades/keyChanges
```

Example response:

```json
[
  {
    "id": "62b8d4170fd64c79a59af2c7af4eaa1f",
    "changeVersion": 543,
    "oldKeyValues": {
      "gradeTypeDescriptor": "uri://ed-fi.org/GradeTypeDescriptor#Grading Period",
      "gradingPeriodDescriptor": "uri://ed-fi.org/GradingPeriodDescriptor#Fourth Six Weeks",
      "gradingPeriodName": "2021-2022 Spring Semester Exam 1",
      "schoolId": 255901001,
      "gradingPeriodSchoolYear": 2022,
      "beginDate": "2022-01-04",
      "localCourseCode": "ALG-1",
      "schoolYear": 2022,
      "sectionIdentifier": "25590100102Trad220ALG122011",
      "sessionName": "2021-2022 Spring Semester",
      "studentUniqueId": "604863"
    },
    "newKeyValues": {
      "gradeTypeDescriptor": "uri://ed-fi.org/GradeTypeDescriptor#Exam",
      "gradingPeriodDescriptor": "uri://ed-fi.org/GradingPeriodDescriptor#Fourth Six Weeks",
      "gradingPeriodName": "2021-2022 Spring Semester Exam 1",
      "schoolId": 255901001,
      "gradingPeriodSchoolYear": 2022,
      "beginDate": "2022-01-04",
      "localCourseCode": "ALG-1",
      "schoolYear": 2022,
      "sectionIdentifier": "25590100102Trad220ALG122011",
      "sessionName": "2021-2022 Spring Semester",
      "studentUniqueId": "604863"
    }
  }
]
```

The generated SQL used to fulfill the request is:

```sql
WITH ChangeWindow AS (
    SELECT DISTINCT 
      c.Id, 
      MIN(c.ChangeVersion) AS InitialChangeVersion, 
      MAX(c.ChangeVersion) AS FinalChangeVersion 
    FROM 
      tracked_changes_edfi.Grade AS c 
      INNER JOIN auth.EducationOrganizationIdToEducationOrganizationId AS rba0 ON c.OldSchoolId = rba0.TargetEducationOrganizationId 
      INNER JOIN auth.EducationOrganizationIdToStudentUSIIncludingDeletes AS rba1 ON c.OldStudentUSI = rba1.StudentUSI 
    WHERE 
      c.NewBeginDate IS NOT NULL -- Exclude tombstones
      AND (
          rba0.SourceEducationOrganizationId IN (SELECT Id FROM @p0) -- Auth check: Relationship with EdOrg
          AND rba1.SourceEducationOrganizationId IN (SELECT Id FROM @p1) -- Auth check: Relationship with People
      ) 
    GROUP BY 
      c.Id
  ) 
SELECT   
  c_old.OldBeginDate,                            c_new.NewBeginDate,
  c_old.OldGradeTypeDescriptorCodeValue,         c_new.NewGradeTypeDescriptorCodeValue,
  c_old.OldGradeTypeDescriptorNamespace,         c_new.NewGradeTypeDescriptorNamespace,
  c_old.OldGradingPeriodDescriptorCodeValue,     c_new.NewGradingPeriodDescriptorCodeValue,
  c_old.OldGradingPeriodDescriptorNamespace,     c_new.NewGradingPeriodDescriptorNamespace,
  c_old.OldGradingPeriodName,                    c_new.NewGradingPeriodName,
  c_old.OldGradingPeriodSchoolYear,              c_new.NewGradingPeriodSchoolYear,
  c_old.OldLocalCourseCode,                      c_new.NewLocalCourseCode,
  c_old.OldSchoolId,                             c_new.NewSchoolId,
  c_old.OldSchoolYear,                           c_new.NewSchoolYear,
  c_old.OldSectionIdentifier,                    c_new.NewSectionIdentifier,
  c_old.OldSessionName,                          c_new.NewSessionName,
  c_old.OldStudentUniqueId,                      c_new.NewStudentUniqueId,

  cw.Id, 
  cw.FinalChangeVersion AS ChangeVersion
FROM 
  ChangeWindow AS cw 
  INNER JOIN tracked_changes_edfi.Grade AS c_old ON cw.Id = c_old.Id AND cw.InitialChangeVersion = c_old.ChangeVersion 
  INNER JOIN tracked_changes_edfi.Grade AS c_new ON cw.Id = c_new.Id AND cw.FinalChangeVersion = c_new.ChangeVersion 
ORDER BY 
  cw.FinalChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

When a resource changes its identifying values multiple times in a given change window, such as `A -> B` and then `B -> C`, the `/keyChanges` endpoint simplifies it to `A -> C` to avoid unnecessary downstream changes. This is handled by grouping by the resource's ID in the `ChangeWindow` CTE above.

Descriptors and concrete abstract resources also get corresponding `/keyChanges` endpoints. Since their identity never changes, those endpoints always return an empty array.

### Delete tracking

Whenever resources or descriptors are deleted, triggers create a tombstone in the corresponding `tracked_changes` table. The `New*` columns are not specified, so they get `NULL` values:

```sql
CREATE TRIGGER [edfi].[edfi_Grade_TR_DeleteTracking] ON [edfi].[Grade] AFTER DELETE AS
BEGIN
    IF @@rowcount = 0 
        RETURN

    SET NOCOUNT ON

    INSERT INTO [tracked_changes_edfi].[Grade](
        OldBeginDate, OldGradeTypeDescriptorId, OldGradeTypeDescriptorNamespace, OldGradeTypeDescriptorCodeValue, OldGradingPeriodDescriptorId, 
        OldGradingPeriodDescriptorNamespace, OldGradingPeriodDescriptorCodeValue, OldGradingPeriodName, OldGradingPeriodSchoolYear, OldLocalCourseCode, 
        OldSchoolId, OldSchoolYear, OldSectionIdentifier, OldSessionName, OldStudentUSI, 
        OldStudentUniqueId, 
        
        Id, 
        Discriminator, 
        ChangeVersion)
    SELECT 
        d.BeginDate, d.GradeTypeDescriptorId, j0.Namespace, j0.CodeValue, d.GradingPeriodDescriptorId, 
        j1.Namespace, j1.CodeValue, d.GradingPeriodName, d.GradingPeriodSchoolYear, d.LocalCourseCode, 
        d.SchoolId, d.SchoolYear, d.SectionIdentifier, d.SessionName, d.StudentUSI, 
        j2.StudentUniqueId, 
        
        d.Id, 
        d.Discriminator, 
        (NEXT VALUE FOR [changes].[ChangeVersionSequence])
    FROM deleted d
        INNER JOIN edfi.Descriptor j0 ON d.GradeTypeDescriptorId = j0.DescriptorId
        INNER JOIN edfi.Descriptor j1 ON d.GradingPeriodDescriptorId = j1.DescriptorId
        INNER JOIN edfi.Student j2 ON d.StudentUSI = j2.StudentUSI
END
```

The feature introduces a `/deletes` endpoint for each resource and descriptor. The endpoint returns the identifying values of deleted entries in the given window, backed by the corresponding `tracked_changes` table. It also supports the `minChangeVersion`, `maxChangeVersion`, `limit`, `offset`, and `totalCount` parameters:

```http
GET /data/v3/ed-fi/grades/deletes
```

Example response:

```json
[
  {
    "id": "62b8d4170fd64c79a59af2c7af4eaa1f",
    "changeVersion": 543,
    "keyValues": {
      "gradeTypeDescriptor": "uri://ed-fi.org/GradeTypeDescriptor#Exam",
      "gradingPeriodDescriptor": "uri://ed-fi.org/GradingPeriodDescriptor#Fourth Six Weeks",
      "gradingPeriodName": "2021-2022 Spring Semester Exam 1",
      "schoolId": 255901001,
      "gradingPeriodSchoolYear": 2022,
      "beginDate": "2022-01-04",
      "localCourseCode": "ALG-1",
      "schoolYear": 2022,
      "sectionIdentifier": "25590100102Trad220ALG122011",
      "sessionName": "2021-2022 Spring Semester",
      "studentUniqueId": "604863"
    }
  }
]
```

The generated SQL used to fulfill the request is:

```sql
WITH TranslatedTrackedChanges AS (
    SELECT 
      c.*, 
      curr0.StudentUSI AS CurrentStudentUSI 
    FROM 
      tracked_changes_edfi.Grade AS c 
      LEFT JOIN edfi.Student AS curr0 ON c.OldStudentUniqueId = curr0.StudentUniqueId
  ) 
SELECT DISTINCT 
  c.Id, 
  c.ChangeVersion, 
  c.OldGradeTypeDescriptorNamespace, 
  c.OldGradeTypeDescriptorCodeValue, 
  c.OldGradingPeriodDescriptorNamespace, 
  c.OldGradingPeriodDescriptorCodeValue, 
  c.OldGradingPeriodName, 
  c.OldSchoolId, 
  c.OldGradingPeriodSchoolYear, 
  c.OldBeginDate, 
  c.OldLocalCourseCode, 
  c.OldSchoolYear, 
  c.OldSectionIdentifier, 
  c.OldSessionName, 
  c.OldStudentUniqueId 
FROM 
  TranslatedTrackedChanges AS c 
  INNER JOIN auth.EducationOrganizationIdToEducationOrganizationId AS rba0 ON c.OldSchoolId = rba0.TargetEducationOrganizationId 
  INNER JOIN auth.EducationOrganizationIdToStudentUSIIncludingDeletes AS rba1 ON c.OldStudentUSI = rba1.StudentUSI 
  LEFT JOIN edfi.Grade AS src ON c.OldGradeTypeDescriptorId = src.GradeTypeDescriptorId 
  AND c.OldGradingPeriodDescriptorId = src.GradingPeriodDescriptorId 
  AND c.OldGradingPeriodName = src.GradingPeriodName 
  AND c.OldSchoolId = src.SchoolId 
  AND c.OldGradingPeriodSchoolYear = src.GradingPeriodSchoolYear 
  AND c.OldBeginDate = src.BeginDate 
  AND c.OldLocalCourseCode = src.LocalCourseCode 
  AND c.OldSchoolYear = src.SchoolYear 
  AND c.OldSectionIdentifier = src.SectionIdentifier 
  AND c.OldSessionName = src.SessionName 
  AND c.CurrentStudentUSI = src.StudentUSI 
WHERE 
  src.GradeTypeDescriptorId IS NULL -- Exclude entries that were recreated
  AND c.NewBeginDate IS NULL -- Exclude key changes
  AND (
      rba0.SourceEducationOrganizationId IN (SELECT Id FROM @p0)    -- Auth check: Relationship with EdOrg
      AND rba1.SourceEducationOrganizationId IN (SELECT Id FROM @p1)-- Auth check: Relationship with People
  ) 
ORDER BY 
  c.ChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

Recreated entries are hidden from the `/deletes` response. See the `Why we need to hide recreated resources from the /deletes endpoint` section below for the reason. The `TranslatedTrackedChanges` CTE gets the latest Student USI from a given UniqueId because USIs change when people resources are recreated.

Failing to make the USI translation results in the issue described in [ODS-6480](https://edfi.atlassian.net/browse/ODS-6480). However, deleting and recreating a descriptor with the same Namespace and CodeValue still results in the same issue because no translation is done for DescriptorIds.

### Descriptors

All descriptor types store their tombstones in the same table, `tracked_changes_edfi.Descriptor`.

```sql
CREATE TABLE [tracked_changes_edfi].[Descriptor]
(
    OldDescriptorId [INT] NOT NULL,
    OldNamespace [NVARCHAR](255) NOT NULL,
    OldCodeValue [NVARCHAR](50) NOT NULL,

    NewDescriptorId [INT] NULL,
    NewNamespace [NVARCHAR](255) NULL,
    NewCodeValue [NVARCHAR](50) NULL,

    Id uniqueidentifier NOT NULL,
    ChangeVersion bigint NOT NULL,
    Discriminator [NVARCHAR](128) NULL,
    CreateDate DateTime2 NOT NULL DEFAULT (getutcdate()),
    CONSTRAINT PK_Descriptor PRIMARY KEY CLUSTERED (ChangeVersion)
)
```

We use the `Discriminator` column to filter for specific descriptor types. An example `Discriminator` value would be `edfi.AbsenceEventCategoryDescriptor`.

The trigger that tracks identity changes is not emitted for descriptors because they do not allow identity changes. The trigger that tracks deletes resides on each descriptor table [here, for example](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e489317ea77f245aff99d57374165c238848f9a0/Application/EdFi.Ods.Standard/Standard/6.1.0/Artifacts/MsSql/Structure/Ods/Changes/0220-CreateTriggersForDeleteTracking.sql#L2270). These delete triggers could be merged into a single trigger in the `edfi.Descriptor` table now that it has the `Discriminator` column.

Deletes endpoint example:

```http
GET /data/v3/ed-fi/crisisTypeDescriptors/deletes
```

Example response:

```json
[
  {
    "id": "83030053c0a44151a822ab6146a6443a",
    "changeVersion": 543,
    "keyValues": {
      "namespace": "uri://ed-fi.org/CrisisTypeDescriptor",
      "codeValue": "Earthquake"
    }
  }
]
```

The generated SQL used to fulfill the request is:

```sql
SELECT DISTINCT 
  c.Id, 
  c.ChangeVersion, 
  c.OldNamespace, 
  c.OldCodeValue 
FROM 
  tracked_changes_edfi.Descriptor AS c 
  LEFT JOIN edfi.CrisisTypeDescriptor AS src ON c.OldDescriptorId = src.CrisisTypeDescriptorId 
WHERE 
  c.Discriminator = @p0 
  AND src.CrisisTypeDescriptorId IS NULL -- Exclude entries that were recreated (bug)
  AND c.NewDescriptorId IS NULL          -- Exclude key changes
  AND (
    -- Namespace-based auth check
    c.OldNamespace LIKE @p1 
    OR c.OldNamespace LIKE @p2 
    OR c.OldNamespace LIKE @p3
  ) 
ORDER BY 
  c.ChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

Most descriptor `/deletes` endpoints use `NoFurtherAuthorizationRequired`, except for `CrisisTypeDescriptor` and `NonMedicalImmunizationExemptionDescriptors`, which use namespace-based authorization as shown in the example SQL above.

When a resource references a descriptor as part of its identity, we store these three fields in the resource's `tracked_changes_<schema>` table:

- `<Descriptor>Namespace`
- `<Descriptor>CodeValue`
- `<Descriptor>Id`

The `<Descriptor>Namespace` and `<Descriptor>CodeValue` are needed to construct and return the descriptor in the `<namespace>#<codeValue>` format in both the `/deletes` and `/keyChanges` response bodies. The `DescriptorId` is needed by the `/deletes` endpoint to join back to the live source table and filter out entries that were recreated. This logic has a bug because recreating a descriptor generates a new DescriptorId, so ODS does not actually hide recreated descriptors.

### Abstract resources

Tombstones are stored in the corresponding abstract `tracked_changes_edfi` table, which as of this writing are:

- `tracked_changes_edfi.EducationOrganization`
- `tracked_changes_edfi.GeneralStudentProgramAssociation`

For example, all EducationOrganization subtypes, such as School and LocalEducationAgency, store their tombstones in `tracked_changes_edfi.EducationOrganization`.

The trigger that tracks identity changes and the one that tracks deletes reside on the abstract `tracked_changes_edfi` tables.

For example, the `tracked_changes_edfi.EducationOrganization` table definition is:

```sql
CREATE TABLE [tracked_changes_edfi].[EducationOrganization]
(
  OldEducationOrganizationId [BIGINT] NOT NULL,
  NewEducationOrganizationId [BIGINT] NULL,

  Id uniqueidentifier NOT NULL,
  ChangeVersion bigint NOT NULL,
  Discriminator [NVARCHAR](128) NULL,
  CreateDate DateTime2 NOT NULL DEFAULT (getutcdate()),
  CONSTRAINT PK_EducationOrganization PRIMARY KEY CLUSTERED (ChangeVersion)
)
```

We use the `Discriminator` column to filter for specific concrete types. An example `Discriminator` value would be `edfi.School`.

Deletes endpoint example:

```http
GET /data/v3/ed-fi/schools/deletes
```

Example response:

```json
[
  {
    "id": "31667e45cff0460694a8fbdd7f283d56",
    "changeVersion": 543,
    "keyValues": {
      "schoolId": 255901001
    }
  }
]
```

The generated SQL used to fulfill the request is:

```sql
SELECT DISTINCT 
  c.Id, 
  c.ChangeVersion, 
  c.OldEducationOrganizationId 
FROM 
  tracked_changes_edfi.EducationOrganization AS c 
  LEFT JOIN edfi.School AS src ON c.OldEducationOrganizationId = src.SchoolId 
WHERE 
  c.Discriminator = @p0 
  AND src.SchoolId IS NULL                  -- Exclude entries that were recreated
  AND c.NewEducationOrganizationId IS NULL  -- Exclude key changes
ORDER BY 
  c.ChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

#### Caveat with custom securable-elements

Consider that the securable element of `OrganizationDepartment` has been overridden in the [RelationshipsAuthorizationContextDataProviderOverridesModule](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/2c6dba9e6ce6b53d037874d4b053131652972a31/Application/EdFi.Ods.Standard/Container/Modules/RelationshipsAuthorizationContextDataProviderOverridesModule.cs#L17) to `ParentEducationOrganizationId` instead of `OrganizationDepartmentId`. Since tombstones for concrete abstract resources are stored in the corresponding abstract `tracked_changes_edfi.*` table (`tracked_changes_edfi.EducationOrganization` in this case), we cannot authorize `OrganizationDepartment` from the `/deletes` endpoint because `tracked_changes_edfi.EducationOrganization` only stores the identifying values of the abstract definition.

This is not an issue in ODS because OrganizationDepartment's `ReadChanges` action is `NoFurtherAuthorizationRequired`; however, this limitation is worth noting.

### SchoolYearTypes

ODS treats `SchoolYearType` as a special OpenAPI exception for the tracked-change routes. Its live resource endpoint supports `minChangeVersion` and `maxChangeVersion`, but ODS does not advertise `/ed-fi/schoolYearTypes/deletes` or `/ed-fi/schoolYearTypes/keyChanges`.

DMS intentionally differs from this ODS OpenAPI exception. The DMS-specific `SchoolYearType` decision is described in the later authorization section.

### Filtering live resources by ChangeVersion

Live resource endpoints are the ordinary resource and descriptor endpoints that return current resource representations, such as `GET /data/v3/ed-fi/students`. These endpoints allow filtering by `minChangeVersion` and `maxChangeVersion`, which internally filter the resource's `ChangeVersion` column, which is indexed. They allow users to retrieve the current representation of resources updated within a given change window.

```http
GET /data/v3/ed-fi/grades?minChangeVersion=123&maxChangeVersion=987
```

The generated SQL used to fulfill the request is:

```sql
WITH authView7f24a2 AS (
    SELECT DISTINCT 
      av.TargetEducationOrganizationId 
    FROM 
      auth.EducationOrganizationIdToEducationOrganizationId AS av 
    WHERE 
      av.SourceEducationOrganizationId IN (SELECT Id FROM @ClaimEducationOrganizationIds)
  ), 
  authViewbde5ba AS (
    SELECT DISTINCT 
      av.StudentUSI 
    FROM 
      auth.EducationOrganizationIdToStudentUSI AS av 
    WHERE 
      av.SourceEducationOrganizationId IN (SELECT Id FROM @ClaimEducationOrganizationIds)
  ) 
SELECT 
  r.AggregateId, 
  r.AggregateData, 
  r.LastModifiedDate 
FROM 
  edfi.Grade AS r 
  INNER JOIN authView7f24a2 ON r.SchoolId = authView7f24a2.TargetEducationOrganizationId  -- Auth check: Relationship with EdOrg
  INNER JOIN authViewbde5ba ON r.StudentUSI = authViewbde5ba.StudentUSI                   -- Auth check: Relationship with People
WHERE 
  ChangeVersion >= @p0 
  AND ChangeVersion <= @p1 
ORDER BY 
  r.AggregateId OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

### Querying the newest change version

The feature introduces the `/availableChangeVersions` endpoint that returns the global newest change version.

```http
GET /changeQueries/v1/availableChangeVersions
```

Example response:

```json
{
  "oldestChangeVersion": 0,
  "newestChangeVersion": 120454
}
```

The generated SQL used to fulfill the request is:

```sql
SELECT changes.GetMaxChangeVersion() as NewestChangeVersion
```

The `changes.GetMaxChangeVersion()` custom function definition is below.

[For MSSQL](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/c1c478dcd939f90705fb1b0c16a91dd6e5066565/Application/EdFi.Ods.Standard/Standard/6.1.0/Artifacts/MsSql/Structure/Ods/Changes/1010-CreateGetMaxChangeVersionFunction.sql#L6):
```sql
CREATE OR ALTER FUNCTION [changes].GetMaxChangeVersion()
RETURNS bigint
AS
BEGIN
    DECLARE @Result bigint;
    SELECT @Result = CONVERT(bigint, seq.current_value) FROM sys.sequences seq
    INNER JOIN sys.schemas sch
    ON seq.schema_id = sch.schema_id
    WHERE seq.name = 'ChangeVersionSequence' AND sch.name = 'changes';
    RETURN @Result;
END
```

[For PGSQL](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/6.1.0/Artifacts/PgSql/Structure/Ods/Changes/1010-CreateGetMaxChangeVersionFunction.sql):
```sql
CREATE OR REPLACE FUNCTION changes.GetMaxChangeVersion() RETURNS bigint AS
$$
DECLARE
	result bigint;
BEGIN
    SELECT last_value FROM changes.ChangeVersionSequence INTO result;
    RETURN result;
END
$$ language plpgsql;
```

Note that the response's `oldestChangeVersion` is hardcoded to `0`.

### Authorization

The `/keyChanges` and `/deletes` endpoints return resource identifying values, which are sensitive data, so these endpoints must apply authorization similar to live resource endpoints.

The feature introduces the `ReadChanges` action that must be granted to the resource's claims to access the `/keyChanges` and `/deletes` endpoints.
The feature also introduces the authorization strategies below, which are meant to be used with the `ReadChanges` action:

- RelationshipsWithEdOrgsAndPeopleIncludingDeletes
- RelationshipsWithStudentsOnlyIncludingDeletes
- RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes

These authorization strategies are the same as their non-prefixed equivalents. The only difference is that they use different authorization views behind the scenes by setting a `pathModifier`. The feature introduces these views:


#### EducationOrganizationIdToContactUSIIncludingDeletes
<details>
  <summary>Definition</summary>

  ```sql
  CREATE VIEW auth.EducationOrganizationIdToContactUSIIncludingDeletes(SourceEducationOrganizationId, ContactUSI) AS
      -- Intact StudentSchoolAssociation and intact StudentContactAssociation
      SELECT	SourceEducationOrganizationId, ContactUSI
      FROM	auth.EducationOrganizationIdToContactUSI

      UNION

      -- Intact StudentSchoolAssociation and deleted StudentContactAssociation
      SELECT edOrgs.SourceEducationOrganizationId, spa_tc.OldContactUSI as ContactUSI
      FROM    auth.EducationOrganizationIdToEducationOrganizationId edOrgs
          JOIN edfi.StudentSchoolAssociation ssa ON edOrgs.TargetEducationOrganizationId = ssa.SchoolId
          JOIN tracked_changes_edfi.StudentContactAssociation spa_tc ON ssa.StudentUSI = spa_tc.OldStudentUSI

      UNION

      -- Deleted StudentSchoolAssociation and intact StudentContactAssociation
      SELECT	edOrgs.SourceEducationOrganizationId, spa.ContactUSI
      FROM    auth.EducationOrganizationIdToEducationOrganizationId edOrgs
          JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.OldSchoolId
          JOIN edfi.StudentContactAssociation spa ON ssa_tc.OldStudentUSI = spa.StudentUSI

      UNION

      -- Deleted StudentSchoolAssociation and StudentContactAssociation
      SELECT	edOrgs.SourceEducationOrganizationId, spa_tc.OldContactUSI as ContactUSI
      FROM    auth.EducationOrganizationIdToEducationOrganizationId edOrgs
          JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.OldSchoolId
          JOIN tracked_changes_edfi.StudentContactAssociation spa_tc ON ssa_tc.OldStudentUSI = spa_tc.OldStudentUSI;
  ```
</details>

#### EducationOrganizationIdToStaffUSIIncludingDeletes
<details>
  <summary>Definition</summary>

  ```sql
  CREATE VIEW auth.EducationOrganizationIdToStaffUSIIncludingDeletes(SourceEducationOrganizationId, StaffUSI) AS
      SELECT	SourceEducationOrganizationId, StaffUSI
      FROM	auth.EducationOrganizationIdToStaffUSI edOrgToStaff
      
      UNION

      -- Deleted employment
      SELECT	edOrgs.SourceEducationOrganizationId, emp_tc.OldStaffUSI as StaffUSI
      FROM	auth.EducationOrganizationIdToEducationOrganizationId edOrgs
              JOIN tracked_changes_edfi.StaffEducationOrganizationEmploymentAssociation emp_tc
                  ON edOrgs.TargetEducationOrganizationId = emp_tc.OldEducationOrganizationId

      UNION

      -- Deleted assignments
      SELECT	edOrgs.SourceEducationOrganizationId, assgn_tc.OldStaffUSI as StaffUSI
      FROM	auth.EducationOrganizationIdToEducationOrganizationId edOrgs
              JOIN tracked_changes_edfi.StaffEducationOrganizationAssignmentAssociation assgn_tc
                  ON edOrgs.TargetEducationOrganizationId = assgn_tc.OldEducationOrganizationId;
  ```
</details>

#### EducationOrganizationIdToStudentUSIIncludingDeletes
<details>
  <summary>Definition</summary>

  ```sql
  CREATE VIEW auth.EducationOrganizationIdToStudentUSIIncludingDeletes(SourceEducationOrganizationId, StudentUSI) AS
      SELECT SourceEducationOrganizationId, StudentUSI
      FROM auth.EducationOrganizationIdToStudentUSI

      UNION

      SELECT edOrgs.SourceEducationOrganizationId, ssa_tc.OldStudentUSI as StudentUSI
      FROM auth.EducationOrganizationIdToEducationOrganizationId edOrgs
          JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.OldSchoolId;
  ```
</details>

#### EducationOrganizationIdToStudentUSIThroughDeletedResponsibility
<details>
  <summary>Definition</summary>

  ```sql
  CREATE OR REPLACE VIEW auth.EducationOrganizationIdToStudentUSIThroughDeletedResponsibility AS
      SELECT  edOrgs.SourceEducationOrganizationId, seora.StudentUSI
      FROM    auth.EducationOrganizationIdToEducationOrganizationId edOrgs
              INNER JOIN edfi.StudentEducationOrganizationResponsibilityAssociation seora
                  ON edOrgs.TargetEducationOrganizationId = seora.EducationOrganizationId

      UNION

      SELECT	edOrgs.SourceEducationOrganizationId, OldStudentUSI as StudentUSI
      FROM	auth.EducationOrganizationIdToEducationOrganizationId edOrgs
              INNER JOIN tracked_changes_edfi.StudentEducationOrganizationResponsibilityAssociation seora_tc
                  ON edOrgs.TargetEducationOrganizationId = seora_tc.OldEducationOrganizationId;
  ```
</details>

These strategies are built on top of the authorization logic described in [auth.md](auth.md), meaning they support the same combination mechanics, although they are not combined with other strategies as of today.

The `ReadChanges` action has to be configured with the equivalent authorization strategy, or strategies, as the `Read` action. The strategy equivalents are listed below:

| Read action                                        | ReadChanges action                                                   |
| -------------------------------------------------- | -------------------------------------------------------------------- |
| NoFurtherAuthorizationRequired                     | NoFurtherAuthorizationRequired                                       |
| NamespaceBased                                     | NamespaceBased                                                       |
| OwnershipBased                                     | Not supported                                                        |
| RelationshipsWithEdOrgsAndPeople                   | RelationshipsWithEdOrgsAndPeopleIncludingDeletes                     |
| RelationshipsWithEdOrgsOnly                        | RelationshipsWithEdOrgsOnly                                          |
| RelationshipsWithPeopleOnly                        | Not supported                                                        |
| RelationshipsWithStudentsOnly                      | RelationshipsWithStudentsOnlyIncludingDeletes                        |
| RelationshipsWithStudentsOnlyThroughResponsibility | RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes   |
| RelationshipsWithEdOrgsOnlyInverted                | RelationshipsWithEdOrgsOnlyInverted                                  |
| RelationshipsWithEdOrgsAndPeopleInverted           | Not supported                                                        |
| Custom view-based strategies                       | Custom view-based strategies (unchanged)                             |

The OwnershipBased strategy is explicitly unsupported as documented in [OwnershipBasedAuthorizationFilterDefinitionsFactory](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/10e54ef6a417b036d27a9d23391500069ba52794/Application/EdFi.Ods.Api/Security/AuthorizationStrategies/OwnershipBased/OwnershipBasedAuthorizationFilterDefinitionsFactory.cs#L76).

Consider the `EducationOrganizationIdToContactUSIIncludingDeletes` view:

```sql
CREATE VIEW [auth].[EducationOrganizationIdToContactUSIIncludingDeletes] AS 
-- Current EdOrgHierarchy + Current StudentSchoolAssociation + Current StudentContactAssociation
SELECT 
  SourceEducationOrganizationId, 
  ContactUSI 
FROM 
  auth.EducationOrganizationIdToContactUSI 
UNION 
-- Current EdOrgHierarchy + Current StudentSchoolAssociation + Deleted/Key-changed StudentContactAssociation
SELECT 
  edOrgs.SourceEducationOrganizationId, 
  spa_tc.OldContactUSI as ContactUSI 
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN edfi.StudentSchoolAssociation ssa ON edOrgs.TargetEducationOrganizationId = ssa.SchoolId 
  JOIN tracked_changes_edfi.StudentContactAssociation spa_tc ON ssa.StudentUSI = spa_tc.OldStudentUSI 
UNION 
-- Current EdOrgHierarchy + Deleted/Key-changed StudentSchoolAssociation + Current StudentContactAssociation
SELECT 
  edOrgs.SourceEducationOrganizationId, 
  spa.ContactUSI 
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.OldSchoolId 
  JOIN edfi.StudentContactAssociation spa ON ssa_tc.OldStudentUSI = spa.StudentUSI 
UNION 
-- Current EdOrgHierarchy + Deleted/Key-changed StudentSchoolAssociation + Deleted/Key-changed StudentContactAssociation
SELECT 
  edOrgs.SourceEducationOrganizationId, 
  spa_tc.OldContactUSI as ContactUSI 
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.OldSchoolId 
  JOIN tracked_changes_edfi.StudentContactAssociation spa_tc ON ssa_tc.OldStudentUSI = spa_tc.OldStudentUSI;
```

These `*IncludingDeletes` views consider current, deleted, and key-changed PrimaryAssociations against the **current** EdOrgHierarchy. Generally speaking, if a token had access to the resource item when it was deleted or updated, then the `/deletes` and `/keyChanges` endpoints grant authorization and return the item.

#### Authorization peculiarities

##### KeyChanges are always authorized based on the old values
Let's take a closer look at the authorization SQL used by the `/grades/keyChanges` endpoint:

```sql
SELECT DISTINCT 
  c.Id, 
  MIN(c.ChangeVersion) AS InitialChangeVersion, 
  MAX(c.ChangeVersion) AS FinalChangeVersion 
FROM 
  tracked_changes_edfi.Grade AS c 
  INNER JOIN auth.EducationOrganizationIdToEducationOrganizationId AS rba0 ON c.OldSchoolId = rba0.TargetEducationOrganizationId 
  INNER JOIN auth.EducationOrganizationIdToStudentUSIIncludingDeletes AS rba1 ON c.OldStudentUSI = rba1.StudentUSI 
WHERE 
  c.NewBeginDate IS NOT NULL -- Exclude tombstones
  AND (
      rba0.SourceEducationOrganizationId IN (SELECT Id FROM @p0) -- Auth check: Relationship with EdOrg
      AND rba1.SourceEducationOrganizationId IN (SELECT Id FROM @p1) -- Auth check: Relationship with People
  ) 
```

Notice that, for keyChanges, the authorization checks are done only against the old values. This means that a token that has access to the old school/student but doesn't have access to the new ones will still be able to read the keyChange record.

##### Once an EdOrgId has access to a person-securable resource, it will forever have access to its deletes and key changes

The `EducationOrganizationIdToContactUSIIncludingDeletes` authorization view above sources PrimaryAssociations from the current table, such as `edfi.StudentSchoolAssociation`, and the deleted or key-changed table, such as `tracked_changes_edfi.StudentSchoolAssociation`. This is needed to grant access to resource items whose PrimaryAssociation was deleted or key-changed. However, the `tracked_changes_edfi.StudentSchoolAssociation` table is not filtered by its ChangeVersion, meaning that an EdOrgId that is no longer associated with a Person will continue to have access to the key changes and deletes of the affected person-securable resource items.

##### Education Organization Ids are authorized against the current hierarchy

The inverse happens when authorizing an EdOrgId. It is always authorized against the current hierarchy. For example, if a LocalEducationAgency changes its parent, the affected EdOrgIds will stop seeing *all* deletes and key changes for the affected resource items, so downstream APIs will not be able to retrieve changes up to the point when they had access.

Arguably, a more correct design would store whether an EducationOrganizationId had access to a resource item when it was deleted or key-changed, but downstream APIs likely already account for and rely on these peculiarities.

### Snapshots

Database snapshots provide a mechanism for downstream APIs to make requests that are served from a static copy of the ODS database isolated from ongoing changes.

Snapshots address the following two issues described in the `Scenarios that could lead to synchronization failures` section below:

- Using limit/offset without using snapshots
- Unresolved references when not using snapshots

#### The `Use-Snapshot` header

The feature introduces a `Use-Snapshot` request header that, when set to `true`, redirects the request to the configured snapshot connection string instead of the primary ODS database. The header is honored by live resource and descriptor endpoints, `/deletes`, `/keyChanges`, and `/availableChangeVersions`.

```http
GET /data/v3/ed-fi/grades
Use-Snapshot: true
```

A few constraints are enforced when the header is supplied:

- Snapshots are read-only. Any non-`GET` request returns `405 Method Not Allowed` with an `Allow: GET` response header (see [SnapshotsAreReadOnlyResult](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Features/ChangeQueries/ActionResults/SnapshotsAreReadOnlyResult.cs)).
- If no snapshot connection string is configured for the ODS instance, the request fails with `404 Not Found`.
- If a snapshot connection string is configured but the underlying database cannot be reached, the [SnapshotNotFoundExceptionTranslator](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Features/ChangeQueries/ExceptionHandling/SnapshotNotFoundExceptionTranslator.cs) returns the same `404 Not Found` response.
- If both `Snapshot` and `ReadReplica` are configured, and the request comes with the `Use-Snapshot: true` header, the `Snapshot` derivative takes precedence over the `ReadReplica`.

The API does not create or drop the snapshot itself. It only routes requests against a snapshot database that is expected to have been created beforehand, such as SQL Server's `CREATE DATABASE ... AS SNAPSHOT OF ...` or a PostgreSQL backup/restore clone.

#### Table structure

The Admin database stores the connection string of each derivative ODS (snapshot or read-replica) per ODS instance in the `dbo.OdsInstanceDerivative` table:

```sql
CREATE TABLE [dbo].[OdsInstanceDerivative](
    [OdsInstanceDerivativeId] INT IDENTITY(1,1) PRIMARY KEY NOT NULL,
    [OdsInstanceId]           INT NOT NULL,
    [DerivativeType]          NVARCHAR(50) NOT NULL,   -- 'Snapshot' or 'ReadReplica'
    [ConnectionString]        NVARCHAR(500) NULL,
    CONSTRAINT [UC_OdsInstanceDerivative_OdsInstanceId_DerivativeType] UNIQUE(OdsInstanceId, DerivativeType)
);
```

Only the `Snapshot` derivative is consulted by the `Use-Snapshot` header path; `ReadReplica` is used independently for offloading regular reads. The unique constraint enforces that only one `Snapshot` or `ReadReplica` is configured per ODS instance.

At startup, the API reads and caches the derivatives for the time configured in `apiSettings.Caching.OdsInstances.AbsoluteExpirationSeconds` (default 300 seconds).

### Dependency Order Endpoint

The ODS API provides the `/metadata/data/v3/dependencies` endpoint that returns the order in which resources should be created/updated/deleted taking into account their reference chain and authorization.

An example response is:

```json
{
  ...
  {
    "resource": "/ed-fi/contacts",
    "order": 3,
    "operations": [
      "Create"
    ]
  },
  {
    "resource": "/ed-fi/students",
    "order": 3,
    "operations": [
      "Create"
    ]
  },
  ...
  {
    "resource": "/ed-fi/studentSchoolAssociations",
    "order": 13,
    "operations": [
      "Create",
      "Update"
    ]
  },
  ...
  {
    "resource": "/ed-fi/students",
    "order": 14,
    "operations": [
      "Update"
    ]
  },
  ...
}
```

The response contains ordered groups of live resource endpoints that can be loaded at the same time. "Delete" operations are to be performed at the reverse order of Create operations.

This endpoint is already implemented in DMS.

### Client extraction logic

The recommended implementation of a data synchronization tool follows this process:

Repeating (on a schedule):

1. Obtain the saved ChangeVersion from the previous synchronization (`0` if this is the first execution) and add 1, this becomes the `MinChangeVersion`.
2. Obtain the source system's newest ChangeVersion using the `/availableChangeVersions` endpoint, this becomes the `MaxChangeVersion`.
3. Iterate through all resources in dependency order; on each resource:
   1. Execute the `/keyChanges` endpoint, specifying the `MinChangeVersion` and `MaxChangeVersion` on the source API, and PUT the new identifying values to the downstream API.
   2. Extract the latest representation of the resource items, using the live resource GET-many endpoint such as `/students`, specifying the `MinChangeVersion` and `MaxChangeVersion` on the source API, and POST the new representations to the downstream API.
4. Iterate through all resources in reverse dependency order; on each resource:
   1. Execute the `/deletes` endpoint, specifying the `MinChangeVersion` and `MaxChangeVersion` on the source API, and DELETE the items on the downstream API.
5. If successful, save the new ChangeVersion value for future change processing.

Ed-Fi provides the [API Publisher](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher), which follows this process out of the box.

#### Order of operations

The execution order above (KeyChanges -> Upserts -> Deletes) is important because:

##### Deletes must come after key changes

Tombstones are stored with the latest identifying values the resource had at the time of deletion.

If a resource changed its identifying values and was then deleted, the key change has to be synchronized first so that the delete operation can find the resource to delete.

##### Deletes must come after Upserts

If a resource reference is updated to point from `A` to `B`, the update must be executed before the delete. Otherwise, deleting `A` will fail because the downstream API still has entries referencing `A`. Running upserts first gives the synchronization tool a chance to remove old references.

##### Key changes must be before Upserts

If a resource changed its identifying values and key changes were synchronized after upserts, the synchronization tool would create a new resource, then attempt to apply the key change and fail because there is already a resource with the same identifying values.

This execution order is not perfect, but it is the least likely to fail. See `Deleting a resource and updating another resource's identity to be the same as the one deleted` below for more information.

### Scenarios that could lead to synchronization failures

The list below contains known issues that could cause resource entries to fail synchronization, such as entries that cannot be posted to the downstream system because of missing references. It also contains more dangerous scenarios that could lead to silent, permanent resource desynchronization.

Note that some of these issues apply even when using snapshots.

#### Missed resource entries due to authorization

Assume a student is associated with School `255901001`, and a downstream API only has access to `255901002`, meaning the downstream API does not see the student-related data.

After some time, the student also gets associated with `255901002`. The downstream API will extract the new StudentSchoolAssociation on the next synchronization; however, it will fail because it references a student that was never extracted. The student's ChangeVersion remains the same after the StudentSchoolAssociation creation.

Synchronization logic must handle this scenario. For example, API Publisher handles it out of the box in [PostResourceProcessingBlocksFactory](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/blob/99a1712db47c4b1284080fa8c0d2bc7a9a2b2fa5/src/EdFi.Tools.ApiPublisher.Connections.Api/Processing/Target/Blocks/PostResourceProcessingBlocksFactory.cs#L487) by parsing StudentSchoolAssociation's `Bad Request` response, extracting the related student on the fly, and retrying the StudentSchoolAssociation creation.

However, there is another related issue: resources authorized based on the existence of a StudentSchoolAssociation, such as StudentTransportation, will not get extracted once the downstream API gains access because their ChangeVersion is behind the downstream API's current change window.

The same issue can happen for any scenario where the downstream API gains access to old data, for example when adding an EdOrgId to the downstream API's token, updating the EdOrg hierarchy in a way that adds accessible EdOrgIds, or creating PrimaryAssociations.

There are no known mitigations other than triggering a full synchronization or granting full access to downstream APIs, which might not be recommended.

#### Using limit/offset without using snapshots

Assume you are extracting `/students` with a ChangeVersion window from 0 to 5,000. Since your page size is 500, you have to request 10 pages. If someone updates a student in the middle of the extraction, such as changing `personalIdentificationDocuments`, the student's ChangeVersion gets updated to 5,001. That value is outside your extraction window, so the student is no longer returned. This shifts the remaining students and can silently skip a student by moving it into an already-paged offset.

Mitigations:

- Sourcing from a snapshot
- Using reverse paging (see below)
- Not specifying a MaxChangeVersion filter (not recommended, see below)

#### Not specifying a MaxChangeVersion filter and not using snapshots

You might attempt to work around the previous issue by not specifying the MaxChangeVersion filter during synchronization. However, this opens the door to another issue. Suppose you are synchronizing `/studentSchoolAssociations`, have already applied its key changes, and are now applying upserts. Then someone changes the identifying values of a StudentSchoolAssociation. When you reach it, you will see it as a new StudentSchoolAssociation, triggering an insert instead of an update. On the next synchronization run, you will attempt to execute the key change, but it will fail because there is already a StudentSchoolAssociation with the same identifying values, leaving the system with two entries instead of one.

Mitigations:

- Always specify a MaxChangeVersion filter when not using snapshots

#### Unresolved references when not using snapshots

Assume you are synchronizing changes without using snapshots and the next scenario happens:

- A Student gets created on ChangeVersion = 10, then updated on ChangeVersion = 30
- A StudentTransportation gets created on ChangeVersion = 15

You're extracting the change window from 10 to 20, since the student got updated outside your extraction window it will not get extracted, and attempting to create the StudentTransportation will fail because it references the student.

Mitigations:

- Take note of the failed entries and retry them on the next synchronization run (requires local storage)

#### Disregarding uncommitted transactions when using snapshots

The `/availableChangeVersions` endpoint calculates the NewestChangeVersion by retrieving the latest value from the `changes.ChangeVersionSequence`. The sequence's maximum value includes changes from uncommitted transactions. If someone creates a snapshot in the middle of a long-running transaction, such as a cascading identity change, the snapshot will not include the uncommitted transaction's changes. However, the `/availableChangeVersions` endpoint will return a NewestChangeVersion that includes the uncommitted changes.

Synchronization tools store and use the NewestChangeVersion as the MinChangeVersion for the upcoming synchronization run, which skips the changes from the uncommitted transactions and leads to silent desynchronization.

Mitigations:

- Subtract a predefined number from the MinChangeVersion filter so that uncommitted transaction changes from the previous run get caught. Naturally, some changes will get applied twice.
- Subtract a predefined number from the MaxChangeVersion filter (not recommended when using snapshots, see below).

#### Subtracting an amount from the MaxChangeVersion while using snapshots

You might attempt to work around the previous issue by subtracting a fixed amount from the MaxChangeVersion. While effective at excluding uncommitted transactions, this can cause the next issue when you are using snapshots.

Assume you are extracting the next resources:

- A Student gets created on ChangeVersion = 10, then updated on ChangeVersion = 30
- A StudentTransportation gets created on ChangeVersion = 15

The snapshot's NewestChangeVersion is 30, but you subtract 10 to account for uncommitted transactions, so your extraction window is from 0 to 20. The synchronization will fail because you will attempt to create a StudentTransportation that references a Student that has not been extracted.

Mitigations:

- Instead of subtracting a predefined number from the MaxChangeVersion, subtract it from the MinChangeVersion.

#### Deleting a resource and updating another resource's identity to be the same as the one deleted

Assume the next operations happen:

- GradebookEntry `A` gets deleted
- GradebookEntry `B` gets updated so that its identifying values are the same as the one that got deleted

Since the synchronization tool executes key changes before deletes, it will first attempt to update GradebookEntry `B` but will fail because GradebookEntry `A` has not been deleted yet, causing a duplicate constraint error.

To mitigate this, the synchronization tool would have to interleave key changes, upserts, and deletes, ordering them by their ChangeVersion. This would ensure operations execute in the same order as they were executed in the source system. API Publisher does not currently support this.

#### Why we need to hide recreated resources from the /deletes endpoint

The `/deletes` endpoints hide recreated resources as explained above. To see why, assume they do *not* hide them, and the next operations happen:

- A student gets created on ChangeVersion = 10
- The student gets deleted on ChangeVersion = 20
- The student gets recreated (with the same uniqueId) on ChangeVersion = 30

The synchronization tool executes upserts before deletes, so it would create the recreated student and then delete it, leaving the downstream API without the student.

One might be tempted to execute deletes before upserts, but that brings other issues described in the `Client extraction logic` section above.

#### Dependencies endpoint might return a wrong ordering when some optional references are initialized

The Data Standard model has references that result in cycles. The dependencies endpoint removes the cycles by ignoring some optional references in [ResourceLoadGraphFactory](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e489317ea77f245aff99d57374165c238848f9a0/Application/EdFi.Ods.Common/Models/Graphs/ResourceLoadGraphFactory.cs#L74). That makes a usable acyclic order, but if your actual resource data includes an optional reference that was removed to break a cycle, posting it can still hit a reference-not-found failure.

To mitigate this issue, synchronization tools would have to implement the same retry logic described in the `Unresolved references when not using snapshots` issue above.

### Reverse Paging (alternative to snapshots)

Reverse paging is an extraction method first described by EA; [see its details here](https://github.com/edanalytics/edfi_api_client?tab=readme-ov-file#reverse-paging). It fixes the `Using limit/offset without using snapshots` issue described above and aims to be an alternative to snapshots. However, it has limitations and requires special handling in the synchronization tool.

In short, it reverses the paging order in which a resource is extracted. It starts with the highest offset and ends with the smallest offset. If an entry gets updated halfway through the extraction, meaning its ChangeVersion moves out of the extraction window, the shifted entries are seen again instead of being skipped.

However, this extraction method is susceptible to entries suddenly appearing in the ChangeVersion extraction window. This can happen when uncommitted transactions get committed, consider the next scenario:

| ResourceId | CommittedChangeVersion | Uncommitted ChangeVersion |
| ---------- | ---------------------- | ------------------------- |
| 1          | 10                     | 140                       |
| 2          | 101                    | N/A                       |
| 3          | 102                    | N/A                       |

Syncing ChangeVersion window 100 to 150

Total-count is 2 (at the start of the sync)

Page 1 extraction

Offset: 1

Limit: 1

Extracted IDs: [3]

Resource `1` uncommitted transaction gets committed.

Page 2 extraction

Offset: 0

Limit: 1

Extracted IDs: [1]

Resource ID `2` does not get extracted, silently desynchronizing.

The resource types that get synced first are the most susceptible to this issue because uncommitted transactions become committed as time passes during synchronization. One can mitigate this by subtracting an amount from the MaxChangeVersion filter to exclude potentially uncommitted transactions.

Additionally, this extraction method is susceptible to the `Unresolved references when not using snapshots` issue described above, meaning it requires retry logic in the synchronization tool.

API publisher already supports reverse paging, but it doesn't support the retry logic needed to solve the `Unresolved references when not using snapshots` issue.

### How to disable the feature

The feature is enabled by default, and the DB backups used by the Minimal and Populated templates include Change Queries objects.

To disable the feature, set `ApiSettings:Features:ChangeQueries` to `false` in the `appsettings.json` file of the EdFi.Ods.WebApi project.

Users must also remove the related DB objects by following the [disabling Change Queries instructions](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/#disabling-change-queries). In summary, they have to execute scripts that:

- Drop tracking triggers
- Drop tracking tables
- Drop auth views
- Drop ChangeVersion column on resource tables
- Drop GetMaxChangeVersion function
- Drop ChangeVersionSequence sequence
- Drop schemas

## What needs to be done in DMS

Many downstream applications in the field connect to the API through the Change Queries feature. To avoid imposing additional work on implementers when migrating from ODS to DMS, the Change Queries feature should avoid introducing breaking changes.

DMS will implement the same Change Queries design as ODS unless specified in the remainder of this document.

We will use the rewrite to make these improvements over ODS:

- Descriptor `/deletes` endpoints will correctly hide recreated descriptors.
- Resource `/deletes` endpoints will correctly hide recreated resources that reference recreated descriptors.
- Concrete abstract resources, such as School, will now get their own `tracked_changes*` table to support SecurableElement overrides.

### Out of scope
The next features are deferred after DMS v1.0
- Support for DB snapshots
- Support for the custom view-based authorization strategy in the tracked-changes endpoints
- Allow disabling the feature

### Database Model

#### Derived tracked-change inventory

Change Query database semantics are compiled into the shared `DerivedRelationalModelSet` defined in [compiled-mapping-set.md](compiled-mapping-set.md), not inferred inside dialect DDL string generation.

The derived model must include SQL-free inventory for:

- `TrackedChangeTableInfo` entries for per-resource tracked-change tables and the shared descriptor tracked-change table, including the standard `Id`, `ChangeVersion`, `CreatedAt`, and descriptor `Discriminator` system columns.
- `TrackedChangeColumnInfo` entries for each tracked old/new value in `ValueColumnsInTableOrder`, including the source JsonPath, canonical storage column when key unification applies, separate old/new nullability, scalar type, and column role.
- `TrackedChangeDescriptorJoinInfo` entries for descriptor reference paths that must be materialized as `Namespace` and `CodeValue`.
- `TrackedChangePersonJoinInfo` entries for Student, Contact, and Staff `SecurableElements` paths that must materialize the person resource `DocumentId`.
- `DocumentStamping.ChangeTracking` on the affected `TriggerKindParameters.DocumentStamping` trigger inventory entries.

The `*IncludingDeletes` authorization views used by `ReadChanges` are the exception: their `ReadChangesAuthorizationViewInfo` entries are a static structural inventory owned by `AuthObjectDefinitions.ReadChangesAuthorizationViewDefinitions` in `Backend.External`, not part of `DerivedRelationalModelSet`, because their shape never varies with the effective schema (see [compiled-mapping-set.md](compiled-mapping-set.md)). Their emission is gated per model set by people-auth availability plus the presence of the five required `tracked_changes_edfi` association tables.

The model derivation pass owns the semantic decisions: which resources get tracked-change tables, which columns are included, how duplicate canonical columns are de-duplicated, and which descriptor/person joins are needed. PostgreSQL and SQL Server emitters render this inventory mechanically into tables, triggers, indexes, views, manifests, and fixture outputs. Runtime Change Query SQL planning must consume the same inventory so endpoint selection, authorization, generated DDL, and manifests cannot drift.

#### `tracked_changes*` tables

Similar to ODS, each resource will get an accompanying `TrackedChangeTableInfo` with the `tracked_changes_<ProjectName>.<ResourceName>` naming convention. The usual DMS identifier-shortening logic applies to avoid exceeding the PostgreSQL length limit.

These tables are similar to the corresponding live tables.

Tracked-change system columns are fixed by role, not by ApiSchema value metadata:

- `Id` stores `dms.Document.DocumentUuid` as PostgreSQL `uuid` / SQL Server `uniqueidentifier`.
- `ChangeVersion` stores the bumped `dms.Document.ContentVersion` as `bigint`.
- `CreatedAt` stores the tracked row insert timestamp as PostgreSQL `timestamp with time zone DEFAULT now()` / SQL Server `datetime2(7) DEFAULT sysutcdatetime()`.
- `Discriminator` is present only for shared descriptor tracked-change tables and uses PostgreSQL `varchar(128)` / SQL Server `nvarchar(128)`.

The `TrackedChangeColumnInfo` value-column list should include the corresponding columns that result from combining the `IdentityJsonPaths` and `SecurableElements` paths from the resource's ApiSchema.json. They should be included twice, with `Old` and `New` prefixes applied directly to the source column name, for example `OldSchoolId_Unified` and `NewStudent_DocumentId`.

Each `TrackedChangeColumnInfo` carries `IsOldColumnNullable` and `IsNewColumnNullable` separately because tombstones populate only old values. `IsOldColumnNullable` follows the tracked source value's nullability. `IsNewColumnNullable` is normally `true` because delete tombstones leave `New*` columns null; key-change rows populate the new values when present.

If a path is a descriptor reference, the inventory will include two columns: the descriptor's `Namespace` and `CodeValue`. The corresponding `TrackedChangeDescriptorJoinInfo` describes the join to `dms.Descriptor` that trigger emitters use for old and new row images. The two `TrackedChangeColumnInfo` entries reference that table-level join by `DescriptorJoinName`; they do not duplicate the join definition.

If a path is backed by a column that participates in key unification, include the canonical storage column instead of the generated alias column. This is both a de-duplication rule and an ODS compatibility rule: tracked-change rows record the shared stored identity value, not each presence-gated binding-site value.

If the same canonical column has been included multiple times (because of key unification) only include it once.

For people `SecurableElements` paths, we will also store the Student, Contact, or Staff `DocumentId`. The corresponding `TrackedChangePersonJoinInfo` describes the resource-table join path needed to reach the person resource for old and new row images. The `TrackedChangeColumnInfo` entry references that table-level join by `PersonJoinName`; it does not duplicate the join definition.

Some `SecurableElements` paths might result in nullable `Old*` and `New*` value columns because of overrides, such as the `StudentAssessment` override.

Apart from people `SecurableElements`, we do not need to store surrogate keys, such as DocumentIds or DescriptorIds.

The inventory will also contain a `TrackedChangeTableInfo` for each concrete abstract resource to support SecurableElement overrides, such as `OrganizationDepartment`.

MSSQL table definition example for the Grade resource:

```sql
CREATE TABLE [tracked_changes_edfi].[Grade]
(
    [OldStudentSectionAssociation_BeginDate] date NOT NULL,
    [OldGradeTypeDescriptor_Namespace] nvarchar(255) NOT NULL,
    [OldGradeTypeDescriptor_CodeValue] nvarchar(50) NOT NULL,
    [OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace] nvarchar(255) NOT NULL,
    [OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue] nvarchar(50) NOT NULL,
    [OldGradingPeriodGradingPeriod_GradingPeriodName] nvarchar(60) NOT NULL,
    [OldSchoolYear_Unified] integer NOT NULL,
    [OldStudentSectionAssociation_LocalCourseCode] nvarchar(60) NOT NULL,
    [OldSchoolId_Unified] bigint NOT NULL,
    [OldStudentSectionAssociation_SectionIdentifier] nvarchar(255) NOT NULL,
    [OldStudentSectionAssociation_SessionName] nvarchar(60) NOT NULL,
    [OldStudentSectionAssociation_StudentUniqueId] nvarchar(32) NOT NULL,
    [OldStudentSectionAssociation_Student_DocumentId] bigint NOT NULL,
    
    [NewStudentSectionAssociation_BeginDate] date NULL,
    [NewGradeTypeDescriptor_Namespace] nvarchar(255) NULL,
    [NewGradeTypeDescriptor_CodeValue] nvarchar(50) NULL,
    [NewGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace] nvarchar(255) NULL,
    [NewGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue] nvarchar(50) NULL,
    [NewGradingPeriodGradingPeriod_GradingPeriodName] nvarchar(60) NULL,
    [NewSchoolYear_Unified] integer NULL,
    [NewStudentSectionAssociation_LocalCourseCode] nvarchar(60) NULL,
    [NewSchoolId_Unified] bigint NULL,
    [NewStudentSectionAssociation_SectionIdentifier] nvarchar(255) NULL,
    [NewStudentSectionAssociation_SessionName] nvarchar(60) NULL,
    [NewStudentSectionAssociation_StudentUniqueId] nvarchar(32) NULL,
    [NewStudentSectionAssociation_Student_DocumentId] bigint NULL,

    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Grade_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_Grade] PRIMARY KEY CLUSTERED ([ChangeVersion])
);
```

All descriptor types will be stored in the same `TrackedChangeTableInfo` of kind `SharedDescriptor`, rendered as `tracked_changes_edfi.Descriptor`, even descriptors that belong to extensions. This follows ODS's convention. The shared descriptor tracked-change table covers every `ConcreteResourceModel` whose `StorageKind = SharedDescriptorTable` in the same `DerivedRelationalModelSet`; manifests and runtime query planning derive descriptor coverage from that existing resource inventory rather than duplicating a coverage list on the tracked-change table.

MSSQL table definition example for the shared `tracked_changes_edfi.Descriptor`:

```sql
CREATE TABLE [tracked_changes_edfi].[Descriptor]
(
  	[Discriminator] nvarchar(128) NOT NULL,

    [OldNamespace] nvarchar(255) NOT NULL,
    [OldCodeValue] nvarchar(50) NOT NULL,

    [NewNamespace] nvarchar(255) NULL,
    [NewCodeValue] nvarchar(50) NULL,

    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Descriptor_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_Descriptor] PRIMARY KEY CLUSTERED ([ChangeVersion])
)
```

##### Operational considerations: tracked-change table volume

The per-resource `tracked_changes_*` tables and the shared `tracked_changes_edfi.Descriptor` are append-only stores of deletes (tombstones) and key-changes. Over very long-running deployments, or in workloads with a high volume of deletes or identity changes, they can take up a disproportionate share of database storage.

DMS does not automate truncation of these tables in v1. The same operational guidance ODS surfaces applies: implementers can truncate the affected `tracked_changes_*` tables periodically when retention is no longer required. The only consequence is loss of visibility into deletes and key-changes that occurred before the truncation — clients whose last-seen `ChangeVersion` predates the truncation point will not see those events on subsequent `/deletes` and `/keyChanges` reads and must perform a full resync.

Because typical ODS deployments rotate per school year, ODS does not automate this either, and DMS adopts the same default.

#### Concrete-resource `ContentVersion` / `ContentLastModifiedAt` mirror

Each `ConcreteResourceModel` with `StorageKind = RelationalTables` gets two columns mirrored onto its root table: `ContentVersion` and `ContentLastModifiedAt`. The shared `dms.Descriptor` table gets the same two columns. The columns are kept in lock-step with `dms.Document` by the existing `*_Stamp` triggers (see the next subsection).

This mirror lets SQL-side integrators (and the existing ODS-shaped community tooling, including the API Publisher and dashboard datastores) read change-version and last-modified per row without joining `dms.Document`, and lets live resource and descriptor endpoints serve `?minChangeVersion=X&maxChangeVersion=Y` as a single-table range seek.

**Scope** — receive the columns and a supporting index:

- Every `ConcreteResourceModel` whose `StorageKind = RelationalTables`, regardless of project schema. This includes core resource roots (`edfi.Student`, `edfi.School`, …) and extension-project resource roots (`tpdm.Candidate`, …); they are all roots of root resources.
- The shared descriptor table `dms.Descriptor`.

Do not receive the columns:

- Child / collection tables (`edfi.SchoolAddress`, …). No independent endpoint and no window-filter consumer.
- `*Extension` resource-extension tables (`tpdm.ContactExtension`, …). Not new resources; the `DocumentId` is shared with the base document, so the mirror on the base root is authoritative.
- Abstract-resource union views. The columns surface through the underlying concrete root tables.

**Invariants:**

1. `dms.Document` remains the source of truth. The mirror is written only by the `*_Stamp` triggers; the relational write path MUST NOT include `ContentVersion` or `ContentLastModifiedAt` in client-writable column projections.
2. For every committed transaction, `<root>.ContentVersion = dms.Document.ContentVersion` and `<root>.ContentLastModifiedAt = dms.Document.ContentLastModifiedAt` for the same `DocumentId`.
3. For representation-changing updates and deletes, a trigger fire allocates exactly one `dms.ChangeVersionSequence` value per affected document and writes it to both `dms.Document` and the mirror, captured via `OUTPUT` (SQL Server) / `RETURNING` (PostgreSQL). Root-resource and descriptor inserts are the exception: `dms.Document` defaults allocate the initial content stamp before the root/descriptor row is inserted, and the root/descriptor insert trigger copies that existing stamp to the mirror without allocating another content version. `NEXT VALUE FOR` MUST NOT be invoked a second time for the same document in the same fire.
4. `IdentityVersion` / `IdentityLastModifiedAt` are not mirrored; they remain internal-only on `dms.Document`.
5. The `affectedDocs` CTE inside each `*_Stamp` trigger MUST exclude rows whose `inserted` / `deleted` images differ **only** in the four stamp columns (`ContentVersion`, `ContentLastModifiedAt`, `IdentityVersion`, `IdentityLastModifiedAt`). This tightens the existing "no-op updates are not representation changes" rule from [update-tracking.md](update-tracking.md) §"Stamping rules" and is the mechanism that keeps nested-trigger fire safe when a child / `_ext` trigger writes to the root mirror.

**MSSQL column additions** — illustrated on `edfi.Student`:

```sql
ALTER TABLE [edfi].[Student]
    ADD [ContentVersion] bigint NOT NULL
        CONSTRAINT DF_Student_ContentVersion DEFAULT 0,
    [ContentLastModifiedAt] datetime2(7) NOT NULL
        CONSTRAINT DF_Student_ContentLastModifiedAt DEFAULT (sysutcdatetime());

CREATE INDEX [IX_Student_ContentVersion] ON [edfi].[Student] ([ContentVersion]);
```

PostgreSQL is the same shape with `DEFAULT 0` and `now()` defaults and a `timestamp with time zone` column type.

The mirror `ContentVersion` default is a non-null sentinel, not a real change-version allocation. The production write path always goes through the `*_Stamp` trigger. Root-resource and descriptor inserts copy the existing `dms.Document` content stamp initialized by document defaults; updates, deletes, child writes, and `_ext` writes allocate a fresh `dms.Document.ContentVersion` and mirror that captured value.

**`dms.Descriptor` index is composite.** Because every descriptor query is qualified by `Discriminator`, the descriptor index is `IX_Descriptor_Discriminator_ContentVersion (Discriminator, ContentVersion)` rather than a single-column `IX_Descriptor_ContentVersion`. Resource-root indexes stay single-column because each resource root has its own table and no analogous "type" filter precedes the change-version range.

**Compiled-mapping-set additions** (defined in [compiled-mapping-set.md](compiled-mapping-set.md)):

- The per-resource derivation pass that builds `ConcreteResourceModel.RelationalModel` adds two synthesized columns to the root `DbTableModel` whenever `StorageKind = RelationalTables`. Their `ColumnKind` is `MirroredContentVersion` and `MirroredContentLastModifiedAt` respectively (defined in [flattening-reconstitution.md](flattening-reconstitution.md)). `SourceJsonPath` and `TargetResource` are null. The write-path `TableWritePlan.ColumnBindings` exclusion rule (also defined in [flattening-reconstitution.md](flattening-reconstitution.md)) keeps them out of client-writable column lists; dialect emitters render the correct DDL defaults for each kind (`0` for `MirroredContentVersion`, current-UTC for `MirroredContentLastModifiedAt`).
- For `StorageKind = SharedDescriptorTable`, the columns are added by the core DDL pass that owns `dms.Descriptor`, not by the per-resource pass, because `dms.Descriptor` is a core table.
- `DeriveIndexInventoryPass` adds one `IX_<Table>_ContentVersion` per in-scope root table and `IX_Descriptor_Discriminator_ContentVersion` on `dms.Descriptor` into `IndexesInCreateOrder`.
- `DbTriggerInfo` for entries with `Kind = DocumentStamping` gains a required `MirrorStampTargetTable: DbTableName` field. The derivation pass assigns the target by rule: same table for root-table triggers, resource's root for child / `_ext` triggers, `dms.Descriptor` for the descriptor trigger. Dialect emitters render the mirror UPDATE against `MirrorStampTargetTable` and MUST NOT re-derive the target from the trigger's source table.

#### Triggers that populate the `tracked_changes*` tables

The existing `*_Stamp` trigger inventory entries will be updated to store tombstones and key changes by attaching `DocumentStamping.ChangeTracking` to the affected `TriggerKindParameters.DocumentStamping` entries. The parameter points to the `TrackedChangeTableInfo` for the source table.

`DocumentStamping.ChangeTracking` extends the existing `TriggerKindParameters.DocumentStamping` trigger render path. It does not introduce a second trigger with an independent key-change predicate.

The dialect trigger emitters must use the tracked-change inventory directly. They must not re-derive old/new columns, descriptor joins, person joins, or key-change predicates from SQL text or from ad hoc DDL-only metadata.

For updates, the key-change workset is the owning trigger's `IdentityProjectionColumns` null-safe old/new value-diff workset. This is the same workset used to decide which documents receive identity stamp updates. Under key unification, Change Query key-change detection uses the canonical storage columns, not the presence-gated alias expressions from [key-unification.md](key-unification.md). This intentionally follows legacy ODS behavior: ODS also stores equality-constrained identity parts once and reuses the same physical value across unified paths, but its tracked key-change triggers compare the stored key columns rather than a per-reference or per-path presence-gated value.

A presence-only change, such as attaching or detaching an optional reference while the shared canonical key value remains unchanged, is a representation change and receives a new content `ChangeVersion`, but it is not a Change Queries key-change event. A key-change row is inserted only when the effective resource identity storage values change.

This is a Change Queries-specific exception to the generic trigger guidance in [key-unification.md](key-unification.md). This `DocumentStamping` / `ChangeTracking` trigger path intentionally uses ODS-compatible canonical storage semantics for its shared identity-stamp and key-change workset. Other identity-maintenance trigger paths that need API binding-path semantics remain governed by the presence-gated alias guidance in [key-unification.md](key-unification.md).

Descriptor paths use the table-level `TrackedChangeDescriptorJoinInfo` entries to join with `dms.Descriptor` and store the descriptor's `Namespace` and `CodeValue`. Value columns identify the needed descriptor join by `DescriptorJoinName`.

People `SecurableElements` paths use table-level `TrackedChangePersonJoinInfo` entries to join until they reach the people resource and store the person `DocumentId`. Value columns identify the needed person join by `PersonJoinName`. The derivation pass can use the same resolution rules as `ResolveSecurableElementColumnPath`; see [auth.md](auth.md) for more information.

<details>
  <summary>MSSQL trigger definition excerpt for the Grade resource: (Click to expand)</summary>

```sql
CREATE OR ALTER TRIGGER [edfi].[TR_Grade_Stamp]
ON [edfi].[Grade]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @stamped TABLE (
        [DocumentId] bigint NOT NULL PRIMARY KEY,
        [ContentVersion] bigint NOT NULL,
        [ContentLastModifiedAt] datetime2(7) NOT NULL
    );

    -- Root-resource INSERT rows copy the dms.Document defaults that were allocated when
    -- the document row was created. Child / _ext INSERT rows still flow through
    -- affectedDocs below because they change an existing root document.
    INSERT INTO @stamped ([DocumentId], [ContentVersion], [ContentLastModifiedAt])
    SELECT d.[DocumentId], d.[ContentVersion], d.[ContentLastModifiedAt]
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL;

    ;WITH affectedDocs AS (
        -- Avoids mirror-stamp self-fires; see "Concrete-resource ContentVersion / ContentLastModifiedAt mirror" above).
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND (
            -- Generated null-safe representation-diff predicate across all non-stamp columns.
            (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL))
            OR (i.[SchoolId_Unified] <> del.[SchoolId_Unified] OR (i.[SchoolId_Unified] IS NULL AND del.[SchoolId_Unified] IS NOT NULL) OR (i.[SchoolId_Unified] IS NOT NULL AND del.[SchoolId_Unified] IS NULL))
            -- Other generated non-stamp column predicates omitted.
        )
        UNION
        SELECT del.[DocumentId]
        FROM deleted del
        LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
        WHERE i.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolId_Unified] <> del.[SchoolId_Unified] OR (i.[SchoolId_Unified] IS NULL AND del.[SchoolId_Unified] IS NOT NULL) OR (i.[SchoolId_Unified] IS NOT NULL AND del.[SchoolId_Unified] IS NULL)) OR (i.[SchoolYear_Unified] <> del.[SchoolYear_Unified] OR (i.[SchoolYear_Unified] IS NULL AND del.[SchoolYear_Unified] IS NOT NULL) OR (i.[SchoolYear_Unified] IS NOT NULL AND del.[SchoolYear_Unified] IS NULL)) OR (i.[GradingPeriodGradingPeriod_DocumentId] <> del.[GradingPeriodGradingPeriod_DocumentId] OR (i.[GradingPeriodGradingPeriod_DocumentId] IS NULL AND del.[GradingPeriodGradingPeriod_DocumentId] IS NOT NULL) OR (i.[GradingPeriodGradingPeriod_DocumentId] IS NOT NULL AND del.[GradingPeriodGradingPeriod_DocumentId] IS NULL)) OR (i.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] <> del.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] OR (i.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] IS NULL AND del.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] IS NOT NULL) OR (i.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] IS NOT NULL AND del.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] IS NULL)) OR (CAST(i.[GradingPeriodGradingPeriod_GradingPeriodName] AS varbinary(max)) <> CAST(del.[GradingPeriodGradingPeriod_GradingPeriodName] AS varbinary(max)) OR (i.[GradingPeriodGradingPeriod_GradingPeriodName] IS NULL AND del.[GradingPeriodGradingPeriod_GradingPeriodName] IS NOT NULL) OR (i.[GradingPeriodGradingPeriod_GradingPeriodName] IS NOT NULL AND del.[GradingPeriodGradingPeriod_GradingPeriodName] IS NULL)) OR (i.[StudentSectionAssociation_DocumentId] <> del.[StudentSectionAssociation_DocumentId] OR (i.[StudentSectionAssociation_DocumentId] IS NULL AND del.[StudentSectionAssociation_DocumentId] IS NOT NULL) OR (i.[StudentSectionAssociation_DocumentId] IS NOT NULL AND del.[StudentSectionAssociation_DocumentId] IS NULL)) OR (i.[StudentSectionAssociation_BeginDate] <> del.[StudentSectionAssociation_BeginDate] OR (i.[StudentSectionAssociation_BeginDate] IS NULL AND del.[StudentSectionAssociation_BeginDate] IS NOT NULL) OR (i.[StudentSectionAssociation_BeginDate] IS NOT NULL AND del.[StudentSectionAssociation_BeginDate] IS NULL)) OR (CAST(i.[StudentSectionAssociation_LocalCourseCode] AS varbinary(max)) <> CAST(del.[StudentSectionAssociation_LocalCourseCode] AS varbinary(max)) OR (i.[StudentSectionAssociation_LocalCourseCode] IS NULL AND del.[StudentSectionAssociation_LocalCourseCode] IS NOT NULL) OR (i.[StudentSectionAssociation_LocalCourseCode] IS NOT NULL AND del.[StudentSectionAssociation_LocalCourseCode] IS NULL)) OR (CAST(i.[StudentSectionAssociation_SectionIdentifier] AS varbinary(max)) <> CAST(del.[StudentSectionAssociation_SectionIdentifier] AS varbinary(max)) OR (i.[StudentSectionAssociation_SectionIdentifier] IS NULL AND del.[StudentSectionAssociation_SectionIdentifier] IS NOT NULL) OR (i.[StudentSectionAssociation_SectionIdentifier] IS NOT NULL AND del.[StudentSectionAssociation_SectionIdentifier] IS NULL)) OR (CAST(i.[StudentSectionAssociation_SessionName] AS varbinary(max)) <> CAST(del.[StudentSectionAssociation_SessionName] AS varbinary(max)) OR (i.[StudentSectionAssociation_SessionName] IS NULL AND del.[StudentSectionAssociation_SessionName] IS NOT NULL) OR (i.[StudentSectionAssociation_SessionName] IS NOT NULL AND del.[StudentSectionAssociation_SessionName] IS NULL)) OR (CAST(i.[StudentSectionAssociation_StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentSectionAssociation_StudentUniqueId] AS varbinary(max)) OR (i.[StudentSectionAssociation_StudentUniqueId] IS NULL AND del.[StudentSectionAssociation_StudentUniqueId] IS NOT NULL) OR (i.[StudentSectionAssociation_StudentUniqueId] IS NOT NULL AND del.[StudentSectionAssociation_StudentUniqueId] IS NULL)) OR (i.[GradeTypeDescriptor_DescriptorId] <> del.[GradeTypeDescriptor_DescriptorId] OR (i.[GradeTypeDescriptor_DescriptorId] IS NULL AND del.[GradeTypeDescriptor_DescriptorId] IS NOT NULL) OR (i.[GradeTypeDescriptor_DescriptorId] IS NOT NULL AND del.[GradeTypeDescriptor_DescriptorId] IS NULL)) OR (i.[PerformanceBaseConversionDescriptor_DescriptorId] <> del.[PerformanceBaseConversionDescriptor_DescriptorId] OR (i.[PerformanceBaseConversionDescriptor_DescriptorId] IS NULL AND del.[PerformanceBaseConversionDescriptor_DescriptorId] IS NOT NULL) OR (i.[PerformanceBaseConversionDescriptor_DescriptorId] IS NOT NULL AND del.[PerformanceBaseConversionDescriptor_DescriptorId] IS NULL)) OR (i.[CurrentGradeAsOfDate] <> del.[CurrentGradeAsOfDate] OR (i.[CurrentGradeAsOfDate] IS NULL AND del.[CurrentGradeAsOfDate] IS NOT NULL) OR (i.[CurrentGradeAsOfDate] IS NOT NULL AND del.[CurrentGradeAsOfDate] IS NULL)) OR (i.[CurrentGradeIndicator] <> del.[CurrentGradeIndicator] OR (i.[CurrentGradeIndicator] IS NULL AND del.[CurrentGradeIndicator] IS NOT NULL) OR (i.[CurrentGradeIndicator] IS NOT NULL AND del.[CurrentGradeIndicator] IS NULL)) OR (CAST(i.[DiagnosticStatement] AS varbinary(max)) <> CAST(del.[DiagnosticStatement] AS varbinary(max)) OR (i.[DiagnosticStatement] IS NULL AND del.[DiagnosticStatement] IS NOT NULL) OR (i.[DiagnosticStatement] IS NOT NULL AND del.[DiagnosticStatement] IS NULL)) OR (CAST(i.[GradeEarnedDescription] AS varbinary(max)) <> CAST(del.[GradeEarnedDescription] AS varbinary(max)) OR (i.[GradeEarnedDescription] IS NULL AND del.[GradeEarnedDescription] IS NOT NULL) OR (i.[GradeEarnedDescription] IS NOT NULL AND del.[GradeEarnedDescription] IS NULL)) OR (CAST(i.[LetterGradeEarned] AS varbinary(max)) <> CAST(del.[LetterGradeEarned] AS varbinary(max)) OR (i.[LetterGradeEarned] IS NULL AND del.[LetterGradeEarned] IS NOT NULL) OR (i.[LetterGradeEarned] IS NOT NULL AND del.[LetterGradeEarned] IS NULL)) OR (i.[NumericGradeEarned] <> del.[NumericGradeEarned] OR (i.[NumericGradeEarned] IS NULL AND del.[NumericGradeEarned] IS NOT NULL) OR (i.[NumericGradeEarned] IS NOT NULL AND del.[NumericGradeEarned] IS NULL))
    )
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt] INTO @stamped
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];

    -- Mirror the stamped values onto MirrorStampTargetTable from this trigger's DbTriggerInfo.
    -- For TR_Grade_Stamp the target is [edfi].[Grade] itself; for child / _ext stamping triggers
    -- the target is the resource's root table (e.g. TR_SchoolAddress_Stamp targets [edfi].[School]).
    UPDATE r
    SET r.[ContentVersion] = s.[ContentVersion],
        r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
    FROM [edfi].[Grade] r
    INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];

    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        -- Store tombstone
        INSERT INTO [tracked_changes_edfi].[Grade] (
            [OldStudentSectionAssociation_BeginDate],
            [OldGradeTypeDescriptor_Namespace],
            [OldGradeTypeDescriptor_CodeValue],
            [OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace],
            [OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue],
            [OldGradingPeriodGradingPeriod_GradingPeriodName],
            [OldSchoolYear_Unified],
            [OldStudentSectionAssociation_LocalCourseCode],
            [OldSchoolId_Unified],
            [OldStudentSectionAssociation_SectionIdentifier],
            [OldStudentSectionAssociation_SessionName],
            [OldStudentSectionAssociation_StudentUniqueId],
            [OldStudentSectionAssociation_Student_DocumentId],
            [NewStudentSectionAssociation_BeginDate],
            [NewGradeTypeDescriptor_Namespace],
            [NewGradeTypeDescriptor_CodeValue],
            [NewGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace],
            [NewGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue],
            [NewGradingPeriodGradingPeriod_GradingPeriodName],
            [NewSchoolYear_Unified],
            [NewStudentSectionAssociation_LocalCourseCode],
            [NewSchoolId_Unified],
            [NewStudentSectionAssociation_SectionIdentifier],
            [NewStudentSectionAssociation_SessionName],
            [NewStudentSectionAssociation_StudentUniqueId],
            [NewStudentSectionAssociation_Student_DocumentId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[StudentSectionAssociation_BeginDate],
            oldGradeTypeDescriptor.[Namespace],
            oldGradeTypeDescriptor.[CodeValue],
            oldGradingPeriodDescriptor.[Namespace],
            oldGradingPeriodDescriptor.[CodeValue],
            del.[GradingPeriodGradingPeriod_GradingPeriodName],
            del.[SchoolYear_Unified],
            del.[StudentSectionAssociation_LocalCourseCode],
            del.[SchoolId_Unified],
            del.[StudentSectionAssociation_SectionIdentifier],
            del.[StudentSectionAssociation_SessionName],
            del.[StudentSectionAssociation_StudentUniqueId],
            oldStudent.[DocumentId],
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            doc.[DocumentUuid],
            doc.[ContentVersion]
        FROM deleted del
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = del.[DocumentId]
        INNER JOIN [dms].[Descriptor] oldGradeTypeDescriptor ON oldGradeTypeDescriptor.[DocumentId] = del.[GradeTypeDescriptor_DescriptorId]
        INNER JOIN [dms].[Descriptor] oldGradingPeriodDescriptor ON oldGradingPeriodDescriptor.[DocumentId] = del.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId]
        INNER JOIN [edfi].[StudentSectionAssociation] oldStudentSectionAssociation ON oldStudentSectionAssociation.[DocumentId] = del.[StudentSectionAssociation_DocumentId]
        INNER JOIN [edfi].[Student] oldStudent ON oldStudent.[DocumentId] = oldStudentSectionAssociation.[Student_DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DECLARE @identityChangedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY, [ContentVersion] bigint NOT NULL);
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @identityChangedDocs ([DocumentId], [ContentVersion])
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[GradeTypeDescriptor_DescriptorId] <> del.[GradeTypeDescriptor_DescriptorId] OR (i.[GradeTypeDescriptor_DescriptorId] IS NULL AND del.[GradeTypeDescriptor_DescriptorId] IS NOT NULL) OR (i.[GradeTypeDescriptor_DescriptorId] IS NOT NULL AND del.[GradeTypeDescriptor_DescriptorId] IS NULL)) OR (i.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] <> del.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] OR (i.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] IS NULL AND del.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] IS NOT NULL) OR (i.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] IS NOT NULL AND del.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId] IS NULL)) OR (CAST(i.[GradingPeriodGradingPeriod_GradingPeriodName] AS varbinary(max)) <> CAST(del.[GradingPeriodGradingPeriod_GradingPeriodName] AS varbinary(max)) OR (i.[GradingPeriodGradingPeriod_GradingPeriodName] IS NULL AND del.[GradingPeriodGradingPeriod_GradingPeriodName] IS NOT NULL) OR (i.[GradingPeriodGradingPeriod_GradingPeriodName] IS NOT NULL AND del.[GradingPeriodGradingPeriod_GradingPeriodName] IS NULL)) OR (i.[SchoolId_Unified] <> del.[SchoolId_Unified] OR (i.[SchoolId_Unified] IS NULL AND del.[SchoolId_Unified] IS NOT NULL) OR (i.[SchoolId_Unified] IS NOT NULL AND del.[SchoolId_Unified] IS NULL)) OR (i.[SchoolYear_Unified] <> del.[SchoolYear_Unified] OR (i.[SchoolYear_Unified] IS NULL AND del.[SchoolYear_Unified] IS NOT NULL) OR (i.[SchoolYear_Unified] IS NOT NULL AND del.[SchoolYear_Unified] IS NULL)) OR (i.[StudentSectionAssociation_BeginDate] <> del.[StudentSectionAssociation_BeginDate] OR (i.[StudentSectionAssociation_BeginDate] IS NULL AND del.[StudentSectionAssociation_BeginDate] IS NOT NULL) OR (i.[StudentSectionAssociation_BeginDate] IS NOT NULL AND del.[StudentSectionAssociation_BeginDate] IS NULL)) OR (CAST(i.[StudentSectionAssociation_LocalCourseCode] AS varbinary(max)) <> CAST(del.[StudentSectionAssociation_LocalCourseCode] AS varbinary(max)) OR (i.[StudentSectionAssociation_LocalCourseCode] IS NULL AND del.[StudentSectionAssociation_LocalCourseCode] IS NOT NULL) OR (i.[StudentSectionAssociation_LocalCourseCode] IS NOT NULL AND del.[StudentSectionAssociation_LocalCourseCode] IS NULL)) OR (CAST(i.[StudentSectionAssociation_SectionIdentifier] AS varbinary(max)) <> CAST(del.[StudentSectionAssociation_SectionIdentifier] AS varbinary(max)) OR (i.[StudentSectionAssociation_SectionIdentifier] IS NULL AND del.[StudentSectionAssociation_SectionIdentifier] IS NOT NULL) OR (i.[StudentSectionAssociation_SectionIdentifier] IS NOT NULL AND del.[StudentSectionAssociation_SectionIdentifier] IS NULL)) OR (CAST(i.[StudentSectionAssociation_SessionName] AS varbinary(max)) <> CAST(del.[StudentSectionAssociation_SessionName] AS varbinary(max)) OR (i.[StudentSectionAssociation_SessionName] IS NULL AND del.[StudentSectionAssociation_SessionName] IS NOT NULL) OR (i.[StudentSectionAssociation_SessionName] IS NOT NULL AND del.[StudentSectionAssociation_SessionName] IS NULL)) OR (CAST(i.[StudentSectionAssociation_StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentSectionAssociation_StudentUniqueId] AS varbinary(max)) OR (i.[StudentSectionAssociation_StudentUniqueId] IS NULL AND del.[StudentSectionAssociation_StudentUniqueId] IS NOT NULL) OR (i.[StudentSectionAssociation_StudentUniqueId] IS NOT NULL AND del.[StudentSectionAssociation_StudentUniqueId] IS NULL));

        -- Store key change
        INSERT INTO [tracked_changes_edfi].[Grade] (
            [OldStudentSectionAssociation_BeginDate],
            [OldGradeTypeDescriptor_Namespace],
            [OldGradeTypeDescriptor_CodeValue],
            [OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace],
            [OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue],
            [OldGradingPeriodGradingPeriod_GradingPeriodName],
            [OldSchoolYear_Unified],
            [OldStudentSectionAssociation_LocalCourseCode],
            [OldSchoolId_Unified],
            [OldStudentSectionAssociation_SectionIdentifier],
            [OldStudentSectionAssociation_SessionName],
            [OldStudentSectionAssociation_StudentUniqueId],
            [OldStudentSectionAssociation_Student_DocumentId],
            [NewStudentSectionAssociation_BeginDate],
            [NewGradeTypeDescriptor_Namespace],
            [NewGradeTypeDescriptor_CodeValue],
            [NewGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace],
            [NewGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue],
            [NewGradingPeriodGradingPeriod_GradingPeriodName],
            [NewSchoolYear_Unified],
            [NewStudentSectionAssociation_LocalCourseCode],
            [NewSchoolId_Unified],
            [NewStudentSectionAssociation_SectionIdentifier],
            [NewStudentSectionAssociation_SessionName],
            [NewStudentSectionAssociation_StudentUniqueId],
            [NewStudentSectionAssociation_Student_DocumentId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[StudentSectionAssociation_BeginDate],
            oldGradeTypeDescriptor.[Namespace],
            oldGradeTypeDescriptor.[CodeValue],
            oldGradingPeriodDescriptor.[Namespace],
            oldGradingPeriodDescriptor.[CodeValue],
            del.[GradingPeriodGradingPeriod_GradingPeriodName],
            del.[SchoolYear_Unified],
            del.[StudentSectionAssociation_LocalCourseCode],
            del.[SchoolId_Unified],
            del.[StudentSectionAssociation_SectionIdentifier],
            del.[StudentSectionAssociation_SessionName],
            del.[StudentSectionAssociation_StudentUniqueId],
            oldStudent.[DocumentId],
            i.[StudentSectionAssociation_BeginDate],
            newGradeTypeDescriptor.[Namespace],
            newGradeTypeDescriptor.[CodeValue],
            newGradingPeriodDescriptor.[Namespace],
            newGradingPeriodDescriptor.[CodeValue],
            i.[GradingPeriodGradingPeriod_GradingPeriodName],
            i.[SchoolYear_Unified],
            i.[StudentSectionAssociation_LocalCourseCode],
            i.[SchoolId_Unified],
            i.[StudentSectionAssociation_SectionIdentifier],
            i.[StudentSectionAssociation_SessionName],
            i.[StudentSectionAssociation_StudentUniqueId],
            newStudent.[DocumentId],
            doc.[DocumentUuid],
            identityChangedDocs.[ContentVersion]
        FROM @identityChangedDocs identityChangedDocs
        INNER JOIN inserted i ON i.[DocumentId] = identityChangedDocs.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Descriptor] oldGradeTypeDescriptor ON oldGradeTypeDescriptor.[DocumentId] = del.[GradeTypeDescriptor_DescriptorId]
        INNER JOIN [dms].[Descriptor] oldGradingPeriodDescriptor ON oldGradingPeriodDescriptor.[DocumentId] = del.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId]
        INNER JOIN [edfi].[StudentSectionAssociation] oldStudentSectionAssociation ON oldStudentSectionAssociation.[DocumentId] = del.[StudentSectionAssociation_DocumentId]
        INNER JOIN [edfi].[Student] oldStudent ON oldStudent.[DocumentId] = oldStudentSectionAssociation.[Student_DocumentId]
        INNER JOIN [dms].[Descriptor] newGradeTypeDescriptor ON newGradeTypeDescriptor.[DocumentId] = i.[GradeTypeDescriptor_DescriptorId]
        INNER JOIN [dms].[Descriptor] newGradingPeriodDescriptor ON newGradingPeriodDescriptor.[DocumentId] = i.[GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId]
        INNER JOIN [edfi].[StudentSectionAssociation] newStudentSectionAssociation ON newStudentSectionAssociation.[DocumentId] = i.[StudentSectionAssociation_DocumentId]
        INNER JOIN [edfi].[Student] newStudent ON newStudent.[DocumentId] = newStudentSectionAssociation.[Student_DocumentId];
    END
END;
```
</details>

##### Cascade-ordering requirement for deletes

Because the `_Stamp` trigger's DELETE branch joins `dms.Document` to read `DocumentUuid` and `ContentVersion`, DMS MUST delete the concrete resource row (or the `dms.Descriptor` row for descriptor resources) before deleting the corresponding `dms.Document` row, within the same transaction. If `dms.Document` were deleted first, the `ON DELETE CASCADE` FK from the resource row to `dms.Document(DocumentId)` would remove the resource row inside the same statement, and the resource's `AFTER DELETE` trigger would fire after the parent row is already gone, causing the `INNER JOIN [dms].[Document]` (and the trigger's leading `UPDATE [dms].[Document] SET [ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence]`) to match no rows and silently drop the tombstone.

DMS therefore issues two `DELETE` statements per document deletion, in order, within the same transaction:

1. `DELETE FROM "<projectSchema>"."<ResourceTable>" WHERE "DocumentId" = @documentId` (or `DELETE FROM "dms"."Descriptor" WHERE "DocumentId" = @documentId` for descriptor resources). This fires the resource's `_Stamp` trigger while `dms.Document` is still present, so the trigger's `UPDATE` allocates a fresh `ChangeVersion` on the parent and the tombstone `INSERT` reads `DocumentUuid` and the freshly bumped `ContentVersion` via the existing join.
2. `DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId`. This finalizes the lifecycle and cascades to `dms.DocumentCache` and `dms.ReferentialIdentity` as defined in [data-model.md](data-model.md).

The leading trigger `UPDATE` of `dms.Document.ContentVersion` therefore runs against a row that the second statement removes. This is intentional: it gives the tombstone the standard read-after-bump `ChangeVersion`, at the cost of one sequence value and one transient row write per delete.

The mirror UPDATE (the second UPDATE in the trigger body, against `MirrorStampTargetTable`) is naturally a zero-row no-op for deleted documents because the resource row is already gone before the trigger fires. The `dms.Document` UPDATE still bumps `ContentVersion` via the first half of the trigger, which is what the tombstone `INSERT` reads from — so the mirror's absence here does not affect the tracked-change row.

Root deletes have one additional trigger-ordering contract: a supported resource or descriptor delete produces exactly one visible tombstone for the deleted root. The tombstone's `ChangeVersion` is the version allocated by the root resource or descriptor delete trigger before the corresponding `dms.Document` row is removed.

Child, nested-child, and `_ext` table deletes caused by database cascades from that root delete MUST NOT create another visible root tombstone, and MUST NOT leave a later visible root stamp that can move a Change Query extraction watermark past the root tombstone. This contract applies to PostgreSQL and SQL Server despite their different trigger and cascade execution behavior.

The `ON DELETE CASCADE` FK from the resource row to `dms.Document` (see [data-model.md](data-model.md)) is retained as a referential-integrity safety net. Any direct `DELETE FROM dms.Document` issued outside the DMS write path will succeed without producing a tombstone; this is acceptable because the supported deletion path is exclusively through DMS.

There is no `*_Stamp` trigger in `dms.Descriptor`, so we will create one that follows the existing convention.

### Authorization

This section covers authorization concerns specific to the `/deletes` and `/keyChanges` endpoints. The base authorization design lives in [auth.md](auth.md).

#### The `ReadChanges` action

DMS retains the distinct `ReadChanges` action introduced by ODS. Requests to `/deletes` or `/keyChanges` for a resource whose claim set does not grant `ReadChanges` return `403 Forbidden` with the `urn:ed-fi:api:security:authorization:access-denied:action` ProblemDetails described in [auth.md](auth.md).

#### Strategies

DMS will support the same authorization strategies as ODS, with the exception of custom view-based strategies which will be deferred until DMS v1.1. 

Meaning that the next strategies have to be implemented for the `/deletes` and `/keyChanges` endpoints:
`NoFurtherAuthorizationRequired`                                      
`NamespaceBased`                                                      
`RelationshipsWithEdOrgsOnly`                                         
`RelationshipsWithEdOrgsOnlyInverted`                            
`RelationshipsWithEdOrgsAndPeopleIncludingDeletes`               
`RelationshipsWithStudentsOnlyIncludingDeletes`                       
`RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes`  

The combinator semantics — AND/OR mixing across strategy categories and execution order — defined in [auth.md](auth.md) apply unchanged for `ReadChanges`.

##### Differences when compared to live resource endpoint auth logic

Strategies that share a name across `Read` and `ReadChanges` (`NoFurtherAuthorizationRequired`, `NamespaceBased`, `RelationshipsWithEdOrgsOnly`, `RelationshipsWithEdOrgsOnlyInverted`) reuse the original authorization views. Strategies whose name carries the `*IncludingDeletes` suffix swap their backing view to the corresponding `*IncludingDeletes` view.

Contrary to the live resources logic which computes the people auth subjects joining intermediate resources (to reach the person's DocumentId), the tracked-change tables denomalize people DocumentIds (initialized in in the `*_Stamp` triggers), meaning no entermediate joins are needed for people auth subjects.

Additionally, the tracked-changes table value-column names include `Old` and `New` prefixes without an underscore separator.

#### Authorization views

Following the existing DMS authorization approach, the `*IncludingDeletes` authorization views return `DocumentId` columns rather than USIs and are renamed accordingly.

Each `*IncludingDeletes` authorization view is represented by `ReadChangesAuthorizationViewInfo` in the static `AuthObjectDefinitions.ReadChangesAuthorizationViewDefinitions` inventory in `Backend.External` — like the people auth views they extend, their shape never varies with the effective schema, so they are not part of `DerivedRelationalModelSet` (see [compiled-mapping-set.md](compiled-mapping-set.md)). The inventory records the view name, output columns, and ordered union arms over current tables and tracked-change tables. Dialect emitters render the views from that inventory, and runtime authorization/query planning consume the same view metadata when composing `/deletes` and `/keyChanges` authorization predicates.

The full inventory of `*IncludingDeletes` views DMS emits:
- `auth.EducationOrganizationIdToStudentDocumentIdIncludingDeletes`              
- `auth.EducationOrganizationIdToContactDocumentIdIncludingDeletes`              
- `auth.EducationOrganizationIdToStaffDocumentIdIncludingDeletes`                
- `auth.EducationOrganizationIdToStudentDocumentIdDeletedResponsibility`

The fourth view drops the ODS-era "Through" from its name (`...DocumentIdDeletedResponsibility`, exactly 63 characters): the direct DMS rename (`...DocumentIdThroughDeletedResponsibility`) is 70 characters, which exceeds PostgreSQL's 63-character identifier limit and would be silently truncated on `CREATE VIEW`. The same name is used on both PostgreSQL and SQL Server.

Each view is materialized as a union over current and tracked-change PrimaryAssociation arms, joined against the **current** `auth.EducationOrganizationIdToEducationOrganizationId` hierarchy (see `Preserved authorization peculiarities` below for why the hierarchy is not joined against historical state).

For example, the `auth.EducationOrganizationIdToContactDocumentIdIncludingDeletes` view definition has four union arms — current/current, current/tracked, tracked/current, and tracked/tracked:

```sql
CREATE VIEW [auth].[EducationOrganizationIdToContactDocumentIdIncludingDeletes] AS 
-- Current EdOrgHierarchy + Current StudentSchoolAssociation + Current StudentContactAssociation
SELECT 
  SourceEducationOrganizationId, 
  Contact_DocumentId
FROM 
  auth.EducationOrganizationIdToContactDocumentId 
UNION 
-- Current EdOrgHierarchy + Current StudentSchoolAssociation + Deleted/Key-changed StudentContactAssociation
SELECT 
  edOrgs.SourceEducationOrganizationId, 
  sca_tc.OldContact_DocumentId as Contact_DocumentId
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN edfi.StudentSchoolAssociation ssa ON edOrgs.TargetEducationOrganizationId = ssa.SchoolId_Unified
  JOIN tracked_changes_edfi.StudentContactAssociation sca_tc ON ssa.Student_DocumentId = sca_tc.OldStudent_DocumentId
UNION 
-- Current EdOrgHierarchy + Deleted/Key-changed StudentSchoolAssociation + Current StudentContactAssociation
SELECT 
  edOrgs.SourceEducationOrganizationId, 
  sca.Contact_DocumentId 
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.OldSchoolId_Unified
  JOIN edfi.StudentContactAssociation sca ON ssa_tc.OldStudent_DocumentId = sca.Student_DocumentId
UNION 
-- Current EdOrgHierarchy + Deleted/Key-changed StudentSchoolAssociation + Deleted/Key-changed StudentContactAssociation
SELECT 
  edOrgs.SourceEducationOrganizationId, 
  sca_tc.OldContact_DocumentId as Contact_DocumentId
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.OldSchoolId_Unified
  JOIN tracked_changes_edfi.StudentContactAssociation sca_tc ON ssa_tc.OldStudent_DocumentId = sca_tc.OldStudent_DocumentId;
```

The views are only emitted when all five PrimaryAssociation resources exist in the derived relational model — following the same guard described in [auth.md](auth.md) for the non-`IncludingDeletes` views — and their five `tracked_changes_edfi` association tables are present in the tracked-change inventory.

#### Preserved authorization peculiarities

DMS deliberately preserves the three ODS authorization peculiarities described in the ODS section of this document.

##### KeyChanges are always authorized based on the old values
This peculiarity will be honored in DMS, except for one known edge case: cascading key changes.

Let's assume that `StudentAssessmentRegistration` references `StudentSchoolAssociation` **as part of its identity**. Then someone changes the `StudentSchoolAssociation` student from A to B, so the cascading key change reaches the `StudentAssessmentRegistration`. The `_Stamp` trigger gets the Student's DocumentId by joining `StudentSchoolAssociation`; but at this point `StudentSchoolAssociation` only has the new value, so the `_Stamp` trigger will store the **new** Student's DocumentId in the old-value person DocumentId column.

Note that `OldStudentUniqueId_Unified` stores the correct value, since the unique id gets denormalized into the `StudentSchoolAssociation` table, meaning that the /keyChanges endpoint returns the correct old and new values, but the authorization check is done against the old-value person DocumentId column, which in this case has the new value.

This behavior is acceptable in the meantime as the scenario doesn't appear in the data standard (remember that `StudentAssessmentRegistration` does not reference `StudentSchoolAssociation` as part of its identity). However, there could be an extension that exposes this behavior.

Some changes that could mitigate this discrepancy are:
- Denormalizing people's Document IDs the same way we denormalize unique IDs.
- Joining directly with Student using its unique ID (undesirable due to the performance degradation of joining using a varchar)

#### Descriptor authorization

The default `ReadChanges` authorization strategy for descriptors is `NoFurtherAuthorizationRequired`. The two ODS exceptions are preserved unchanged:

- `CrisisTypeDescriptor` uses `NamespaceBased`
- `NonMedicalImmunizationExemptionDescriptor` uses `NamespaceBased`

#### SchoolYearType

`SchoolYearType` is treated as a regular resource for DMS Change Queries. MetaEd emits `SchoolYearType` `/deletes` and `/keyChanges` paths with the rest of the resource Change Query OpenAPI surface, and runtime route resolution serves those endpoints through the same DMS effective resource model path used for other resources.

`ReadChanges` authorization for `SchoolYearType` is resolved through the same authorization metadata as other resources. Because its identity is immutable, `/keyChanges` is expected to return an empty result unless a future model allows identity updates; that follows the normal key-change tracking semantics and does not require a `SchoolYearType`-specific routing or authorization exception.

#### Concrete abstract resources

Each concrete abstract resource (e.g. `School`, `LocalEducationAgency`, `OrganizationDepartment`) gets its own `TrackedChangeTableInfo` and `tracked_changes_*` table, as described in the `tracked_changes*` tables section above.

This carries a direct authorization payoff: the ODS-era `OrganizationDepartment` limitation no longer applies. In ODS, `OrganizationDepartment.ReadChanges` could not honor its `ParentEducationOrganizationId` securable-element override because tombstones lived in the abstract `tracked_changes_edfi.EducationOrganization` table, which only stored `EducationOrganizationId`. In DMS, `OrganizationDepartment`'s own tracked-change table stores both the abstract identity and any override-specified columns, so any relationship-based `ReadChanges` strategy works without falling back to `NoFurtherAuthorizationRequired`.

Other concrete abstract resources with SecurableElement overrides — including any introduced via extensions — get the same benefit automatically.

### Change Query route source of truth

OpenAPI advertises and documents the Change Query surface, but it is not the runtime source of truth for Change Query route generation.

The `/changeQueries/v1/availableChangeVersions` endpoint is a fixed DMS runtime route. It does not come from `ApiSchema.json` resource metadata.

Resource and descriptor `/deletes` and `/keyChanges` routes are resolved by classifying the trailing path segment, then resolving `{schema}/{resource}` through DMS effective resource metadata: the effective `ApiSchema.json` endpoint mappings and the compiled RelationalBackend `MappingSet.Model` / `ConcreteResourceModel` inventory. The OpenAPI surface should match the supported runtime surface for discoverability, but DMS must not use OpenAPI paths as the authority for whether a resource or descriptor Change Query route exists.

### /deletes endpoints

Each resource and descriptor known to the DMS effective resource model can route to an accompanying `/deletes` endpoint, and the response body remains the same as ODS. The emitted OpenAPI surface advertises the supported discoverable endpoints.

An example generated SQL query used to fulfill the `GET grades/deletes` request is:

```sql
SELECT 
  c.Id, 
  c.ChangeVersion, 

  c.OldStudentSectionAssociation_BeginDate,
  c.OldGradeTypeDescriptor_CodeValue,
  c.OldGradeTypeDescriptor_Namespace,
  c.OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue,
  c.OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace,
  c.OldGradingPeriodGradingPeriod_GradingPeriodName,
  c.OldSchoolYear_Unified,
  c.OldStudentSectionAssociation_LocalCourseCode,
  c.OldSchoolId_Unified,
  c.OldStudentSectionAssociation_SectionIdentifier,
  c.OldStudentSectionAssociation_SessionName,
  c.OldStudentSectionAssociation_StudentUniqueId
FROM 
  tracked_changes_edfi.Grade AS c
  LEFT JOIN dms.Descriptor AS OldGradeTypeDescriptor 
    ON OldGradeTypeDescriptor.Discriminator = 'GradeTypeDescriptor'
    AND OldGradeTypeDescriptor.CodeValue = c.OldGradeTypeDescriptor_CodeValue
    AND OldGradeTypeDescriptor.Namespace = c.OldGradeTypeDescriptor_Namespace

  LEFT JOIN dms.Descriptor AS OldGradingPeriodDescriptor
    ON OldGradingPeriodDescriptor.Discriminator = 'GradingPeriodDescriptor'
    AND OldGradingPeriodDescriptor.CodeValue = c.OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue
    AND OldGradingPeriodDescriptor.Namespace = c.OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace

  LEFT JOIN edfi.Grade AS src 
    ON OldGradeTypeDescriptor.DocumentId                    = src.GradeTypeDescriptor_DescriptorId 
    AND OldGradingPeriodDescriptor.DocumentId               = src.GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId 
    AND c.OldGradingPeriodGradingPeriod_GradingPeriodName  = src.GradingPeriodGradingPeriod_GradingPeriodName
    AND c.OldSchoolId_Unified                              = src.SchoolId_Unified
    AND c.OldSchoolYear_Unified                            = src.SchoolYear_Unified
    AND c.OldStudentSectionAssociation_BeginDate           = src.StudentSectionAssociation_BeginDate
    AND c.OldStudentSectionAssociation_LocalCourseCode     = src.StudentSectionAssociation_LocalCourseCode
    AND c.OldStudentSectionAssociation_SectionIdentifier   = src.StudentSectionAssociation_SectionIdentifier
    AND c.OldStudentSectionAssociation_SessionName         = src.StudentSectionAssociation_SessionName
    AND c.OldStudentSectionAssociation_StudentUniqueId     = src.StudentSectionAssociation_StudentUniqueId
WHERE 
  src.StudentSectionAssociation_BeginDate IS NULL -- Exclude entries that were recreated, use any identity column
  AND c.NewStudentSectionAssociation_BeginDate IS NULL -- Exclude key changes, use any New* identity column
  AND (
      c.ChangeVersion >= @MinChangeVersion 
      AND c.ChangeVersion <= @MaxChangeVersion
  )
  AND (
      -- Auth check: Relationship with EdOrg:
      c.OldSchoolId_Unified IN (SELECT TargetEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE SourceEducationOrganizationId IN (SELECT Id FROM @TokenEducationOrganizationIds))
      -- Auth check: Relationship with People:
      AND c.OldStudentSectionAssociation_Student_DocumentId IN (SELECT Student_DocumentId FROM auth.EducationOrganizationIdToStudentDocumentIdIncludingDeletes WHERE SourceEducationOrganizationId IN (SELECT Id FROM @TokenEducationOrganizationIds))
  )
ORDER BY 
  c.ChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

In the example above, we join with the live table using identifying values instead of surrogate keys so that we can hide entries that were recreated. The same applies to descriptors, where we join using the Namespace and CodeValue.

#### `*_RefKey` index ordering for `/deletes`

The live-table join used by `/deletes` intentionally does not specify the live row's `DocumentId`. It probes the current resource table by the resource's identifying storage values so it can detect whether a deleted resource was recreated under a new `DocumentId` and suppress the tombstone from the response.

This makes the physical order of the `UX_<Table>_RefKey` key columns important. If the reference-key uniqueness remains ordered as `(DocumentId, <identity storage columns...>)`, the recreated-resource probe cannot use the index efficiently because the leading `DocumentId` value is not part of the predicate. The index is still valid for uniqueness and FK enforcement, but it is poorly shaped for queries whose predicate starts with the identity values.

For DMS, emit `*_RefKey` with the public identity storage columns first, any internal identity-lineage anchors next, and
the referenced resource `DocumentId` last:
`(<identity storage columns...>, <lineage anchor DocumentIds...>, DocumentId)`. The composite reference FKs that target
`*_RefKey` use the same target-column ordering. `/deletes` predicates still begin with the complete public identity
prefix, so the anchors do not weaken the recreated-resource anti-join seek shape.

When incoming sites select different demanded anchor sets, emit one deterministic
`UX_<Table>_RefKey_<AnchorSetId>` variant per distinct set. Every variant preserves the same public identity left prefix;
the `/deletes` anti-join may use any applicable variant and does not predicate on internal anchors.

Descriptor `/deletes` uses the same conceptual anti-join, but it probes `dms.Descriptor` by `(Discriminator, CodeValue, Namespace)`. DMS v1 will not add a separate descriptor identity lookup index for this path because descriptor deletes and recreations are expected to be rare.

An example generated SQL query used to fulfill the `GET crisisTypeDescriptors/deletes` request is:

```sql
SELECT DISTINCT 
  c.Id, 
  c.ChangeVersion, 
  c.OldCodeValue,
  c.OldNamespace
FROM 
  tracked_changes_edfi.Descriptor AS c 
  LEFT JOIN dms.Descriptor AS src 
    ON src.Discriminator = 'CrisisTypeDescriptor'
    AND src.CodeValue = c.OldCodeValue
    AND src.Namespace = c.OldNamespace
WHERE 
  c.Discriminator = 'CrisisTypeDescriptor'
  AND src.CodeValue IS NULL              -- Exclude entries that were recreated, use any identity column
  AND c.NewCodeValue IS NULL            -- Exclude key changes, use any New* identity column
  AND (
    -- Namespace-based auth check
    c.OldNamespace LIKE @p1
    OR c.OldNamespace LIKE @p2
    OR c.OldNamespace LIKE @p3
  ) 
ORDER BY 
  c.ChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

### /keyChanges endpoints

Each resource and descriptor known to the DMS effective resource model can route to an accompanying `/keyChanges` endpoint, and the response body remains the same as ODS. The emitted OpenAPI surface advertises the supported discoverable endpoints.

An example generated SQL query used to fulfill the `GET grades/keyChanges` request is:

```sql
WITH ChangeWindow AS (
    SELECT
      c.Id, 
      MIN(c.ChangeVersion) AS InitialChangeVersion, 
      MAX(c.ChangeVersion) AS FinalChangeVersion 
    FROM 
      tracked_changes_edfi.Grade AS c 
    WHERE 
      c.NewStudentSectionAssociation_BeginDate IS NOT NULL -- Exclude tombstones, use any New* identity column
      AND (
          c.ChangeVersion >= @MinChangeVersion 
          AND c.ChangeVersion <= @MaxChangeVersion
      )
      AND (
         -- Auth check: Relationship with EdOrg:
         c.OldSchoolId_Unified IN (SELECT TargetEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE SourceEducationOrganizationId IN (SELECT Id FROM @TokenEducationOrganizationIds))
         -- Auth check: Relationship with People:
         AND c.OldStudentSectionAssociation_Student_DocumentId IN (SELECT Student_DocumentId FROM auth.EducationOrganizationIdToStudentDocumentIdIncludingDeletes WHERE SourceEducationOrganizationId IN (SELECT Id FROM @TokenEducationOrganizationIds))
      )
    GROUP BY 
      c.Id
  ) 
SELECT 
  cw.Id, 
  cw.FinalChangeVersion AS ChangeVersion, 
  
  c_old.OldStudentSectionAssociation_BeginDate,
  c_old.OldGradeTypeDescriptor_CodeValue,
  c_old.OldGradeTypeDescriptor_Namespace,
  c_old.OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue,
  c_old.OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace,
  c_old.OldGradingPeriodGradingPeriod_GradingPeriodName,
  c_old.OldSchoolYear_Unified,
  c_old.OldStudentSectionAssociation_LocalCourseCode,
  c_old.OldSchoolId_Unified,
  c_old.OldStudentSectionAssociation_SectionIdentifier,
  c_old.OldStudentSectionAssociation_SessionName,
  c_old.OldStudentSectionAssociation_StudentUniqueId,

  c_new.NewStudentSectionAssociation_BeginDate,
  c_new.NewGradeTypeDescriptor_CodeValue,
  c_new.NewGradeTypeDescriptor_Namespace,
  c_new.NewGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue,
  c_new.NewGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace,
  c_new.NewGradingPeriodGradingPeriod_GradingPeriodName,
  c_new.NewSchoolYear_Unified,
  c_new.NewStudentSectionAssociation_LocalCourseCode,
  c_new.NewSchoolId_Unified,
  c_new.NewStudentSectionAssociation_SectionIdentifier,
  c_new.NewStudentSectionAssociation_SessionName,
  c_new.NewStudentSectionAssociation_StudentUniqueId
FROM 
  ChangeWindow AS cw 
  INNER JOIN tracked_changes_edfi.Grade AS c_old ON cw.Id = c_old.Id AND cw.InitialChangeVersion = c_old.ChangeVersion 
  INNER JOIN tracked_changes_edfi.Grade AS c_new ON cw.Id = c_new.Id AND cw.FinalChangeVersion = c_new.ChangeVersion 
ORDER BY 
  cw.FinalChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

#### Response field mapping

To avoid introducing breaking changes, the `/deletes` and `/keyChanges` endpoints must return identifying field names exactly as they appear in the resource's `queryFieldMapping` in `ApiSchema.json`. Physical tracked-change column names such as `OldSchoolId_Unified`, `OldStudentUniqueId_Unified`, and shortened PostgreSQL names are storage details and must not become response field names.

Runtime response shaping maps the tracked-change value columns back to public fields from metadata, using the `ConcreteResourceModel`, `TrackedChangeTableInfo`, and the resource's query-field mappings:

1. Select only tracked-change value columns whose `Origin` includes `Identity`.
2. Ignore `PersonDocumentId` value columns when shaping `keyValues`, `oldKeyValues`, and `newKeyValues`. They exist for `ReadChanges` authorization, not for the response contract.
3. Treat `Scalar` columns as one response field.
4. Treat paired `DescriptorNamespace` and `DescriptorCodeValue` columns with the same `SourceJsonPath` and `DescriptorJoinName` as one descriptor response field, composing the value as `<namespace>#<codeValue>`.
5. Use tracked-change system columns for the top-level `id` and `changeVersion` values.

For each logical identity value, resolve the public response field name in this order:

1. Exact path: `TrackedChangeColumnInfo.SourceJsonPath` equals one path in `queryFieldMapping`.
2. Equality/unification path: `SourceJsonPath` is equivalent to another path through `RelationalResourceModel.KeyUnificationEqualityConstraints`, and that equivalent path appears in `queryFieldMapping`.
3. Reference alias path: the `queryFieldMapping` path is a generated reference alias that the compiled relational query capability resolves back to the same identity binding or canonical storage column as the tracked value.

The exact-path rule must run before the equality/unification rule. Some resources expose both an exact public field and an equality-related public field for the same stored value; the exact public field wins.

The mapper must not fall back to physical column names or to matching only the final JSONPath segment. If no deterministic `queryFieldMapping` field can be resolved, the resource's Change Query endpoint is unsupported until the metadata is corrected. A diagnostic can mention potential final-segment matches, but those matches are not authoritative enough to shape the response.

Reference aliases are not a separate metadata table or registry. They are `queryFieldMapping` paths generated into `ApiSchema.json` and normalized into `ConcreteResourceModel.QueryFieldMappingsByQueryField`. Relational GET-many already interprets these paths through `ReferenceIdentityQueryTargetResolver.ResolveAliasPath` while compiling `MappingSet.QueryCapabilitiesByResource`. Change Query response mapping must consume that compiled query-capability output so live query filtering and tracked-change response shaping agree.

The Change Query mapper should not reimplement `ResolveAliasPath` or instantiate a second alias resolver. For the alias case, it should read the selected resource's `RelationalQueryCapability` from `MappingSet.QueryCapabilitiesByResource` and inspect `SupportedFieldsByQueryField`. Each `SupportedRelationalQueryField` carries the public `QueryFieldName`, the original `queryFieldMapping` path, and the resolved relational target. When the supported field targets a root column whose `SourceJsonPath` or unified canonical storage column matches the tracked-change logical value, the mapper uses `SupportedRelationalQueryField.QueryFieldName` as the response field name.

Do not use `GetQueryCapabilityOrThrow()` for this mapper lookup. That helper is appropriate for live GET-many execution, but it throws when a resource's overall GET-many capability is intentionally omitted because of an unrelated unsupported query field. Change Query response shaping only needs the supported identity fields that were compiled, so it should read the `RelationalQueryCapability` entry directly and fail only when the needed identity response field cannot be resolved deterministically.

`ResolveAliasPath` only considers schema-mangled query paths with exactly two property segments whose leaf ends with `UniqueId`, for example `$.studentReference.studentEducationOrganizationAssociationUniqueId`. It validates the alias against `DocumentReferenceBinding` metadata and returns a tri-state result:

- `NoMatch`: the path is not a recognized alias shape; the mapper continues to report an unmapped response field.
- `Match`: exactly one reference identity candidate group matches; the candidate resolves to the representative root-table binding column, and under key unification to its canonical storage column.
- `Ambiguous`: more than one candidate group matches; the resource is unsupported because there is no deterministic response-field mapping.

The alias bridge recognizes two shapes:

1. **Through-reference aliases**: the resource identity reaches a person unique id through an intermediate reference. The alias leaf is `lowerCamel(TargetResourceName) + "UniqueId"`, and the alias parent is either the identity-source parent or the public person reference parent after role adjustment. The public person parent is deliberately limited to `studentUniqueId -> studentReference`, `staffUniqueId -> staffReference`, and `contactUniqueId -> contactReference`; the mapper must not invent arbitrary `fooUniqueId -> fooReference` relationships.
2. **Direct-site superclass aliases**: the resource has a direct person reference, but the generated alias leaf names the concrete resource's superclass. The alias leaf is `lowerCamel(SuperclassResource.ResourceName) + "UniqueId"`, the alias parent must be the matched reference object path, and the `ConcreteResourceModel` must carry superclass metadata. Without superclass metadata, this shape does not match.

Both shapes require the candidate to be a local propagated identity binding: the candidate's `ReferenceJsonPath` must be the reference object path plus the identity leaf, and the public `queryFieldMapping` field name must match the role-adjusted identity leaf. These checks intentionally keep the bridge metadata-driven and prevent broad string-convention matching.

For example, `studentAssessmentRegistrations` stores the tracked student unique id in `tracked_changes_edfi.StudentAssessmentRegistration.OldStudentUniqueId_Unified` / `NewStudentUniqueId_Unified` with `SourceJsonPath = $.studentEducationOrganizationAssociationReference.studentUniqueId`. The public query field is not a direct path match:

```json
"studentUniqueId": [
  {
    "path": "$.studentReference.studentEducationOrganizationAssociationUniqueId",
    "type": "string"
  }
]
```

The reference-alias rule resolves that generated query path back to the `StudentEducationOrganizationAssociation_StudentUniqueId` identity binding, whose canonical storage column is `StudentUniqueId_Unified`. The response field is therefore `studentUniqueId`, even though the tracked-change storage column is `OldStudentUniqueId_Unified` / `NewStudentUniqueId_Unified`.

### /availableChangeVersions endpoint

DMS will introduce the hardcoded `/changeQueries/v1/availableChangeVersions` endpoint with the same request/response bodies and query strings as ODS. This endpoint is not derived from `ApiSchema.json` or OpenAPI metadata.
The `oldestChangeVersion` remains hardcoded to `0`.

One distinction is that the function resides in the `dms` schema instead of `changes` to be consistent with existing DMS schemas. PostgreSQL emits and calls `"dms"."GetMaxChangeVersion"()`, while SQL Server uses `[dms].[GetMaxChangeVersion]`.

### Filtering live resources and descriptors by ChangeVersion
Live resource and descriptor GET-many endpoints support `?minChangeVersion=X&maxChangeVersion=Y`. 

For live resource endpoints, with the mirror on the concrete tables, the runtime query planner emits a direct range filter on the concrete table — no join to `dms.Document` is required for the change-version predicate.

The generated SQL used to fulfill a `GET /data/v3/ed-fi/grades?minChangeVersion=123&maxChangeVersion=987` request is:

```sql
DROP TABLE IF EXISTS "page";
CREATE TEMP TABLE "page" ("DocumentId" bigint PRIMARY KEY) ON COMMIT DROP;

WITH page_ids AS (
    SELECT r."DocumentId"
    FROM "edfi"."Grade" r
    WHERE r.ContentVersion >= @MinChangeVersion AND r.ContentVersion <= @MaxChangeVersion -- Range filter on ContentVersion
    ORDER BY r."DocumentId" ASC
    LIMIT @limit OFFSET @offset
)
INSERT INTO "page" ("DocumentId")
SELECT "DocumentId" 
FROM page_ids;

-- The rest of the reconstitution queries are omitted for brevity.
```

Descriptor endpoints need to join with `dms.Descriptor` in order to emit the range filter leveraging the `IX_Descriptor_Discriminator_ContentVersion`:

```sql
SELECT 
  page_document_ids."DocumentId" AS "DocumentId", 
  document."DocumentUuid" AS "DocumentUuid", 
  document."ContentLastModifiedAt" AS "ContentLastModifiedAt", 
  document."ResourceKeyId" AS "ResourceKeyId", 
  descriptor."Namespace" AS "Namespace", 
  descriptor."CodeValue" AS "CodeValue", 
  descriptor."ShortDescription" AS "ShortDescription", 
  descriptor."Description" AS "Description", 
  descriptor."EffectiveBeginDate" AS "EffectiveBeginDate", 
  descriptor."EffectiveEndDate" AS "EffectiveEndDate", 
  descriptor."Discriminator" AS "Discriminator" 
FROM 
  (
    SELECT r."DocumentId" 
    FROM 
      "dms"."Document" r 
      INNER JOIN "dms"."Descriptor" d ON d."DocumentId" = r."DocumentId" 
    WHERE 
      d."Discriminator" = @discriminator -- Required for IX_Descriptor_Discriminator_ContentVersion to be used as a range seek
      AND d.ContentVersion >= @MinChangeVersion AND d.ContentVersion <= @MaxChangeVersion -- Range filter on ContentVersion
      AND (
        r."ResourceKeyId" = @resourceKeyId
      ) 
    ORDER BY 
      r."DocumentId" ASC 
    LIMIT 
      @limit OFFSET @offset
  ) page_document_ids 
  INNER JOIN dms."Document" document ON document."DocumentId" = page_document_ids."DocumentId" 
  LEFT JOIN dms."Descriptor" descriptor ON descriptor."DocumentId" = page_document_ids."DocumentId" 
ORDER BY 
  page_document_ids."DocumentId" ASC;
```

The planner uses this path for every resource with a `MirroredContentVersion` column in its `DbTableModel` — which is every `StorageKind = RelationalTables` resource. There is no fallback path; the mirror is universal for in-scope tables.

`/deletes`, `/keyChanges`, and `/availableChangeVersions` are unchanged. The mirror affects only the live resource read path.

Reads of `_lastModifiedDate` and per-item `ChangeVersion` in response bodies remain sourced from `dms.Document` for now; switching to the concrete-table mirror is an optional future read-path optimization (the values are identical per Invariant #2).


### Snapshot support is deferred

Snapshot support is deferred and will not be available for DMS v1.0; as such, the `/deletes`, `/keyChanges`, `/availableChangeVersions`, and live resource and descriptors endpoints will not support the `Use-Snapshot` header.

**DMS v1.0 behavior on receipt of `Use-Snapshot`.** DMS silently ignores the `Use-Snapshot` request header on Change Query and live resource/descriptor GET-many requests. The header has no effect; the request is processed against current data without snapshot isolation. No `Warning` header is set and no error ProblemDetails is emitted.

**Operator guidance — Ed-Fi API Publisher reading from a DMS v1.0 source.** The Ed-Fi API Publisher sends `Use-Snapshot: true` by default when probing snapshot support against a source whose API major version is at least 7 (see `EdFi.Tools.ApiPublisher.Connections.Api/Processing/Source/Isolation/EdFiApiSourceIsolationApplicator.cs`). Because DMS v1.0 silently ignores that header, reads from a DMS v1.0 source are not snapshot-isolated: concurrent writes against the source may be visible mid-publish and can produce inconsistent published data. Operators publishing from a DMS v1.0 source should either accept that risk or run the Publisher with `--ignoreIsolation=true`, which is the explicit acknowledgment that source isolation is unavailable. Snapshot support in DMS is targeted for a later release.

### Model and DDL verification

Tests should assert the shared inventory before asserting rendered SQL. At minimum, fixture coverage should validate:

- `TrackedChangeTableInfo` creation for regular resources, concrete abstract resources, and the shared descriptor table.
- `TrackedChangeColumnInfo` old/new column pairs and separate old/new nullability for identity paths, securable element paths, canonical key-unification storage columns, descriptor `Namespace`/`CodeValue` projections, and person `DocumentId` projections.
- `TrackedChangeDescriptorJoinInfo` and `TrackedChangePersonJoinInfo` paths used by trigger emitters, with value columns referencing them by join name rather than duplicating join definitions.
- `DocumentStamping.ChangeTracking` attachment to the correct `TriggerKindParameters.DocumentStamping` trigger entries.
- ChangeTracking key-change rows using the owning `DbTriggerInfo.IdentityProjectionColumns` workset, including key-unification cases where canonical storage columns change without direct alias-column updates, and presence-only alias changes do not emit key-change rows when the canonical identity storage values are unchanged.
- `ReadChangesAuthorizationViewInfo` union arms for current/current, current/tracked, tracked/current, and tracked/tracked association combinations.
- Manifest output for tracked-change tables, triggers, and ReadChanges authorization views so SQL generation tests are checking renderer behavior, not hidden semantic compilation in the DDL emitter.
- Mirror columns (`ContentVersion`, `ContentLastModifiedAt`) tagged with `ColumnKind.MirroredContentVersion` and `ColumnKind.MirroredContentLastModifiedAt` on every `ConcreteResourceModel` with `StorageKind = RelationalTables`, including extension-project resources; absent on `StorageKind = SharedDescriptorTable` resources (the columns live on `dms.Descriptor` instead, added by the core DDL pass).
- `IX_<Table>_ContentVersion` per in-scope root in `IndexesInCreateOrder` (single-column), and `IX_Descriptor_Discriminator_ContentVersion` composite index on `dms.Descriptor` with key columns in order `[Discriminator, ContentVersion]`.
- Every `DbTriggerInfo` with `Kind = DocumentStamping` has a non-null `MirrorStampTargetTable` matching the per-trigger rule (same table for root, resource's root for child / `_ext`, `dms.Descriptor` for the descriptor trigger).
- DB-behavior: mirror equals source (`<root>.ContentVersion = dms.Document.ContentVersion` and `<root>.ContentLastModifiedAt = dms.Document.ContentLastModifiedAt`) after every write path — insert, update, no-op update, identity change, child-collection write, `_ext` write, FK-cascade update, descriptor write. Run on at least a root-only resource (`edfi.Student`), a child-bearing resource (`edfi.School` with `SchoolAddress` writes), an `_ext`-bearing resource, an extension-project resource (e.g. `tpdm.Candidate`), and a descriptor.
- DB-behavior: stamp-only updates (`UPDATE <root> SET ContentVersion = ContentVersion + 1 …`) do not allocate a new sequence value, do not fire additional mirror UPDATEs, and do not insert `tracked_changes_*` rows; multi-row UPDATEs that stamp N documents allocate N distinct `ContentVersion` values, and each document's mirror equals its `dms.Document` stamp.
- DB-behavior: root deletes with cascaded child, nested-child, or `_ext` rows produce exactly one visible root tombstone in the relevant `tracked_changes_*` table. The tombstone's `ChangeVersion` is the final delete ChangeVersion exposed to Change Queries, and no later visible root stamp or tracked-change row can advance an extraction watermark past that tombstone. Run this on PostgreSQL and SQL Server for at least one child-bearing resource and one extension-bearing resource.
- DB-behavior: `IdentityVersion` and `IdentityLastModifiedAt` columns are absent from every in-scope root table and from `dms.Descriptor`.
- Emitted-SQL snapshot: `?minChangeVersion=X&maxChangeVersion=Y` produces a single-table range filter on the concrete table for `/ed-fi/students`, on `dms.Descriptor` (with the `Discriminator` predicate) for descriptors, and the same shape for at least one extension-project resource endpoint.

### ProblemDetails

Authorization-related ProblemDetails for Change Queries are owned by [auth.md](auth.md) and are not repeated here. 


#### 1. Parameter Validation Failures (400 Bad Request)

These errors indicate invalid query string values on `/deletes`, `/keyChanges`, or live resource and descriptor GET-many requests using `minChangeVersion` / `maxChangeVersion`.

**Type**: `urn:ed-fi:api:bad-request:parameter-validation-failed`

**Title**: `Parameter Validation Failed`

**Status**: `400`

**Detail**: `Parameters supplied to the request were invalid.`

| Scenario | Error |
|---|---|
| `minChangeVersion` cannot be parsed as an integer | `MinChangeVersion must be a numeric value greater than or equal to 0.` |
| `maxChangeVersion` cannot be parsed as an integer | `MaxChangeVersion must be a numeric value greater than or equal to 0.` |
| `minChangeVersion` is greater than `maxChangeVersion` | `MinChangeVersion must be less than or equal to MaxChangeVersion.` |


#### 2. Resource or Endpoint Not Found (404 Not Found)

These errors indicate that the requested Change Queries route or resource cannot be resolved.

**Type**: `urn:ed-fi:api:not-found`

**Title**: `Not Found`

**Status**: `404`

**Default Detail**: `The specified data could not be found.`

| Scenario | Error |
|---|---|
| `/deletes` or `/keyChanges` is requested for an unknown `{schema}/{resource}` pair | *(empty)* |
| `/changeQueries/v1/availableChangeVersions` is not routed because Change Queries support is not available in the runtime | `Path '{path}' does not exist. Check the resource name and try again.` |

#### 3. Feature Disabled (404 Not Found)

If DMS keeps a runtime feature flag for Change Queries, requests to Change Queries endpoints while the feature is disabled should follow the ODS feature-disabled response shape.

**Type**: `urn:ed-fi:api:system:configuration:feature-disabled`

**Title**: `Feature Disabled`

**Status**: `404`

**Detail**: `The 'ChangeQueries' feature is disabled.`

**Error**: *(empty)*

> Note: This ProblemDetail does not apply yet as the support to disable the feature will be deferred.

#### 4. Snapshot ProblemDetails Are Deferred

Snapshot support is deferred for DMS v1.0. The `Use-Snapshot` header is therefore not part of the DMS v1.0 Change Queries contract, and DMS v1.0 should not emit ODS snapshot-specific ProblemDetails for Change Queries.

DMS should preserve the ODS response shapes below whenever snapshot support gets added:

| Scenario | Type | Title | Status | Detail |
|---|---|---|---|---|
| `Use-Snapshot: true` is supplied on a non-`GET` request | `urn:ed-fi:api:snapshots:method-not-allowed` | `Method Not Allowed with Snapshots` | `405` | `An attempt was made to modify data in a Snapshot, but this data is read-only.` |
| `Use-Snapshot: true` is supplied but no snapshot connection string is configured, or the snapshot database cannot be reached | `urn:ed-fi:api:not-found` | `Not Found` | `404` | `Snapshot not found.` |

For the `405` case, the response must include an `Allow: GET` header.
