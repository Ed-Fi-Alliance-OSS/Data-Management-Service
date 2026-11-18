-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Adds the education organization projection service claim, granted to no claim set.
--
-- A catalog loads the embedded claims only while its claims tables are empty, so a catalog
-- provisioned by an earlier release keeps its stored hierarchy. This script therefore also
-- appends the claim to a stored hierarchy that lacks it; a catalog with no stored hierarchy yet
-- receives the claim from the embedded claims when they are first loaded. Both statements are
-- insert-if-missing and leave every existing claim and grant unchanged.
--
-- "Missing" follows CMS's own claim-name matching, which is exact first and then
-- StringComparison.OrdinalIgnoreCase. For this all-ASCII name that equivalence is exactly an
-- ASCII-only name that is equal after ASCII upper-casing, so a claim an operator already provides
-- in any letter case is kept as it is: never renamed, replaced or duplicated. The "C" collation
-- keeps upper() ASCII-only and independent of the database's collation.

INSERT INTO "dmscs"."ResourceClaim" ("ResourceName", "ClaimName")
SELECT v."ResourceName", v."ClaimName" FROM (
    VALUES
        ('educationOrganizationProjection','http://ed-fi.org/identity/claims/services/educationOrganizationProjection')
) AS v("ResourceName", "ClaimName")
WHERE NOT EXISTS (
    SELECT 1 FROM "dmscs"."ResourceClaim" s
    WHERE (s."ClaimName" COLLATE "C") ~ '^[ -~]*$'
      AND upper(s."ClaimName" COLLATE "C") = upper(v."ClaimName" COLLATE "C")
);

-- The claim is looked for at every depth, so a claim an operator already placed under another
-- claim is not duplicated, and a hierarchy that already has it is not touched at all. When the
-- claim is appended, LastModifiedDate, the hierarchy's concurrency token, advances so a writer
-- holding the pre-upgrade hierarchy fails with a conflict instead of overwriting the claim.
--
-- The appended claim takes its name from the equivalent ResourceClaim row the statement above
-- kept or inserted, so it keeps an operator's spelling and the hierarchy and the metadata name the
-- claim identically, as claim-set grant changes require. The canonical spelling wins when several
-- equivalent rows exist, and the lowest Id breaks any remaining tie. jsonb_build_object escapes
-- the name.
WITH RECURSIVE "HierarchyClaim" AS (
    SELECT h."Id" AS "HierarchyId", c.value AS "Claim"
    FROM "dmscs"."ClaimsHierarchy" h
    CROSS JOIN LATERAL jsonb_array_elements(
        CASE WHEN jsonb_typeof(h."Hierarchy") = 'array' THEN h."Hierarchy" ELSE '[]'::jsonb END
    ) c
    UNION ALL
    SELECT parent."HierarchyId", c.value
    FROM "HierarchyClaim" parent
    CROSS JOIN LATERAL jsonb_array_elements(
        CASE
            WHEN jsonb_typeof(parent."Claim" -> 'claims') = 'array' THEN parent."Claim" -> 'claims'
            ELSE '[]'::jsonb
        END
    ) c
),
"MetadataClaim" AS (
    SELECT s."ClaimName"
    FROM "dmscs"."ResourceClaim" s
    WHERE (s."ClaimName" COLLATE "C") ~ '^[ -~]*$'
      AND upper(s."ClaimName" COLLATE "C")
          = upper('http://ed-fi.org/identity/claims/services/educationOrganizationProjection' COLLATE "C")
    ORDER BY
        CASE
            WHEN (s."ClaimName" COLLATE "C")
                = ('http://ed-fi.org/identity/claims/services/educationOrganizationProjection' COLLATE "C")
            THEN 0
            ELSE 1
        END,
        s."Id"
    LIMIT 1
)
UPDATE "dmscs"."ClaimsHierarchy" AS h
SET "Hierarchy" = h."Hierarchy" || jsonb_build_array(
        jsonb_build_object(
            'name', m."ClaimName",
            'defaultAuthorization',
            '{"actions":[{"name":"Read","authorizationStrategies":[{"name":"NoFurtherAuthorizationRequired"}]}]}'::jsonb,
            'claimSets', '[]'::jsonb,
            'claims', '[]'::jsonb
        )
    ),
    "LastModifiedDate" = now(),
    "LastModifiedAt" = now() AT TIME ZONE 'UTC',
    "ModifiedBy" = '0039_Add_EducationOrganizationProjection_Claim'
FROM "MetadataClaim" m
WHERE jsonb_typeof(h."Hierarchy") = 'array'
  AND NOT EXISTS (
        SELECT 1 FROM "HierarchyClaim" hc
        WHERE hc."HierarchyId" = h."Id"
          AND ((hc."Claim" ->> 'name') COLLATE "C") ~ '^[ -~]*$'
          AND upper((hc."Claim" ->> 'name') COLLATE "C")
              = upper('http://ed-fi.org/identity/claims/services/educationOrganizationProjection' COLLATE "C")
    );
