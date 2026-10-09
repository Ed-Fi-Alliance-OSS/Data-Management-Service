-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Test-only catalog mutation; the v3 EffectiveSchema fingerprint is deliberately unchanged.
DECLARE @drop nvarchar(max); SELECT @drop = STRING_AGG(CONVERT(nvarchar(max), N'ALTER TABLE '+QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+N'.'+QUOTENAME(OBJECT_NAME(parent_object_id))+N' DROP CONSTRAINT '+QUOTENAME(name)+N';'), N' ') FROM sys.foreign_keys WHERE referenced_object_id=OBJECT_ID(N'dms.Descriptor'); EXEC sys.sp_executesql @drop; DROP TABLE [dms].[Descriptor]; CREATE TABLE [dms].[Descriptor] ([DocumentId] bigint NOT NULL PRIMARY KEY REFERENCES [dms].[Document]([DocumentId]) ON DELETE CASCADE, [ResourceKeyId] smallint NOT NULL, [Namespace] nvarchar(255) NOT NULL, [CodeValue] nvarchar(50) NOT NULL, [Uri] nvarchar(306) NOT NULL, [Discriminator] nvarchar(128) NOT NULL, UNIQUE ([Uri], [Discriminator]));
