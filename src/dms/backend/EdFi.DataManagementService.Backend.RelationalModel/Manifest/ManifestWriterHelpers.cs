// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.RelationalModel.Manifest;

/// <summary>
/// Shared JSON manifest writing helpers used by both <see cref="DerivedModelSetManifestEmitter"/>
/// and <see cref="Build.RelationalModelManifestEmitter"/>.
/// </summary>
internal static class ManifestWriterHelpers
{
    /// <summary>
    /// Writes a key column entry.
    /// </summary>
    internal static void WriteKeyColumn(Utf8JsonWriter writer, DbKeyColumn keyColumn)
    {
        writer.WriteStartObject();
        writer.WriteString("name", keyColumn.ColumnName.Value);
        writer.WriteString("kind", keyColumn.Kind.ToString());
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a table column entry, including kind, scalar type, nullability, and source JSON path.
    /// </summary>
    internal static void WriteColumn(Utf8JsonWriter writer, DbColumnModel column)
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
        writer.WritePropertyName("storage");
        WriteColumnStorage(writer, column.Storage);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes key-unification class metadata.
    /// </summary>
    internal static void WriteKeyUnificationClass(
        Utf8JsonWriter writer,
        KeyUnificationClass keyUnificationClass
    )
    {
        writer.WriteStartObject();
        writer.WriteString("canonical_column", keyUnificationClass.CanonicalColumn.Value);
        writer.WritePropertyName("member_path_columns");
        WriteColumnNameList(writer, keyUnificationClass.MemberPathColumns);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes column storage metadata.
    /// </summary>
    internal static void WriteColumnStorage(Utf8JsonWriter writer, ColumnStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        writer.WriteStartObject();

        switch (storage)
        {
            case ColumnStorage.Stored:
                writer.WriteString("kind", nameof(ColumnStorage.Stored));
                break;
            case ColumnStorage.UnifiedAlias unifiedAlias:
                writer.WriteString("kind", nameof(ColumnStorage.UnifiedAlias));
                writer.WriteString("canonical_column", unifiedAlias.CanonicalColumn.Value);
                if (unifiedAlias.PresenceColumn is { } presenceColumn)
                {
                    writer.WriteString("presence_column", presenceColumn.Value);
                }
                else
                {
                    writer.WriteNull("presence_column");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(storage),
                    storage,
                    "Unknown column storage type."
                );
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a scalar type entry, including optional max length and decimal precision/scale metadata.
    /// </summary>
    internal static void WriteScalarType(Utf8JsonWriter writer, RelationalScalarType? scalarType)
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
    internal static void WriteConstraint(Utf8JsonWriter writer, TableConstraint constraint)
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
            case TableConstraint.AllOrNoneNullability allOrNone:
                writer.WriteString("kind", "AllOrNoneNullability");
                writer.WriteString("name", allOrNone.Name);
                writer.WriteString("fk_column", allOrNone.FkColumn.Value);
                writer.WritePropertyName("dependent_columns");
                WriteColumnNameList(writer, allOrNone.DependentColumns);
                break;
            case TableConstraint.NullOrTrue nullOrTrue:
                writer.WriteString("kind", "NullOrTrue");
                writer.WriteString("name", nullOrTrue.Name);
                writer.WriteString("column", nullOrTrue.Column.Value);
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
    internal static void WriteColumnNameList(Utf8JsonWriter writer, IReadOnlyList<DbColumnName> columns)
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
    internal static void WriteDescriptorEdge(Utf8JsonWriter writer, DescriptorEdgeSource edge)
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
    internal static void WriteExtensionSite(Utf8JsonWriter writer, ExtensionSite site)
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
    internal static void WriteTableReference(Utf8JsonWriter writer, DbTableName tableName)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", tableName.Schema.Value);
        writer.WriteString("name", tableName.Name);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a resource reference object containing project name and resource name.
    /// </summary>
    internal static void WriteResourceReference(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the resource identity portion of the manifest as a named property.
    /// </summary>
    internal static void WriteResource(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }
}
