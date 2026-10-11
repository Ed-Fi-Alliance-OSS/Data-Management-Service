-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Test-only catalog mutation; the v3 EffectiveSchema fingerprint is deliberately unchanged.
ALTER TABLE [dms].[Descriptor] DROP CONSTRAINT [FK_Descriptor_Document];
ALTER TABLE [dms].[Descriptor] ADD CONSTRAINT [FK_Descriptor_Document]
    FOREIGN KEY ([DocumentId]) REFERENCES [dms].[Document] ([DocumentId]) ON DELETE CASCADE;
