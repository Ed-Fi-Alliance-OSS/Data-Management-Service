CREATE SCHEMA "edfi";

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

CREATE TABLE "edfi"."SchoolAddressPhoneNumber" (
    "DocumentId" bigint NOT NULL,
    "AddressOrdinal" integer NOT NULL,
    "PhoneNumberOrdinal" integer NOT NULL,
    "PhoneNumber" varchar(20) NOT NULL,
    CONSTRAINT "PK_SchoolAddressPhoneNumber" PRIMARY KEY ("DocumentId", "AddressOrdinal", "PhoneNumberOrdinal")
);

ALTER TABLE "edfi"."SchoolAddress" ADD CONSTRAINT "FK_SchoolAddress_School" FOREIGN KEY ("DocumentId") REFERENCES "edfi"."School" ("DocumentId") ON DELETE CASCADE;

ALTER TABLE "edfi"."SchoolAddressPhoneNumber" ADD CONSTRAINT "FK_SchoolAddressPhoneNumber_SchoolAddress" FOREIGN KEY ("DocumentId", "AddressOrdinal") REFERENCES "edfi"."SchoolAddress" ("DocumentId", "AddressOrdinal") ON DELETE CASCADE;

