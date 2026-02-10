// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Emits a deterministic JSON manifest summarizing the effective API schema set,
/// intended for golden-test comparison and diagnostics.
/// </summary>
public static class EffectiveSchemaManifestEmitter
{
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true };

    /// <summary>
    /// Emits a JSON manifest for the given effective schema info.
    /// </summary>
    /// <param name="effectiveSchema">The effective schema info to serialize.</param>
    /// <param name="includeResourceKeys">Whether to include the resource_keys array in the output.</param>
    /// <returns>The JSON manifest string with a trailing newline.</returns>
    public static string Emit(EffectiveSchemaInfo effectiveSchema, bool includeResourceKeys = true)
    {
        ValidateInput(effectiveSchema);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            WriteManifest(writer, effectiveSchema, includeResourceKeys);
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        return json + "\n";
    }

    private static void ValidateInput(EffectiveSchemaInfo effectiveSchema)
    {
        ArgumentNullException.ThrowIfNull(effectiveSchema);

        if (string.IsNullOrEmpty(effectiveSchema.ApiSchemaFormatVersion))
        {
            throw new ArgumentException("ApiSchemaFormatVersion must not be empty.", nameof(effectiveSchema));
        }

        if (string.IsNullOrEmpty(effectiveSchema.RelationalMappingVersion))
        {
            throw new ArgumentException(
                "RelationalMappingVersion must not be empty.",
                nameof(effectiveSchema)
            );
        }

        if (string.IsNullOrEmpty(effectiveSchema.EffectiveSchemaHash))
        {
            throw new ArgumentException("EffectiveSchemaHash must not be empty.", nameof(effectiveSchema));
        }

        if (effectiveSchema.ResourceKeySeedHash is null || effectiveSchema.ResourceKeySeedHash.Length == 0)
        {
            throw new ArgumentException(
                "ResourceKeySeedHash must not be null or empty.",
                nameof(effectiveSchema)
            );
        }

        if (effectiveSchema.SchemaComponentsInEndpointOrder.Count == 0)
        {
            throw new ArgumentException(
                "SchemaComponentsInEndpointOrder must not be empty.",
                nameof(effectiveSchema)
            );
        }

        foreach (var component in effectiveSchema.SchemaComponentsInEndpointOrder)
        {
            if (string.IsNullOrEmpty(component.ProjectHash))
            {
                throw new ArgumentException(
                    $"ProjectHash must not be empty for schema component '{component.ProjectEndpointName}'.",
                    nameof(effectiveSchema)
                );
            }
        }

        if (effectiveSchema.ResourceKeyCount != effectiveSchema.ResourceKeysInIdOrder.Count)
        {
            throw new ArgumentException(
                $"ResourceKeyCount ({effectiveSchema.ResourceKeyCount}) does not match ResourceKeysInIdOrder.Count ({effectiveSchema.ResourceKeysInIdOrder.Count}).",
                nameof(effectiveSchema)
            );
        }
    }

    private static void WriteManifest(
        Utf8JsonWriter writer,
        EffectiveSchemaInfo effectiveSchema,
        bool includeResourceKeys
    )
    {
        writer.WriteStartObject();

        writer.WriteString("api_schema_format_version", effectiveSchema.ApiSchemaFormatVersion);
        writer.WriteString("relational_mapping_version", effectiveSchema.RelationalMappingVersion);
        writer.WriteString("effective_schema_hash", effectiveSchema.EffectiveSchemaHash);
        writer.WriteNumber("resource_key_count", effectiveSchema.ResourceKeyCount);
        writer.WriteString(
            "resource_key_seed_hash",
            Convert.ToHexStringLower(effectiveSchema.ResourceKeySeedHash)
        );

        writer.WritePropertyName("schema_components");
        writer.WriteStartArray();
        foreach (var component in effectiveSchema.SchemaComponentsInEndpointOrder)
        {
            WriteSchemaComponent(writer, component);
        }
        writer.WriteEndArray();

        if (includeResourceKeys)
        {
            writer.WritePropertyName("resource_keys");
            writer.WriteStartArray();
            foreach (var resourceKey in effectiveSchema.ResourceKeysInIdOrder)
            {
                WriteResourceKey(writer, resourceKey);
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteSchemaComponent(Utf8JsonWriter writer, SchemaComponentInfo component)
    {
        writer.WriteStartObject();
        writer.WriteString("project_endpoint_name", component.ProjectEndpointName);
        writer.WriteString("project_name", component.ProjectName);
        writer.WriteString("project_version", component.ProjectVersion);
        writer.WriteBoolean("is_extension_project", component.IsExtensionProject);
        writer.WriteString("project_hash", component.ProjectHash);
        writer.WriteEndObject();
    }

    private static void WriteResourceKey(Utf8JsonWriter writer, ResourceKeyEntry resourceKey)
    {
        writer.WriteStartObject();
        writer.WriteNumber("resource_key_id", resourceKey.ResourceKeyId);
        writer.WriteString("project_name", resourceKey.Resource.ProjectName);
        writer.WriteString("resource_name", resourceKey.Resource.ResourceName);
        writer.WriteString("resource_version", resourceKey.ResourceVersion);
        writer.WriteEndObject();
    }
}
