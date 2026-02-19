CREATE SCHEMA IF NOT EXISTS "edfi";

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

CREATE TABLE IF NOT EXISTS "edfi"."SchoolAddressPhoneNumber"
(
    "DocumentId" bigint NOT NULL,
    "AddressOrdinal" integer NOT NULL,
    "PhoneNumberOrdinal" integer NOT NULL,
    "PhoneNumber" varchar(20) NOT NULL,
    CONSTRAINT "PK_SchoolAddressPhoneNumber" PRIMARY KEY ("DocumentId", "AddressOrdinal", "PhoneNumberOrdinal")
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'FK_SchoolAddress_School'
        AND conrelid = to_regclass('"edfi"."SchoolAddress"')
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
        WHERE conname = 'FK_SchoolAddressPhoneNumber_SchoolAddress'
        AND conrelid = to_regclass('"edfi"."SchoolAddressPhoneNumber"')
    )
    THEN
        ALTER TABLE "edfi"."SchoolAddressPhoneNumber"
        ADD CONSTRAINT "FK_SchoolAddressPhoneNumber_SchoolAddress"
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

DROP TRIGGER IF EXISTS "TR_School_Stamp" ON "edfi"."School";
CREATE TRIGGER "TR_School_Stamp"
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

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp"
BEFORE INSERT OR UPDATE ON "edfi"."SchoolAddress"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddressPhoneNumber_Stamp"()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE "dms"."Document"
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE "DocumentId" = NEW."DocumentId";
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolAddressPhoneNumber_Stamp" ON "edfi"."SchoolAddressPhoneNumber";
CREATE TRIGGER "TR_SchoolAddressPhoneNumber_Stamp"
BEFORE INSERT OR UPDATE ON "edfi"."SchoolAddressPhoneNumber"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddressPhoneNumber_Stamp"();

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

DROP TRIGGER IF EXISTS "TR_School_ReferentialIdentity" ON "edfi"."School";
CREATE TRIGGER "TR_School_ReferentialIdentity"
BEFORE INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"();

