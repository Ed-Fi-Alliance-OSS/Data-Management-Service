// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class NormalizedPlanDtoJson
{
    // Explicit \n keeps fixture output stable across platforms.
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, NewLine = "\n" };

    public static string EmitCanonicalJson(ResourceWritePlanDto value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Emit(writer => WriteResourceWritePlan(writer, value));
    }

    public static string EmitCanonicalJson(ResourceReadPlanDto value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Emit(writer => WriteResourceReadPlan(writer, value));
    }

    public static string EmitCanonicalJson(PageDocumentIdSqlPlanDto value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Emit(writer => WriteQueryPlan(writer, value));
    }

    public static string ComputeCanonicalSha256(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    public static string ComputeCanonicalSha256(ResourceWritePlanDto value)
    {
        return ComputeCanonicalSha256(EmitCanonicalJson(value));
    }

    public static string ComputeCanonicalSha256(ResourceReadPlanDto value)
    {
        return ComputeCanonicalSha256(EmitCanonicalJson(value));
    }

    public static string ComputeCanonicalSha256(PageDocumentIdSqlPlanDto value)
    {
        return ComputeCanonicalSha256(EmitCanonicalJson(value));
    }

    private static string Emit(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteResourceWritePlan(Utf8JsonWriter writer, ResourceWritePlanDto value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("resource");
        WriteQualifiedResourceName(writer, value.Resource);

        writer.WritePropertyName("table_plans_in_dependency_order");
        writer.WriteStartArray();
        foreach (var tablePlan in value.TablePlansInDependencyOrder)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("table");
            WriteTableName(writer, tablePlan.Table);
            writer.WriteString(
                "insert_sql",
                PlanJsonCanonicalization.NormalizeMultilineText(tablePlan.InsertSql)
            );

            if (tablePlan.UpdateSql is null)
            {
                writer.WriteNull("update_sql");
            }
            else
            {
                writer.WriteString(
                    "update_sql",
                    PlanJsonCanonicalization.NormalizeMultilineText(tablePlan.UpdateSql)
                );
            }

            if (tablePlan.DeleteByParentSql is null)
            {
                writer.WriteNull("delete_by_parent_sql");
            }
            else
            {
                writer.WriteString(
                    "delete_by_parent_sql",
                    PlanJsonCanonicalization.NormalizeMultilineText(tablePlan.DeleteByParentSql)
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

            writer.WritePropertyName("column_bindings");
            writer.WriteStartArray();
            foreach (var binding in tablePlan.ColumnBindings)
            {
                writer.WriteStartObject();
                writer.WriteString("column_name", binding.ColumnName);
                writer.WriteString("parameter_name", binding.ParameterName);
                writer.WritePropertyName("source");
                WriteWriteValueSource(writer, binding.Source);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("key_unification_plans");
            writer.WriteStartArray();
            foreach (var plan in tablePlan.KeyUnificationPlans)
            {
                writer.WriteStartObject();
                writer.WriteString("canonical_column_name", plan.CanonicalColumnName);
                writer.WriteNumber("canonical_binding_index", plan.CanonicalBindingIndex);
                writer.WritePropertyName("members_in_order");
                writer.WriteStartArray();
                foreach (var member in plan.MembersInOrder)
                {
                    WriteKeyUnificationMember(writer, member);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteResourceReadPlan(Utf8JsonWriter writer, ResourceReadPlanDto value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("resource");
        WriteQualifiedResourceName(writer, value.Resource);

        writer.WritePropertyName("keyset_table");
        writer.WriteStartObject();
        writer.WriteString("temp_table_name", value.KeysetTable.TempTableName);
        writer.WriteString("document_id_column_name", value.KeysetTable.DocumentIdColumnName);
        writer.WriteEndObject();

        writer.WritePropertyName("table_plans_in_dependency_order");
        writer.WriteStartArray();
        foreach (var tablePlan in value.TablePlansInDependencyOrder)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("table");
            WriteTableName(writer, tablePlan.Table);
            writer.WriteString(
                "select_by_keyset_sql",
                PlanJsonCanonicalization.NormalizeMultilineText(tablePlan.SelectByKeysetSql)
            );
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("reference_identity_projection_plans_in_dependency_order");
        writer.WriteStartArray();
        foreach (var tablePlan in value.ReferenceIdentityProjectionPlansInDependencyOrder)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("table");
            WriteTableName(writer, tablePlan.Table);
            writer.WritePropertyName("bindings_in_order");
            writer.WriteStartArray();
            foreach (var binding in tablePlan.BindingsInOrder)
            {
                writer.WriteStartObject();
                writer.WriteBoolean("is_identity_component", binding.IsIdentityComponent);
                writer.WriteString("reference_object_path", binding.ReferenceObjectPath);
                writer.WritePropertyName("target_resource");
                WriteQualifiedResourceName(writer, binding.TargetResource);
                writer.WriteNumber("fk_column_ordinal", binding.FkColumnOrdinal);
                writer.WritePropertyName("identity_field_ordinals_in_order");
                writer.WriteStartArray();
                foreach (var fieldOrdinal in binding.IdentityFieldOrdinalsInOrder)
                {
                    writer.WriteStartObject();
                    writer.WriteString("reference_json_path", fieldOrdinal.ReferenceJsonPath);
                    writer.WriteNumber("column_ordinal", fieldOrdinal.ColumnOrdinal);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("descriptor_projection_plans_in_order");
        writer.WriteStartArray();
        foreach (var plan in value.DescriptorProjectionPlansInOrder)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "select_by_keyset_sql",
                PlanJsonCanonicalization.NormalizeMultilineText(plan.SelectByKeysetSql)
            );
            writer.WritePropertyName("result_shape");
            writer.WriteStartObject();
            writer.WriteNumber("descriptor_id_ordinal", plan.ResultShape.DescriptorIdOrdinal);
            writer.WriteNumber("uri_ordinal", plan.ResultShape.UriOrdinal);
            writer.WriteEndObject();

            writer.WritePropertyName("sources_in_order");
            writer.WriteStartArray();
            foreach (var source in plan.SourcesInOrder)
            {
                writer.WriteStartObject();
                writer.WriteString("descriptor_value_path", source.DescriptorValuePath);
                writer.WritePropertyName("table");
                WriteTableName(writer, source.Table);
                writer.WritePropertyName("descriptor_resource");
                WriteQualifiedResourceName(writer, source.DescriptorResource);
                writer.WriteNumber("descriptor_id_column_ordinal", source.DescriptorIdColumnOrdinal);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteQueryPlan(Utf8JsonWriter writer, PageDocumentIdSqlPlanDto value)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "page_document_id_sql",
            PlanJsonCanonicalization.NormalizeMultilineText(value.PageDocumentIdSql)
        );

        if (value.TotalCountSql is null)
        {
            writer.WriteNull("total_count_sql");
        }
        else
        {
            writer.WriteString(
                "total_count_sql",
                PlanJsonCanonicalization.NormalizeMultilineText(value.TotalCountSql)
            );
        }

        writer.WritePropertyName("page_parameters_in_order");
        writer.WriteStartArray();
        foreach (var parameter in value.PageParametersInOrder)
        {
            writer.WriteStartObject();
            writer.WriteString("role", PlanJsonCanonicalization.ToQueryParameterRoleToken(parameter.Role));
            writer.WriteString("parameter_name", parameter.ParameterName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        if (value.TotalCountParametersInOrder is null)
        {
            writer.WriteNull("total_count_parameters_in_order");
        }
        else
        {
            writer.WritePropertyName("total_count_parameters_in_order");
            writer.WriteStartArray();
            foreach (var parameter in value.TotalCountParametersInOrder.Value)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "role",
                    PlanJsonCanonicalization.ToQueryParameterRoleToken(parameter.Role)
                );
                writer.WriteString("parameter_name", parameter.ParameterName);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteQualifiedResourceName(Utf8JsonWriter writer, QualifiedResourceNameDto value)
    {
        writer.WriteStartObject();
        writer.WriteString("project_name", value.ProjectName);
        writer.WriteString("resource_name", value.ResourceName);
        writer.WriteEndObject();
    }

    private static void WriteTableName(Utf8JsonWriter writer, DbTableNameDto value)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", value.Schema);
        writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }

    private static void WriteWriteValueSource(Utf8JsonWriter writer, WriteValueSourceDto source)
    {
        writer.WriteStartObject();

        switch (source)
        {
            case WriteValueSourceDto.DocumentId:
                writer.WriteString("kind", "document_id");
                break;

            case WriteValueSourceDto.ParentKeyPart parentKeyPart:
                writer.WriteString("kind", "parent_key_part");
                writer.WriteNumber("index", parentKeyPart.Index);
                break;

            case WriteValueSourceDto.Ordinal:
                writer.WriteString("kind", "ordinal");
                break;

            case WriteValueSourceDto.Scalar scalar:
                writer.WriteString("kind", "scalar");
                writer.WriteString("relative_path", scalar.RelativePath);
                writer.WritePropertyName("scalar_type");
                WriteScalarType(writer, scalar.ScalarType);
                break;

            case WriteValueSourceDto.DocumentReference documentReference:
                writer.WriteString("kind", "document_reference");
                writer.WriteNumber("binding_index", documentReference.BindingIndex);
                break;

            case WriteValueSourceDto.DescriptorReference descriptorReference:
                writer.WriteString("kind", "descriptor_reference");
                writer.WritePropertyName("descriptor_resource");
                WriteQualifiedResourceName(writer, descriptorReference.DescriptorResource);
                writer.WriteString("relative_path", descriptorReference.RelativePath);

                if (descriptorReference.DescriptorValuePath is null)
                {
                    writer.WriteNull("descriptor_value_path");
                }
                else
                {
                    writer.WriteString("descriptor_value_path", descriptorReference.DescriptorValuePath);
                }

                break;

            case WriteValueSourceDto.Precomputed:
                writer.WriteString("kind", "precomputed");
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(source),
                    source.GetType().Name,
                    "Unsupported write value source DTO kind."
                );
        }

        writer.WriteEndObject();
    }

    private static void WriteScalarType(Utf8JsonWriter writer, RelationalScalarTypeDto value)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", ToNormalizedScalarKind(value.Kind));

        if (value.MaxLength is null)
        {
            writer.WriteNull("max_length");
        }
        else
        {
            writer.WriteNumber("max_length", value.MaxLength.Value);
        }

        if (value.DecimalPrecision is null)
        {
            writer.WriteNull("decimal_precision");
        }
        else
        {
            writer.WriteNumber("decimal_precision", value.DecimalPrecision.Value);
        }

        if (value.DecimalScale is null)
        {
            writer.WriteNull("decimal_scale");
        }
        else
        {
            writer.WriteNumber("decimal_scale", value.DecimalScale.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteKeyUnificationMember(
        Utf8JsonWriter writer,
        KeyUnificationMemberWritePlanDto value
    )
    {
        writer.WriteStartObject();

        switch (value)
        {
            case KeyUnificationMemberWritePlanDto.ScalarMember scalar:
                writer.WriteString("kind", "scalar");
                writer.WriteString("member_path_column_name", scalar.MemberPathColumnName);
                writer.WriteString("relative_path", scalar.RelativePath);
                WriteNullableString(writer, "presence_column_name", scalar.PresenceColumnName);
                WriteNullableInt(writer, "presence_binding_index", scalar.PresenceBindingIndex);
                writer.WriteBoolean("presence_is_synthetic", scalar.PresenceIsSynthetic);
                writer.WritePropertyName("scalar_type");
                WriteScalarType(writer, scalar.ScalarType);
                break;

            case KeyUnificationMemberWritePlanDto.DescriptorMember descriptor:
                writer.WriteString("kind", "descriptor");
                writer.WriteString("member_path_column_name", descriptor.MemberPathColumnName);
                writer.WriteString("relative_path", descriptor.RelativePath);
                WriteNullableString(writer, "presence_column_name", descriptor.PresenceColumnName);
                WriteNullableInt(writer, "presence_binding_index", descriptor.PresenceBindingIndex);
                writer.WriteBoolean("presence_is_synthetic", descriptor.PresenceIsSynthetic);
                writer.WritePropertyName("descriptor_resource");
                WriteQualifiedResourceName(writer, descriptor.DescriptorResource);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value.GetType().Name,
                    "Unsupported key-unification member DTO kind."
                );
        }

        writer.WriteEndObject();
    }

    private static string ToNormalizedScalarKind(NormalizedScalarKind value)
    {
        return value switch
        {
            NormalizedScalarKind.String => "string",
            NormalizedScalarKind.Int32 => "int32",
            NormalizedScalarKind.Int64 => "int64",
            NormalizedScalarKind.Decimal => "decimal",
            NormalizedScalarKind.Boolean => "boolean",
            NormalizedScalarKind.Date => "date",
            NormalizedScalarKind.DateTime => "date_time",
            NormalizedScalarKind.Time => "time",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported scalar kind DTO."),
        };
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
}
