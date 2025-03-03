-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dmscs.AuthorizationStrategy (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    AuthorizationStrategyName VARCHAR(255) NOT NULL,
    DisplayName VARCHAR(255) NOT NULL
);

COMMENT ON COLUMN dmscs.AuthorizationStrategy.Id IS 'Authorization Strategy Identifier.';
COMMENT ON COLUMN dmscs.AuthorizationStrategy.AuthorizationStrategyName IS 'Authorization Strategy Name';
COMMENT ON COLUMN dmscs.AuthorizationStrategy.DisplayName IS 'Display Name';
