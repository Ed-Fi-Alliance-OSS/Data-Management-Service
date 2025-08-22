-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
CREATE TABLE IF NOT EXISTS dmscs.OpenIddictRole (
    Id uuid NOT NULL PRIMARY KEY,
    Name varchar(100) NOT NULL UNIQUE
);
