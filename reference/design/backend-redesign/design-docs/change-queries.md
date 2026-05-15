# Change Queries

## Purpose

The Change Queries feature allows client systems to retrieve data that has changed since a specified version number. This keeps client systems in sync with the DMS without requiring them to pull the complete dataset.

Unlike Change Data Capture (CDC), this feature does **not** store the old and new values for every write. Instead, it stores only the current representation and its version number, similar to [SQL Server Change Tracking](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/track-data-changes-sql-server). This means downstream systems need special handling to maintain consistency, as described throughout this document.

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

The `SchoolYearType` resource is excluded from this feature because it is immutable. The OpenAPI metadata does not include the `/deletes` or `/keyChanges` endpoints.

However, for the sake of simplicity, the generic DeletesController and KeyChangesController do not check for SchoolYearType by name, and the generated DB scripts still add ChangeVersion, a tracked changes table, and delete tracking for SchoolYearType. Its update trigger updates ChangeVersion but does not insert key-change rows.

The `ReadChanges` action is not configured for this resource, so attempts to request its `/deletes` or `/keyChanges` endpoints result in authorization denied.

### Filtering resources by ChangeVersion

Resource and descriptor endpoints allow filtering by `minChangeVersion` and `maxChangeVersion`, which internally filter the resource's `ChangeVersion` column. These endpoints allow users to retrieve the current representation of resources updated within a given change window.

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

The `changes.GetMaxChangeVersion()` custom function definition is:

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

Note that the response's `oldestChangeVersion` is hardcoded to `0`.

### Authorization

The `/keyChanges` and `/deletes` endpoints return resource identifying values, which are sensitive data, so these endpoints must apply authorization similar to resource endpoints.

The feature introduces the `ReadChanges` action that must be granted to the resource's claims to access the `/keyChanges` and `/deletes` endpoints.
The feature also introduces the authorization strategies below, which are meant to be used with the `ReadChanges` action:

- RelationshipsWithEdOrgsAndPeopleIncludingDeletes
- RelationshipsWithStudentsOnlyIncludingDeletes
- RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes

These authorization strategies are the same as their non-prefixed equivalents. The only difference is that they use different authorization views behind the scenes by setting a `pathModifier`. The feature introduces these views:

- [EducationOrganizationIdToContactUSIIncludingDeletes](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e489317ea77f245aff99d57374165c238848f9a0/Application/EdFi.Ods.Standard/Standard/6.1.0/Artifacts/PgSql/Structure/Ods/Changes/1020-AuthViewsIncludingDeletes.sql#L46)
- [EducationOrganizationIdToStaffUSIIncludingDeletes](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e489317ea77f245aff99d57374165c238848f9a0/Application/EdFi.Ods.Standard/Standard/6.1.0/Artifacts/PgSql/Structure/Ods/Changes/1020-AuthViewsIncludingDeletes.sql#L22)
- [EducationOrganizationIdToStudentUSIIncludingDeletes](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e489317ea77f245aff99d57374165c238848f9a0/Application/EdFi.Ods.Standard/Standard/6.1.0/Artifacts/PgSql/Structure/Ods/Changes/1020-AuthViewsIncludingDeletes.sql#L8)
- [EducationOrganizationIdToStudentUSIThroughDeletedResponsibility](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e489317ea77f245aff99d57374165c238848f9a0/Application/EdFi.Ods.Standard/Standard/6.1.0/Artifacts/PgSql/Structure/Ods/Changes/1040-AuthViewStudentResponsibilityIncludingDeletes.sql#L6)

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

The feature introduces a `Use-Snapshot` request header that, when set to `true`, redirects the request to the configured snapshot connection string instead of the primary ODS database. The header is honored by all resource, descriptor, `/deletes`, `/keyChanges`, and `/availableChangeVersions` endpoints.

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

The response contains ordered groups of resource endpoints that can be loaded at the same time. "Delete" operations are to be performed at the reverse order of Create operations.

This endpoint is already implemented in DMS.

### Client extraction logic

The recommended implementation of a data synchronization tool follows this process:

Repeating (on a schedule):

1. Obtain the saved ChangeVersion from the previous synchronization (`0` if this is the first execution) and add 1, this becomes the `MinChangeVersion`.
2. Obtain the source system's newest ChangeVersion using the `/availableChangeVersions` endpoint, this becomes the `MaxChangeVersion`.
3. Iterate through all resources in dependency order; on each resource:
   1. Execute the `/keyChanges` endpoint, specifying the `MinChangeVersion` and `MaxChangeVersion` on the source API, and PUT the new identifying values to the downstream API.
   2. Extract the latest representation of the resource items, using the usual GET-many endpoint such as `/students`, specifying the `MinChangeVersion` and `MaxChangeVersion` on the source API, and POST the new representations to the downstream API.
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

### Database Model

#### `tracked_changes*` tables

Similar to ODS, each resource will get an accompanying `tracked_changes*` table with the `tracked_changes_<ProjectName>.<ResourceName>` naming convention. The usual DMS identifier-shortening logic applies to avoid exceeding the PostgreSQL length limit.

These tables are similar to the corresponding live tables.

These tables should include the corresponding columns that result from combining the `IdentityJsonPaths` and `SecurableElements` paths from the resource's ApiSchema.json. They should be included twice, with the `Old_` and `New_` column name prefixes.

If a path is a descriptor reference, we will include two columns: the descriptor's `Namespace` and `CodeValue`. These columns will be populated by joining with `dms.Descriptor` in the triggers (see below).

If a path is backed by a column that participates in key unification, include the canonical column instead of the generated column.

If the same canonical column has been included multiple times (because of key unification) only include it once.

For people `SecurableElements` paths, we will also store the Student, Contact, or Staff `DocumentId`, which will be populated by joining with the people resource in the triggers (see below).

Some `SecurableElements` paths might result in nullable columns because of overrides, such as the `StudentAssessment` override.

Apart from people `SecurableElements`, we do not need to store surrogate keys, such as DocumentIds or DescriptorIds.

We will also emit a `tracked_changes*` table for each concrete abstract resource to support SecurableElement overrides, such as `OrganizationDepartment`.

MSSQL table definition example for the Grade resource:

```sql
CREATE TABLE [tracked_changes_edfi].[Grade]
(
    [Old_StudentSectionAssociation_BeginDate] date NOT NULL,
    [Old_GradeTypeDescriptor_Namespace] nvarchar(255) NOT NULL,
    [Old_GradeTypeDescriptor_CodeValue] nvarchar(50) NOT NULL,
    [Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace] nvarchar(255) NOT NULL,
    [Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue] nvarchar(50) NOT NULL,
    [Old_GradingPeriodGradingPeriod_GradingPeriodName] nvarchar(60) NOT NULL,
    [Old_SchoolYear_Unified] integer NOT NULL,
    [Old_StudentSectionAssociation_LocalCourseCode] nvarchar(60) NOT NULL,
    [Old_SchoolId_Unified] bigint NOT NULL,
    [Old_StudentSectionAssociation_SectionIdentifier] nvarchar(255) NOT NULL,
    [Old_StudentSectionAssociation_SessionName] nvarchar(60) NOT NULL,
    [Old_StudentSectionAssociation_StudentUniqueId] nvarchar(32) NOT NULL,
    [Old_StudentSectionAssociation_Student_DocumentId] bigint NOT NULL,
    
    [New_StudentSectionAssociation_BeginDate] date NULL,
    [New_GradeTypeDescriptor_Namespace] nvarchar(255) NULL,
    [New_GradeTypeDescriptor_CodeValue] nvarchar(50) NULL,
    [New_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace] nvarchar(255) NULL,
    [New_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue] nvarchar(50) NULL,
    [New_GradingPeriodGradingPeriod_GradingPeriodName] nvarchar(60) NULL,
    [New_SchoolYear_Unified] integer NULL,
    [New_StudentSectionAssociation_LocalCourseCode] nvarchar(60) NULL,
    [New_SchoolId_Unified] bigint NULL,
    [New_StudentSectionAssociation_SectionIdentifier] nvarchar(255) NULL,
    [New_StudentSectionAssociation_SessionName] nvarchar(60) NULL,
    [New_StudentSectionAssociation_StudentUniqueId] nvarchar(32) NULL,
    [New_StudentSectionAssociation_Student_DocumentId] bigint NULL,

    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Grade_CreatedAt] DEFAULT (sysutcdatetime())
    CONSTRAINT [PK_tracked_changes_edfi_Grade] PRIMARY KEY CLUSTERED ([ChangeVersion])
);
```

All descriptor types will be stored in the same table, `tracked_changes_edfi.Descriptor`, even descriptors that belong to extensions. This follows ODS's convention.

MSSQL table definition example for the shared `tracked_changes_edfi.Descriptor`:

```sql
CREATE TABLE [tracked_changes_edfi].[Descriptor]
(
    [Old_Namespace] nvarchar(255) NOT NULL,
    [Old_CodeValue] nvarchar(50) NOT NULL,

    [New_Namespace] nvarchar(255) NULL,
    [New_CodeValue] nvarchar(50) NULL,

    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Grade_CreatedAt] DEFAULT (sysutcdatetime())
    CONSTRAINT [PK_tracked_changes_edfi_Grade] PRIMARY KEY CLUSTERED ([ChangeVersion])
)
```

#### Triggers that populate the `tracked_changes*` tables

The existing `*_Stamp` triggers will be updated to store tombstones and key changes.

We have to join with `dms.Descriptor` to store the descriptor's Namespace and CodeValue.

Additionally, for people SecurableElements, we have to join until we reach the people resource to store the DocumentId. Use the `ResolveSecurableElementColumnPath` helper function to get the intermediate tables that need to be joined. See [auth.md](auth.md) for more information.

MSSQL trigger definition example for the Grade resource:

```sql
CREATE OR ALTER TRIGGER [edfi].[TR_Grade_Stamp]
ON [edfi].[Grade]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (
      -- CTE definition omitted for brevity as no changes are needed
    )
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[Grade] (
            [Old_StudentSectionAssociation_BeginDate],
            [Old_GradeTypeDescriptor_Namespace],
            [Old_GradeTypeDescriptor_CodeValue],
            [Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace],
            [Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue],
            [Old_GradingPeriodGradingPeriod_GradingPeriodName],
            [Old_SchoolYear_Unified],
            [Old_StudentSectionAssociation_LocalCourseCode],
            [Old_SchoolId_Unified],
            [Old_StudentSectionAssociation_SectionIdentifier],
            [Old_StudentSectionAssociation_SessionName],
            [Old_StudentSectionAssociation_StudentUniqueId],
            [Old_StudentSectionAssociation_Student_DocumentId],
            [New_StudentSectionAssociation_BeginDate],
            [New_GradeTypeDescriptor_Namespace],
            [New_GradeTypeDescriptor_CodeValue],
            [New_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace],
            [New_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue],
            [New_GradingPeriodGradingPeriod_GradingPeriodName],
            [New_SchoolYear_Unified],
            [New_StudentSectionAssociation_LocalCourseCode],
            [New_SchoolId_Unified],
            [New_StudentSectionAssociation_SectionIdentifier],
            [New_StudentSectionAssociation_SessionName],
            [New_StudentSectionAssociation_StudentUniqueId],
            [New_StudentSectionAssociation_Student_DocumentId],
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
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([GradeTypeDescriptor_DescriptorId]) OR UPDATE([GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId]) OR UPDATE([GradingPeriodGradingPeriod_GradingPeriodName]) OR UPDATE([SchoolId_Unified]) OR UPDATE([SchoolYear_Unified]) OR UPDATE([StudentSectionAssociation_BeginDate]) OR UPDATE([StudentSectionAssociation_LocalCourseCode]) OR UPDATE([StudentSectionAssociation_SectionIdentifier]) OR UPDATE([StudentSectionAssociation_SessionName]) OR UPDATE([StudentSectionAssociation_StudentUniqueId]))
    BEGIN
        DECLARE @identityChangedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY, [IdentityVersion] bigint NOT NULL);
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        OUTPUT inserted.[DocumentId], inserted.[IdentityVersion] INTO @identityChangedDocs ([DocumentId], [IdentityVersion])
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId];
        -- WHERE clause omitted for brevity as no changes are needed

        INSERT INTO [tracked_changes_edfi].[Grade] (
            [Old_StudentSectionAssociation_BeginDate],
            [Old_GradeTypeDescriptor_Namespace],
            [Old_GradeTypeDescriptor_CodeValue],
            [Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace],
            [Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue],
            [Old_GradingPeriodGradingPeriod_GradingPeriodName],
            [Old_SchoolYear_Unified],
            [Old_StudentSectionAssociation_LocalCourseCode],
            [Old_SchoolId_Unified],
            [Old_StudentSectionAssociation_SectionIdentifier],
            [Old_StudentSectionAssociation_SessionName],
            [Old_StudentSectionAssociation_StudentUniqueId],
            [Old_StudentSectionAssociation_Student_DocumentId],
            [New_StudentSectionAssociation_BeginDate],
            [New_GradeTypeDescriptor_Namespace],
            [New_GradeTypeDescriptor_CodeValue],
            [New_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace],
            [New_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue],
            [New_GradingPeriodGradingPeriod_GradingPeriodName],
            [New_SchoolYear_Unified],
            [New_StudentSectionAssociation_LocalCourseCode],
            [New_SchoolId_Unified],
            [New_StudentSectionAssociation_SectionIdentifier],
            [New_StudentSectionAssociation_SessionName],
            [New_StudentSectionAssociation_StudentUniqueId],
            [New_StudentSectionAssociation_Student_DocumentId],
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

There is no `*_Stamp` trigger in `dms.Descriptor`, so we will create one that follows the existing convention.

#### Authorization views

Following the existing DMS authorization approach, the authorization views should return DocumentIds instead of USIs and be renamed appropriately.

For example, the `auth.EducationOrganizationIdToContactDocumentIdIncludingDeletes` view definition should be similar to:

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
  sca_tc.Old_Contact_DocumentId as Contact_DocumentId
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN edfi.StudentSchoolAssociation ssa ON edOrgs.TargetEducationOrganizationId = ssa.SchoolId_Unified
  JOIN tracked_changes_edfi.StudentContactAssociation sca_tc ON ssa.Student_DocumentId = sca_tc.Old_Student_DocumentId 
UNION 
-- Current EdOrgHierarchy + Deleted/Key-changed StudentSchoolAssociation + Current StudentContactAssociation
SELECT 
  edOrgs.SourceEducationOrganizationId, 
  sca.Contact_DocumentId 
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.Old_SchoolId_Unified 
  JOIN edfi.StudentContactAssociation sca ON ssa_tc.Old_Student_DocumentId = sca.Student_DocumentId 
UNION 
-- Current EdOrgHierarchy + Deleted/Key-changed StudentSchoolAssociation + Deleted/Key-changed StudentContactAssociation
SELECT 
  edOrgs.SourceEducationOrganizationId, 
  sca_tc.Old_Contact_DocumentId as Contact_DocumentId
FROM 
  auth.EducationOrganizationIdToEducationOrganizationId edOrgs 
  JOIN tracked_changes_edfi.StudentSchoolAssociation ssa_tc ON edOrgs.TargetEducationOrganizationId = ssa_tc.Old_SchoolId_Unified 
  JOIN tracked_changes_edfi.StudentContactAssociation sca_tc ON ssa_tc.Old_Student_DocumentId = sca_tc.Old_Student_DocumentId;
```

### /deletes endpoints

Each resource and descriptor will get an accompanying `/deletes` endpoint. The response body should list the identifying fields with the same names used in the resource's `queryFieldMapping` in ApiSchema.json.

An example generated SQL query used to fulfill the `GET grades/deletes` request is:

```sql
SELECT 
  c.Id, 
  c.ChangeVersion, 

  c.Old_StudentSectionAssociation_BeginDate,                            
  c.Old_GradeTypeDescriptor_CodeValue,         
  c.Old_GradeTypeDescriptor_Namespace,         
  c.Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue,     
  c.Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace,     
  c.Old_GradingPeriodGradingPeriod_GradingPeriodName,                    
  c.Old_SchoolYear_Unified,              
  c.Old_StudentSectionAssociation_LocalCourseCode,                      
  c.Old_SchoolId_Unified,                             
  c.Old_StudentSectionAssociation_SectionIdentifier,                    
  c.Old_StudentSectionAssociation_SessionName,                          
  c.Old_StudentSectionAssociation_StudentUniqueId,
FROM 
  tracked_changes_edfi.Grade AS c
  LEFT JOIN dms.Descriptor AS OldGradeTypeDescriptor 
    ON OldGradeTypeDescriptor.Discriminator = 'GradeTypeDescriptor'
    AND OldGradeTypeDescriptor.CodeValue = c.Old_GradeTypeDescriptor_CodeValue
    AND OldGradeTypeDescriptor.Namespace = c.Old_GradeTypeDescriptor_Namespace

  LEFT JOIN dms.Descriptor AS OldGradingPeriodDescriptor
    ON OldGradingPeriodDescriptor.Discriminator = 'GradingPeriodDescriptor'
    AND OldGradingPeriodDescriptor.CodeValue = c.Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue
    AND OldGradingPeriodDescriptor.Namespace = c.Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace

  LEFT JOIN edfi.Grade AS src 
    ON OldGradeTypeDescriptor.DocumentId                    = src.GradeTypeDescriptor_DescriptorId 
    AND OldGradingPeriodDescriptor.DocumentId               = src.GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId 
    AND c.Old_GradingPeriodGradingPeriod_GradingPeriodName  = src.GradingPeriodGradingPeriod_GradingPeriodName 
    AND c.Old_SchoolId_Unified                              = src.SchoolId_Unified 
    AND c.Old_SchoolYear_Unified                            = src.SchoolYear_Unified 
    AND c.Old_StudentSectionAssociation_BeginDate           = src.StudentSectionAssociation_BeginDate 
    AND c.Old_StudentSectionAssociation_LocalCourseCode     = src.StudentSectionAssociation_LocalCourseCode 
    AND c.Old_StudentSectionAssociation_SectionIdentifier   = src.StudentSectionAssociation_SectionIdentifier 
    AND c.Old_StudentSectionAssociation_SessionName         = src.StudentSectionAssociation_SessionName 
    AND c.Old_StudentSectionAssociation_StudentUniqueId     = src.StudentSectionAssociation_StudentUniqueId
WHERE 
  src.StudentSectionAssociation_BeginDate IS NULL -- Exclude entries that were recreated, use any identity column
  AND c.NewBeginDate IS NULL -- Exclude key changes, use any New_* identity column
  AND (
      c.ChangeVersion >= @MinChangeVersion 
      AND c.ChangeVersion <= @MaxChangeVersion
  )
  AND (
      -- Auth check: Relationship with EdOrg:
      c.Old_SchoolId_Unified IN (SELECT TargetEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE SourceEducationOrganizationId IN (SELECT Id FROM @TokenEducationOrganizationIds))
      -- Auth check: Relationship with People:
      AND c.Old_StudentSectionAssociation_Student_DocumentId IN (SELECT Student_DocumentId FROM auth.EducationOrganizationIdToStudentDocumentIdIncludingDeletes WHERE SourceEducationOrganizationId IN (SELECT Id FROM @TokenEducationOrganizationIds))
  )
ORDER BY 
  c.ChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

In the example above, we join with the live table using identifying values instead of surrogate keys so that we can hide entries that were recreated. The same applies to descriptors, where we join using the Namespace and CodeValue.

An example generated SQL query used to fulfill the `GET crisisTypeDescriptors/deletes` request is:

```sql
SELECT DISTINCT 
  c.Id, 
  c.ChangeVersion, 
  c.Old_CodeValue,
  c.Old_Namespace 
FROM 
  tracked_changes_edfi.Descriptor AS c 
  LEFT JOIN dms.Descriptor AS src 
    ON src.Discriminator = 'CrisisTypeDescriptor'
    AND src.CodeValue = c.Old_CodeValue
    AND src.Namespace = c.Old_Namespace
WHERE 
  c.Discriminator = 'CrisisTypeDescriptor'
  AND src.CodeValue IS NULL              -- Exclude entries that were recreated, use any identity column
  AND c.New_CodeValue IS NULL            -- Exclude key changes, use any New_* identity column
  AND (
    -- Namespace-based auth check
    c.Old_Namespace LIKE @p1 
    OR c.Old_Namespace LIKE @p2 
    OR c.Old_Namespace LIKE @p3
  ) 
ORDER BY 
  c.ChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

### /keyChanges endpoints

Each resource and descriptor will get an accompanying `/keyChanges` endpoint. The response body should list the identifying fields with the same names used in the resource's `queryFieldMapping` in ApiSchema.json, plus the `Old_*` and `New_*` prefixes.

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
      c.New_StudentSectionAssociation_BeginDate IS NOT NULL -- Exclude tombstones, use any New_* identity column
      AND (
          c.ChangeVersion >= @MinChangeVersion 
          AND c.ChangeVersion <= @MaxChangeVersion
      )
      AND (
         -- Auth check: Relationship with EdOrg:
         c.Old_SchoolId_Unified IN (SELECT TargetEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE SourceEducationOrganizationId IN (SELECT Id FROM @TokenEducationOrganizationIds))
         -- Auth check: Relationship with People:
         AND c.Old_StudentSectionAssociation_Student_DocumentId IN (SELECT Student_DocumentId FROM auth.EducationOrganizationIdToStudentDocumentIdIncludingDeletes WHERE SourceEducationOrganizationId IN (SELECT Id FROM @TokenEducationOrganizationIds))
      )
    GROUP BY 
      c.Id
  ) 
SELECT 
  cw.Id, 
  cw.FinalChangeVersion AS ChangeVersion, 
  
  c_old.Old_StudentSectionAssociation_BeginDate,                            
  c_old.Old_GradeTypeDescriptor_CodeValue,         
  c_old.Old_GradeTypeDescriptor_Namespace,         
  c_old.Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue,     
  c_old.Old_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace,     
  c_old.Old_GradingPeriodGradingPeriod_GradingPeriodName,                    
  c_old.Old_SchoolYear_Unified,              
  c_old.Old_StudentSectionAssociation_LocalCourseCode,                      
  c_old.Old_SchoolId_Unified,                             
  c_old.Old_StudentSectionAssociation_SectionIdentifier,                    
  c_old.Old_StudentSectionAssociation_SessionName,                          
  c_old.Old_StudentSectionAssociation_StudentUniqueId,   

  c_new.New_StudentSectionAssociation_BeginDate,                            
  c_new.New_GradeTypeDescriptor_CodeValue,         
  c_new.New_GradeTypeDescriptor_Namespace,         
  c_new.New_GradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue,     
  c_new.New_GradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace,     
  c_new.New_GradingPeriodGradingPeriod_GradingPeriodName,                    
  c_new.New_SchoolYear_Unified,              
  c_new.New_StudentSectionAssociation_LocalCourseCode,                      
  c_new.New_SchoolId_Unified,                             
  c_new.New_StudentSectionAssociation_SectionIdentifier,                    
  c_new.New_StudentSectionAssociation_SessionName,                          
  c_new.New_StudentSectionAssociation_StudentUniqueId              
FROM 
  ChangeWindow AS cw 
  INNER JOIN tracked_changes_edfi.Grade AS c_old ON cw.Id = c_old.Id AND cw.InitialChangeVersion = c_old.ChangeVersion 
  INNER JOIN tracked_changes_edfi.Grade AS c_new ON cw.Id = c_new.Id AND cw.FinalChangeVersion = c_new.ChangeVersion 
ORDER BY 
  cw.FinalChangeVersion OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

### Tickets

- Emit the ChangeVersion sequence and the `changes.GetMaxChangeVersion()` function
- Emit the auth views
- Emit the `tracked_changes_<schema>` tables
- Emit the triggers that populate `tracked_changes_*` tables
- Emit the indirect triggers (similar to ODS, see above)
- Move the `DocumentId` column to the last position in `*_RefKey` indexes to improve performance for queries that do not specify the `DocumentId` (also likely needed by downstream SQL applications in the field)
  - Emit a new index in `dms.Descriptor` for the Discriminator, CodeValue, Namespace columns
  - Emit a new index in `dms.Document` for the ContentVersion column
- Add the `ReadChanges` action and authorization strategies to CMS's auth metadata
- Update resource and descriptor endpoints to filter by Min/Max ChangeVersion.
- Add the `/deletes` endpoint.
  - Same contract as ODS; there should not be breaking changes.
  - Add cascading deletes tests for abstract resources like StudentProgramAssociations; see ODS-4087.
- Add the `/keyChanges` endpoint.
  - Same contract as ODS; there should not be breaking changes.
  - Should also support descriptors and return empty results; see ODS-5422.
  - Add cascading key changes tests.
  - Add a test for total count; see ODS-5423.
- Add the `/availableChangeVersions` endpoint.
  - Same contract as ODS; there should not be breaking changes.
- MetaEd and DMS: Update the OpenAPI spec to include the `/deletes`, `/keyChanges`, and `/availableChangeVersions` endpoints (exclude SchoolYearTypes). Also update the existing resource and descriptor endpoints to include the Min/Max ChangeVersion filters.
- Consider whether to add specialized tests in a separate ticket or handle them on each endpoint ticket.
- Remove `dms.DocumentChangeEvent` because the new tables replace it

#### Stretch-goals

- Snapshot support
- Allow disabling the feature
- Spike ticket to add view-based auth

#### Open questions

- We should introduce the snapshots feature because downstream APIs must implement reverse paging with retry to get equivalent behavior. Should we add it for v1.0 or treat it as a stretch goal? CMS already supports storing derivatives, but DMS has to be updated to use them.
- Should we allow disabling the feature for v1.0? If so, users would need to tell the DDL emitter whether they want to include the change objects. The API would then check for the existence of the schema on boot and fail fast if the feature is enabled in appsettings.json.
