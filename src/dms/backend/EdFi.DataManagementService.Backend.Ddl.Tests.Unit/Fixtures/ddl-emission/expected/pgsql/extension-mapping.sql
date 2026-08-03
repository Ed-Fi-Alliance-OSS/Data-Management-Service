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
        WHERE conname = 'FK_SchoolExtension_School'
        AND conrelid = to_regclass('"sample"."SchoolExtension"')
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
        AND conrelid = to_regclass('"sample"."SchoolAddressExtension"')
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
RETURNS TRIGGER AS $func$
DECLARE
    _stampedContentVersion bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN
        _stampedContentVersion := nextval('"dms"."ChangeVersionSequence"');
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := now();
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
        NEW."CreatedAt" := now();
    ELSIF TG_OP = 'UPDATE' THEN
        _stampedContentVersion := nextval('"dms"."ChangeVersionSequence"');
        NEW."ContentVersion" := _stampedContentVersion;
        NEW."ContentLastModifiedAt" := now();
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD."SchoolId" IS DISTINCT FROM NEW."SchoolId") THEN
        NEW."IdentityVersion" := nextval('"dms"."ChangeVersionSequence"');
        NEW."IdentityLastModifiedAt" := now();
    END IF;
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_School_Stamp" ON "edfi"."School";
CREATE TRIGGER "TR_School_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."School"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_School_Stamp"();

CREATE OR REPLACE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "edfi"."School" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE r."DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."AddressOrdinal" IS DISTINCT FROM NEW."AddressOrdinal" OR OLD."Street" IS DISTINCT FROM NEW."Street") THEN
        RETURN NEW;
    END IF;
    UPDATE "edfi"."School" r
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE r."DocumentId" = NEW."DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolAddress_Stamp" ON "edfi"."SchoolAddress";
CREATE TRIGGER "TR_SchoolAddress_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "edfi"."SchoolAddress"
FOR EACH ROW
EXECUTE FUNCTION "edfi"."TF_TR_SchoolAddress_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolAddressExtension_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "edfi"."School" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE r."DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."AddressOrdinal" IS DISTINCT FROM NEW."AddressOrdinal" OR OLD."AddressExtensionData" IS DISTINCT FROM NEW."AddressExtensionData") THEN
        RETURN NEW;
    END IF;
    UPDATE "edfi"."School" r
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE r."DocumentId" = NEW."DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolAddressExtension_Stamp" ON "sample"."SchoolAddressExtension";
CREATE TRIGGER "TR_SchoolAddressExtension_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "sample"."SchoolAddressExtension"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolAddressExtension_Stamp"();

CREATE OR REPLACE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp"()
RETURNS TRIGGER AS $func$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "edfi"."School" r
        SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
        WHERE r."DocumentId" = OLD."DocumentId";
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD."DocumentId" IS DISTINCT FROM NEW."DocumentId" OR OLD."ExtensionData" IS DISTINCT FROM NEW."ExtensionData") THEN
        RETURN NEW;
    END IF;
    UPDATE "edfi"."School" r
    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
    WHERE r."DocumentId" = NEW."DocumentId";
    RETURN NEW;
END;
$func$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_SchoolExtension_Stamp" ON "sample"."SchoolExtension";
CREATE TRIGGER "TR_SchoolExtension_Stamp"
BEFORE INSERT OR UPDATE OR DELETE ON "sample"."SchoolExtension"
FOR EACH ROW
EXECUTE FUNCTION "sample"."TF_TR_SchoolExtension_Stamp"();

