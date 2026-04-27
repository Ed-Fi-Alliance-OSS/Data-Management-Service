// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.SchemaTools.Introspection;

public static class ProvisionedSchemaManifestEmitter
{
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, NewLine = "\n" };

    private static string NormalizeDefinition(string definition) =>
        definition.Replace("\r\n", "\n").Replace("\r", "\n");

    public static string Emit(ProvisionedSchemaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var buffer = new ArrayBufferWriter<byte>();

        using (var w = new Utf8JsonWriter(buffer, _writerOptions))
        {
            w.WriteStartObject();
            w.WriteString("manifest_version", manifest.ManifestVersion);
            w.WriteString("dialect", manifest.Dialect);

            WriteSchemas(w, manifest.Schemas);
            WriteTables(w, manifest.Tables);
            WriteColumns(w, manifest.Columns);
            WriteConstraints(w, manifest.Constraints);
            WriteIndexes(w, manifest.Indexes);
            WriteViews(w, manifest.Views);
            WriteTriggers(w, manifest.Triggers);
            WriteSequences(w, manifest.Sequences);
            WriteTableTypes(w, manifest.TableTypes);
            WriteFunctions(w, manifest.Functions);
            WriteSeedData(w, manifest.SeedData);

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteSchemas(Utf8JsonWriter w, IReadOnlyList<SchemaEntry> schemas)
    {
        w.WritePropertyName("schemas");
        w.WriteStartArray();
        foreach (var entry in schemas)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteTables(Utf8JsonWriter w, IReadOnlyList<TableEntry> tables)
    {
        w.WritePropertyName("tables");
        w.WriteStartArray();
        foreach (var entry in tables)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("table_name", entry.TableName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteColumns(Utf8JsonWriter w, IReadOnlyList<ColumnEntry> columns)
    {
        w.WritePropertyName("columns");
        w.WriteStartArray();
        foreach (var entry in columns)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("table_name", entry.TableName);
            w.WriteString("column_name", entry.ColumnName);
            w.WriteNumber("ordinal_position", entry.OrdinalPosition);
            w.WriteString("data_type", entry.DataType);
            w.WriteBoolean("is_nullable", entry.IsNullable);
            if (entry.DefaultExpression is null)
            {
                w.WriteNull("default_expression");
            }
            else
            {
                w.WriteString("default_expression", entry.DefaultExpression);
            }
            w.WriteBoolean("is_computed", entry.IsComputed);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteConstraints(Utf8JsonWriter w, IReadOnlyList<ConstraintEntry> constraints)
    {
        w.WritePropertyName("constraints");
        w.WriteStartArray();
        foreach (var entry in constraints)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("table_name", entry.TableName);
            w.WriteString("constraint_name", entry.ConstraintName);
            w.WriteString("constraint_type", entry.ConstraintType);
            w.WritePropertyName("columns");
            w.WriteStartArray();
            foreach (var col in entry.Columns)
            {
                w.WriteStringValue(col);
            }
            w.WriteEndArray();
            if (entry.ReferencedSchema is null)
            {
                w.WriteNull("referenced_schema");
            }
            else
            {
                w.WriteString("referenced_schema", entry.ReferencedSchema);
            }
            if (entry.ReferencedTable is null)
            {
                w.WriteNull("referenced_table");
            }
            else
            {
                w.WriteString("referenced_table", entry.ReferencedTable);
            }
            if (entry.ReferencedColumns is null)
            {
                w.WriteNull("referenced_columns");
            }
            else
            {
                w.WritePropertyName("referenced_columns");
                w.WriteStartArray();
                foreach (var col in entry.ReferencedColumns)
                {
                    w.WriteStringValue(col);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteIndexes(Utf8JsonWriter w, IReadOnlyList<IndexEntry> indexes)
    {
        w.WritePropertyName("indexes");
        w.WriteStartArray();
        foreach (var entry in indexes)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("table_name", entry.TableName);
            w.WriteString("index_name", entry.IndexName);
            w.WriteBoolean("is_unique", entry.IsUnique);
            w.WritePropertyName("columns");
            w.WriteStartArray();
            foreach (var col in entry.Columns)
            {
                w.WriteStringValue(col);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteViews(Utf8JsonWriter w, IReadOnlyList<ViewEntry> views)
    {
        w.WritePropertyName("views");
        w.WriteStartArray();
        foreach (var entry in views)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("view_name", entry.ViewName);
            w.WriteString("definition", NormalizeDefinition(entry.Definition));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteTriggers(Utf8JsonWriter w, IReadOnlyList<TriggerEntry> triggers)
    {
        w.WritePropertyName("triggers");
        w.WriteStartArray();
        foreach (var entry in triggers)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("table_name", entry.TableName);
            w.WriteString("trigger_name", entry.TriggerName);
            w.WriteString("event_manipulation", entry.EventManipulation);
            w.WriteString("action_timing", entry.ActionTiming);
            w.WriteString("definition", NormalizeDefinition(entry.Definition));
            if (entry.FunctionName is null)
            {
                w.WriteNull("function_name");
            }
            else
            {
                w.WriteString("function_name", entry.FunctionName);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteSequences(Utf8JsonWriter w, IReadOnlyList<SequenceEntry> sequences)
    {
        w.WritePropertyName("sequences");
        w.WriteStartArray();
        foreach (var entry in sequences)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("sequence_name", entry.SequenceName);
            w.WriteString("data_type", entry.DataType);
            w.WriteNumber("start_value", entry.StartValue);
            w.WriteNumber("increment_by", entry.IncrementBy);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteTableTypes(Utf8JsonWriter w, IReadOnlyList<TableTypeEntry> tableTypes)
    {
        w.WritePropertyName("table_types");
        w.WriteStartArray();
        foreach (var entry in tableTypes)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("table_type_name", entry.TableTypeName);
            w.WritePropertyName("columns");
            w.WriteStartArray();
            foreach (var col in entry.Columns)
            {
                w.WriteStartObject();
                w.WriteString("column_name", col.ColumnName);
                w.WriteNumber("ordinal_position", col.OrdinalPosition);
                w.WriteString("data_type", col.DataType);
                w.WriteBoolean("is_nullable", col.IsNullable);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteFunctions(Utf8JsonWriter w, IReadOnlyList<FunctionEntry> functions)
    {
        w.WritePropertyName("functions");
        w.WriteStartArray();
        foreach (var entry in functions)
        {
            w.WriteStartObject();
            w.WriteString("schema_name", entry.SchemaName);
            w.WriteString("function_name", entry.FunctionName);
            w.WriteString("return_type", entry.ReturnType);
            w.WritePropertyName("parameter_types");
            w.WriteStartArray();
            foreach (var paramType in entry.ParameterTypes)
            {
                w.WriteStringValue(paramType);
            }
            w.WriteEndArray();
            w.WriteString("definition", NormalizeDefinition(entry.Definition));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteSeedData(Utf8JsonWriter w, SeedData seedData)
    {
        w.WritePropertyName("seed_data");
        w.WriteStartObject();

        WriteEffectiveSchema(w, seedData.EffectiveSchema);
        WriteSchemaComponents(w, seedData.SchemaComponents);
        WriteResourceKeys(w, seedData.ResourceKeys);

        w.WriteEndObject();
    }

    private static void WriteEffectiveSchema(Utf8JsonWriter w, EffectiveSchemaEntry entry)
    {
        w.WritePropertyName("effective_schema");
        w.WriteStartObject();
        w.WriteNumber("effective_schema_singleton_id", entry.EffectiveSchemaSingletonId);
        w.WriteString("api_schema_format_version", entry.ApiSchemaFormatVersion);
        w.WriteString("effective_schema_hash", entry.EffectiveSchemaHash);
        w.WriteNumber("resource_key_count", entry.ResourceKeyCount);
        w.WriteString("resource_key_seed_hash", entry.ResourceKeySeedHash);
        w.WriteEndObject();
    }

    private static void WriteSchemaComponents(
        Utf8JsonWriter w,
        IReadOnlyList<SchemaComponentEntry> components
    )
    {
        w.WritePropertyName("schema_components");
        w.WriteStartArray();
        foreach (var entry in components)
        {
            w.WriteStartObject();
            w.WriteString("effective_schema_hash", entry.EffectiveSchemaHash);
            w.WriteString("project_endpoint_name", entry.ProjectEndpointName);
            w.WriteString("project_name", entry.ProjectName);
            w.WriteString("project_version", entry.ProjectVersion);
            w.WriteBoolean("is_extension_project", entry.IsExtensionProject);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteResourceKeys(Utf8JsonWriter w, IReadOnlyList<ResourceKeyEntry> resourceKeys)
    {
        w.WritePropertyName("resource_keys");
        w.WriteStartArray();
        foreach (var entry in resourceKeys)
        {
            w.WriteStartObject();
            w.WriteNumber("resource_key_id", entry.ResourceKeyId);
            w.WriteString("project_name", entry.ProjectName);
            w.WriteString("resource_name", entry.ResourceName);
            w.WriteString("resource_version", entry.ResourceVersion);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
