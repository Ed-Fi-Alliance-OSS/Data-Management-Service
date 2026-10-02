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

INSERT INTO dmscs.ResourceClaim (ResourceName, ClaimName)
SELECT v.ResourceName, v.ClaimName FROM (
    VALUES
        ('educationOrganizationProjection','http://ed-fi.org/identity/claims/services/educationOrganizationProjection')
) AS v(ResourceName, ClaimName)
WHERE NOT EXISTS (
    SELECT 1 FROM dmscs.ResourceClaim s WHERE s.ClaimName = v.ClaimName
);

-- The claim is looked for at every depth, so a claim an operator already placed under another
-- claim is not duplicated. LastModifiedDate is the hierarchy's concurrency token: advancing it
-- makes a writer holding the pre-upgrade hierarchy fail with a conflict instead of overwriting it.
WITH HierarchyClaim AS (
    SELECT h.Id AS HierarchyId, c.[value] AS ClaimJson
    FROM dmscs.ClaimsHierarchy h
    CROSS APPLY OPENJSON(h.Hierarchy) c
    WHERE ISJSON(h.Hierarchy) = 1 AND c.[type] = 5
    UNION ALL
    SELECT parent.HierarchyId, child.[value]
    FROM HierarchyClaim parent
    CROSS APPLY OPENJSON(parent.ClaimJson, '$.claims') child
    WHERE child.[type] = 5
)
UPDATE h
SET Hierarchy = JSON_MODIFY(
        h.Hierarchy,
        'append $',
        JSON_QUERY(N'{"name":"http://ed-fi.org/identity/claims/services/educationOrganizationProjection","defaultAuthorization":{"actions":[{"name":"Read","authorizationStrategies":[{"name":"NoFurtherAuthorizationRequired"}]}]},"claimSets":[],"claims":[]}')
    ),
    LastModifiedDate = SYSUTCDATETIME(),
    LastModifiedAt = SYSUTCDATETIME(),
    ModifiedBy = N'0035_Add_EducationOrganizationProjection_Claim'
FROM dmscs.ClaimsHierarchy h
WHERE ISJSON(h.Hierarchy) = 1
  AND LEFT(LTRIM(h.Hierarchy), 1) = N'['
  AND NOT EXISTS (
        SELECT 1 FROM HierarchyClaim hc
        WHERE hc.HierarchyId = h.Id
          AND JSON_VALUE(hc.ClaimJson, '$.name')
              = N'http://ed-fi.org/identity/claims/services/educationOrganizationProjection'
    );
