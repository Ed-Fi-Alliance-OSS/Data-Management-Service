CREATE SCHEMA "edfi";
CREATE SCHEMA "sample";

CREATE TABLE "edfi"."School" (
    "DocumentId" bigint NOT NULL,
    "SchoolId" integer NOT NULL,
    CONSTRAINT "PK_School" PRIMARY KEY ("DocumentId")
);

CREATE TABLE "edfi"."SchoolAddress" (
    "DocumentId" bigint NOT NULL,
    "AddressOrdinal" integer NOT NULL,
    "Street" varchar(100) NOT NULL,
    CONSTRAINT "PK_SchoolAddress" PRIMARY KEY ("DocumentId", "AddressOrdinal")
);

CREATE TABLE "sample"."SchoolExtension" (
    "DocumentId" bigint NOT NULL,
    "ExtensionData" varchar(200) NULL,
    CONSTRAINT "PK_SchoolExtension" PRIMARY KEY ("DocumentId")
);

CREATE TABLE "sample"."SchoolAddressExtension" (
    "DocumentId" bigint NOT NULL,
    "AddressOrdinal" integer NOT NULL,
    "AddressExtensionData" varchar(100) NULL,
    CONSTRAINT "PK_SchoolAddressExtension" PRIMARY KEY ("DocumentId", "AddressOrdinal")
);

ALTER TABLE "edfi"."SchoolAddress" ADD CONSTRAINT "FK_SchoolAddress_School" FOREIGN KEY ("DocumentId") REFERENCES "edfi"."School" ("DocumentId") ON DELETE CASCADE;

ALTER TABLE "sample"."SchoolExtension" ADD CONSTRAINT "FK_SchoolExtension_School" FOREIGN KEY ("DocumentId") REFERENCES "edfi"."School" ("DocumentId") ON DELETE CASCADE;

ALTER TABLE "sample"."SchoolAddressExtension" ADD CONSTRAINT "FK_SchoolAddressExtension_SchoolAddress" FOREIGN KEY ("DocumentId", "AddressOrdinal") REFERENCES "edfi"."SchoolAddress" ("DocumentId", "AddressOrdinal") ON DELETE CASCADE;

