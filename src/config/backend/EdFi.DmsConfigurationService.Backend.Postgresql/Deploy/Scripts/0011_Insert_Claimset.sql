-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved, ResourceClaims)
VALUES
    ('SISVendor', TRUE, '[]'::jsonb),
    ('Ed-FiSandbox', TRUE, '[]'::jsonb),
    ('RosterVendor', TRUE, '[]'::jsonb),
    ('AssessmentVendor', TRUE, '[]'::jsonb),
    ('AssessmentRead', TRUE, '[]'::jsonb),
    ('BootstrapDescriptorsandEdOrgs', TRUE, '[]'::jsonb),
    ('DistrictHostedSISVendor', TRUE, '[]'::jsonb),
    ('Ed-FiODSAdminApp', TRUE, '[]'::jsonb),
    ('ABConnect', TRUE, '[]'::jsonb),
    ('Ed-FiAPIPublisher-Reader', TRUE, '[]'::jsonb),
    ('Ed-FiAPIPublisher-Writer', TRUE, '[]'::jsonb),
    ('FinanceVendor', TRUE, '[]'::jsonb),
    ('EducationPreparationProgram', TRUE, '[]'::jsonb);
