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

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'INSERT' OR (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        DELETE FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = NEW."DocumentId" AND "ResourceKeyId" = 1;
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES ("dms"."uuidv5"('edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiSchool' || '$.schoolId=' || NEW."SchoolId"::text), NEW."DocumentId", 1);
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_ReferentialIdentity" ON "edfi"."School";
CREATE TRIGGER "TR_School_ReferentialIdentity"
AFTER INSERT OR UPDATE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_ReferentialIdentity"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_School_Stamp"()
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
    _stampedContentLastModifiedAt timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN
        SELECT "ContentVersion", "ContentLastModifiedAt"
        INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt
        FROM "dms"."Document"
        WHERE "DocumentId" = NEW."DocumentId";
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := _stampedContentLastModifiedAt;
    ELSIF TG_OP = 'UPDATE' THEN
        UPDATE "dms"."Document"
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId"
        RETURNING "ContentVersion", "ContentLastModifiedAt" INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt;
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := _stampedContentLastModifiedAt;
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        UPDATE "dms"."Document"
        SET "IdentityVersion" = nextval('"dms"."ChangeVersionSequence"'), "IdentityLastModifiedAt" = now()
        WHERE "DocumentId" = NEW."DocumentId";
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_Stamp" ON "edfi"."School";
CREATE TRIGGER "TR_School_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_ins"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT newtab."DocumentId" AS "DocumentId"
        FROM newtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_upd"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT n."DocumentId" AS "DocumentId"
        FROM newtab n
        LEFT JOIN oldtab o ON o."DocumentId" = n."DocumentId" AND o."AddressOrdinal" = n."AddressOrdinal"
        WHERE o."DocumentId" IS NULL OR n."DocumentId" IS DISTINCT FROM o."DocumentId" OR n."AddressOrdinal" IS DISTINCT FROM o."AddressOrdinal" OR n."Street" IS DISTINCT FROM o."Street"
        UNION
        SELECT o."DocumentId" AS "DocumentId"
        FROM oldtab o
        LEFT JOIN newtab n ON n."DocumentId" = o."DocumentId" AND n."AddressOrdinal" = o."AddressOrdinal"
        WHERE n."DocumentId" IS NULL OR n."DocumentId" IS DISTINCT FROM o."DocumentId" OR n."AddressOrdinal" IS DISTINCT FROM o."AddressOrdinal" OR n."Street" IS DISTINCT FROM o."Street"
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_del"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT oldtab."DocumentId" AS "DocumentId"
        FROM oldtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp" ON "edfi"."SchoolAddress";
DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp_ins" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp_ins"
AFTER INSERT ON "edfi"."SchoolAddress"
REFERENCING NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_ins"();

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp_upd" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp_upd"
AFTER UPDATE ON "edfi"."SchoolAddress"
REFERENCING OLD TABLE AS oldtab NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_upd"();

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp_del" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp_del"
AFTER DELETE ON "edfi"."SchoolAddress"
REFERENCING OLD TABLE AS oldtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp_del"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddressPhoneNumber_Stamp_ins"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT newtab."DocumentId" AS "DocumentId"
        FROM newtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddressPhoneNumber_Stamp_upd"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT n."DocumentId" AS "DocumentId"
        FROM newtab n
        LEFT JOIN oldtab o ON o."DocumentId" = n."DocumentId" AND o."AddressOrdinal" = n."AddressOrdinal" AND o."PhoneNumberOrdinal" = n."PhoneNumberOrdinal"
        WHERE o."DocumentId" IS NULL OR n."DocumentId" IS DISTINCT FROM o."DocumentId" OR n."AddressOrdinal" IS DISTINCT FROM o."AddressOrdinal" OR n."PhoneNumberOrdinal" IS DISTINCT FROM o."PhoneNumberOrdinal" OR n."PhoneNumber" IS DISTINCT FROM o."PhoneNumber"
        UNION
        SELECT o."DocumentId" AS "DocumentId"
        FROM oldtab o
        LEFT JOIN newtab n ON n."DocumentId" = o."DocumentId" AND n."AddressOrdinal" = o."AddressOrdinal" AND n."PhoneNumberOrdinal" = o."PhoneNumberOrdinal"
        WHERE n."DocumentId" IS NULL OR n."DocumentId" IS DISTINCT FROM o."DocumentId" OR n."AddressOrdinal" IS DISTINCT FROM o."AddressOrdinal" OR n."PhoneNumberOrdinal" IS DISTINCT FROM o."PhoneNumberOrdinal" OR n."PhoneNumber" IS DISTINCT FROM o."PhoneNumber"
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddressPhoneNumber_Stamp_del"()
RETURNS TRIGGER AS $func$
BEGIN
    WITH affected AS (
        SELECT DISTINCT oldtab."DocumentId" AS "DocumentId"
        FROM oldtab
    ),
    stamped AS (
        UPDATE "dms"."Document" d
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        FROM affected a
        WHERE d."DocumentId" = a."DocumentId"
        AND EXISTS (SELECT 1 FROM "edfi"."School" r WHERE r."DocumentId" = a."DocumentId")
        RETURNING d."DocumentId", d."ContentVersion", d."ContentLastModifiedAt"
    )
    UPDATE "edfi"."School" r
    SET "ContentVersion" = stamped."ContentVersion", "ContentLastModifiedAt" = stamped."ContentLastModifiedAt"
    FROM stamped
    WHERE r."DocumentId" = stamped."DocumentId";
    RETURN NULL;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolAddressPhoneNumber_Stamp" ON "edfi"."SchoolAddressPhoneNumber";
DROP TRIGGER IF EXISTS "TR_SchoolAddressPhoneNumber_Stamp_ins" ON "edfi"."SchoolAddressPhoneNumber";
CREATE TRIGGER "TR_SchoolAddressPhoneNumber_Stamp_ins"
AFTER INSERT ON "edfi"."SchoolAddressPhoneNumber"
REFERENCING NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddressPhoneNumber_Stamp_ins"();

DROP TRIGGER IF EXISTS "TR_SchoolAddressPhoneNumber_Stamp_upd" ON "edfi"."SchoolAddressPhoneNumber";
CREATE TRIGGER "TR_SchoolAddressPhoneNumber_Stamp_upd"
AFTER UPDATE ON "edfi"."SchoolAddressPhoneNumber"
REFERENCING OLD TABLE AS oldtab NEW TABLE AS newtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddressPhoneNumber_Stamp_upd"();

DROP TRIGGER IF EXISTS "TR_SchoolAddressPhoneNumber_Stamp_del" ON "edfi"."SchoolAddressPhoneNumber";
CREATE TRIGGER "TR_SchoolAddressPhoneNumber_Stamp_del"
AFTER DELETE ON "edfi"."SchoolAddressPhoneNumber"
REFERENCING OLD TABLE AS oldtab
FOR EACH STATEMENT
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddressPhoneNumber_Stamp_del"();

