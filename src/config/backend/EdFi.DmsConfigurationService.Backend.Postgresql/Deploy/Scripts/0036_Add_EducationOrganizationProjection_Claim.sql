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
)
UPDATE "dmscs"."ClaimsHierarchy" AS h
SET "Hierarchy" = h."Hierarchy" || jsonb_build_array(
        '{"name":"http://ed-fi.org/identity/claims/services/educationOrganizationProjection","defaultAuthorization":{"actions":[{"name":"Read","authorizationStrategies":[{"name":"NoFurtherAuthorizationRequired"}]}]},"claimSets":[],"claims":[]}'::jsonb
    ),
    "LastModifiedDate" = now(),
    "LastModifiedAt" = now() AT TIME ZONE 'UTC',
    "ModifiedBy" = '0036_Add_EducationOrganizationProjection_Claim'
WHERE jsonb_typeof(h."Hierarchy") = 'array'
  AND NOT EXISTS (
        SELECT 1 FROM "HierarchyClaim" hc
        WHERE hc."HierarchyId" = h."Id"
          AND ((hc."Claim" ->> 'name') COLLATE "C") ~ '^[ -~]*$'
          AND upper((hc."Claim" ->> 'name') COLLATE "C")
              = upper('http://ed-fi.org/identity/claims/services/educationOrganizationProjection' COLLATE "C")
    );
