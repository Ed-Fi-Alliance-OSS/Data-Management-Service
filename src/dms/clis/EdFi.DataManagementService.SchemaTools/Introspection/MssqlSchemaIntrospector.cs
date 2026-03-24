// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.SchemaTools.Introspection;

/// <summary>
/// SQL Server implementation of <see cref="ISchemaIntrospector"/>.
/// Uses <c>sys.*</c> catalog views for schema introspection.
/// </summary>
public class MssqlSchemaIntrospector : SchemaIntrospectorBase
{
    protected override string DialectName => "mssql";

    private static readonly DialectIntrospectionSql _dialect =
        new(
            SchemasSql: """
                SELECT s.name AS schema_name
                FROM sys.schemas s
                WHERE s.name {SCHEMA_FILTER}
                """,
            TablesSql: """
                SELECT
                    s.name AS schema_name,
                    t.name AS table_name
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name {SCHEMA_FILTER}
                """,
            ColumnsSql: """
                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    c.name AS column_name,
                    c.column_id AS ordinal_position,
                    CASE
                        WHEN tp.name IN ('nvarchar', 'nchar', 'varchar', 'char', 'binary', 'varbinary')
                            THEN tp.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(
                                CASE WHEN tp.name IN ('nvarchar', 'nchar') THEN c.max_length / 2 ELSE c.max_length END
                            AS varchar) END + ')'
                        WHEN tp.name IN ('decimal', 'numeric')
                            THEN tp.name + '(' + CAST(c.precision AS varchar) + ',' + CAST(c.scale AS varchar) + ')'
                        ELSE tp.name
                    END AS data_type,
                    c.is_nullable AS is_nullable,
                    dc.definition AS default_expression,
                    c.is_computed AS is_computed
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.types tp ON tp.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
                WHERE s.name {SCHEMA_FILTER}
                """,
            ConstraintsSql: """
                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    kc.name AS constraint_name,
                    CASE kc.type
                        WHEN 'PK' THEN 'PRIMARY KEY'
                        WHEN 'UQ' THEN 'UNIQUE'
                    END AS constraint_type,
                    NULL AS referenced_schema,
                    NULL AS referenced_table
                FROM sys.key_constraints kc
                JOIN sys.tables t ON t.object_id = kc.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name {SCHEMA_FILTER}
                    AND kc.type IN ('PK', 'UQ')

                UNION ALL

                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    fk.name AS constraint_name,
                    'FOREIGN KEY' AS constraint_type,
                    rs.name AS referenced_schema,
                    rt.name AS referenced_table
                FROM sys.foreign_keys fk
                JOIN sys.tables t ON t.object_id = fk.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
                JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
                WHERE s.name {SCHEMA_FILTER}

                UNION ALL

                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    cc.name AS constraint_name,
                    'CHECK' AS constraint_type,
                    NULL AS referenced_schema,
                    NULL AS referenced_table
                FROM sys.check_constraints cc
                JOIN sys.tables t ON t.object_id = cc.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name {SCHEMA_FILTER}
                """,
            ConstraintColumnsSql: """
                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    kc.name AS constraint_name,
                    c.name AS column_name,
                    ic.key_ordinal AS ordinal_position,
                    CAST(0 AS bit) AS is_referenced
                FROM sys.key_constraints kc
                JOIN sys.tables t ON t.object_id = kc.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE s.name {SCHEMA_FILTER}
                    AND kc.type IN ('PK', 'UQ')

                UNION ALL

                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    fk.name AS constraint_name,
                    pc.name AS column_name,
                    fkc.constraint_column_id AS ordinal_position,
                    CAST(0 AS bit) AS is_referenced
                FROM sys.foreign_key_columns fkc
                JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
                JOIN sys.tables t ON t.object_id = fk.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                WHERE s.name {SCHEMA_FILTER}

                UNION ALL

                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    fk.name AS constraint_name,
                    rc.name AS column_name,
                    fkc.constraint_column_id AS ordinal_position,
                    CAST(1 AS bit) AS is_referenced
                FROM sys.foreign_key_columns fkc
                JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
                JOIN sys.tables t ON t.object_id = fk.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                WHERE s.name {SCHEMA_FILTER}

                UNION ALL

                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    cc.name AS constraint_name,
                    c.name AS column_name,
                    1 AS ordinal_position,
                    CAST(0 AS bit) AS is_referenced
                FROM sys.check_constraints cc
                JOIN sys.tables t ON t.object_id = cc.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
                WHERE s.name {SCHEMA_FILTER}
                    AND cc.parent_column_id > 0
                """,
            IndexesSql: """
                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    i.name AS index_name,
                    i.is_unique
                FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name {SCHEMA_FILTER}
                    AND i.type > 0
                    AND i.is_primary_key = 0
                    AND i.is_unique_constraint = 0
                    AND i.name IS NOT NULL
                """,
            IndexColumnsSql: """
                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    i.name AS index_name,
                    c.name AS column_name,
                    ic.key_ordinal AS ordinal_position
                FROM sys.index_columns ic
                JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE s.name {SCHEMA_FILTER}
                    AND i.type > 0
                    AND i.is_primary_key = 0
                    AND i.is_unique_constraint = 0
                    AND i.name IS NOT NULL
                    AND ic.key_ordinal > 0
                """,
            ViewsSql: """
                SELECT
                    s.name AS schema_name,
                    v.name AS view_name,
                    OBJECT_DEFINITION(v.object_id) AS definition
                FROM sys.views v
                JOIN sys.schemas s ON s.schema_id = v.schema_id
                WHERE s.name {SCHEMA_FILTER}
                """,
            TriggersSql: """
                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    tr.name AS trigger_name,
                    STRING_AGG(te.type_desc, ' OR ') WITHIN GROUP (ORDER BY te.type_desc) AS event_manipulation,
                    CASE WHEN tr.is_instead_of_trigger = 1 THEN 'INSTEAD OF' ELSE 'AFTER' END AS action_timing,
                    OBJECT_DEFINITION(tr.object_id) AS definition,
                    NULL AS function_name
                FROM sys.triggers tr
                JOIN sys.tables t ON t.object_id = tr.parent_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.trigger_events te ON te.object_id = tr.object_id
                WHERE s.name {SCHEMA_FILTER}
                    AND tr.parent_class = 1
                GROUP BY s.name, t.name, tr.name, tr.is_instead_of_trigger, tr.object_id
                """,
            SequencesSql: """
                SELECT
                    s.name AS schema_name,
                    seq.name AS sequence_name,
                    tp.name AS data_type,
                    CAST(seq.start_value AS bigint) AS start_value,
                    CAST(seq.increment AS bigint) AS increment_by
                FROM sys.sequences seq
                JOIN sys.schemas s ON s.schema_id = seq.schema_id
                JOIN sys.types tp ON tp.user_type_id = seq.user_type_id
                WHERE s.name {SCHEMA_FILTER}
                """,
            FunctionsSql: """
                SELECT
                    s.name AS schema_name,
                    o.name AS function_name,
                    CASE o.type
                        WHEN 'FN' THEN 'scalar'
                        WHEN 'IF' THEN 'table'
                        WHEN 'TF' THEN 'table'
                        WHEN 'P' THEN 'void'
                        ELSE 'unknown'
                    END AS return_type,
                    OBJECT_DEFINITION(o.object_id) AS definition
                FROM sys.objects o
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE s.name {SCHEMA_FILTER}
                    AND o.type IN ('FN', 'IF', 'TF', 'P')
                """,
            FunctionParametersSql: """
                SELECT
                    s.name AS schema_name,
                    o.name AS function_name,
                    tp.name AS parameter_type,
                    p.parameter_id AS ordinal_position
                FROM sys.parameters p
                JOIN sys.objects o ON o.object_id = p.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                JOIN sys.types tp ON tp.user_type_id = p.user_type_id
                WHERE s.name {SCHEMA_FILTER}
                    AND o.type IN ('FN', 'IF', 'TF', 'P')
                    AND p.parameter_id > 0
                """,
            TableTypesSql: """
                SELECT
                    s.name AS schema_name,
                    tt.name AS table_type_name
                FROM sys.table_types tt
                JOIN sys.schemas s ON s.schema_id = tt.schema_id
                WHERE s.name {SCHEMA_FILTER}
                """,
            TableTypeColumnsSql: """
                SELECT
                    s.name AS schema_name,
                    tt.name AS table_type_name,
                    c.name AS column_name,
                    c.column_id AS ordinal_position,
                    CASE
                        WHEN tp.name IN ('nvarchar', 'nchar', 'varchar', 'char', 'binary', 'varbinary')
                            THEN tp.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(
                                CASE WHEN tp.name IN ('nvarchar', 'nchar') THEN c.max_length / 2 ELSE c.max_length END
                            AS varchar) END + ')'
                        WHEN tp.name IN ('decimal', 'numeric')
                            THEN tp.name + '(' + CAST(c.precision AS varchar) + ',' + CAST(c.scale AS varchar) + ')'
                        ELSE tp.name
                    END AS data_type,
                    c.is_nullable AS is_nullable
                FROM sys.columns c
                JOIN sys.table_types tt ON tt.type_table_object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = tt.schema_id
                JOIN sys.types tp ON tp.user_type_id = c.user_type_id
                WHERE s.name {SCHEMA_FILTER}
                """,
            EffectiveSchemaSql: """
                SELECT
                    [EffectiveSchemaSingletonId] AS effective_schema_singleton_id,
                    [ApiSchemaFormatVersion]     AS api_schema_format_version,
                    [EffectiveSchemaHash]        AS effective_schema_hash,
                    [ResourceKeyCount]           AS resource_key_count,
                    [ResourceKeySeedHash]        AS resource_key_seed_hash
                FROM [dms].[EffectiveSchema]
                """,
            SchemaComponentsSql: """
                SELECT
                    [EffectiveSchemaHash]  AS effective_schema_hash,
                    [ProjectEndpointName]  AS project_endpoint_name,
                    [ProjectName]          AS project_name,
                    [ProjectVersion]       AS project_version,
                    [IsExtensionProject]   AS is_extension_project
                FROM [dms].[SchemaComponent]
                """,
            ResourceKeysSql: """
                SELECT
                    [ResourceKeyId]   AS resource_key_id,
                    [ProjectName]     AS project_name,
                    [ResourceName]    AS resource_name,
                    [ResourceVersion] AS resource_version
                FROM [dms].[ResourceKey]
                """
        );

    protected override DialectIntrospectionSql Dialect => _dialect;

    protected override DbConnection CreateConnection(string connectionString) =>
        new SqlConnection(connectionString);

    protected override (string SqlFragment, Action<DbCommand> AddParameters) BuildSchemaFilter(
        IReadOnlyList<string> allowlist
    )
    {
        var paramNames = new string[allowlist.Count];
        for (int i = 0; i < allowlist.Count; i++)
        {
            paramNames[i] = $"@s{i}";
        }
        var inClause = $"IN ({string.Join(", ", paramNames)})";

        return (
            inClause,
            command =>
            {
                for (int i = 0; i < allowlist.Count; i++)
                {
                    var param = command.CreateParameter();
                    param.ParameterName = $"@s{i}";
                    param.Value = allowlist[i];
                    command.Parameters.Add(param);
                }
            }
        );
    }
}
