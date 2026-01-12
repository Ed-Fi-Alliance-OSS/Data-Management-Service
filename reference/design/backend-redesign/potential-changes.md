
# Potential changes

This document describes potential changes we could make to the [backend redesign proposal](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/783).

A summarized representation of the Data Model should look like this.
Some columns are omitted for brevity.

```sql
CREATE TABLE dms.ResourceKey (
    ResourceKeyId smallint NOT NULL PRIMARY KEY,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(32) NOT NULL,
    CONSTRAINT UX_ResourceKey_ProjectName_ResourceName UNIQUE (ProjectName, ResourceName)
);

CREATE TABLE dms.Document (
    DocumentId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,

    -- Only initialized if the Aggregate Data feature is enabled or if the CDC/Kafka feature is enabled:
    DocumentJson nvarchar(max) NULL, 
    DocumentJsonChangeVersion bigint NULL,

    ResourceKeyId smallint NOT NULL,
    CONSTRAINT FK_Document_ResourceKey FOREIGN KEY (ResourceKeyId) REFERENCES dms.ResourceKey (ResourceKeyId)
);

CREATE TABLE edfi.EducationOrganization(
	DocumentId bigint NOT NULL PRIMARY KEY,
    CONSTRAINT FK_EdOrg_Document FOREIGN KEY (DocumentId) REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,

    EducationOrganizationId bigint NOT NULL,
    CONSTRAINT UX_EdOrg_Identity UNIQUE (EducationOrganizationId),
    CONSTRAINT UX_EdOrg_DocumentId_EducationOrganizationId UNIQUE (DocumentId,EducationOrganizationId),

    -- Change tracking columns not needed since EdOrgs don't participate in cascading key changes (yet? or ever?) and can't be modified by POST/PUT	
)

CREATE TABLE edfi.School(
	DocumentId bigint NOT NULL PRIMARY KEY,
    
    Id uniqueidentifier NOT NULL,
    CONSTRAINT UX_SchoolId UNIQUE (Id),

	SchoolId bigint NOT NULL,
    CONSTRAINT FK_School_EdOrg FOREIGN KEY (DocumentId,SchoolId) REFERENCES edfi.EducationOrganization (DocumentId,EducationOrganizationId) ON DELETE CASCADE,
	CONSTRAINT UX_School_Identity UNIQUE (SchoolId),

	LastModifiedDate datetime2(7) NOT NULL DEFAULT (getutcdate()),
	ChangeVersion bigint NOT NULL DEFAULT (NEXT VALUE FOR dms.ChangeVersionSequence),
)


CREATE TABLE edfi.Student(
	DocumentId bigint NOT NULL PRIMARY KEY,
    CONSTRAINT FK_Student_Document FOREIGN KEY (DocumentId) REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    
    Id uniqueidentifier NOT NULL,
    CONSTRAINT UX_Student_Id UNIQUE (Id),

	StudentUniqueId varchar(32) NOT NULL,
    CONSTRAINT UX_Student_Identity UNIQUE (StudentUniqueId),
	
	LastModifiedDate datetime2(7) NOT NULL DEFAULT (getutcdate()),
    IdentityVersion int NOT NULL DEFAULT (0), -- Let's assume that Student allows identity updates
	ChangeVersion bigint NOT NULL DEFAULT (NEXT VALUE FOR dms.ChangeVersionSequence),
    CONSTRAINT UX_Student_DocumentId_IdentityVersion UNIQUE (DocumentId,IdentityVersion),
)

CREATE TABLE edfi.StudentSchoolAssociation(
	DocumentId bigint NOT NULL PRIMARY KEY,
    CONSTRAINT FK_SSA_Document FOREIGN KEY (DocumentId) REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    
    Id uniqueidentifier NOT NULL,
    CONSTRAINT UX_SSA_Id UNIQUE (Id),

	Student_DocumentId bigint NOT NULL,
    Student_IdentityVersion int NOT NULL,
    CONSTRAINT FK_SSA_Student FOREIGN KEY (Student_DocumentId,Student_IdentityVersion) REFERENCES edfi.Student (DocumentId,IdentityVersion) ON UPDATE CASCADE,

    School_DocumentId bigint NOT NULL,
    CONSTRAINT FK_SSA_School FOREIGN KEY (School_DocumentId) REFERENCES edfi.School (DocumentId) ON UPDATE CASCADE,

    CONSTRAINT UX_SSA_Identity UNIQUE (Student_DocumentId,School_DocumentId),
	
	LastModifiedDate datetime2(7) NOT NULL DEFAULT (getutcdate()),
    IdentityVersion int NOT NULL DEFAULT (0),
	ChangeVersion bigint NOT NULL DEFAULT (NEXT VALUE FOR dms.ChangeVersionSequence),
    CONSTRAINT UX_SSA_DocumentId_IdentityVersion UNIQUE (DocumentId,IdentityVersion),
)

CREATE TABLE edfi.StudentAssessmentRegistration(
	DocumentId bigint NOT NULL PRIMARY KEY,
    CONSTRAINT FK_SAR_Document FOREIGN KEY (DocumentId) REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    
    Id uniqueidentifier NOT NULL,
    CONSTRAINT UX_SAR_Id UNIQUE (Id),

	StudentSchoolAssociation_DocumentId bigint NOT NULL,
    StudentSchoolAssociation_IdentityVersion int NOT NULL,
    CONSTRAINT FK_SAR_Student FOREIGN KEY (StudentSchoolAssociation_DocumentId,StudentSchoolAssociation_IdentityVersion) REFERENCES edfi.StudentSchoolAssociation (DocumentId,IdentityVersion) ON UPDATE CASCADE,

	LastModifiedDate datetime2(7) NOT NULL DEFAULT (getutcdate()),
    IdentityVersion int NOT NULL DEFAULT (0),
	ChangeVersion bigint NOT NULL DEFAULT (NEXT VALUE FOR dms.ChangeVersionSequence),
    CONSTRAINT UX_SAR_DocumentId_IdentityVersion UNIQUE (DocumentId,IdentityVersion),
)

-- tracked_changes_* Tables are only created if the Change Queries feature is set to ODS-style
```

## What changed
### Dropped the `dms.ReferentialIdentity` table
**Why:** As already stated in the [strengths-risks.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/7e3cac5a3a08d5dcd822ad770eed9a1701874a17/reference/design/backend-redesign/strengths-risks.md#referenceedge-integrity-highest-operational-risk) document, keeping  `dms.ReferentialIdentity` consistent when cascading key changes occur requires complex locking logic, which  makes introducing bugs easy, and bugs in this code section can have a high (costly?) impact. Because of locking, throughput could also be impacted if multiple requests upsert a resource that references a hot resource (such as a school).

[overview.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/7e3cac5a3a08d5dcd822ad770eed9a1701874a17/reference/design/backend-redesign/overview.md#if-we-removed-it) explains the drawbacks of not having a Referential ID, I hypothesize that these drawbacks are easier to manage than the drawbacks of maintaining `dms.ReferentialIdentity` and `dms.ReferenceEdge`.

In this proposal, all resources use a surrogate key as the primary key, meaning that cascading key changes are fast because they only modify the `IdentityVersion`, `ChangeVersion`, and `LastModifiedDate` of the affected resources.

This proposal improves the performance of cascading key changes but penalizes reads, as they would require joining multiple tables to materialize the resource's full identity. How often do cascading key changes occur in the field? If they are rare, the current ODS design (with natural keys as PKs) may be optimal.

Enabling the AggregateData feature (cache) could lower the performance impact of joining multiple tables to materialize the resource's full identity. Although scripts that access the DB directly wouldn't benefit from the cache, and would need to use views that internally have many joins.

### Use triggers for change tracking (for etag & lastModifiedDate updates)
**Why:** The original proposal uses C# logic to update the etag/lastModifiedDate when there's a cascading key change, meaning that there's additional latency spent in DB roundtrips. 
It also introduces `DocumentChangeEvent` and `IdentityChangeEvent`, which store all the changes made to all resources. These tables could become large over time (as already noted [here](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/47c27bb69a7ac46362dbfe573fe999dbf30644c2/reference/design/backend-redesign/update-tracking.md#operational-and-performance-notes)).

In this proposal, notice that the `StudentAssessmentRegistration` references `StudentSchoolAssociation` with a combination of `StudentSchoolAssociation_DocumentId`, and `StudentSchoolAssociation_IdentityVersion`; the foreign key is configured to cascade updates, a trigger monitors changes to any of these columns and increases the `IdentityVersion`, `ChangeVersion`, and `LastModifiedDate`.

The main drawback is that we need to generate resource-specific triggers, similar to how ODS does it.

## Change Queries considerations
In this proposal, `dms.Document` is the only table that could become large and potentially require partitioning. The table is only needed for the CDC/Kafka streaming feature, to conveniently expose all table changes in a single, dependency-sorted, topic.

## Example triggers for change tracking

```sql
CREATE TRIGGER edfi_SSA_TR_UpdateVersions ON edfi.StudentSchoolAssociation
AFTER UPDATE
AS
BEGIN
    -- Increase ChangeVersion and LastModifiedDate
    UPDATE edfi.StudentSchoolAssociation
        SET     
            ChangeVersion = (NEXT VALUE FOR dms.ChangeVersionSequence),
            LastModifiedDate = GETDATE()
        FROM edfi.StudentSchoolAssociation u
        WHERE EXISTS (SELECT 1 FROM inserted i WHERE i.id = u.id);

    -- Increase IdentityVersion only if the identifying values changed
    UPDATE StudentSchoolAssociation SET IdentityVersion += 1
        FROM edfi.StudentSchoolAssociation SSA
        INNER JOIN inserted i ON SSA.ID = i.ID
        INNER JOIN deleted d ON SSA.ID = d.ID
        WHERE i.Student_DocumentId <> d.Student_DocumentId 
            OR i.Student_IdentityVersion <> d.Student_IdentityVersion;
END;
```

We would also need a trigger on child tables that increases the ChangeVersion of the root table (its implementation is an exercise for the reader).