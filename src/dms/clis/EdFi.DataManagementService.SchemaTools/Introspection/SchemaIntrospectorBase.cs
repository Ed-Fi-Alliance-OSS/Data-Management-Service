// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.SchemaTools.Introspection;

/// <summary>
/// Abstract base class implementing <see cref="ISchemaIntrospector"/>. Executes
/// dialect-agnostic reader logic using ADO.NET's <see cref="DbConnection"/> and
/// <see cref="DbDataReader"/> abstractions. Concrete subclasses provide
/// engine-specific SQL strings and connection creation.
/// </summary>
public abstract class SchemaIntrospectorBase : ISchemaIntrospector
{
    protected abstract string DialectName { get; }
    protected abstract DialectIntrospectionSql Dialect { get; }
    protected abstract DbConnection CreateConnection(string connectionString);
    protected abstract (string SqlFragment, Action<DbCommand> AddParameters) BuildSchemaFilter(
        IReadOnlyList<string> allowlist
    );

    public ProvisionedSchemaManifest Introspect(
        string connectionString,
        IReadOnlyList<string> schemaAllowlist
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(schemaAllowlist);

        if (schemaAllowlist.Count == 0)
        {
            throw new ArgumentException("Schema allowlist must contain at least one schema.", nameof(schemaAllowlist));
        }

        var (filterFragment, addFilterParams) = BuildSchemaFilter(schemaAllowlist);

        using var connection = CreateConnection(connectionString);
        connection.Open();

        var schemas = ReadSchemas(connection, filterFragment, addFilterParams);
        var tables = ReadTables(connection, filterFragment, addFilterParams);
        var columns = ReadColumns(connection, filterFragment, addFilterParams);

        var constraints = ReadConstraints(connection, filterFragment, addFilterParams);
        var constraintColumns = ReadConstraintColumns(connection, filterFragment, addFilterParams);
        var assembledConstraints = AssembleConstraints(constraints, constraintColumns);

        var indexes = ReadIndexes(connection, filterFragment, addFilterParams);
        var indexColumns = ReadIndexColumns(connection, filterFragment, addFilterParams);
        var assembledIndexes = AssembleIndexes(indexes, indexColumns);

        var views = ReadViews(connection, filterFragment, addFilterParams);
        var triggers = ReadTriggers(connection, filterFragment, addFilterParams);
        var sequences = ReadSequences(connection, filterFragment, addFilterParams);

        var tableTypes = ReadTableTypes(connection, filterFragment, addFilterParams);
        var tableTypeColumns = ReadTableTypeColumns(connection, filterFragment, addFilterParams);
        var assembledTableTypes = AssembleTableTypes(tableTypes, tableTypeColumns);

        var functions = ReadFunctions(connection, filterFragment, addFilterParams);
        var functionParams = ReadFunctionParameters(connection, filterFragment, addFilterParams);
        var assembledFunctions = AssembleFunctions(functions, functionParams);

        var effectiveSchema = ReadEffectiveSchema(connection);
        var schemaComponents = ReadSchemaComponents(connection);
        var resourceKeys = ReadResourceKeys(connection);
        var seedData = new SeedData(effectiveSchema, schemaComponents, resourceKeys);

        return new ProvisionedSchemaManifest(
            ManifestVersion: "1",
            Dialect: DialectName,
            Schemas: schemas,
            Tables: tables,
            Columns: columns,
            Constraints: assembledConstraints,
            Indexes: assembledIndexes,
            Views: views,
            Triggers: triggers,
            Sequences: sequences,
            TableTypes: assembledTableTypes,
            Functions: assembledFunctions,
            SeedData: seedData
        );
    }

    private static List<T> ExecuteFilteredQuery<T>(
        DbConnection connection,
        string sqlTemplate,
        string filterFragment,
        Action<DbCommand> addFilterParams,
        Func<DbDataReader, T> projector
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sqlTemplate.Replace("{SCHEMA_FILTER}", filterFragment);
        addFilterParams(command);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(projector(reader));
        }
        return results;
    }

    private static List<T> ExecuteQuery<T>(
        DbConnection connection,
        string sql,
        Func<DbDataReader, T> projector
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(projector(reader));
        }
        return results;
    }

    private static string? ReadNullableString(DbDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
    }

    private List<SchemaEntry> ReadSchemas(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.SchemasSql, filterFragment, addFilterParams,
            r => new SchemaEntry(r.GetString(r.GetOrdinal("schema_name"))))
        .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
        .ToList();

    private List<TableEntry> ReadTables(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.TablesSql, filterFragment, addFilterParams,
            r => new TableEntry(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name"))))
        .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
        .ThenBy(x => x.TableName, StringComparer.Ordinal)
        .ToList();

    private List<ColumnEntry> ReadColumns(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.ColumnsSql, filterFragment, addFilterParams,
            r => new ColumnEntry(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("column_name")),
                r.GetInt32(r.GetOrdinal("ordinal_position")),
                r.GetString(r.GetOrdinal("data_type")),
                r.GetBoolean(r.GetOrdinal("is_nullable")),
                ReadNullableString(r, "default_expression"),
                r.GetBoolean(r.GetOrdinal("is_computed"))))
        .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
        .ThenBy(x => x.TableName, StringComparer.Ordinal)
        .ThenBy(x => x.ColumnName, StringComparer.Ordinal)
        .ToList();

    private sealed record RawConstraint(
        string SchemaName, string TableName, string ConstraintName,
        string ConstraintType, string? ReferencedSchema, string? ReferencedTable
    );

    private sealed record RawConstraintColumn(
        string SchemaName, string TableName, string ConstraintName,
        string ColumnName, int OrdinalPosition, bool IsReferenced
    );

    private List<RawConstraint> ReadConstraints(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.ConstraintsSql, filterFragment, addFilterParams,
            r => new RawConstraint(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("constraint_name")),
                r.GetString(r.GetOrdinal("constraint_type")),
                ReadNullableString(r, "referenced_schema"),
                ReadNullableString(r, "referenced_table")));

    private List<RawConstraintColumn> ReadConstraintColumns(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.ConstraintColumnsSql, filterFragment, addFilterParams,
            r => new RawConstraintColumn(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("constraint_name")),
                r.GetString(r.GetOrdinal("column_name")),
                r.GetInt32(r.GetOrdinal("ordinal_position")),
                r.GetBoolean(r.GetOrdinal("is_referenced"))));

    private static List<ConstraintEntry> AssembleConstraints(
        List<RawConstraint> constraints, List<RawConstraintColumn> columns)
    {
        var columnLookup = columns
            .GroupBy(c => (c.SchemaName, c.TableName, c.ConstraintName))
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.OrdinalPosition).ToList());

        var results = new List<ConstraintEntry>();
        foreach (var c in constraints)
        {
            var key = (c.SchemaName, c.TableName, c.ConstraintName);
            var cols = columnLookup.GetValueOrDefault(key, []);

            var constraintCols = cols
                .Where(col => !col.IsReferenced)
                .Select(col => col.ColumnName)
                .ToList();

            List<string>? referencedCols = null;
            if (c.ConstraintType == "FOREIGN KEY")
            {
                referencedCols = cols
                    .Where(col => col.IsReferenced)
                    .Select(col => col.ColumnName)
                    .ToList();
            }

            results.Add(new ConstraintEntry(
                c.SchemaName, c.TableName, c.ConstraintName, c.ConstraintType,
                constraintCols, c.ReferencedSchema, c.ReferencedTable, referencedCols
            ));
        }
        return results
            .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
            .ThenBy(x => x.TableName, StringComparer.Ordinal)
            .ThenBy(x => x.ConstraintName, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record RawIndex(string SchemaName, string TableName, string IndexName, bool IsUnique);

    private sealed record RawIndexColumn(
        string SchemaName, string TableName, string IndexName,
        string ColumnName, int OrdinalPosition
    );

    private List<RawIndex> ReadIndexes(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.IndexesSql, filterFragment, addFilterParams,
            r => new RawIndex(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("index_name")),
                r.GetBoolean(r.GetOrdinal("is_unique"))));

    private List<RawIndexColumn> ReadIndexColumns(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.IndexColumnsSql, filterFragment, addFilterParams,
            r => new RawIndexColumn(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("index_name")),
                r.GetString(r.GetOrdinal("column_name")),
                r.GetInt32(r.GetOrdinal("ordinal_position"))));

    private static List<IndexEntry> AssembleIndexes(List<RawIndex> indexes, List<RawIndexColumn> columns)
    {
        var columnLookup = columns
            .GroupBy(c => (c.SchemaName, c.TableName, c.IndexName))
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.OrdinalPosition).Select(c => c.ColumnName).ToList());

        var results = new List<IndexEntry>();
        foreach (var idx in indexes)
        {
            var key = (idx.SchemaName, idx.TableName, idx.IndexName);
            var cols = columnLookup.GetValueOrDefault(key, []);
            results.Add(new IndexEntry(idx.SchemaName, idx.TableName, idx.IndexName, idx.IsUnique, cols));
        }
        return results
            .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
            .ThenBy(x => x.TableName, StringComparer.Ordinal)
            .ThenBy(x => x.IndexName, StringComparer.Ordinal)
            .ToList();
    }

    private List<ViewEntry> ReadViews(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.ViewsSql, filterFragment, addFilterParams,
            r => new ViewEntry(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("view_name")),
                r.GetString(r.GetOrdinal("definition"))))
        .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
        .ThenBy(x => x.ViewName, StringComparer.Ordinal)
        .ToList();

    private List<TriggerEntry> ReadTriggers(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.TriggersSql, filterFragment, addFilterParams,
            r => new TriggerEntry(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("trigger_name")),
                r.GetString(r.GetOrdinal("event_manipulation")),
                r.GetString(r.GetOrdinal("action_timing")),
                r.GetString(r.GetOrdinal("definition")),
                ReadNullableString(r, "function_name")))
        .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
        .ThenBy(x => x.TableName, StringComparer.Ordinal)
        .ThenBy(x => x.TriggerName, StringComparer.Ordinal)
        .ThenBy(x => x.EventManipulation, StringComparer.Ordinal)
        .ToList();

    private List<SequenceEntry> ReadSequences(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.SequencesSql, filterFragment, addFilterParams,
            r => new SequenceEntry(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("sequence_name")),
                r.GetString(r.GetOrdinal("data_type")),
                r.GetInt64(r.GetOrdinal("start_value")),
                r.GetInt64(r.GetOrdinal("increment_by"))))
        .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
        .ThenBy(x => x.SequenceName, StringComparer.Ordinal)
        .ToList();

    private sealed record RawTableType(string SchemaName, string TableTypeName);

    private sealed record RawTableTypeColumn(
        string SchemaName, string TableTypeName, string ColumnName,
        int OrdinalPosition, string DataType, bool IsNullable
    );

    private List<RawTableType> ReadTableTypes(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.TableTypesSql, filterFragment, addFilterParams,
            r => new RawTableType(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_type_name"))));

    private List<RawTableTypeColumn> ReadTableTypeColumns(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.TableTypeColumnsSql, filterFragment, addFilterParams,
            r => new RawTableTypeColumn(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_type_name")),
                r.GetString(r.GetOrdinal("column_name")),
                r.GetInt32(r.GetOrdinal("ordinal_position")),
                r.GetString(r.GetOrdinal("data_type")),
                r.GetBoolean(r.GetOrdinal("is_nullable"))));

    private static List<TableTypeEntry> AssembleTableTypes(
        List<RawTableType> tableTypes, List<RawTableTypeColumn> columns)
    {
        var columnLookup = columns
            .GroupBy(c => (c.SchemaName, c.TableTypeName))
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.OrdinalPosition).ToList());

        var results = new List<TableTypeEntry>();
        foreach (var tt in tableTypes)
        {
            var key = (tt.SchemaName, tt.TableTypeName);
            var cols = columnLookup.GetValueOrDefault(key, []);
            results.Add(new TableTypeEntry(
                tt.SchemaName,
                tt.TableTypeName,
                cols.Select(c => new TableTypeColumnEntry(c.ColumnName, c.OrdinalPosition, c.DataType, c.IsNullable))
                    .ToList()
            ));
        }
        return results
            .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
            .ThenBy(x => x.TableTypeName, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record RawFunction(string SchemaName, string FunctionName, string SpecificName, string ReturnType, string Definition);

    private sealed record RawFunctionParameter(
        string SchemaName, string FunctionName, string SpecificName, string ParameterType, int OrdinalPosition
    );

    private List<RawFunction> ReadFunctions(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.FunctionsSql, filterFragment, addFilterParams,
            r => new RawFunction(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("function_name")),
                r.GetString(r.GetOrdinal("specific_name")),
                r.GetString(r.GetOrdinal("return_type")),
                r.GetString(r.GetOrdinal("definition"))));

    private List<RawFunctionParameter> ReadFunctionParameters(
        DbConnection connection, string filterFragment, Action<DbCommand> addFilterParams
    ) =>
        ExecuteFilteredQuery(connection, Dialect.FunctionParametersSql, filterFragment, addFilterParams,
            r => new RawFunctionParameter(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("function_name")),
                r.GetString(r.GetOrdinal("specific_name")),
                r.GetString(r.GetOrdinal("parameter_type")),
                r.GetInt32(r.GetOrdinal("ordinal_position"))));

    private static List<FunctionEntry> AssembleFunctions(
        List<RawFunction> functions, List<RawFunctionParameter> parameters)
    {
        var paramLookup = parameters
            .GroupBy(p => (p.SchemaName, p.SpecificName))
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.OrdinalPosition).Select(p => p.ParameterType).ToList());

        var results = new List<FunctionEntry>();
        foreach (var f in functions)
        {
            var key = (f.SchemaName, f.SpecificName);
            var paramTypes = paramLookup.GetValueOrDefault(key, []);
            results.Add(new FunctionEntry(f.SchemaName, f.FunctionName, f.ReturnType, paramTypes, f.Definition));
        }
        return results
            .OrderBy(x => x.SchemaName, StringComparer.Ordinal)
            .ThenBy(x => x.FunctionName, StringComparer.Ordinal)
            .ThenBy(x => string.Join(",", x.ParameterTypes), StringComparer.Ordinal)
            .ToList();
    }

    private EffectiveSchemaEntry ReadEffectiveSchema(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Dialect.EffectiveSchemaSql;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("dms.EffectiveSchema table is empty; expected exactly one row.");
        }
        return new EffectiveSchemaEntry(
            reader.GetInt16(reader.GetOrdinal("effective_schema_singleton_id")),
            reader.GetString(reader.GetOrdinal("api_schema_format_version")),
            reader.GetString(reader.GetOrdinal("effective_schema_hash")),
            reader.GetInt16(reader.GetOrdinal("resource_key_count")),
            Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(reader.GetOrdinal("resource_key_seed_hash")))
        );
    }

    private List<SchemaComponentEntry> ReadSchemaComponents(DbConnection connection) =>
        ExecuteQuery(connection, Dialect.SchemaComponentsSql,
            r => new SchemaComponentEntry(
                r.GetString(r.GetOrdinal("effective_schema_hash")),
                r.GetString(r.GetOrdinal("project_endpoint_name")),
                r.GetString(r.GetOrdinal("project_name")),
                r.GetString(r.GetOrdinal("project_version")),
                r.GetBoolean(r.GetOrdinal("is_extension_project"))))
        .OrderBy(x => x.EffectiveSchemaHash, StringComparer.Ordinal)
        .ThenBy(x => x.ProjectEndpointName, StringComparer.Ordinal)
        .ToList();

    private List<ResourceKeyEntry> ReadResourceKeys(DbConnection connection) =>
        ExecuteQuery(connection, Dialect.ResourceKeysSql,
            r => new ResourceKeyEntry(
                r.GetInt16(r.GetOrdinal("resource_key_id")),
                r.GetString(r.GetOrdinal("project_name")),
                r.GetString(r.GetOrdinal("resource_name")),
                r.GetString(r.GetOrdinal("resource_version"))))
        .OrderBy(x => x.ResourceKeyId)
        .ToList();
}
