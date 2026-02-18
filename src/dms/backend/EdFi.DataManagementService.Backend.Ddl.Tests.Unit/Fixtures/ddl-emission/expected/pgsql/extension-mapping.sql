CREATE SCHEMA IF NOT EXISTS "edfi";
CREATE SCHEMA IF NOT EXISTS "sample";

CREATE TABLE IF NOT EXISTS "edfi"."School"
(
    "DocumentId" bigint NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "edfi"."SchoolAddress"
(
    "DocumentId" bigint NOT NULL,
    "AddressOrdinal" integer NOT NULL,
    "Street" varchar(100) NOT NULL,
    CONSTRAINT "PK_SchoolAddress" PRIMARY KEY ("DocumentId", "AddressOrdinal")
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolExtension"
(
    "DocumentId" bigint NOT NULL,
    "ExtensionData" varchar(200) NULL,
    CONSTRAINT "PK_SchoolExtension" PRIMARY KEY ("DocumentId")
);

CREATE TABLE IF NOT EXISTS "sample"."SchoolAddressExtension"
(
    "DocumentId" bigint NOT NULL,
    "AddressOrdinal" integer NOT NULL,
    "AddressExtensionData" varchar(100) NULL,
    CONSTRAINT "PK_SchoolAddressExtension" PRIMARY KEY ("DocumentId", "AddressOrdinal")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolAddress_School'
        AND conrelid = to_regclass('edfi.SchoolAddress')
    )
    THEN
        ALTER TABLE "edfi"."SchoolAddress"
        ADD CONSTRAINT "FK_SchoolAddress_School"
        FOREIGN KEY ("DocumentId")
        REFERENCES "edfi"."School" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolExtension_School'
        AND conrelid = to_regclass('sample.SchoolExtension')
    )
    THEN
        ALTER TABLE "sample"."SchoolExtension"
        ADD CONSTRAINT "FK_SchoolExtension_School"
        FOREIGN KEY ("DocumentId")
        REFERENCES "edfi"."School" ("DocumentId")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolAddressExtension_SchoolAddress'
        AND conrelid = to_regclass('sample.SchoolAddressExtension')
    )
    THEN
        ALTER TABLE "sample"."SchoolAddressExtension"
        ADD CONSTRAINT "FK_SchoolAddressExtension_SchoolAddress"
        FOREIGN KEY ("DocumentId", "AddressOrdinal")
        REFERENCES "edfi"."SchoolAddress" ("DocumentId", "AddressOrdinal")
        ON DELETE CASCADE
        ON UPDATE NO ACTION;
    END IF;
END $$;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_SchoolAddress_Stamp"
BEFORE INSERT OR UPDATE ON "edfi"."SchoolAddress"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolAddressExtension_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_SchoolAddressExtension_Stamp"
BEFORE INSERT OR UPDATE ON "sample"."SchoolAddressExtension"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolAddressExtension_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_SchoolExtension_Stamp"
BEFORE INSERT OR UPDATE ON "sample"."SchoolExtension"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"()
RETURNS TRIGGER AS $$
BEGIN
    DELETE FROM "dms"."ReferentialIdentity"
    WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
    INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
    VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$$.schoolId=' || NEW."SchoolId"::text), NEW."DocumentId", 1);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER "TR_School_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"();

