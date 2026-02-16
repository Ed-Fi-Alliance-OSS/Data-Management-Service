CREATE SCHEMA "edfi";

CREATE TABLE "edfi"."School" (
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId")
);

CREATE TABLE "edfi"."LocalEducationAgency" (
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    CONSTRAINT "PK_LocalEducationAgency" PRIMARY KEY ("DocumentId")
);

CREATE TABLE "edfi"."EducationOrganizationIdentity" (
    "DocumentId" bigint NOT NULL,
    "EducationOrganizationId" integer NOT NULL,
    "Discriminator" varchar(50) NOT NULL,
    CONSTRAINT "PK_EducationOrganizationIdentity" PRIMARY KEY ("DocumentId")
);

ALTER TABLE "edfi"."School" ADD CONSTRAINT "FK_School_EducationOrganizationIdentity" FOREIGN KEY ("DocumentId") REFERENCES "edfi"."EducationOrganizationIdentity" ("DocumentId") ON DELETE CASCADE;

ALTER TABLE "edfi"."LocalEducationAgency" ADD CONSTRAINT "FK_LocalEducationAgency_EducationOrganizationIdentity" FOREIGN KEY ("DocumentId") REFERENCES "edfi"."EducationOrganizationIdentity" ("DocumentId") ON DELETE CASCADE;

CREATE OR REPLACE VIEW "edfi"."EducationOrganization" AS
SELECT "DocumentId" AS "DocumentId", "EducationOrganizationId" AS "EducationOrganizationId", 'School'::varchar(50) AS "Discriminator"
FROM "edfi"."School"
UNION ALL
SELECT "DocumentId" AS "DocumentId", "EducationOrganizationId" AS "EducationOrganizationId", 'LocalEducationAgency'::varchar(50) AS "Discriminator"
FROM "edfi"."LocalEducationAgency"
;

