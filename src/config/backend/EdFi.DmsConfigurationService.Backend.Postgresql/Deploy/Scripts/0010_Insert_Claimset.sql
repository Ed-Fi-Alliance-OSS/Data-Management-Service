-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved)
SELECT v.ClaimSetName, v.IsSystemReserved
FROM (
    SELECT
        ROW_NUMBER() OVER () AS OrderIndex,
        ClaimSetName,
        IsSystemReserved
    FROM (
        VALUES
            ('E2E-NameSpaceBasedClaimSet', TRUE),
            ('E2E-NoFurtherAuthRequiredClaimSet', TRUE),
            ('E2E-RelationshipsWithEdOrgsOnlyClaimSet', TRUE),
            ('SISVendor', TRUE),
            ('EdFiSandbox', TRUE),
            ('RosterVendor', TRUE),
            ('AssessmentVendor', TRUE),
            ('AssessmentRead', TRUE),
            ('BootstrapDescriptorsandEdOrgs', TRUE),
            ('DistrictHostedSISVendor', TRUE),
            ('EdFiODSAdminApp', TRUE),
            ('ABConnect', TRUE),
            ('EdFiAPIPublisherReader', TRUE),
            ('EdFiAPIPublisherWriter', TRUE),
            ('FinanceVendor', TRUE),
            ('EducationPreparationProgram', TRUE)
    ) AS t(ClaimSetName, IsSystemReserved)
) v
LEFT JOIN dmscs.ClaimSet s ON s.ClaimSetName = v.ClaimSetName
WHERE s.ClaimSetName IS NULL
ORDER BY v.OrderIndex;
