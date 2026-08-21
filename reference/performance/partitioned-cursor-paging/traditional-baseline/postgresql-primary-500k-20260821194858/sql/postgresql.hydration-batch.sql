DROP TABLE IF EXISTS "page";
CREATE TEMP TABLE "page" ("DocumentId" bigint PRIMARY KEY) ON COMMIT DROP;

WITH page_ids AS (
SELECT r."DocumentId"
FROM "edfi"."Student" r
ORDER BY r."DocumentId" ASC
LIMIT @limit OFFSET @offset
)
INSERT INTO "page" ("DocumentId")
SELECT "DocumentId" FROM page_ids;

SELECT
    d."DocumentId",
    d."DocumentUuid",
    d."ContentVersion",
    d."IdentityVersion",
    d."ContentLastModifiedAt",
    d."IdentityLastModifiedAt"
FROM "dms"."Document" d
INNER JOIN "page" k ON d."DocumentId" = k."DocumentId"
ORDER BY d."DocumentId";

SELECT
    r."DocumentId",
    r."Person_DocumentId",
    r."Person_PersonId",
    r."Person_SourceSystemDescriptor_DescriptorId",
    r."BirthCountryDescriptor_DescriptorId",
    r."BirthSexDescriptor_DescriptorId",
    r."BirthStateAbbreviationDescriptor_DescriptorId",
    r."CitizenshipStatusDescriptor_DescriptorId",
    r."BirthCity",
    r."BirthDate",
    r."BirthInternationalProvince",
    r."DateEnteredUS",
    r."FirstName",
    r."GenerationCodeSuffix",
    r."LastSurname",
    r."MaidenName",
    r."MiddleName",
    r."MultipleBirthStatus",
    r."PersonalTitlePrefix",
    r."PreferredFirstName",
    r."PreferredLastSurname",
    r."StudentUniqueId"
FROM "edfi"."Student" r
INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
ORDER BY
    r."DocumentId" ASC
;


SELECT
    t."CollectionItemId",
    t."Ordinal",
    t."Student_DocumentId",
    t."IdentificationDocumentUseDescriptor_DescriptorId",
    t."IssuerCountryDescriptor_DescriptorId",
    t."PersonalInformationVerificationDescriptor_DescriptorId",
    t."DocumentExpirationDate",
    t."DocumentTitle",
    t."IssuerDocumentIdentificationCode",
    t."IssuerName"
FROM "edfi"."StudentIdentificationDocument" t
INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
ORDER BY
    t."Student_DocumentId" ASC,
    t."Ordinal" ASC
;


SELECT
    t."CollectionItemId",
    t."Ordinal",
    t."Student_DocumentId",
    t."OtherNameTypeDescriptor_DescriptorId",
    t."FirstName",
    t."GenerationCodeSuffix",
    t."LastSurname",
    t."MiddleName",
    t."PersonalTitlePrefix"
FROM "edfi"."StudentOtherName" t
INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
ORDER BY
    t."Student_DocumentId" ASC,
    t."Ordinal" ASC
;


SELECT
    t."CollectionItemId",
    t."Ordinal",
    t."Student_DocumentId",
    t."IdentificationDocumentUseDescriptor_DescriptorId",
    t."IssuerCountryDescriptor_DescriptorId",
    t."PersonalInformationVerificationDescriptor_DescriptorId",
    t."PersonalDocumentExpirationDate",
    t."PersonalDocumentTitle",
    t."PersonalIssuerDocumentIdentificationCode",
    t."PersonalIssuerName"
FROM "edfi"."StudentPersonalIdentificationDocument" t
INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
ORDER BY
    t."Student_DocumentId" ASC,
    t."Ordinal" ASC
;


SELECT
    t."CollectionItemId",
    t."Ordinal",
    t."Student_DocumentId",
    t."VisaDescriptor_DescriptorId"
FROM "edfi"."StudentVisa" t
INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
ORDER BY
    t."Student_DocumentId" ASC,
    t."Ordinal" ASC
;


SELECT
    p."DescriptorId",
    d."Uri"
FROM
    (
        SELECT t0."Person_SourceSystemDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."Student" t0
        INNER JOIN "page" k ON t0."DocumentId" = k."DocumentId"
        WHERE t0."Person_SourceSystemDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t1."BirthCountryDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."Student" t1
        INNER JOIN "page" k ON t1."DocumentId" = k."DocumentId"
        WHERE t1."BirthCountryDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t2."BirthSexDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."Student" t2
        INNER JOIN "page" k ON t2."DocumentId" = k."DocumentId"
        WHERE t2."BirthSexDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t3."BirthStateAbbreviationDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."Student" t3
        INNER JOIN "page" k ON t3."DocumentId" = k."DocumentId"
        WHERE t3."BirthStateAbbreviationDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t4."CitizenshipStatusDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."Student" t4
        INNER JOIN "page" k ON t4."DocumentId" = k."DocumentId"
        WHERE t4."CitizenshipStatusDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t5."IdentificationDocumentUseDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."StudentIdentificationDocument" t5
        INNER JOIN "page" k ON t5."Student_DocumentId" = k."DocumentId"
        WHERE t5."IdentificationDocumentUseDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t6."IssuerCountryDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."StudentIdentificationDocument" t6
        INNER JOIN "page" k ON t6."Student_DocumentId" = k."DocumentId"
        WHERE t6."IssuerCountryDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t7."PersonalInformationVerificationDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."StudentIdentificationDocument" t7
        INNER JOIN "page" k ON t7."Student_DocumentId" = k."DocumentId"
        WHERE t7."PersonalInformationVerificationDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t8."OtherNameTypeDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."StudentOtherName" t8
        INNER JOIN "page" k ON t8."Student_DocumentId" = k."DocumentId"
        WHERE t8."OtherNameTypeDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t9."IdentificationDocumentUseDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."StudentPersonalIdentificationDocument" t9
        INNER JOIN "page" k ON t9."Student_DocumentId" = k."DocumentId"
        WHERE t9."IdentificationDocumentUseDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t10."IssuerCountryDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."StudentPersonalIdentificationDocument" t10
        INNER JOIN "page" k ON t10."Student_DocumentId" = k."DocumentId"
        WHERE t10."IssuerCountryDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t11."PersonalInformationVerificationDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."StudentPersonalIdentificationDocument" t11
        INNER JOIN "page" k ON t11."Student_DocumentId" = k."DocumentId"
        WHERE t11."PersonalInformationVerificationDescriptor_DescriptorId" IS NOT NULL
        UNION
        SELECT t12."VisaDescriptor_DescriptorId" AS "DescriptorId"
        FROM "edfi"."StudentVisa" t12
        INNER JOIN "page" k ON t12."Student_DocumentId" = k."DocumentId"
        WHERE t12."VisaDescriptor_DescriptorId" IS NOT NULL
    ) p
INNER JOIN "dms"."Descriptor" d ON d."DocumentId" = p."DescriptorId"
ORDER BY
    p."DescriptorId" ASC
;


SELECT
    doc."DocumentId",
    doc."DocumentUuid",
    doc."ResourceKeyId"
FROM
    (
        SELECT DISTINCT t0."Person_DocumentId" AS "DocumentId"
        FROM "edfi"."Student" t0
        INNER JOIN "page" k ON t0."DocumentId" = k."DocumentId"
        WHERE t0."Person_DocumentId" IS NOT NULL
    ) p
INNER JOIN "dms"."Document" doc ON doc."DocumentId" = p."DocumentId"
ORDER BY
    doc."DocumentId" ASC
;


