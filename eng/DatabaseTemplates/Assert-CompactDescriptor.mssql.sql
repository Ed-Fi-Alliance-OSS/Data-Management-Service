-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Render with Get-CompactDescriptorAssertionSql. No SET initializer is run here:
-- verify effective options on the actual inspection/runtime connection.
DECLARE @expected nvarchar(max) = N'__EXPECTED_INVENTORY__';
IF NOT EXISTS (SELECT 1 FROM [dms].[EffectiveSchema] WHERE [EffectiveSchemaHash] = JSON_VALUE(@expected, '$.effective_schema_hash'))
    THROW 51000, 'Compact descriptor baseline: EffectiveSchemaHash/schema set mismatch', 1;
IF EXISTS (SELECT 1 FROM OPENJSON(@expected, '$.projects') p WHERE SCHEMA_ID(JSON_VALUE(p.value, '$.physical_schema')) IS NULL)
    THROW 51000, 'Compact descriptor baseline: missing project schema', 1;
IF (SELECT COUNT(*) FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = JSON_VALUE(@expected, '$.effective_schema_hash')) <> (SELECT COUNT(*) FROM OPENJSON(@expected, '$.projects'))
    OR EXISTS (SELECT 1 FROM OPENJSON(@expected, '$.projects') p WHERE NOT EXISTS (
        SELECT 1 FROM [dms].[SchemaComponent] c WHERE c.[EffectiveSchemaHash] = JSON_VALUE(@expected, '$.effective_schema_hash')
            AND c.[ProjectEndpointName] COLLATE Latin1_General_100_BIN2 = JSON_VALUE(p.value, '$.project_endpoint_name') COLLATE Latin1_General_100_BIN2
            AND c.[ProjectName] COLLATE Latin1_General_100_BIN2 = JSON_VALUE(p.value, '$.project_name') COLLATE Latin1_General_100_BIN2
            AND c.[ProjectVersion] COLLATE Latin1_General_100_BIN2 = JSON_VALUE(p.value, '$.project_version') COLLATE Latin1_General_100_BIN2
            AND c.[IsExtensionProject] = CASE JSON_VALUE(p.value, '$.is_extension') WHEN 'true' THEN 1 ELSE 0 END))
    THROW 51000, 'Compact descriptor baseline: manifest project/schema set mismatch', 1;
IF (SELECT COUNT(*) FROM [dms].[ResourceKey]) <> (SELECT COUNT(*) FROM OPENJSON(@expected, '$.resources'))
    OR EXISTS (SELECT 1 FROM OPENJSON(@expected, '$.resources') r WHERE NOT EXISTS (
        SELECT 1 FROM [dms].[ResourceKey] k WHERE k.[ProjectName] COLLATE Latin1_General_100_BIN2 = JSON_VALUE(r.value, '$.project_name') COLLATE Latin1_General_100_BIN2
            AND k.[ResourceName] COLLATE Latin1_General_100_BIN2 = JSON_VALUE(r.value, '$.resource_name') COLLATE Latin1_General_100_BIN2))
    THROW 51000, 'Compact descriptor baseline: manifest resource/schema set mismatch', 1;
IF SESSIONPROPERTY('ANSI_NULLS') <> 1 OR SESSIONPROPERTY('ANSI_PADDING') <> 1
    OR SESSIONPROPERTY('ANSI_WARNINGS') <> 1 OR SESSIONPROPERTY('CONCAT_NULL_YIELDS_NULL') <> 1
    OR SESSIONPROPERTY('QUOTED_IDENTIFIER') <> 1 OR SESSIONPROPERTY('NUMERIC_ROUNDABORT') <> 0
    OR (SESSIONPROPERTY('ARITHABORT') <> 1 AND (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) < 90)
    THROW 51000, 'Compact descriptor baseline: incompatible effective computed-index SET options', 1;

DECLARE @schema sysname, @table sysname, @name sysname, @item nvarchar(max), @definition nvarchar(max);
DECLARE @relation int, @target int, @constraint int, @index int, @actual nvarchar(max), @wanted nvarchar(max), @message nvarchar(2048);
DECLARE column_checks CURSOR LOCAL FAST_FORWARD FOR
    SELECT value FROM OPENJSON(@expected, '$.columns') UNION ALL SELECT value FROM OPENJSON(@expected, '$.required_columns');
OPEN column_checks;
FETCH NEXT FROM column_checks INTO @item;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @schema = JSON_VALUE(@item, '$.schema'), @table = JSON_VALUE(@item, '$.table'), @name = JSON_VALUE(@item, '$.name');
    SET @relation = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@table), 'U');
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @relation AND name = @name COLLATE Latin1_General_100_BIN2
        AND system_type_id = CASE JSON_VALUE(@item, '$.scalar_type') WHEN 'Int32' THEN 56 WHEN 'Int64' THEN 127 WHEN 'Int16' THEN 52 END
        AND is_nullable = CASE JSON_VALUE(@item, '$.is_nullable') WHEN 'true' THEN 1 ELSE 0 END AND is_computed = 0)
    BEGIN
        SET @message = N'Compact descriptor baseline: missing/noncompact stored column ' + @schema + N'.' + @table + N'.' + @name;
        THROW 51000, @message, 1;
    END;
    FETCH NEXT FROM column_checks INTO @item;
END;
CLOSE column_checks;
DEALLOCATE column_checks;

DECLARE constraint_checks CURSOR LOCAL FAST_FORWARD FOR SELECT value FROM OPENJSON(@expected, '$.constraints');
OPEN constraint_checks;
FETCH NEXT FROM constraint_checks INTO @item;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @schema = JSON_VALUE(@item, '$.schema'), @table = JSON_VALUE(@item, '$.table'), @definition = JSON_QUERY(@item, '$.definition');
    SELECT @name = JSON_VALUE(@definition, '$.name'), @relation = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@table), 'U');
    SET @message = N'Compact descriptor baseline: constraint columns/target/actions mismatch ' + @schema + N'.' + @table + N'.' + @name;
    SELECT @wanted = STRING_AGG(CONVERT(nvarchar(max), value), N',') WITHIN GROUP (ORDER BY CONVERT(int, [key])) FROM OPENJSON(@definition, '$.columns');
    IF JSON_VALUE(@definition, '$.kind') = 'ForeignKey'
    BEGIN
        SET @constraint = NULL;
        SET @target = OBJECT_ID(QUOTENAME(JSON_VALUE(@definition, '$.target_table.schema')) + N'.' + QUOTENAME(JSON_VALUE(@definition, '$.target_table.name')), 'U');
        SELECT @constraint = object_id FROM sys.foreign_keys WHERE parent_object_id = @relation AND name = @name COLLATE Latin1_General_100_BIN2
            AND referenced_object_id = @target AND is_disabled = 0 AND is_not_trusted = 0
            AND delete_referential_action_desc = CASE JSON_VALUE(@definition, '$.on_delete') WHEN 'Cascade' THEN 'CASCADE' WHEN 'NoAction' THEN 'NO_ACTION' END
            AND update_referential_action_desc = CASE JSON_VALUE(@definition, '$.on_update') WHEN 'Cascade' THEN 'CASCADE' WHEN 'NoAction' THEN 'NO_ACTION' END;
        SELECT @actual = STRING_AGG(CONVERT(nvarchar(max), c.name), N',') WITHIN GROUP (ORDER BY k.constraint_column_id)
        FROM sys.foreign_key_columns k JOIN sys.columns c ON c.object_id = k.parent_object_id AND c.column_id = k.parent_column_id
        WHERE k.constraint_object_id = @constraint;
        IF @constraint IS NULL OR @actual IS NULL OR @actual COLLATE Latin1_General_100_BIN2 <> @wanted COLLATE Latin1_General_100_BIN2
            THROW 51000, @message, 1;
        SELECT @wanted = STRING_AGG(CONVERT(nvarchar(max), value), N',') WITHIN GROUP (ORDER BY CONVERT(int, [key])) FROM OPENJSON(@definition, '$.target_columns');
        SELECT @actual = STRING_AGG(CONVERT(nvarchar(max), c.name), N',') WITHIN GROUP (ORDER BY k.constraint_column_id)
        FROM sys.foreign_key_columns k JOIN sys.columns c ON c.object_id = k.referenced_object_id AND c.column_id = k.referenced_column_id
        WHERE k.constraint_object_id = @constraint;
        IF @actual IS NULL OR @actual COLLATE Latin1_General_100_BIN2 <> @wanted COLLATE Latin1_General_100_BIN2
            THROW 51000, @message, 1;
    END
    ELSE
    BEGIN
        SET @index = NULL;
        SELECT @index = k.unique_index_id FROM sys.key_constraints k JOIN sys.indexes i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
        WHERE k.parent_object_id = @relation AND k.name = @name COLLATE Latin1_General_100_BIN2 AND k.type = 'UQ' AND i.is_unique = 1 AND i.is_disabled = 0 AND i.has_filter = 0;
        SELECT @actual = STRING_AGG(CONVERT(nvarchar(max), c.name), N',') WITHIN GROUP (ORDER BY k.key_ordinal)
        FROM sys.index_columns k JOIN sys.columns c ON c.object_id = k.object_id AND c.column_id = k.column_id
        WHERE k.object_id = @relation AND k.index_id = @index AND k.key_ordinal > 0;
        IF @index IS NULL OR @actual IS NULL OR @actual COLLATE Latin1_General_100_BIN2 <> @wanted COLLATE Latin1_General_100_BIN2
            THROW 51000, @message, 1;
    END;
    FETCH NEXT FROM constraint_checks INTO @item;
END;
CLOSE constraint_checks;
DEALLOCATE constraint_checks;

DECLARE index_checks CURSOR LOCAL FAST_FORWARD FOR SELECT value FROM OPENJSON(@expected, '$.indexes');
OPEN index_checks;
FETCH NEXT FROM index_checks INTO @item;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @schema = JSON_VALUE(@item, '$.table.schema'), @table = JSON_VALUE(@item, '$.table.name'), @name = JSON_VALUE(@item, '$.name');
    SET @relation = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@table), 'U');
    SET @index = NULL;
    SELECT @index = index_id FROM sys.indexes WHERE object_id = @relation AND name = @name COLLATE Latin1_General_100_BIN2
        AND is_unique = CASE JSON_VALUE(@item, '$.is_unique') WHEN 'true' THEN 1 ELSE 0 END
        AND is_primary_key = CASE JSON_VALUE(@item, '$.kind') WHEN 'PrimaryKey' THEN 1 ELSE 0 END
        AND is_disabled = 0 AND has_filter = 0;
    SELECT @wanted = STRING_AGG(CONVERT(nvarchar(max), value), N',') WITHIN GROUP (ORDER BY CONVERT(int, [key])) FROM OPENJSON(@item, '$.key_columns');
    SELECT @actual = STRING_AGG(CONVERT(nvarchar(max), c.name), N',') WITHIN GROUP (ORDER BY k.key_ordinal)
    FROM sys.index_columns k JOIN sys.columns c ON c.object_id = k.object_id AND c.column_id = k.column_id
    WHERE k.object_id = @relation AND k.index_id = @index AND k.key_ordinal > 0;
    IF @index IS NULL OR @actual IS NULL OR @actual COLLATE Latin1_General_100_BIN2 <> @wanted COLLATE Latin1_General_100_BIN2
    BEGIN
        SET @message = N'Compact descriptor baseline: missing/incorrect index ' + @schema + N'.' + @table + N'.' + @name;
        THROW 51000, @message, 1;
    END;
    FETCH NEXT FROM index_checks INTO @item;
END;
CLOSE index_checks;
DEALLOCATE index_checks;

SET @relation = OBJECT_ID(N'dms.Descriptor', 'U');
IF NOT EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = @relation AND name = N'DescriptorId'
    AND system_type_id = 56 AND CONVERT(bigint, seed_value) = 1 AND CONVERT(bigint, increment_value) = 1)
    THROW 51000, 'Compact descriptor baseline: DescriptorId needs independent int IDENTITY(1,1)', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id IN (@relation, OBJECT_ID(N'tracked_changes_edfi.Descriptor'))
    AND name IN (N'Discriminator', N'UriLowered', N'NamespaceLowered', N'CodeValueLowered'))
    OR EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @relation AND name IN (N'UX_Descriptor_Uri_Discriminator', N'IX_Descriptor_Discriminator_ContentVersion'))
    THROW 51000, 'Compact descriptor baseline: obsolete descriptor storage/index remains', 1;
IF NOT EXISTS (SELECT 1 FROM sys.computed_columns WHERE object_id = @relation AND name = N'Uri'
    AND is_persisted = 0 AND system_type_id = 231 AND max_length = 612
    AND collation_name = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))
    AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(definition, N'(', N''), N')', N''), N'[', N''), N']', N''), N' ', N'') COLLATE Latin1_General_100_BIN2 = N'Namespace+N''#''+CodeValue')
    THROW 51000, 'Compact descriptor baseline: Uri must be non-persisted, unlowered, nvarchar(306), with former collation', 1;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = @relation AND name IN (N'Namespace', N'CodeValue')
    AND collation_name = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))) <> 2
    THROW 51000, 'Compact descriptor baseline: URI component collation changed', 1;
SET @index = NULL;
SELECT @index = index_id FROM sys.indexes WHERE object_id = @relation AND name = N'UX_Descriptor_ResourceKeyId_Uri' AND is_unique = 1 AND is_disabled = 0 AND has_filter = 0;
SELECT @actual = STRING_AGG(CONVERT(nvarchar(max), c.name), N',') WITHIN GROUP (ORDER BY k.key_ordinal)
FROM sys.index_columns k JOIN sys.columns c ON c.object_id = k.object_id AND c.column_id = k.column_id
WHERE k.object_id = @relation AND k.index_id = @index AND k.key_ordinal > 0;
IF @index IS NULL OR @actual IS NULL OR @actual COLLATE Latin1_General_100_BIN2 <> N'ResourceKeyId,Uri'
    THROW 51000, 'Compact descriptor baseline: required whole-URI unique index missing', 1;
IF EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns k ON k.object_id = i.object_id AND k.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = k.object_id AND c.column_id = k.column_id
    WHERE i.object_id = @relation AND i.is_unique = 1 AND k.key_ordinal > 0 AND c.name IN (N'Namespace', N'CodeValue'))
    THROW 51000, 'Compact descriptor baseline: component uniqueness is forbidden', 1;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'tracked_changes_edfi.Descriptor')
    AND referenced_object_id IN (@relation, OBJECT_ID(N'dms.Document')))
    THROW 51000, 'Compact descriptor baseline: history must survive live descriptor/document deletion', 1;
