// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public static class RelationalModelManifestEmitter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    public static string Emit(RelationalModelBuildResult buildResult)
    {
        if (buildResult is null)
        {
            throw new ArgumentNullException(nameof(buildResult));
        }

        return Emit(buildResult.ResourceModel, buildResult.ExtensionSites);
    }

    public static string Emit(
        RelationalResourceModel resourceModel,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(extensionSites);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteManifest(writer, resourceModel, extensionSites);
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        return json + "\n";
    }

    private static void WriteManifest(
        Utf8JsonWriter writer,
        RelationalResourceModel resourceModel,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        writer.WriteStartObject();

        WriteResource(writer, resourceModel.Resource);
        writer.WriteString("physical_schema", resourceModel.PhysicalSchema.Value);

        writer.WritePropertyName("tables");
        writer.WriteStartArray();
        foreach (var table in OrderTables(resourceModel))
        {
            WriteTable(writer, table);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("descriptor_edge_sources");
        writer.WriteStartArray();
        foreach (var edge in OrderDescriptorEdges(resourceModel.DescriptorEdgeSources))
        {
            WriteDescriptorEdge(writer, edge);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("extension_sites");
        writer.WriteStartArray();
        foreach (var site in OrderExtensionSites(extensionSites))
        {
            WriteExtensionSite(writer, site);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteResource(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }

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
        foreach (var column in OrderColumns(table))
        {
            WriteColumn(writer, column);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("constraints");
        writer.WriteStartArray();
        foreach (var constraint in OrderConstraints(table))
        {
            WriteConstraint(writer, constraint);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteKeyColumn(Utf8JsonWriter writer, DbKeyColumn keyColumn)
    {
        writer.WriteStartObject();
        writer.WriteString("name", keyColumn.ColumnName.Value);
        writer.WriteString("kind", keyColumn.Kind.ToString());
        writer.WriteEndObject();
    }

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

    private static void WriteColumnNameList(Utf8JsonWriter writer, IReadOnlyList<DbColumnName> columns)
    {
        writer.WriteStartArray();
        foreach (var column in columns)
        {
            writer.WriteStringValue(column.Value);
        }
        writer.WriteEndArray();
    }

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

    private static void WriteExtensionSite(Utf8JsonWriter writer, ExtensionSite site)
    {
        writer.WriteStartObject();
        writer.WriteString("owning_scope", site.OwningScope.Canonical);
        writer.WriteString("extension_path", site.ExtensionPath.Canonical);
        writer.WritePropertyName("project_keys");
        writer.WriteStartArray();
        foreach (var projectKey in site.ProjectKeys.OrderBy(key => key, StringComparer.Ordinal))
        {
            writer.WriteStringValue(projectKey);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteTableReference(Utf8JsonWriter writer, DbTableName tableName)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", tableName.Schema.Value);
        writer.WriteString("name", tableName.Name);
        writer.WriteEndObject();
    }

    private static void WriteResourceReference(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<DbTableModel> OrderTables(RelationalResourceModel resourceModel)
    {
        return resourceModel
            .TablesInReadDependencyOrder.OrderBy(table => CountArrayDepth(table.JsonScope))
            .ThenBy(table => table.JsonScope.Canonical, StringComparer.Ordinal)
            .ThenBy(table => table.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(table => table.Table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<DbColumnModel> OrderColumns(DbTableModel table)
    {
        var keyColumnOrder = BuildKeyColumnOrder(table.Key.Columns);

        return table
            .Columns.OrderBy(column => GetColumnGroup(column, keyColumnOrder))
            .ThenBy(column => GetColumnKeyIndex(column, keyColumnOrder))
            .ThenBy(column => column.ColumnName.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<TableConstraint> OrderConstraints(DbTableModel table)
    {
        return table
            .Constraints.OrderBy(GetConstraintGroup)
            .ThenBy(GetConstraintName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<DescriptorEdgeSource> OrderDescriptorEdges(
        IReadOnlyList<DescriptorEdgeSource> edges
    )
    {
        return edges
            .OrderBy(edge => edge.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Table.Name, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorValuePath.Canonical, StringComparer.Ordinal)
            .ThenBy(edge => edge.FkColumn.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorResource.ProjectName, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorResource.ResourceName, StringComparer.Ordinal)
            .ThenBy(edge => edge.IsIdentityComponent)
            .ToArray();
    }

    private static IReadOnlyList<ExtensionSite> OrderExtensionSites(
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        return extensionSites
            .OrderBy(site => site.OwningScope.Canonical, StringComparer.Ordinal)
            .ThenBy(site => site.ExtensionPath.Canonical, StringComparer.Ordinal)
            .ThenBy(site => string.Join("|", site.ProjectKeys), StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, int> BuildKeyColumnOrder(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        Dictionary<string, int> keyOrder = new(StringComparer.Ordinal);

        for (var index = 0; index < keyColumns.Count; index++)
        {
            keyOrder[keyColumns[index].ColumnName.Value] = index;
        }

        return keyOrder;
    }

    private static int GetColumnGroup(DbColumnModel column, IReadOnlyDictionary<string, int> keyColumnOrder)
    {
        if (keyColumnOrder.ContainsKey(column.ColumnName.Value))
        {
            return 0;
        }

        return column.Kind switch
        {
            ColumnKind.DescriptorFk => 1,
            ColumnKind.Scalar => 2,
            _ => 3,
        };
    }

    private static int GetColumnKeyIndex(
        DbColumnModel column,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        return keyColumnOrder.TryGetValue(column.ColumnName.Value, out var index) ? index : int.MaxValue;
    }

    private static int GetConstraintGroup(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique => 1,
            TableConstraint.ForeignKey => 2,
            _ => 99,
        };
    }

    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            _ => string.Empty,
        };
    }

    private static int CountArrayDepth(JsonPathExpression scope)
    {
        var depth = 0;

        foreach (var segment in scope.Segments)
        {
            if (segment is JsonPathSegment.AnyArrayElement)
            {
                depth++;
            }
        }

        return depth;
    }
}
