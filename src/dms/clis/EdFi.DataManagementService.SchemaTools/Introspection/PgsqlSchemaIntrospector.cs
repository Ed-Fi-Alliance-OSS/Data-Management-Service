// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.SchemaTools.Introspection;

/// <summary>
/// PostgreSQL implementation of <see cref="ISchemaIntrospector"/>.
/// Uses <c>pg_catalog</c> views for schema introspection.
/// </summary>
public class PgsqlSchemaIntrospector : SchemaIntrospectorBase
{
    protected override string DialectName => "pgsql";

    private static readonly DialectIntrospectionSql _dialect =
        new(
            SchemasSql: """
                SELECT nspname AS schema_name
                FROM pg_catalog.pg_namespace
                WHERE nspname {SCHEMA_FILTER}
                """,
            TablesSql: """
                SELECT schemaname AS schema_name, tablename AS table_name
                FROM pg_catalog.pg_tables
                WHERE schemaname {SCHEMA_FILTER}
                """,
            ColumnsSql: """
                SELECT
                    c.table_schema AS schema_name,
                    c.table_name,
                    c.column_name,
                    c.ordinal_position,
                    CASE
                        WHEN c.data_type = 'USER-DEFINED' THEN c.udt_name
                        WHEN c.data_type = 'ARRAY' THEN c.udt_name
                        WHEN c.character_maximum_length IS NOT NULL
                            THEN c.data_type || '(' || c.character_maximum_length || ')'
                        WHEN c.data_type = 'numeric' AND c.numeric_precision IS NOT NULL
                            THEN 'numeric(' || c.numeric_precision || ',' || c.numeric_scale || ')'
                        ELSE c.data_type::text
                    END AS data_type,
                    c.is_nullable = 'YES' AS is_nullable,
                    c.column_default AS default_expression,
                    COALESCE(c.is_generated = 'ALWAYS', false) AS is_computed
                FROM information_schema.columns c
                WHERE c.table_schema {SCHEMA_FILTER}
                """,
            ConstraintsSql: """
                SELECT
                    n.nspname AS schema_name,
                    cl.relname AS table_name,
                    con.conname AS constraint_name,
                    CASE con.contype
                        WHEN 'p' THEN 'PRIMARY KEY'
                        WHEN 'u' THEN 'UNIQUE'
                        WHEN 'f' THEN 'FOREIGN KEY'
                        WHEN 'c' THEN 'CHECK'
                    END AS constraint_type,
                    CASE WHEN con.contype = 'f' THEN rn.nspname ELSE NULL END AS referenced_schema,
                    CASE WHEN con.contype = 'f' THEN rcl.relname ELSE NULL END AS referenced_table
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class cl ON cl.oid = con.conrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = cl.relnamespace
                LEFT JOIN pg_catalog.pg_class rcl ON rcl.oid = con.confrelid
                LEFT JOIN pg_catalog.pg_namespace rn ON rn.oid = rcl.relnamespace
                WHERE n.nspname {SCHEMA_FILTER}
                    AND con.contype IN ('p', 'u', 'f', 'c')
                """,
            ConstraintColumnsSql: """
                SELECT
                    n.nspname AS schema_name,
                    cl.relname AS table_name,
                    con.conname AS constraint_name,
                    att.attname AS column_name,
                    ord.ordinality::int AS ordinal_position,
                    false AS is_referenced
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class cl ON cl.oid = con.conrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = cl.relnamespace
                CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS ord(attnum, ordinality)
                JOIN pg_catalog.pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = ord.attnum
                WHERE n.nspname {SCHEMA_FILTER}
                    AND con.contype IN ('p', 'u', 'f', 'c')

                UNION ALL

                SELECT
                    n.nspname AS schema_name,
                    cl.relname AS table_name,
                    con.conname AS constraint_name,
                    att.attname AS column_name,
                    ord.ordinality::int AS ordinal_position,
                    true AS is_referenced
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class cl ON cl.oid = con.conrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = cl.relnamespace
                CROSS JOIN LATERAL unnest(con.confkey) WITH ORDINALITY AS ord(attnum, ordinality)
                JOIN pg_catalog.pg_attribute att ON att.attrelid = con.confrelid AND att.attnum = ord.attnum
                WHERE n.nspname {SCHEMA_FILTER}
                    AND con.contype = 'f'
                """,
            IndexesSql: """
                SELECT
                    n.nspname AS schema_name,
                    t.relname AS table_name,
                    i.relname AS index_name,
                    ix.indisunique AS is_unique
                FROM pg_catalog.pg_index ix
                JOIN pg_catalog.pg_class i ON i.oid = ix.indexrelid
                JOIN pg_catalog.pg_class t ON t.oid = ix.indrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace
                LEFT JOIN pg_catalog.pg_constraint con
                    ON con.conindid = ix.indexrelid AND con.contype IN ('p', 'u')
                WHERE n.nspname {SCHEMA_FILTER}
                    AND con.oid IS NULL
                """,
            IndexColumnsSql: """
                SELECT
                    n.nspname AS schema_name,
                    t.relname AS table_name,
                    i.relname AS index_name,
                    att.attname AS column_name,
                    ord.ordinality::int AS ordinal_position
                FROM pg_catalog.pg_index ix
                JOIN pg_catalog.pg_class i ON i.oid = ix.indexrelid
                JOIN pg_catalog.pg_class t ON t.oid = ix.indrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace
                LEFT JOIN pg_catalog.pg_constraint con
                    ON con.conindid = ix.indexrelid AND con.contype IN ('p', 'u')
                CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS ord(attnum, ordinality)
                JOIN pg_catalog.pg_attribute att ON att.attrelid = t.oid AND att.attnum = ord.attnum
                WHERE n.nspname {SCHEMA_FILTER}
                    AND con.oid IS NULL
                    AND ord.attnum > 0
                """,
            ViewsSql: """
                SELECT
                    schemaname AS schema_name,
                    viewname AS view_name,
                    definition
                FROM pg_catalog.pg_views
                WHERE schemaname {SCHEMA_FILTER}
                """,
            TriggersSql: """
                SELECT
                    n.nspname AS schema_name,
                    cl.relname AS table_name,
                    t.tgname AS trigger_name,
                    -- Event bits: INSERT=4, DELETE=8, UPDATE=16, TRUNCATE=32
                    CONCAT_WS(' OR ',
                        CASE WHEN (t.tgtype::int &  4) =  4 THEN 'INSERT'   END,
                        CASE WHEN (t.tgtype::int &  8) =  8 THEN 'DELETE'   END,
                        CASE WHEN (t.tgtype::int & 16) = 16 THEN 'UPDATE'   END,
                        CASE WHEN (t.tgtype::int & 32) = 32 THEN 'TRUNCATE' END
                    ) AS event_manipulation,
                    -- Timing bits: INSTEAD OF=64, BEFORE=2, else AFTER
                    CASE
                        WHEN (t.tgtype::int & 64) = 64 THEN 'INSTEAD OF'
                        WHEN (t.tgtype::int & 2) = 2 THEN 'BEFORE'
                        ELSE 'AFTER'
                    END AS action_timing,
                    pg_get_triggerdef(t.oid) AS definition,
                    p.proname AS function_name
                FROM pg_catalog.pg_trigger t
                JOIN pg_catalog.pg_class cl ON cl.oid = t.tgrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = cl.relnamespace
                JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
                WHERE n.nspname {SCHEMA_FILTER}
                    AND NOT t.tgisinternal
                """,
            SequencesSql: """
                SELECT
                    schemaname AS schema_name,
                    sequencename AS sequence_name,
                    data_type::text,
                    start_value,
                    increment_by
                FROM pg_catalog.pg_sequences
                WHERE schemaname {SCHEMA_FILTER}
                """,
            FunctionsSql: """
                SELECT
                    n.nspname AS schema_name,
                    p.proname AS function_name,
                    p.oid::text AS specific_name,
                    pg_get_function_result(p.oid) AS return_type,
                    pg_get_functiondef(p.oid) AS definition
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname {SCHEMA_FILTER}
                    AND p.prokind IN ('f', 'p')
                """,
            FunctionParametersSql: """
                SELECT
                    n.nspname AS schema_name,
                    p.proname AS function_name,
                    p.oid::text AS specific_name,
                    format_type(unnest_type, NULL) AS parameter_type,
                    ord::int AS ordinal_position
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                CROSS JOIN LATERAL unnest(p.proargtypes) WITH ORDINALITY AS u(unnest_type, ord)
                WHERE n.nspname {SCHEMA_FILTER}
                    AND p.prokind IN ('f', 'p')
                """,
            TableTypesSql: """
                SELECT NULL WHERE false
                """,
            TableTypeColumnsSql: """
                SELECT NULL WHERE false
                """,
            EffectiveSchemaSql: """
                SELECT
                    "EffectiveSchemaSingletonId" AS effective_schema_singleton_id,
                    "ApiSchemaFormatVersion"     AS api_schema_format_version,
                    "EffectiveSchemaHash"        AS effective_schema_hash,
                    "ResourceKeyCount"           AS resource_key_count,
                    "ResourceKeySeedHash"        AS resource_key_seed_hash
                FROM "dms"."EffectiveSchema"
                """,
            SchemaComponentsSql: """
                SELECT
                    "EffectiveSchemaHash"  AS effective_schema_hash,
                    "ProjectEndpointName"  AS project_endpoint_name,
                    "ProjectName"          AS project_name,
                    "ProjectVersion"       AS project_version,
                    "IsExtensionProject"   AS is_extension_project
                FROM "dms"."SchemaComponent"
                """,
            ResourceKeysSql: """
                SELECT
                    "ResourceKeyId"   AS resource_key_id,
                    "ProjectName"     AS project_name,
                    "ResourceName"    AS resource_name,
                    "ResourceVersion" AS resource_version
                FROM "dms"."ResourceKey"
                """
        );

    protected override DialectIntrospectionSql Dialect => _dialect;

    protected override DbConnection CreateConnection(string connectionString) =>
        new NpgsqlConnection(connectionString);

    protected override (string SqlFragment, Action<DbCommand> AddParameters) BuildSchemaFilter(
        IReadOnlyList<string> allowlist
    )
    {
        return (
            "= ANY(@schemas)",
            command =>
            {
#pragma warning disable S3265 // NpgsqlDbType.Array is meant to be OR'd with element types despite no [Flags]
                var param = new NpgsqlParameter("@schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
#pragma warning restore S3265
                {
                    Value = allowlist.ToArray(),
                };
                command.Parameters.Add(param);
            }
        );
    }
}
