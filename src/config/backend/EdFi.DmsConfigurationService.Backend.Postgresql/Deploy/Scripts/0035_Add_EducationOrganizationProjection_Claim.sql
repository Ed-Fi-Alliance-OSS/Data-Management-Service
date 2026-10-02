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

INSERT INTO "dmscs"."ResourceClaim" ("ResourceName", "ClaimName")
SELECT v."ResourceName", v."ClaimName" FROM (
    VALUES
        ('educationOrganizationProjection','http://ed-fi.org/identity/claims/services/educationOrganizationProjection')
) AS v("ResourceName", "ClaimName")
WHERE NOT EXISTS (
    SELECT 1 FROM "dmscs"."ResourceClaim" s WHERE s."ClaimName" = v."ClaimName"
);

-- The claim is looked for at every depth, so a claim an operator already placed under another
-- claim is not duplicated. LastModifiedDate is the hierarchy's concurrency token: advancing it
-- makes a writer holding the pre-upgrade hierarchy fail with a conflict instead of overwriting it.
UPDATE "dmscs"."ClaimsHierarchy"
SET "Hierarchy" = "Hierarchy" || jsonb_build_array(
        '{"name":"http://ed-fi.org/identity/claims/services/educationOrganizationProjection","defaultAuthorization":{"actions":[{"name":"Read","authorizationStrategies":[{"name":"NoFurtherAuthorizationRequired"}]}]},"claimSets":[],"claims":[]}'::jsonb
    ),
    "LastModifiedDate" = now(),
    "LastModifiedAt" = now() AT TIME ZONE 'UTC',
    "ModifiedBy" = '0035_Add_EducationOrganizationProjection_Claim'
WHERE jsonb_typeof("Hierarchy") = 'array'
  AND NOT jsonb_path_exists(
        "Hierarchy",
        '$[*] ? (@.name == $claim)',
        '{"claim":"http://ed-fi.org/identity/claims/services/educationOrganizationProjection"}'
    )
  AND NOT jsonb_path_exists(
        "Hierarchy",
        '$.**.claims[*] ? (@.name == $claim)',
        '{"claim":"http://ed-fi.org/identity/claims/services/educationOrganizationProjection"}'
    );
