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
            var descriptorFkDeduplicationsByTable = BuildDescriptorForeignKeyDeduplicationLookup(
                resourceModel.DescriptorForeignKeyDeduplications
            );

            foreach (var table in resourceModel.TablesInDependencyOrder)
            {
                WriteTable(writer, table, descriptorFkDeduplicationsByTable);
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
}
