CREATE SCHEMA IF NOT EXISTS "edfi";
CREATE SCHEMA IF NOT EXISTS "auth";
CREATE SCHEMA IF NOT EXISTS "tracked_changes_edfi";

CREATE TABLE IF NOT EXISTS "edfi"."StaffEducationOrganizationAssignmentAssociation"
(
    "DocumentId" bigint NOT NULL,
    "Staff_DocumentId" bigint NOT NULL,
    "EducationOrganization_EducationOrganizationId" integer NOT NULL,
    CONSTRAINT "PK_StaffEducationOrganizationAssignmentAssociation" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."StaffEducationOrganizationEmploymentAssociation"
(
    "DocumentId" bigint NOT NULL,
    "Staff_DocumentId" bigint NOT NULL,
    "EducationOrganization_EducationOrganizationId" integer NOT NULL,
    CONSTRAINT "PK_StaffEducationOrganizationEmploymentAssociation" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."StudentContactAssociation"
(
    "DocumentId" bigint NOT NULL,
    "Student_DocumentId" bigint NOT NULL,
    "Contact_DocumentId" bigint NOT NULL,
    CONSTRAINT "PK_StudentContactAssociation" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."StudentEducationOrganizationResponsibilityAssociation"
(
    "DocumentId" bigint NOT NULL,
    "Student_DocumentId" bigint NOT NULL,
    "EducationOrganization_EducationOrganizationId" integer NOT NULL,
    CONSTRAINT "PK_StudentEducationOrganizationResponsibilityAssociation" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."StudentSchoolAssociation"
(
    "DocumentId" bigint NOT NULL,
    "Student_DocumentId" bigint NOT NULL,
    "SchoolId_Unified" integer NOT NULL,
    CONSTRAINT "PK_StudentSchoolAssociation" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "auth"."EducationOrganizationIdToEducationOrganizationId"
(
    "SourceEducationOrganizationId" bigint NOT NULL,
    "TargetEducationOrganizationId" bigint NOT NULL,
    CONSTRAINT "PK_EducationOrganizationIdToEducationOrganizationId" PRIMARY KEY ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."StaffEducationOrganizationAssignmentAssociation"
(
    "OldEducationOrganization_EducationOrganizationId" integer NOT NULL,
    "NewEducationOrganization_EducationOrganizationId" integer NULL,
    "OldStaff_DocumentId" bigint NOT NULL,
    "NewStaff_DocumentId" bigint NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_StaffEducationOrganizationAs_21269a4e1f" PRIMARY KEY ("ChangeVersion")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."StaffEducationOrganizationEmploymentAssociation"
(
    "OldEducationOrganization_EducationOrganizationId" integer NOT NULL,
    "NewEducationOrganization_EducationOrganizationId" integer NULL,
    "OldStaff_DocumentId" bigint NOT NULL,
    "NewStaff_DocumentId" bigint NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_StaffEducationOrganizationEm_6b655adbbd" PRIMARY KEY ("ChangeVersion")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."StudentContactAssociation"
(
    "OldStudent_DocumentId" bigint NOT NULL,
    "NewStudent_DocumentId" bigint NULL,
    "OldContact_DocumentId" bigint NOT NULL,
    "NewContact_DocumentId" bigint NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_StudentContactAssociation" PRIMARY KEY ("ChangeVersion")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."StudentEducationOrganizationResponsibilityAssociation"
(
    "OldEducationOrganization_EducationOrganizationId" integer NOT NULL,
    "NewEducationOrganization_EducationOrganizationId" integer NULL,
    "OldStudent_DocumentId" bigint NOT NULL,
    "NewStudent_DocumentId" bigint NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_StudentEducationOrganization_1f44fed0a1" PRIMARY KEY ("ChangeVersion")
);

CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."StudentSchoolAssociation"
(
    "OldSchoolId_Unified" integer NOT NULL,
    "NewSchoolId_Unified" integer NULL,
    "OldStudent_DocumentId" bigint NOT NULL,
    "NewStudent_DocumentId" bigint NULL,
    "Id" uuid NOT NULL,
    "ChangeVersion" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_tracked_changes_edfi_StudentSchoolAssociation" PRIMARY KEY ("ChangeVersion")
);

CREATE INDEX IF NOT EXISTS "IX_EducationOrganizationIdToEducationOrganizationId_Target" ON "auth"."EducationOrganizationIdToEducationOrganizationId" ("TargetEducationOrganizationId") INCLUDE ("SourceEducationOrganizationId");

CREATE OR REPLACE VIEW "auth"."EducationOrganizationIdToContactDocumentId" AS
SELECT DISTINCT
    edOrg."SourceEducationOrganizationId",
    sca."Contact_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "edfi"."StudentSchoolAssociation" ssa ON edOrg."TargetEducationOrganizationId" = ssa."SchoolId_Unified"
INNER JOIN "edfi"."StudentContactAssociation" sca ON ssa."Student_DocumentId" = sca."Student_DocumentId"
;

CREATE OR REPLACE VIEW "auth"."EducationOrganizationIdToStaffDocumentId" AS
SELECT
    edOrg."SourceEducationOrganizationId",
    seoaa."Staff_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "edfi"."StaffEducationOrganizationAssignmentAssociation" seoaa ON edOrg."TargetEducationOrganizationId" = seoaa."EducationOrganization_EducationOrganizationId"
UNION
SELECT
    edOrg."SourceEducationOrganizationId",
    seoea."Staff_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "edfi"."StaffEducationOrganizationEmploymentAssociation" seoea ON edOrg."TargetEducationOrganizationId" = seoea."EducationOrganization_EducationOrganizationId"
;

CREATE OR REPLACE VIEW "auth"."EducationOrganizationIdToStudentDocumentId" AS
SELECT DISTINCT
    edOrg."SourceEducationOrganizationId",
    ssa."Student_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "edfi"."StudentSchoolAssociation" ssa ON edOrg."TargetEducationOrganizationId" = ssa."SchoolId_Unified"
;

CREATE OR REPLACE VIEW "auth"."EducationOrganizationIdToStudentDocumentIdThroughResponsibility" AS
SELECT DISTINCT
    edOrg."SourceEducationOrganizationId",
    seora."Student_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "edfi"."StudentEducationOrganizationResponsibilityAssociation" seora ON edOrg."TargetEducationOrganizationId" = seora."EducationOrganization_EducationOrganizationId"
;

CREATE OR REPLACE VIEW "auth"."EducationOrganizationIdToContactDocumentIdIncludingDeletes" AS
SELECT
    edOrgToContact."SourceEducationOrganizationId",
    edOrgToContact."Contact_DocumentId"
FROM "auth"."EducationOrganizationIdToContactDocumentId" edOrgToContact
UNION
SELECT
    edOrg."SourceEducationOrganizationId",
    sca_tc."OldContact_DocumentId" AS "Contact_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "edfi"."StudentSchoolAssociation" ssa ON edOrg."TargetEducationOrganizationId" = ssa."SchoolId_Unified"
INNER JOIN "tracked_changes_edfi"."StudentContactAssociation" sca_tc ON ssa."Student_DocumentId" = sca_tc."OldStudent_DocumentId"
UNION
SELECT
    edOrg."SourceEducationOrganizationId",
    sca."Contact_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "tracked_changes_edfi"."StudentSchoolAssociation" ssa_tc ON edOrg."TargetEducationOrganizationId" = ssa_tc."OldSchoolId_Unified"
INNER JOIN "edfi"."StudentContactAssociation" sca ON ssa_tc."OldStudent_DocumentId" = sca."Student_DocumentId"
UNION
SELECT
    edOrg."SourceEducationOrganizationId",
    sca_tc."OldContact_DocumentId" AS "Contact_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "tracked_changes_edfi"."StudentSchoolAssociation" ssa_tc ON edOrg."TargetEducationOrganizationId" = ssa_tc."OldSchoolId_Unified"
INNER JOIN "tracked_changes_edfi"."StudentContactAssociation" sca_tc ON ssa_tc."OldStudent_DocumentId" = sca_tc."OldStudent_DocumentId"
;

CREATE OR REPLACE VIEW "auth"."EducationOrganizationIdToStaffDocumentIdIncludingDeletes" AS
SELECT
    edOrgToStaff."SourceEducationOrganizationId",
    edOrgToStaff."Staff_DocumentId"
FROM "auth"."EducationOrganizationIdToStaffDocumentId" edOrgToStaff
UNION
SELECT
    edOrg."SourceEducationOrganizationId",
    seoaa_tc."OldStaff_DocumentId" AS "Staff_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "tracked_changes_edfi"."StaffEducationOrganizationAssignmentAssociation" seoaa_tc ON edOrg."TargetEducationOrganizationId" = seoaa_tc."OldEducationOrganization_EducationOrganizationId"
UNION
SELECT
    edOrg."SourceEducationOrganizationId",
    seoea_tc."OldStaff_DocumentId" AS "Staff_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "tracked_changes_edfi"."StaffEducationOrganizationEmploymentAssociation" seoea_tc ON edOrg."TargetEducationOrganizationId" = seoea_tc."OldEducationOrganization_EducationOrganizationId"
;

CREATE OR REPLACE VIEW "auth"."EducationOrganizationIdToStudentDocumentIdDeletedResponsibility" AS
SELECT
    edOrgToStudentResp."SourceEducationOrganizationId",
    edOrgToStudentResp."Student_DocumentId"
FROM "auth"."EducationOrganizationIdToStudentDocumentIdThroughResponsibility" edOrgToStudentResp
UNION
SELECT
    edOrg."SourceEducationOrganizationId",
    seora_tc."OldStudent_DocumentId" AS "Student_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "tracked_changes_edfi"."StudentEducationOrganizationResponsibilityAssociation" seora_tc ON edOrg."TargetEducationOrganizationId" = seora_tc."OldEducationOrganization_EducationOrganizationId"
;

CREATE OR REPLACE VIEW "auth"."EducationOrganizationIdToStudentDocumentIdIncludingDeletes" AS
SELECT
    edOrgToStudent."SourceEducationOrganizationId",
    edOrgToStudent."Student_DocumentId"
FROM "auth"."EducationOrganizationIdToStudentDocumentId" edOrgToStudent
UNION
SELECT
    edOrg."SourceEducationOrganizationId",
    ssa_tc."OldStudent_DocumentId" AS "Student_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "tracked_changes_edfi"."StudentSchoolAssociation" ssa_tc ON edOrg."TargetEducationOrganizationId" = ssa_tc."OldSchoolId_Unified"
;

