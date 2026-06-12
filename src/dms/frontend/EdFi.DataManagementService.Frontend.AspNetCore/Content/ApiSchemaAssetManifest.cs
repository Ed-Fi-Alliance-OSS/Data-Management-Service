// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

/// <summary>
/// Represents a project entry in the bootstrap-api-schema-manifest.json file.
/// </summary>
public record ApiSchemaProject(
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("projectEndpointName")] string ProjectEndpointName,
    [property: JsonPropertyName("isExtensionProject")] bool IsExtensionProject,
    [property: JsonPropertyName("schemaPath")] string SchemaPath,
    [property: JsonPropertyName("discoverySpecPath")] string? DiscoverySpecPath,
    [property: JsonPropertyName("xsdDirectory")] string? XsdDirectory
);

/// <summary>
/// Represents the bootstrap-api-schema-manifest.json file structure.
/// Absent keys and null values for optional fields both mean "asset not provided".
/// </summary>
public record ApiSchemaAssetManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("projects")] IReadOnlyList<ApiSchemaProject> Projects
);
