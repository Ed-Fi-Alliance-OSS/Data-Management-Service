-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
CREATE TABLE IF NOT EXISTS dmscs.openiddict_client_rol (
    client_id UUID NOT NULL REFERENCES dmscs.openiddict_application(id) ON DELETE CASCADE,
    rol_id UUID NOT NULL REFERENCES dmscs.openiddict_rol(id) ON DELETE CASCADE,
    PRIMARY KEY (client_id, rol_id)
);
