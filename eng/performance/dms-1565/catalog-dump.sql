-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
-- DMS-1565 focused catalog dump (sort the output with LC_ALL=C sort): one line per object attribute, excluding sizes and identity/sequence positions.
SET NOCOUNT ON;
    SELECT CONCAT('SCHEMA|', s.name) AS line FROM sys.schemas s WHERE s.schema_id BETWEEN 5 AND 16383
;
    SELECT CONCAT('COL|', s.name, '.', t.name, '|', c.column_id, '|', c.name, '|', ty.name, '|', c.max_length, '|', c.precision, '|', c.scale,
        '|null=', c.is_nullable, '|coll=', ISNULL(c.collation_name, ''), '|ident=', c.is_identity,
        '|computed=', ISNULL(cc.definition, ''), '|persisted=', ISNULL(CAST(cc.is_persisted AS varchar(1)), ''),
        '|default=', ISNULL(dc.definition, ''))
    FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.columns c ON c.object_id = t.object_id
    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
    LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
;
    SELECT CONCAT('IDX|', s.name, '.', t.name, '|', i.name, '|', i.type_desc COLLATE DATABASE_DEFAULT, '|unique=', i.is_unique, '|pk=', i.is_primary_key,
        '|uc=', i.is_unique_constraint, '|disabled=', i.is_disabled, '|filter=', ISNULL(i.filter_definition, ''), '|cols=',
        (STUFF((SELECT ',' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END + CASE WHEN ic.is_included_column = 1 THEN ' INCL' ELSE '' END
               FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
               ORDER BY ic.is_included_column, ic.key_ordinal, c.name FOR XML PATH('')), 1, 1, '') COLLATE DATABASE_DEFAULT))
    FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE i.type > 0
;
    SELECT CONCAT('FK|', s.name, '.', t.name, '|', fk.name, '|->', rs.name, '.', rt.name, '|del=', fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT,
        '|upd=', fk.update_referential_action_desc COLLATE DATABASE_DEFAULT, '|disabled=', fk.is_disabled, '|untrusted=', fk.is_not_trusted, '|cols=',
        (STUFF((SELECT ',' + pc.name + '>' + rc.name FROM sys.foreign_key_columns fkc
               JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
               JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
               WHERE fkc.constraint_object_id = fk.object_id ORDER BY fkc.constraint_column_id FOR XML PATH('')), 1, 1, '') COLLATE DATABASE_DEFAULT))
    FROM sys.foreign_keys fk JOIN sys.tables t ON t.object_id = fk.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
;
    SELECT CONCAT('CK|', s.name, '.', t.name, '|', ck.name, '|disabled=', ck.is_disabled, '|untrusted=', ck.is_not_trusted, '|', ck.definition)
    FROM sys.check_constraints ck JOIN sys.tables t ON t.object_id = ck.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
;
    SELECT CONCAT('TRG|', s.name, '.', t.name, '|', tr.name, '|disabled=', tr.is_disabled, '|insteadof=', tr.is_instead_of_trigger,
        '|sha=', CONVERT(varchar(64), HASHBYTES('SHA2_256', TRIM(NCHAR(10) + N' ' FROM REPLACE(m.definition, NCHAR(13), N''))), 2))
    FROM sys.triggers tr JOIN sys.tables t ON t.object_id = tr.parent_id JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.sql_modules m ON m.object_id = tr.object_id
;
    SELECT CONCAT('MOD|', s.name, '.', o.name, '|', o.type_desc COLLATE DATABASE_DEFAULT, '|sha=', CONVERT(varchar(64), HASHBYTES('SHA2_256', TRIM(NCHAR(10) + N' ' FROM REPLACE(m.definition, NCHAR(13), N''))), 2))
    FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
    WHERE o.type IN ('V', 'P', 'FN', 'IF', 'TF')
;
    SELECT CONCAT('SEQ|', s.name, '.', sq.name, '|', TYPE_NAME(sq.user_type_id), '|start=', CAST(sq.start_value AS varchar(40)), '|inc=', CAST(sq.increment AS varchar(40)))
    FROM sys.sequences sq JOIN sys.schemas s ON s.schema_id = sq.schema_id
;
