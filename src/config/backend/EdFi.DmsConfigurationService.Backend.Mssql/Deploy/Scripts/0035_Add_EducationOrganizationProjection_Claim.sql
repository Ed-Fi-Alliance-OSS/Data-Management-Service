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
-- in any letter case is kept as it is: never renamed, replaced or duplicated. Comparisons use a
-- binary collation so they do not depend on the database's default collation; the ASCII check
-- keeps UPPER() from folding a non-ASCII letter onto an ASCII one, which CMS does not do.

INSERT INTO dmscs.ResourceClaim (ResourceName, ClaimName)
SELECT v.ResourceName, v.ClaimName FROM (
    VALUES
        (N'educationOrganizationProjection',N'http://ed-fi.org/identity/claims/services/educationOrganizationProjection')
) AS v(ResourceName, ClaimName)
WHERE NOT EXISTS (
    SELECT 1 FROM dmscs.ResourceClaim s
    WHERE s.ClaimName COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^ -~]%'
      AND UPPER(s.ClaimName COLLATE Latin1_General_100_BIN2)
          = UPPER(v.ClaimName COLLATE Latin1_General_100_BIN2)
);

-- The claim is looked for at every depth, so a claim an operator already placed under another
-- claim is not duplicated, and a hierarchy that already has it is not touched at all. When the
-- claim is appended, LastModifiedDate, the hierarchy's concurrency token, advances so a writer
-- holding the pre-upgrade hierarchy fails with a conflict instead of overwriting the claim.
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
          AND JSON_VALUE(hc.ClaimJson, '$.name') COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^ -~]%'
          AND UPPER(JSON_VALUE(hc.ClaimJson, '$.name') COLLATE Latin1_General_100_BIN2)
              = UPPER(N'http://ed-fi.org/identity/claims/services/educationOrganizationProjection' COLLATE Latin1_General_100_BIN2)
    );
