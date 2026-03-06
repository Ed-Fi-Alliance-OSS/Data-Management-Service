// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class MappingSetManifestJsonEmitter
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

        foreach (var resource in GetResourcesInNameOrder(mappingSet))
        {
            ResourceWritePlan? writePlan = mappingSet.WritePlansByResource.TryGetValue(
                resource,
                out var writePlanCandidate
            )
                ? writePlanCandidate
                : null;
            ResourceReadPlan? readPlan = mappingSet.ReadPlansByResource.TryGetValue(
                resource,
                out var readPlanCandidate
            )
                ? readPlanCandidate
                : null;

            writer.WriteStartObject();
            writer.WritePropertyName("resource");
            WriteQualifiedResourceName(writer, resource);

            writer.WritePropertyName("write_plan");
            WriteWritePlanOrNull(writer, writePlan);

            writer.WritePropertyName("read_plan");
            WriteReadPlanOrNull(writer, readPlan);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static IEnumerable<QualifiedResourceName> GetResourcesInNameOrder(MappingSet mappingSet)
    {
        return mappingSet
            .Model.ConcreteResourcesInNameOrder.Select(resource => resource.ResourceKey.Resource)
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal);
    }

    private static void WriteWritePlanOrNull(Utf8JsonWriter writer, ResourceWritePlan? writePlan)
    {
        if (writePlan is null)
        {
            writer.WriteNullValue();
            return;
        }

        WriteWritePlan(writer, writePlan);
    }

    private static void WriteWritePlan(Utf8JsonWriter writer, ResourceWritePlan writePlan)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("table_plans_in_dependency_order");
        writer.WriteStartArray();

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            WriteWriteTablePlan(writer, tablePlan);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteWriteTablePlan(Utf8JsonWriter writer, TableWritePlan tablePlan)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("table");
        WriteTableName(writer, tablePlan.TableModel.Table);
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

        if (tablePlan.DeleteByParentSql is null)
        {
            writer.WriteNull("delete_by_parent_sql_sha256");
        }
        else
        {
            writer.WriteString(
                "delete_by_parent_sql_sha256",
                PlanManifestConventions.ComputeNormalizedSha256(tablePlan.DeleteByParentSql)
            );
        }

        writer.WritePropertyName("bulk_insert_batching");
        writer.WriteStartObject();
        writer.WriteNumber("max_rows_per_batch", tablePlan.BulkInsertBatching.MaxRowsPerBatch);
        writer.WriteNumber("parameters_per_row", tablePlan.BulkInsertBatching.ParametersPerRow);
        writer.WriteNumber(
            "max_parameters_per_command",
            tablePlan.BulkInsertBatching.MaxParametersPerCommand
        );
        writer.WriteEndObject();

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
        writer.WritePropertyName("key_unification_plans");
        writer.WriteStartArray();

        foreach (var keyUnificationPlan in tablePlan.KeyUnificationPlans)
        {
            writer.WriteStartObject();
            writer.WriteString("canonical_column_name", keyUnificationPlan.CanonicalColumn.Value);
            writer.WriteNumber("canonical_binding_index", keyUnificationPlan.CanonicalBindingIndex);
            writer.WritePropertyName("members_in_order");
            writer.WriteStartArray();

            foreach (var member in keyUnificationPlan.MembersInOrder)
            {
                WriteKeyUnificationMember(writer, member);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteReadPlanOrNull(Utf8JsonWriter writer, ResourceReadPlan? readPlan)
    {
        if (readPlan is null)
        {
            writer.WriteNullValue();
            return;
        }

        WriteReadPlanDiagnosticSummary(writer, readPlan);
    }

    private static void WriteReadPlanDiagnosticSummary(Utf8JsonWriter writer, ResourceReadPlan readPlan)
    {
        writer.WriteStartObject();

        // The manifest is diagnostic-only; the normalized plan codec remains authoritative.
        writer.WritePropertyName("keyset_table");
        writer.WriteStartObject();
        writer.WriteString("temp_table_name", readPlan.KeysetTable.Table.Name);
        writer.WriteString("document_id_column_name", readPlan.KeysetTable.DocumentIdColumnName.Value);
        writer.WriteEndObject();

        writer.WritePropertyName("table_plans_in_dependency_order");
        writer.WriteStartArray();

        foreach (var tablePlan in readPlan.TablePlansInDependencyOrder)
        {
            WriteReadTablePlanDiagnosticSummary(writer, tablePlan);
        }

        writer.WriteEndArray();
        WriteStory05ProjectionPlaceholderArray(
            writer,
            "reference_identity_projection_plans_in_dependency_order"
        );
        WriteStory05ProjectionPlaceholderArray(writer, "descriptor_projection_plans_in_order");

        writer.WriteEndObject();
    }

    private static void WriteReadTablePlanDiagnosticSummary(Utf8JsonWriter writer, TableReadPlan tablePlan)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("table");
        WriteTableName(writer, tablePlan.TableModel.Table);
        writer.WriteString(
            "select_by_keyset_sql_sha256",
            PlanManifestConventions.ComputeNormalizedSha256(tablePlan.SelectByKeysetSql)
        );

        writer.WritePropertyName("select_list_columns_in_order");
        writer.WriteStartArray();

        foreach (var columnName in GetDiagnosticSelectListColumnsInOrder(tablePlan))
        {
            writer.WriteStringValue(columnName);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("order_by_key_columns_in_order");
        writer.WriteStartArray();

        foreach (var orderByKeyColumn in GetDiagnosticOrderByKeyColumnsInOrder(tablePlan))
        {
            writer.WriteStringValue(orderByKeyColumn);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteQualifiedResourceName(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }

    private static void WriteTableName(Utf8JsonWriter writer, DbTableName tableName)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", tableName.Schema.Value);
        writer.WriteString("name", tableName.Name);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<string> GetDiagnosticSelectListColumnsInOrder(TableReadPlan tablePlan)
    {
        return tablePlan.TableModel.Columns.Select(static column => column.ColumnName.Value).ToArray();
    }

    private static IReadOnlyList<string> GetDiagnosticOrderByKeyColumnsInOrder(TableReadPlan tablePlan)
    {
        return tablePlan.TableModel.Key.Columns.Select(static column => column.ColumnName.Value).ToArray();
    }

    private static void WriteStory05ProjectionPlaceholderArray(Utf8JsonWriter writer, string propertyName)
    {
        // Story 05 keeps these projection contracts as explicit empty placeholders.
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        writer.WriteEndArray();
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

    private static void WriteKeyUnificationMember(Utf8JsonWriter writer, KeyUnificationMemberWritePlan member)
    {
        writer.WriteStartObject();

        switch (member)
        {
            case KeyUnificationMemberWritePlan.ScalarMember scalar:
                writer.WriteString("kind", "scalar");
                writer.WriteString("member_path_column_name", scalar.MemberPathColumn.Value);
                writer.WriteString("relative_path", scalar.RelativePath.Canonical);
                WriteNullableString(writer, "presence_column_name", scalar.PresenceColumn?.Value);
                WriteNullableInt(writer, "presence_binding_index", scalar.PresenceBindingIndex);
                writer.WriteBoolean("presence_is_synthetic", scalar.PresenceIsSynthetic);
                writer.WriteString("scalar_kind", ToScalarKindToken(scalar.ScalarType.Kind));
                break;

            case KeyUnificationMemberWritePlan.DescriptorMember descriptor:
                writer.WriteString("kind", "descriptor");
                writer.WriteString("member_path_column_name", descriptor.MemberPathColumn.Value);
                writer.WriteString("relative_path", descriptor.RelativePath.Canonical);
                WriteNullableString(writer, "presence_column_name", descriptor.PresenceColumn?.Value);
                WriteNullableInt(writer, "presence_binding_index", descriptor.PresenceBindingIndex);
                writer.WriteBoolean("presence_is_synthetic", descriptor.PresenceIsSynthetic);
                writer.WritePropertyName("descriptor_resource");
                WriteQualifiedResourceName(writer, descriptor.DescriptorResource);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(member),
                    member.GetType().Name,
                    "Unsupported key-unification member plan."
                );
        }

        writer.WriteEndObject();
    }

    private static void WriteNullableInt(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
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
