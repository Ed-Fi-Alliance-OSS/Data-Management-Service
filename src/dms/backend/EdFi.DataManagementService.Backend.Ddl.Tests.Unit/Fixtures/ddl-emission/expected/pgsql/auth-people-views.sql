CREATE SCHEMA IF NOT EXISTS "edfi";
CREATE SCHEMA IF NOT EXISTS "auth";

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
SELECT DISTINCT
    edOrg."SourceEducationOrganizationId",
    seoaa."Staff_DocumentId"
FROM "auth"."EducationOrganizationIdToEducationOrganizationId" edOrg
INNER JOIN "edfi"."StaffEducationOrganizationAssignmentAssociation" seoaa ON edOrg."TargetEducationOrganizationId" = seoaa."EducationOrganization_EducationOrganizationId"
UNION
SELECT DISTINCT
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

