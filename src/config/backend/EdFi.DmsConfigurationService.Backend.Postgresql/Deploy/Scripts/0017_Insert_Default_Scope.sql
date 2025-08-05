-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

INSERT INTO dmscs.openiddict_scope (id, name, description)
SELECT gen_random_uuid(), 'EdFi_Full_Access', 'Default Ed-Fi full access scope.'
WHERE NOT EXISTS (
    SELECT 1 FROM dmscs.openiddict_scope WHERE name = 'EdFi_Full_Access'
);
