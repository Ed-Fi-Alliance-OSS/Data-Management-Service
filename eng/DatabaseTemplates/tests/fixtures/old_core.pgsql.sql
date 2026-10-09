-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Test-only catalog mutation; the v3 EffectiveSchema fingerprint is deliberately unchanged.
DROP TABLE "dms"."Descriptor" CASCADE; CREATE TABLE "dms"."Descriptor" ("DocumentId" bigint NOT NULL PRIMARY KEY REFERENCES dms."Document"("DocumentId") ON DELETE CASCADE, "ResourceKeyId" smallint NOT NULL, "Namespace" varchar(255) NOT NULL, "CodeValue" varchar(50) NOT NULL, "Uri" varchar(306) NOT NULL, "Discriminator" varchar(128) NOT NULL, UNIQUE ("Uri", "Discriminator"));
