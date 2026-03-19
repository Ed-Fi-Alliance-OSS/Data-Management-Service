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
    /// Writes explicit stable-identity metadata for a table.
    /// </summary>
    internal static void WriteIdentityMetadata(
        Utf8JsonWriter writer,
        DbTableIdentityMetadata identityMetadata
    )
    {
        writer.WriteStartObject();
        writer.WriteString("table_kind", identityMetadata.TableKind.ToString());
        writer.WritePropertyName("physical_row_identity_columns");
        WriteColumnNameList(writer, identityMetadata.PhysicalRowIdentityColumns);
        writer.WritePropertyName("root_scope_locator_columns");
        WriteColumnNameList(writer, identityMetadata.RootScopeLocatorColumns);
        writer.WritePropertyName("immediate_parent_scope_locator_columns");
        WriteColumnNameList(writer, identityMetadata.ImmediateParentScopeLocatorColumns);
        writer.WritePropertyName("semantic_identity_bindings");
        writer.WriteStartArray();
        foreach (var binding in identityMetadata.SemanticIdentityBindings)
        {
            WriteSemanticIdentityBinding(writer, binding);
        }
        writer.WriteEndArray();
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

    /// <summary>
    /// Writes a table object with its key columns, columns, key-unification classes, optional
    /// descriptor FK de-duplication diagnostics, and constraints.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="table">The table model to write.</param>
    /// <param name="descriptorFkDeduplicationsByTable">
    /// Optional descriptor FK de-duplication diagnostics grouped by table.
    /// When <c>null</c>, the <c>descriptor_fk_deduplications</c> section is omitted.
    /// </param>
    internal static void WriteTable(
        Utf8JsonWriter writer,
        DbTableModel table,
        IReadOnlyDictionary<
            DbTableName,
            DescriptorForeignKeyDeduplication[]
        >? descriptorFkDeduplicationsByTable = null
    )
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

        writer.WritePropertyName("key_unification_classes");
        writer.WriteStartArray();
        foreach (var keyUnificationClass in table.KeyUnificationClasses)
        {
            WriteKeyUnificationClass(writer, keyUnificationClass);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("identity_metadata");
        WriteIdentityMetadata(writer, table.IdentityMetadata);

        if (descriptorFkDeduplicationsByTable is not null)
        {
            writer.WritePropertyName("descriptor_fk_deduplications");
            writer.WriteStartArray();
            descriptorFkDeduplicationsByTable.TryGetValue(table.Table, out var deduplications);

            foreach (var deduplication in deduplications ?? Array.Empty<DescriptorForeignKeyDeduplication>())
            {
                WriteDescriptorForeignKeyDeduplication(writer, table, deduplication);
            }
            writer.WriteEndArray();
        }

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
    /// Writes one semantic-identity member binding.
    /// </summary>
    private static void WriteSemanticIdentityBinding(
        Utf8JsonWriter writer,
        CollectionSemanticIdentityBinding binding
    )
    {
        writer.WriteStartObject();
        writer.WriteString("relative_path", binding.RelativePath.Canonical);
        writer.WriteString("column", binding.ColumnName.Value);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Builds a lookup of descriptor FK de-duplication diagnostics grouped by table.
    /// </summary>
    internal static IReadOnlyDictionary<
        DbTableName,
        DescriptorForeignKeyDeduplication[]
    > BuildDescriptorForeignKeyDeduplicationLookup(
        IReadOnlyList<DescriptorForeignKeyDeduplication> descriptorForeignKeyDeduplications
    )
    {
        return descriptorForeignKeyDeduplications
            .GroupBy(entry => entry.Table)
            .ToDictionary(
                group => group.Key,
                group =>
                    group
                        .OrderBy(entry => entry.StorageColumn.Value, StringComparer.Ordinal)
                        .ThenBy(
                            entry => string.Join("|", entry.BindingColumns.Select(column => column.Value)),
                            StringComparer.Ordinal
                        )
                        .ToArray()
            );
    }

    /// <summary>
    /// Writes one descriptor FK de-duplication diagnostic entry.
    /// </summary>
    private static void WriteDescriptorForeignKeyDeduplication(
        Utf8JsonWriter writer,
        DbTableModel table,
        DescriptorForeignKeyDeduplication deduplication
    )
    {
        writer.WriteStartObject();
        writer.WriteString("storage_column", deduplication.StorageColumn.Value);
        writer.WritePropertyName("binding_columns");
        WriteColumnNameList(
            writer,
            deduplication.BindingColumns.OrderBy(column => column.Value, StringComparer.Ordinal).ToArray()
        );
        writer.WriteString(
            "constraint_name",
            ResolveDescriptorForeignKeyConstraintName(table, deduplication.StorageColumn)
        );
        writer.WriteEndObject();
    }

    /// <summary>
    /// Resolves the emitted descriptor FK constraint name for one storage column.
    /// </summary>
    private static string ResolveDescriptorForeignKeyConstraintName(
        DbTableModel table,
        DbColumnName storageColumn
    )
    {
        var descriptorTableName = new DbTableName(new DbSchemaName("dms"), "Descriptor");

        var matchingConstraintNames = table
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Where(constraint =>
                constraint.TargetTable.Equals(descriptorTableName)
                && constraint.TargetColumns.Count == 1
                && constraint.TargetColumns[0].Equals(RelationalNameConventions.DocumentIdColumnName)
                && constraint.Columns.Count == 1
                && constraint.Columns[0].Equals(storageColumn)
            )
            .Select(constraint => constraint.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return matchingConstraintNames.Length switch
        {
            1 => matchingConstraintNames[0],
            0 => throw new InvalidOperationException(
                $"Expected descriptor FK constraint for table '{table.Table}' "
                    + $"storage column '{storageColumn.Value}', but none were found."
            ),
            _ => throw new InvalidOperationException(
                $"Expected exactly one descriptor FK constraint for table '{table.Table}' "
                    + $"storage column '{storageColumn.Value}', but found "
                    + $"{matchingConstraintNames.Length}."
            ),
        };
    }

    /// <summary>
    /// Writes per-resource key-unification equality-constraint diagnostics.
    /// </summary>
    internal static void WriteKeyUnificationEqualityConstraintDiagnostics(
        Utf8JsonWriter writer,
        KeyUnificationEqualityConstraintDiagnostics diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        writer.WriteStartObject();

        writer.WritePropertyName("applied");
        writer.WriteStartArray();
        foreach (var applied in diagnostics.Applied)
        {
            writer.WriteStartObject();
            writer.WriteString("endpoint_a_path", applied.EndpointAPath.Canonical);
            writer.WriteString("endpoint_b_path", applied.EndpointBPath.Canonical);
            writer.WritePropertyName("table");
            WriteTableReference(writer, applied.Table);
            writer.WriteString("endpoint_a_column", applied.EndpointAColumn.Value);
            writer.WriteString("endpoint_b_column", applied.EndpointBColumn.Value);
            writer.WriteString("canonical_column", applied.CanonicalColumn.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("redundant");
        writer.WriteStartArray();
        foreach (var redundant in diagnostics.Redundant)
        {
            writer.WriteStartObject();
            writer.WriteString("endpoint_a_path", redundant.EndpointAPath.Canonical);
            writer.WriteString("endpoint_b_path", redundant.EndpointBPath.Canonical);
            writer.WritePropertyName("binding");
            WriteEndpointBinding(writer, redundant.Binding);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("ignored");
        writer.WriteStartArray();
        foreach (var ignored in diagnostics.Ignored)
        {
            writer.WriteStartObject();
            writer.WriteString("endpoint_a_path", ignored.EndpointAPath.Canonical);
            writer.WriteString("endpoint_b_path", ignored.EndpointBPath.Canonical);
            writer.WriteString("reason", ToManifestIgnoredReason(ignored.Reason));
            writer.WritePropertyName("endpoint_a_binding");
            WriteEndpointBinding(writer, ignored.EndpointABinding);
            writer.WritePropertyName("endpoint_b_binding");
            WriteEndpointBinding(writer, ignored.EndpointBBinding);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("ignored_by_reason");
        writer.WriteStartObject();
        foreach (
            var ignoredByReason in diagnostics.IgnoredByReason.OrderBy(
                entry => ToManifestIgnoredReason(entry.Reason),
                StringComparer.Ordinal
            )
        )
        {
            writer.WriteNumber(ToManifestIgnoredReason(ignoredByReason.Reason), ignoredByReason.Count);
        }
        writer.WriteEndObject();

        writer.WritePropertyName("skipped");
        writer.WriteStartArray();
        foreach (var skipped in diagnostics.Skipped)
        {
            writer.WriteStartObject();
            writer.WriteString("source_path", skipped.SourcePath.Canonical);
            writer.WriteString("target_path", skipped.TargetPath.Canonical);
            writer.WriteString("unresolved_endpoint", skipped.UnresolvedEndpoint);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes one endpoint-binding object with table and column.
    /// </summary>
    private static void WriteEndpointBinding(Utf8JsonWriter writer, KeyUnificationEndpointBinding binding)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("table");
        WriteTableReference(writer, binding.Table);
        writer.WriteString("column", binding.Column.Value);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Converts ignored-reason enum values to manifest wire names.
    /// </summary>
    private static string ToManifestIgnoredReason(KeyUnificationIgnoredReason reason)
    {
        return reason switch
        {
            KeyUnificationIgnoredReason.CrossTable => "cross_table",
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "Unsupported key-unification ignored reason."
            ),
        };
    }
}
