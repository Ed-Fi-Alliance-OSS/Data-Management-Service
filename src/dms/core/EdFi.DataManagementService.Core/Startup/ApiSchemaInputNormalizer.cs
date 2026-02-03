// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Normalizes API schema inputs to a deterministic canonical form.
/// This ensures that the same logical schema always produces the same
/// EffectiveSchemaHash regardless of formatting differences.
///
/// Normalization includes:
/// - Stripping OpenAPI payloads (not needed for hashing/model derivation)
/// - Sorting extensions by projectEndpointName (ordinal) for determinism
/// - Validating inputs and failing fast with actionable errors
/// </summary>
internal class ApiSchemaInputNormalizer(ILogger<ApiSchemaInputNormalizer> _logger) : IApiSchemaInputNormalizer
{
    /// <inheritdoc />
    public ApiSchemaNormalizationResult Normalize(ApiSchemaDocumentNodes nodes)
    {
        _logger.LogDebug("Beginning schema normalization");

        // Step 1: Validate core schema
        var coreValidation = ValidateSchema(nodes.CoreApiSchemaRootNode, "core");
        if (coreValidation != null)
        {
            return coreValidation;
        }

        var coreVersion = GetApiSchemaVersion(nodes.CoreApiSchemaRootNode);
        _logger.LogDebug("Core schema apiSchemaVersion: {Version}", SanitizeForLog(coreVersion));

        // Step 2: Validate all extensions and check for version mismatches
        var extensionSchemas = new List<(JsonNode Node, string EndpointName, string SchemaSource)>();
        for (int i = 0; i < nodes.ExtensionApiSchemaRootNodes.Length; i++)
        {
            var extNode = nodes.ExtensionApiSchemaRootNodes[i];
            var schemaSource = $"extension[{i}]";

            var extValidation = ValidateSchema(extNode, schemaSource);
            if (extValidation != null)
            {
                return extValidation;
            }

            var extVersion = GetApiSchemaVersion(extNode);
            if (extVersion != coreVersion)
            {
                _logger.LogError(
                    "apiSchemaVersion mismatch in {SchemaSource}: expected {Expected}, got {Actual}",
                    SanitizeForLog(schemaSource),
                    SanitizeForLog(coreVersion),
                    SanitizeForLog(extVersion)
                );
                return new ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult(
                    coreVersion,
                    extVersion,
                    schemaSource
                );
            }

            var endpointName = GetProjectEndpointName(extNode);
            extensionSchemas.Add((extNode, endpointName, schemaSource));
        }

        // Step 3: Check for projectEndpointName collisions
        var collisionResult = CheckForEndpointNameCollisions(nodes.CoreApiSchemaRootNode, extensionSchemas);
        if (collisionResult != null)
        {
            return collisionResult;
        }

        // Step 4: Strip OpenAPI payloads and sort extensions
        var strippedCoreNode = StripOpenApiPayloads(nodes.CoreApiSchemaRootNode);

        var sortedExtensions = extensionSchemas
            .OrderBy(x => x.EndpointName, StringComparer.Ordinal)
            .Select(x => StripOpenApiPayloads(x.Node))
            .ToArray();

        _logger.LogDebug(
            "Schema normalization complete. Core schema and {ExtensionCount} extension(s) processed",
            sortedExtensions.Length
        );

        var normalizedNodes = new ApiSchemaDocumentNodes(strippedCoreNode, sortedExtensions);
        return new ApiSchemaNormalizationResult.SuccessResult(normalizedNodes);
    }

    /// <summary>
    /// Validates that a schema node has the required structure.
    /// </summary>
    private ApiSchemaNormalizationResult? ValidateSchema(JsonNode node, string schemaSource)
    {
        var projectSchema = node["projectSchema"];
        if (projectSchema == null)
        {
            _logger.LogError(
                "Schema {SchemaSource} is missing projectSchema node",
                SanitizeForLog(schemaSource)
            );
            return new ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult(
                schemaSource,
                "Missing projectSchema node"
            );
        }

        var apiSchemaVersion = node["apiSchemaVersion"]?.GetValue<string>();
        if (string.IsNullOrEmpty(apiSchemaVersion))
        {
            _logger.LogError(
                "Schema {SchemaSource} is missing apiSchemaVersion",
                SanitizeForLog(schemaSource)
            );
            return new ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult(
                schemaSource,
                "Missing apiSchemaVersion"
            );
        }

        var projectEndpointName = projectSchema["projectEndpointName"]?.GetValue<string>();
        if (string.IsNullOrEmpty(projectEndpointName))
        {
            _logger.LogError(
                "Schema {SchemaSource} is missing projectEndpointName in projectSchema",
                SanitizeForLog(schemaSource)
            );
            return new ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult(
                schemaSource,
                "Missing projectEndpointName in projectSchema"
            );
        }

        return null;
    }

    /// <summary>
    /// Gets the apiSchemaVersion from a schema node.
    /// </summary>
    private static string GetApiSchemaVersion(JsonNode node) =>
        node["apiSchemaVersion"]?.GetValue<string>() ?? string.Empty;

    /// <summary>
    /// Gets the projectEndpointName from a schema node.
    /// </summary>
    private static string GetProjectEndpointName(JsonNode node) =>
        node["projectSchema"]?["projectEndpointName"]?.GetValue<string>() ?? string.Empty;

    /// <summary>
    /// Checks for projectEndpointName collisions across all schemas.
    /// Reports all collisions found, not just the first one.
    /// </summary>
    private ApiSchemaNormalizationResult? CheckForEndpointNameCollisions(
        JsonNode coreNode,
        List<(JsonNode Node, string EndpointName, string SchemaSource)> extensions
    )
    {
        var coreEndpointName = GetProjectEndpointName(coreNode);

        var allEndpoints = extensions
            .Select(ext => (EndpointName: ext.EndpointName, SchemaSource: ext.SchemaSource))
            .Prepend((EndpointName: coreEndpointName, SchemaSource: "core"))
            .ToList();

        var collisions = allEndpoints
            .GroupBy(x => x.EndpointName)
            .Where(g => g.Count() > 1)
            .Select(g => new ApiSchemaNormalizationResult.EndpointNameCollision(
                g.Key,
                g.Select(x => x.SchemaSource).ToArray()
            ))
            .ToList();

        if (collisions.Count > 0)
        {
            foreach (var collision in collisions)
            {
                _logger.LogError(
                    "Duplicate projectEndpointName {EndpointName} found in: {Sources}",
                    SanitizeForLog(collision.ProjectEndpointName),
                    string.Join(", ", collision.ConflictingSources.Select(SanitizeForLog))
                );
            }
            return new ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult(collisions);
        }

        return null;
    }

    /// <summary>
    /// Strips OpenAPI payloads from a schema node.
    /// Returns a new node with the following paths removed:
    /// - projectSchema.openApiBaseDocuments
    /// - projectSchema.resourceSchemas[*].openApiFragments
    /// - projectSchema.abstractResources[*].openApiFragment
    /// </summary>
    private static JsonNode StripOpenApiPayloads(JsonNode node)
    {
        var cloned = node.DeepClone();
        var projectSchema = cloned["projectSchema"]?.AsObject();

        if (projectSchema != null)
        {
            projectSchema.Remove("openApiBaseDocuments");

            var resourceSchemas = projectSchema["resourceSchemas"]?.AsObject();
            if (resourceSchemas != null)
            {
                foreach (var (_, resourceSchema) in resourceSchemas)
                {
                    resourceSchema?.AsObject()?.Remove("openApiFragments");
                }
            }

            var abstractResources = projectSchema["abstractResources"]?.AsObject();
            if (abstractResources != null)
            {
                foreach (var (_, abstractResource) in abstractResources)
                {
                    abstractResource?.AsObject()?.Remove("openApiFragment");
                }
            }
        }

        return cloned;
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/)
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c)
                    || c == ' '
                    || c == '_'
                    || c == '-'
                    || c == '.'
                    || c == ':'
                    || c == '/'
                )
                .ToArray()
        );
    }
}
