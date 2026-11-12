-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Rendered owners come only from the reviewed implementation manifest.
DECLARE @timestampOwners nvarchar(max) = N'__EXPECTED_OWNERS__';
DECLARE @timestampRelation int = OBJECT_ID(N'[dms].[Document]', 'U');
IF @timestampRelation IS NULL
    THROW 51000, 'Timestamp baseline: missing table dms.Document', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @timestampRelation AND name = N'ContentLastModifiedAt')
    THROW 51000, 'Timestamp baseline: forbidden column dms.Document.ContentLastModifiedAt', 1;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @timestampRelation AND name = N'ContentVersion' COLLATE Latin1_General_100_BIN2
    AND system_type_id = 127 AND is_nullable = 0 AND is_computed = 0)
    THROW 51000, 'Timestamp baseline: missing/invalid stored column dms.Document.ContentVersion', 1;

DECLARE @timestampSchema sysname, @timestampTable sysname, @timestampMessage nvarchar(2048);
DECLARE timestamp_checks CURSOR LOCAL FAST_FORWARD FOR
    SELECT [schema], [table] FROM OPENJSON(@timestampOwners) WITH ([schema] sysname, [table] sysname);
OPEN timestamp_checks;
FETCH NEXT FROM timestamp_checks INTO @timestampSchema, @timestampTable;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @timestampRelation = OBJECT_ID(QUOTENAME(@timestampSchema) + N'.' + QUOTENAME(@timestampTable), 'U');
    IF @timestampRelation IS NULL
    BEGIN
        SET @timestampMessage = N'Timestamp baseline: missing owner table ' + @timestampSchema + N'.' + @timestampTable;
        THROW 51000, @timestampMessage, 1;
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @timestampRelation AND name = N'ContentLastModifiedAt' COLLATE Latin1_General_100_BIN2
        AND system_type_id = 42 AND scale = 7 AND is_nullable = 0 AND is_computed = 0)
    BEGIN
        SET @timestampMessage = N'Timestamp baseline: missing/invalid authoritative column ' + @timestampSchema + N'.' + @timestampTable + N'.ContentLastModifiedAt';
        THROW 51000, @timestampMessage, 1;
    END;
    FETCH NEXT FROM timestamp_checks INTO @timestampSchema, @timestampTable;
END;
CLOSE timestamp_checks;
DEALLOCATE timestamp_checks;
