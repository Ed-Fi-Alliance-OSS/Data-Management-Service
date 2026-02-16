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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    IF TG_OP = 'UPDATE' AND (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_LocalEducationAgency_Stamp"
BEFORE INSERT OR UPDATE ON "edfi"."LocalEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AbstractIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO "edfi"."EducationOrganizationIdentity" ("DocumentId", "EducationOrganizationId", "Discriminator")
    VALUES (NEW."DocumentId", NEW."EducationOrganizationId", 'Ed-Fi:LocalEducationAgency')
    ON CONFLICT ("DocumentId")
    DO UPDATE SET "EducationOrganizationId" = EXCLUDED."EducationOrganizationId";
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_LocalEducationAgency_AbstractIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."LocalEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_AbstractIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_LocalEducationAgency_ReferentialIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "dms"."ReferentialIdentity"
    WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 3;
    INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
    VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiLocalEducationAgency' || '$$.educationOrganizationId=' || NEW."EducationOrganizationId"::text), NEW."DocumentId", 3);
    DELETE FROM "dms"."ReferentialIdentity"
    WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
    INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
    VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiEducationOrganization' || '$$.educationOrganizationId=' || NEW."EducationOrganizationId"::text), NEW."DocumentId", 1);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_LocalEducationAgency_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."LocalEducationAgency"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_LocalEducationAgency_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    IF TG_OP = 'UPDATE' AND (OLD."EducationOrganizationId" IS DISTINCT FROM NEW."EducationOrganizationId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_School_Stamp"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_AbstractIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO "edfi"."EducationOrganizationIdentity" ("DocumentId", "EducationOrganizationId", "Discriminator")
    VALUES (NEW."DocumentId", NEW."EducationOrganizationId", 'Ed-Fi:School')
    ON CONFLICT ("DocumentId")
    DO UPDATE SET "EducationOrganizationId" = EXCLUDED."EducationOrganizationId";
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_School_AbstractIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_AbstractIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "dms"."ReferentialIdentity"
    WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 2;
    INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
    VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$$.educationOrganizationId=' || NEW."EducationOrganizationId"::text), NEW."DocumentId", 2);
    DELETE FROM "dms"."ReferentialIdentity"
    WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
    INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
    VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiEducationOrganization' || '$$.educationOrganizationId=' || NEW."EducationOrganizationId"::text), NEW."DocumentId", 1);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_School_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"();

CREATE OR REPLACE VIEW "edfi"."EducationOrganization" AS
SELECT "DocumentId" AS "DocumentId", "EducationOrganizationId" AS "EducationOrganizationId", 'School'::varchar(50) AS "Discriminator"
FROM "edfi"."School"
UNION ALL
SELECT "DocumentId" AS "DocumentId", "EducationOrganizationId" AS "EducationOrganizationId", 'LocalEducationAgency'::varchar(50) AS "Discriminator"
FROM "edfi"."LocalEducationAgency"
;

