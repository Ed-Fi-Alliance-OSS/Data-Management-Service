-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved)
VALUES
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
    ('EducationPreparationProgram', TRUE);
