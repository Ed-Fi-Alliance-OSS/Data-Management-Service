-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Test-only catalog mutation; the v3 EffectiveSchema fingerprint is deliberately unchanged.
DROP INDEX "tracked_changes_edfi"."IX_Descriptor_ResourceKeyId_ChangeVersion";
