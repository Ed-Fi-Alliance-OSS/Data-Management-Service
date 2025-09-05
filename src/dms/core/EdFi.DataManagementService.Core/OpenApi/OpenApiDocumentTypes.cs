// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Contains all supporting data types and records used by the OpenApiDocument class
/// for processing OpenAPI extensions and schema generation.
/// </summary>
/// <summary>
/// Provides context and shared resources for extension processing operations.
/// Contains the core OpenAPI resources, component schemas, and logger instance
/// needed throughout the extension processing pipeline.
/// </summary>
/// <param name="OpenApiCoreResources">The complete OpenAPI core document containing all base schemas and definitions</param>
/// <param name="ComponentsSchemas">Direct reference to the components.schemas section for faster access during processing</param>
/// <param name="Logger">Logger instance for recording extension processing activities and debugging information</param>
public sealed record ExtensionContext(
    JsonNode OpenApiCoreResources,
    JsonObject ComponentsSchemas,
    ILogger Logger
);

/// <summary>
/// Represents a validated and parsed extension object ready for processing.
/// Contains both the complete extension object and its extracted properties section.
/// </summary>
/// <param name="ExtensionObject">The complete validated JsonObject representing the extension schema</param>
/// <param name="ExtensionProperties">The 'properties' section extracted from the extension object, or null for direct schema extensions</param>
public sealed record ValidatedExtension(JsonObject ExtensionObject, JsonObject? ExtensionProperties);

/// <summary>
/// Contains the results of analyzing property conflicts between core and extension schemas.
/// Separates properties into those that conflict with existing core properties (requiring redirection)
/// and those that can be added directly to the main extension schema.
/// </summary>
/// <param name="ConflictingProperties">List of properties that already exist in the core schema and need to be redirected to their referenced schemas</param>
/// <param name="NonConflictingProperties">Properties that don't conflict with core schema and can be added directly to the main extension</param>
public sealed record PropertyConflictAnalysis(
    List<PropertyRedirection> ConflictingProperties,
    JsonObject NonConflictingProperties
);

/// <summary>
/// Provides standardized naming convention for extension schemas.
/// Generates consistent schema names for both the main extension schema and project-specific variants.
/// </summary>
/// <param name="ExtensionSchemaName">Name of the main extension schema (e.g., "StudentExtension")</param>
/// <param name="ProjectExtensionSchemaName">Name of the project-specific extension schema (e.g., "sample_StudentExtension")</param>
public sealed record ExtensionSchemaNames(string ExtensionSchemaName, string ProjectExtensionSchemaName);

/// <summary>
/// Represents a property that conflicts with an existing core schema property
/// and must be redirected to its referenced schema instead of the main extension.
/// </summary>
/// <param name="PropertyName">The name of the property that conflicts (e.g., "addresses")</param>
/// <param name="ReferencedSchemaName">The name of the schema that this property references in the core (e.g., "EdFi_StudentAddress")</param>
public sealed record PropertyRedirection(string PropertyName, string ReferencedSchemaName);

/// <summary>
/// Contains the result of creating an extension schema for a redirected property.
/// Includes the generated schema and optionally identifies a referenced schema that needs further resolution.
/// </summary>
/// <param name="Schema">The generated JsonObject schema ready to be added to the OpenAPI specification</param>
/// <param name="ReferencedSchemaName">Optional name of a schema that this extension references and may need additional processing</param>
public sealed record ExtensionSchemaResult(JsonObject Schema, string? ReferencedSchemaName = null);
