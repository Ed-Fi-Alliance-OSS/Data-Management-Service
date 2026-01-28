// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Emits a deterministic JSON manifest for a relational resource model build, intended for diagnostics
/// and validation.
/// </summary>
public static class RelationalModelManifestEmitter
{
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true };

    /// <summary>
    /// Emits a JSON manifest for a completed relational model build.
    /// </summary>
    /// <param name="buildResult">The build result containing the resource model and extension sites.</param>
    /// <returns>The JSON manifest.</returns>
    public static string Emit(RelationalModelBuildResult buildResult)
    {
        if (buildResult is null)
        {
            throw new ArgumentNullException(nameof(buildResult));
        }

        return Emit(buildResult.ResourceModel, buildResult.ExtensionSites);
    }

    /// <summary>
    /// Emits a JSON manifest for a resource model and its associated extension sites.
    /// </summary>
    /// <param name="resourceModel">The resource model to serialize.</param>
    /// <param name="extensionSites">The ordered extension sites to include.</param>
    /// <returns>The JSON manifest.</returns>
    public static string Emit(
        RelationalResourceModel resourceModel,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(extensionSites);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            WriteManifest(writer, resourceModel, extensionSites);
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        return json + "\n";
    }

    /// <summary>
    /// Writes the top-level manifest object to the provided JSON writer.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="resourceModel">The resource model to serialize.</param>
    /// <param name="extensionSites">The extension sites to include.</param>
    private static void WriteManifest(
        Utf8JsonWriter writer,
        RelationalResourceModel resourceModel,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        writer.WriteStartObject();

        WriteResource(writer, resourceModel.Resource);
        writer.WriteString("physical_schema", resourceModel.PhysicalSchema.Value);
        writer.WriteString("storage_kind", resourceModel.StorageKind.ToString());

        writer.WritePropertyName("tables");
        writer.WriteStartArray();
        if (resourceModel.StorageKind != ResourceStorageKind.SharedDescriptorTable)
        {
            foreach (var table in resourceModel.TablesInReadDependencyOrder)
            {
                WriteTable(writer, table);
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("descriptor_edge_sources");
        writer.WriteStartArray();
        foreach (var edge in resourceModel.DescriptorEdgeSources)
        {
            WriteDescriptorEdge(writer, edge);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("extension_sites");
        writer.WriteStartArray();
        foreach (var site in extensionSites)
        {
            WriteExtensionSite(writer, site);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the resource identity portion of the manifest.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="resource">The resource identity to write.</param>
    private static void WriteResource(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a table model entry, including key columns, columns, and constraints.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="table">The table model to write.</param>
    private static void WriteTable(Utf8JsonWriter writer, DbTableModel table)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", table.Table.Schema.Value);
        writer.WriteString("name", table.Table.Name);
        writer.WriteString("scope", table.JsonScope.Canonical);

        writer.WritePropertyName("key_columns");
        writer.WriteStartArray();
        foreach (var keyColumn in table.Key.Columns)
        {
            WriteKeyColumn(writer, keyColumn);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var column in table.Columns)
        {
            WriteColumn(writer, column);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("constraints");
        writer.WriteStartArray();
        foreach (var constraint in table.Constraints)
        {
            WriteConstraint(writer, constraint);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a key column entry.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="keyColumn">The key column to write.</param>
    private static void WriteKeyColumn(Utf8JsonWriter writer, DbKeyColumn keyColumn)
    {
        writer.WriteStartObject();
        writer.WriteString("name", keyColumn.ColumnName.Value);
        writer.WriteString("kind", keyColumn.Kind.ToString());
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a table column entry, including kind, scalar type, nullability, and source JSON path.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="column">The column model to write.</param>
    private static void WriteColumn(Utf8JsonWriter writer, DbColumnModel column)
    {
        writer.WriteStartObject();
        writer.WriteString("name", column.ColumnName.Value);
        writer.WriteString("kind", column.Kind.ToString());
        writer.WritePropertyName("type");
        WriteScalarType(writer, column.ScalarType);
        writer.WriteBoolean("is_nullable", column.IsNullable);
        writer.WritePropertyName("source_path");
        if (column.SourceJsonPath is { } sourcePath)
        {
            writer.WriteStringValue(sourcePath.Canonical);
        }
        else
        {
            writer.WriteNullValue();
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a scalar type entry, including optional max length and decimal precision/scale metadata.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="scalarType">The scalar type to write.</param>
    private static void WriteScalarType(Utf8JsonWriter writer, RelationalScalarType? scalarType)
    {
        if (scalarType is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", scalarType.Kind.ToString());

        if (scalarType.MaxLength is not null)
        {
            writer.WriteNumber("max_length", scalarType.MaxLength.Value);
        }

        if (scalarType.Decimal is not null)
        {
            writer.WriteNumber("precision", scalarType.Decimal.Value.Precision);
            writer.WriteNumber("scale", scalarType.Decimal.Value.Scale);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a table constraint entry, using a constraint-type specific shape.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="constraint">The constraint to write.</param>
    private static void WriteConstraint(Utf8JsonWriter writer, TableConstraint constraint)
    {
        writer.WriteStartObject();

        switch (constraint)
        {
            case TableConstraint.Unique unique:
                writer.WriteString("kind", "Unique");
                writer.WriteString("name", unique.Name);
                writer.WritePropertyName("columns");
                WriteColumnNameList(writer, unique.Columns);
                break;
            case TableConstraint.ForeignKey foreignKey:
                writer.WriteString("kind", "ForeignKey");
                writer.WriteString("name", foreignKey.Name);
                writer.WritePropertyName("columns");
                WriteColumnNameList(writer, foreignKey.Columns);
                writer.WritePropertyName("target_table");
                WriteTableReference(writer, foreignKey.TargetTable);
                writer.WritePropertyName("target_columns");
                WriteColumnNameList(writer, foreignKey.TargetColumns);
                writer.WriteString("on_delete", foreignKey.OnDelete.ToString());
                writer.WriteString("on_update", foreignKey.OnUpdate.ToString());
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(constraint),
                    constraint,
                    "Unknown table constraint type."
                );
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an array of column names.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="columns">The column names to write.</param>
    private static void WriteColumnNameList(Utf8JsonWriter writer, IReadOnlyList<DbColumnName> columns)
    {
        writer.WriteStartArray();
        foreach (var column in columns)
        {
            writer.WriteStringValue(column.Value);
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes a descriptor edge source entry.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="edge">The descriptor edge source to write.</param>
    private static void WriteDescriptorEdge(Utf8JsonWriter writer, DescriptorEdgeSource edge)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("is_identity_component", edge.IsIdentityComponent);
        writer.WriteString("descriptor_value_path", edge.DescriptorValuePath.Canonical);
        writer.WritePropertyName("table");
        WriteTableReference(writer, edge.Table);
        writer.WriteString("fk_column", edge.FkColumn.Value);
        writer.WritePropertyName("descriptor_resource");
        WriteResourceReference(writer, edge.DescriptorResource);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an extension site entry.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="site">The extension site to write.</param>
    private static void WriteExtensionSite(Utf8JsonWriter writer, ExtensionSite site)
    {
        writer.WriteStartObject();
        writer.WriteString("owning_scope", site.OwningScope.Canonical);
        writer.WriteString("extension_path", site.ExtensionPath.Canonical);
        writer.WritePropertyName("project_keys");
        writer.WriteStartArray();
        foreach (var projectKey in site.ProjectKeys)
        {
            writer.WriteStringValue(projectKey);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a table reference object containing schema and name.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="tableName">The referenced table name.</param>
    private static void WriteTableReference(Utf8JsonWriter writer, DbTableName tableName)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", tableName.Schema.Value);
        writer.WriteString("name", tableName.Name);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a resource reference object containing project name and resource name.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="resource">The referenced resource identity.</param>
    private static void WriteResourceReference(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }
}
