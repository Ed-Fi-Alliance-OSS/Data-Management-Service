-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

ALTER TABLE "dmscs"."DataStore"
    ADD COLUMN IF NOT EXISTS "Provider" VARCHAR(50);

COMMENT ON COLUMN "dmscs"."DataStore"."Provider" IS 'Explicit relational provider token for the data store.';
