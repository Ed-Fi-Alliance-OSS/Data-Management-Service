// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;
using static EdFi.DataManagementService.Backend.RelationalModel.Manifest.ManifestWriterHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Emits a deterministic JSON manifest for a relational resource model build, intended for diagnostics
/// and validation.
/// </summary>
public static class RelationalModelManifestEmitter
{
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, NewLine = "\n" };
    private static readonly DbTableName _descriptorTableName = new(new DbSchemaName("dms"), "Descriptor");

    /// <summary>
    /// Emits a JSON manifest for a completed relational model build.
    /// </summary>
    /// <param name="buildResult">The build result containing the resource model and extension sites.</param>
    /// <returns>The JSON manifest.</returns>
    public static string Emit(RelationalModelBuildResult buildResult)
    {
        ArgumentNullException.ThrowIfNull(buildResult);

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
            var descriptorForeignKeyDeduplicationsByTable = BuildDescriptorForeignKeyDeduplicationLookup(
                resourceModel.DescriptorForeignKeyDeduplications
            );

            foreach (var table in resourceModel.TablesInDependencyOrder)
            {
                WriteTable(writer, table, descriptorForeignKeyDeduplicationsByTable);
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("key_unification_equality_constraints");
        WriteKeyUnificationEqualityConstraintDiagnostics(
            writer,
            resourceModel.KeyUnificationEqualityConstraints
        );

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
    /// Writes a table model entry, including key columns, columns, and constraints.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="table">The table model to write.</param>
    /// <param name="descriptorForeignKeyDeduplicationsByTable">
    /// Descriptor FK de-duplication diagnostics grouped by table.
    /// </param>
    private static void WriteTable(
        Utf8JsonWriter writer,
        DbTableModel table,
        IReadOnlyDictionary<
            DbTableName,
            DescriptorForeignKeyDeduplication[]
        > descriptorForeignKeyDeduplicationsByTable
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

        writer.WritePropertyName("descriptor_fk_deduplications");
        writer.WriteStartArray();
        descriptorForeignKeyDeduplicationsByTable.TryGetValue(table.Table, out var deduplications);

        foreach (var deduplication in deduplications ?? Array.Empty<DescriptorForeignKeyDeduplication>())
        {
            WriteDescriptorForeignKeyDeduplication(writer, table, deduplication);
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

    private static IReadOnlyDictionary<
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
    /// Writes per-resource key-unification equality-constraint diagnostics.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="diagnostics">The key-unification diagnostics payload.</param>
    private static void WriteKeyUnificationEqualityConstraintDiagnostics(
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

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes one endpoint-binding object with table and column.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="binding">The endpoint binding to write.</param>
    private static void WriteEndpointBinding(Utf8JsonWriter writer, KeyUnificationEndpointBinding binding)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("table");
        WriteTableReference(writer, binding.Table);
        writer.WriteString("column", binding.Column.Value);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes one descriptor FK de-duplication diagnostic entry.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="table">The table owning the de-duplication group.</param>
    /// <param name="deduplication">The de-duplication group to write.</param>
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
    /// <param name="table">The table containing the descriptor FK constraints.</param>
    /// <param name="storageColumn">The descriptor storage column.</param>
    /// <returns>The single descriptor FK constraint name for the storage column.</returns>
    private static string ResolveDescriptorForeignKeyConstraintName(
        DbTableModel table,
        DbColumnName storageColumn
    )
    {
        var matchingConstraintNames = table
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Where(constraint =>
                IsDescriptorForeignKeyConstraint(constraint)
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
    /// Checks whether a foreign key targets the canonical shared descriptor table contract.
    /// </summary>
    /// <param name="foreignKey">The foreign key to check.</param>
    /// <returns>
    /// <c>true</c> when the key targets <c>dms.Descriptor(DocumentId)</c>; otherwise <c>false</c>.
    /// </returns>
    private static bool IsDescriptorForeignKeyConstraint(TableConstraint.ForeignKey foreignKey)
    {
        if (!foreignKey.TargetTable.Equals(_descriptorTableName))
        {
            return false;
        }

        return foreignKey.TargetColumns.Count == 1
            && foreignKey.TargetColumns[0].Equals(RelationalNameConventions.DocumentIdColumnName);
    }

    /// <summary>
    /// Converts ignored-reason enum values to manifest wire names.
    /// </summary>
    /// <param name="reason">The ignored reason enum.</param>
    /// <returns>The manifest wire-name for the reason.</returns>
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
