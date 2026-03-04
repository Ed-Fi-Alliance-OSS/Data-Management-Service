// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class ThinSliceMappingSetManifestJsonEmitter
{
    // Explicit \n keeps fixture output stable across platforms.
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, NewLine = "\n" };

    public static string Emit(IReadOnlyList<MappingSet> mappingSets)
    {
        ArgumentNullException.ThrowIfNull(mappingSets);

        var orderedMappingSets = mappingSets
            .OrderBy(mappingSet => mappingSet.Key.EffectiveSchemaHash, StringComparer.Ordinal)
            .ThenBy(
                mappingSet => PlanManifestConventions.ToManifestDialect(mappingSet.Key.Dialect),
                StringComparer.Ordinal
            )
            .ThenBy(mappingSet => mappingSet.Key.RelationalMappingVersion, StringComparer.Ordinal)
            .ToArray();

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("mapping_sets");
            writer.WriteStartArray();

            foreach (var mappingSet in orderedMappingSets)
            {
                WriteMappingSet(writer, mappingSet);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteMappingSet(Utf8JsonWriter writer, MappingSet mappingSet)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("mapping_set_key");
        writer.WriteStartObject();
        writer.WriteString("effective_schema_hash", mappingSet.Key.EffectiveSchemaHash);
        writer.WriteString("dialect", PlanManifestConventions.ToManifestDialect(mappingSet.Key.Dialect));
        writer.WriteString("relational_mapping_version", mappingSet.Key.RelationalMappingVersion);
        writer.WriteEndObject();

        writer.WritePropertyName("resources");
        writer.WriteStartArray();

        foreach (var resource in GetSupportedResourcesInNameOrder(mappingSet))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("resource");
            WriteQualifiedResourceName(writer, resource);

            writer.WritePropertyName("write_plan");
            WriteWritePlan(writer, mappingSet.WritePlansByResource[resource]);

            writer.WritePropertyName("read_plan");
            WriteReadPlan(writer, mappingSet.ReadPlansByResource[resource]);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static IEnumerable<QualifiedResourceName> GetSupportedResourcesInNameOrder(MappingSet mappingSet)
    {
        return mappingSet
            .Model.ConcreteResourcesInNameOrder.Select(resource => resource.ResourceKey.Resource)
            .Where(resource =>
                mappingSet.WritePlansByResource.ContainsKey(resource)
                && mappingSet.ReadPlansByResource.ContainsKey(resource)
            )
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal);
    }

    private static void WriteWritePlan(Utf8JsonWriter writer, ResourceWritePlan writePlan)
    {
        if (writePlan.TablePlansInDependencyOrder.Length != 1)
        {
            throw new InvalidOperationException(
                $"Thin-slice manifest expects a single root write table plan. Found: {writePlan.TablePlansInDependencyOrder.Length}."
            );
        }

        var tablePlan = writePlan.TablePlansInDependencyOrder[0];

        writer.WriteStartObject();
        writer.WriteString(
            "insert_sql_sha256",
            PlanManifestConventions.ComputeNormalizedSha256(tablePlan.InsertSql)
        );

        if (tablePlan.UpdateSql is null)
        {
            writer.WriteNull("update_sql_sha256");
        }
        else
        {
            writer.WriteString(
                "update_sql_sha256",
                PlanManifestConventions.ComputeNormalizedSha256(tablePlan.UpdateSql)
            );
        }

        writer.WritePropertyName("column_bindings_in_order");
        writer.WriteStartArray();

        foreach (var binding in tablePlan.ColumnBindings)
        {
            writer.WriteStartObject();
            writer.WriteString("column_name", binding.Column.ColumnName.Value);
            writer.WriteString("column_kind", ToColumnKindToken(binding.Column.Kind));
            writer.WriteString("parameter_name", binding.ParameterName);
            writer.WritePropertyName("write_value_source");
            WriteWriteValueSource(writer, binding.Source);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteReadPlan(Utf8JsonWriter writer, ResourceReadPlan readPlan)
    {
        if (readPlan.TablePlansInDependencyOrder.Length != 1)
        {
            throw new InvalidOperationException(
                $"Thin-slice manifest expects a single root read table plan. Found: {readPlan.TablePlansInDependencyOrder.Length}."
            );
        }

        var tablePlan = readPlan.TablePlansInDependencyOrder[0];

        writer.WriteStartObject();
        writer.WriteString(
            "select_by_keyset_sql_sha256",
            PlanManifestConventions.ComputeNormalizedSha256(tablePlan.SelectByKeysetSql)
        );

        writer.WritePropertyName("select_list_columns_in_order");
        writer.WriteStartArray();

        foreach (var column in tablePlan.TableModel.Columns)
        {
            writer.WriteStringValue(column.ColumnName.Value);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("order_by_key_columns_in_order");
        writer.WriteStartArray();

        foreach (var orderByKeyColumn in GetOrderByKeyColumnsInCompilerOrder(tablePlan.TableModel))
        {
            writer.WriteStringValue(orderByKeyColumn.Value);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("keyset_table");
        writer.WriteStartObject();
        writer.WriteString("temp_table_name", readPlan.KeysetTable.Table.Name);
        writer.WriteString("document_id_column_name", readPlan.KeysetTable.DocumentIdColumnName.Value);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteQualifiedResourceName(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<DbColumnName> GetOrderByKeyColumnsInCompilerOrder(DbTableModel rootTable)
    {
        var rootDocumentIdKeyColumns = rootTable
            .Key.Columns.Where(column => RelationalNameConventions.IsDocumentIdColumn(column.ColumnName))
            .Select(static column => column.ColumnName)
            .ToArray();

        if (rootDocumentIdKeyColumns.Length != 1)
        {
            var keyColumnList = string.Join(
                ", ",
                rootTable.Key.Columns.Select(column => column.ColumnName.Value)
            );

            throw new InvalidOperationException(
                $"Thin-slice manifest expects exactly one root document-id key column for '{rootTable.Table}'. "
                    + $"Found {rootDocumentIdKeyColumns.Length}. Key columns: [{keyColumnList}]."
            );
        }

        var rootDocumentIdKeyColumn = rootDocumentIdKeyColumns[0];

        List<DbColumnName> orderByKeyColumns = [rootDocumentIdKeyColumn];

        foreach (var keyColumn in rootTable.Key.Columns)
        {
            if (keyColumn.ColumnName == rootDocumentIdKeyColumn)
            {
                continue;
            }

            orderByKeyColumns.Add(keyColumn.ColumnName);
        }

        return orderByKeyColumns;
    }

    private static void WriteWriteValueSource(Utf8JsonWriter writer, WriteValueSource valueSource)
    {
        writer.WriteStartObject();

        switch (valueSource)
        {
            case WriteValueSource.DocumentId:
                writer.WriteString("kind", "document_id");
                break;

            case WriteValueSource.ParentKeyPart parentKeyPart:
                writer.WriteString("kind", "parent_key_part");
                writer.WriteNumber("index", parentKeyPart.Index);
                break;

            case WriteValueSource.Ordinal:
                writer.WriteString("kind", "ordinal");
                break;

            case WriteValueSource.Scalar scalar:
                writer.WriteString("kind", "scalar");
                writer.WriteString("relative_path", scalar.RelativePath.Canonical);
                writer.WriteString("scalar_kind", ToScalarKindToken(scalar.Type.Kind));
                break;

            case WriteValueSource.DocumentReference documentReference:
                writer.WriteString("kind", "document_reference");
                writer.WriteNumber("binding_index", documentReference.BindingIndex);
                break;

            case WriteValueSource.DescriptorReference descriptorReference:
                writer.WriteString("kind", "descriptor_reference");
                writer.WritePropertyName("descriptor_resource");
                WriteQualifiedResourceName(writer, descriptorReference.DescriptorResource);
                writer.WriteString("relative_path", descriptorReference.RelativePath.Canonical);

                if (descriptorReference.DescriptorValuePath is null)
                {
                    writer.WriteNull("descriptor_value_path");
                }
                else
                {
                    writer.WriteString(
                        "descriptor_value_path",
                        descriptorReference.DescriptorValuePath.Value.Canonical
                    );
                }

                break;

            case WriteValueSource.Precomputed:
                writer.WriteString("kind", "precomputed");
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(valueSource),
                    valueSource.GetType().Name,
                    "Unsupported write value source."
                );
        }

        writer.WriteEndObject();
    }

    private static string ToColumnKindToken(ColumnKind columnKind)
    {
        return columnKind switch
        {
            ColumnKind.Scalar => "scalar",
            ColumnKind.DocumentFk => "document_fk",
            ColumnKind.DescriptorFk => "descriptor_fk",
            ColumnKind.Ordinal => "ordinal",
            ColumnKind.ParentKeyPart => "parent_key_part",
            _ => throw new ArgumentOutOfRangeException(
                nameof(columnKind),
                columnKind,
                "Unsupported column kind."
            ),
        };
    }

    private static string ToScalarKindToken(ScalarKind scalarKind)
    {
        return scalarKind switch
        {
            ScalarKind.String => "string",
            ScalarKind.Int32 => "int32",
            ScalarKind.Int64 => "int64",
            ScalarKind.Decimal => "decimal",
            ScalarKind.Boolean => "boolean",
            ScalarKind.Date => "date",
            ScalarKind.DateTime => "date_time",
            ScalarKind.Time => "time",
            _ => throw new ArgumentOutOfRangeException(
                nameof(scalarKind),
                scalarKind,
                "Unsupported scalar kind."
            ),
        };
    }
}
