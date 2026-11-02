-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Test-only catalog mutation; the v3 EffectiveSchema fingerprint is deliberately unchanged.
-- Rebuilds the lowered whole-URI index without the pg_c_utf8 pin, so folding follows the database default.
DROP INDEX "dms"."UX_Descriptor_UriLowered_ResourceKeyId";
CREATE UNIQUE INDEX "UX_Descriptor_UriLowered_ResourceKeyId" ON "dms"."Descriptor" (lower("Namespace" || '#' || "CodeValue"), "ResourceKeyId");
