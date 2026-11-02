-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Test-only catalog mutation; the v3 EffectiveSchema fingerprint is deliberately unchanged.
-- Rebuilds UriLowered as a persisted column.
DROP INDEX [UX_Descriptor_UriLowered_ResourceKeyId] ON [dms].[Descriptor]; ALTER TABLE [dms].[Descriptor] DROP COLUMN [UriLowered]; ALTER TABLE [dms].[Descriptor] ADD [UriLowered] AS LOWER(([Namespace]+N'#'+[CodeValue]) COLLATE SQL_Latin1_General_CP1_CI_AS) PERSISTED; CREATE UNIQUE INDEX [UX_Descriptor_UriLowered_ResourceKeyId] ON [dms].[Descriptor] ([UriLowered], [ResourceKeyId]);
