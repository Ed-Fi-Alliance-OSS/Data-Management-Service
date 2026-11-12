-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Test-only catalog mutation; the v3 EffectiveSchema fingerprint is deliberately unchanged.
-- Moves the descriptor identity components off the database-default collation; the expression index is rebuilt over them.
ALTER TABLE "dms"."Descriptor" ALTER COLUMN "Namespace" TYPE varchar(255) COLLATE "C", ALTER COLUMN "CodeValue" TYPE varchar(50) COLLATE "C";
