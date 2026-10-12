-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Test-only catalog mutation; the v3 EffectiveSchema fingerprint is deliberately unchanged.
-- Moves the descriptor identity components off the DMS identity collation, as on a database provisioned before DMS-1443 under a different default.
DROP INDEX [UX_Descriptor_ResourceKeyId_Uri] ON [dms].[Descriptor]; ALTER TABLE [dms].[Descriptor] DROP COLUMN [Uri]; ALTER TABLE [dms].[Descriptor] ALTER COLUMN [Namespace] nvarchar(255) COLLATE Latin1_General_100_CI_AS NOT NULL; ALTER TABLE [dms].[Descriptor] ALTER COLUMN [CodeValue] nvarchar(50) COLLATE Latin1_General_100_CI_AS NOT NULL; ALTER TABLE [dms].[Descriptor] ADD [Uri] AS ([Namespace]+N'#'+[CodeValue]); CREATE UNIQUE INDEX [UX_Descriptor_ResourceKeyId_Uri] ON [dms].[Descriptor] ([ResourceKeyId], [Uri]);
