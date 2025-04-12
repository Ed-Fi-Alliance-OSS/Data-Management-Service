// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Provides information from a loaded ApiSchema.json document
/// </summary>
public class OpenApiDocument(ILogger _logger)
{
    /// <summary>
    /// Inserts exts from extension OpenAPI fragments into the _ext section of the corresponding
    /// core OpenAPI endpoint.
    /// </summary>
    private void InsertExts(JsonObject openApiExtensionFragmentList, JsonNode openApiCoreResources)
    {
        foreach ((string componentSchemaName, JsonNode? extObject) in openApiExtensionFragmentList)
        {
            if (extObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty exts schema name '{componentSchemaName}'. Extension fragment validation failed?"
                );
            }

            // Get the component.schema location for _ext insert
            JsonObject locationForExt =
                openApiCoreResources
                    .SelectNodeFromPath($"$.components.schemas.{componentSchemaName}.properties", _logger)
                    ?.AsObject()
                ?? throw new InvalidOperationException(
                    $"OpenAPI extension fragment expects Core to have '$.components.schemas.{componentSchemaName}.properties'. Extension fragment validation failed?"
                );

            // If _ext has already been added by another extension, we don't support a second one
            if (locationForExt["_ext"] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second _ext to '$.components.schemas.{componentSchemaName}.properties', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForExt.Add("_ext", extObject.DeepClone());
        }
    }

    /// <summary>
    /// Inserts new endpoint paths from extension OpenAPI fragments into the paths section of the corresponding
    /// core OpenAPI endpoint.
    /// </summary>
    private void InsertNewPaths(JsonObject newPaths, JsonNode openApiCoreResources)
    {
        foreach ((string pathName, JsonNode? pathObject) in newPaths)
        {
            if (pathObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newPaths path name '{pathName}'. Extension fragment validation failed?"
                );
            }

            JsonObject locationForPaths = openApiCoreResources
                .SelectRequiredNodeFromPath("$.paths", _logger)
                .AsObject();

            // If pathName has already been added by another extension, we don't support a second one
            if (locationForPaths[pathName] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second path '$.paths.{pathName}', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForPaths.Add(pathName, pathObject.DeepClone());
        }
    }

    /// <summary>
    /// Inserts new schema objects from extension OpenAPI fragments into the components.schemas section of the
    /// core OpenAPI specification.
    /// </summary>
    private void InsertNewSchemas(JsonObject newSchemas, JsonNode openApiCoreResources)
    {
        foreach ((string schemaName, JsonNode? schemaObject) in newSchemas)
        {
            if (schemaObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newSchemas path name '{schemaName}'. Extension fragment validation failed?"
                );
            }

            JsonObject locationForSchemas = openApiCoreResources
                .SelectRequiredNodeFromPath("$.components.schemas", _logger)
                .AsObject();

            // If schemaName has already been added by another extension, we don't support a second one
            if (locationForSchemas[schemaName] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second schema '$.components.schemas.{schemaName}', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForSchemas.Add(schemaName, schemaObject.DeepClone());
        }
    }

    /// <summary>
    /// Inserts new global tag objects from extension OpenAPI fragments into the tags section of the
    /// core OpenAPI specification.
    /// </summary>
    private void InsertNewTags(JsonArray newTagObjects, JsonNode openApiCoreResources)
    {
        // This is where the extension tags will be added
        JsonArray globalTags = openApiCoreResources.SelectRequiredNodeFromPath("$.tags", _logger).AsArray();

        // Helper to test for tag uniqueness
        HashSet<string> existingTagNames = [];
        foreach (JsonNode? globalTag in globalTags)
        {
            if (globalTag == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI specification has empty global tag. Extension fragment validation failed?"
                );
            }

            string tagName =
                globalTag["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    $"OpenAPI specification has newTag with no name. Extension fragment validation failed?"
                );
            existingTagNames.Add(tagName);
        }

        foreach (JsonNode? newTagObject in newTagObjects)
        {
            if (newTagObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newTag. Extension fragment validation failed?"
                );
            }

            string tagObjectName =
                newTagObject["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    $"OpenAPI extension fragment has newTag with no name. Extension fragment validation failed?"
                );

            // If tag has already been added by another extension, we don't support a second one
            if (existingTagNames.Contains(tagObjectName))
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second tag named '{tagObjectName}', which is not supported. Extension fragment validation failed?"
                );
            }

            globalTags.Add(newTagObject.DeepClone());
        }
    }

    /// <summary>
    /// Finds the openApiExtensionDescriptors and openApiExtensionResources in extension ApiSchemaDocument.
    /// </summary>
    private List<JsonNode> FindOpenApiExtensionFragments(JsonNode extensionApiSchemaRootNode, string section)
    {
        string[] paths = [$"$.projectSchema.openApiExtension{section}Fragments"];

        List<JsonNode> selectedNodes = new List<JsonNode>();

        // Iterate over the paths and select the nodes
        foreach (var path in paths)
        {
            JsonNode node = extensionApiSchemaRootNode.SelectRequiredNodeFromPath(path, _logger);
            if (node != null)
            {
                selectedNodes.Add(node.DeepClone());
            }
        }

        return selectedNodes;
    }

    public enum DocumentSection
    {
        Resource,
        Descriptor
    }

    /// <summary>
    /// Creates an OpenAPI specification derived from the given core and extension ApiSchemas
    /// </summary>
    public JsonNode CreateDocument(ApiSchemaNodes apiSchemas, DocumentSection documentSection)
    {
        // Get the core OpenAPI spec as a copy since we are going to modify it
        JsonNode openApiSpecification = apiSchemas
            .CoreApiSchemaRootNode.SelectRequiredNodeFromPath(
                $"$.projectSchema.openApiCore{documentSection}s",
                _logger
            )
            .DeepClone();

        #region [DMS-597] Workaround for DMS-628 Descriptor schemas should have `Descriptor` suffix
        if (documentSection == DocumentSection.Descriptor)
        {
            var openApiJsonSpec = openApiSpecification.ToJsonString();
            foreach (var schema in openApiSpecification["components"]!["schemas"]!.AsObject())
            {
                openApiJsonSpec = openApiJsonSpec.Replace(schema.Key, $"{schema.Key}Descriptor");
            }

            openApiSpecification = JsonNode.Parse(openApiJsonSpec)!;
        }
        #endregion

        #region [DMS-597] Workaround for DMS-633 SchoolYearType shouldn't be inlined
        if (documentSection == DocumentSection.Resource)
        {
            var resourceWithInlinedSchoolYearType = openApiSpecification["components"]!["schemas"]!
                .AsObject()
                .Select(schema =>
                {
                    var properties = schema.Value!["properties"]!
                        .AsObject()
                        .Where(property =>
                            property.Key == "schoolYearTypeReference" && property.Value!["properties"] != null
                        )
                        .ToList();

                    return (schema, properties);
                })
                .Where(schema => schema.properties.Any())
                .ToList();

            foreach (var resource in resourceWithInlinedSchoolYearType)
            {
                foreach (var property in resource.properties)
                {
                    resource.schema.Value!["properties"]![property.Key] = JsonNode.Parse(
                        $"{{ \"$ref\": \"#/components/schemas/EdFi_SchoolYearTypeReference\" }}");
                }
            }
        }
        #endregion

        #region Workaround for DMS-627 Array references should not be partially inlined
        if (documentSection == DocumentSection.Resource)
        {
            var resourceWithArrayReferences = openApiSpecification["components"]!["schemas"]!
                .AsObject()
                .Select(schema =>
                {
                    var properties = schema.Value!["properties"]!
                        .AsObject()
                        .Where(property =>
                            property.Value!["type"]?.GetValue<string>() == "array"
                            && property.Value["items"]!["$ref"] == null
                        )
                        .ToList();

                    return (schema, properties);
                })
                .Where(schema => schema.properties.Any())
                .ToList();

            foreach (var resource in resourceWithArrayReferences)
            {
                foreach (var referenceProperty in resource.properties)
                {
                    var newRefName =
                        resource.schema.Key.Replace("EdFi_", "")
                        + referenceProperty.Value!["items"]!["properties"]!.AsObject().ToArray()[0].Value![
                            "$ref"
                        ]!
                            .GetValue<string>()
                            .Replace("#/components/schemas/", "")
                            .Replace("_Reference", "")
                            .Replace("EdFi_", "");

                    openApiSpecification["components"]!["schemas"]![newRefName] = referenceProperty.Value![
                        "items"
                    ]!.DeepClone();

                    resource.schema.Value!["properties"]![referenceProperty.Key]!["items"] = JsonNode.Parse(
                        $"{{ \"$ref\": \"#/components/schemas/{newRefName}\" }}"
                    );
                }
            }
        }
        #endregion

        // Get each extension OpenAPI fragment to insert into core OpenAPI spec
        foreach (JsonNode extensionApiSchemaRootNode in apiSchemas.ExtensionApiSchemaRootNodes)
        {
            List<JsonNode> openApiExtensionFragments = FindOpenApiExtensionFragments(
                extensionApiSchemaRootNode,
                documentSection.ToString()
            );

            foreach (JsonNode openApiExtensionFragment in openApiExtensionFragments)
            {
                InsertExts(
                    openApiExtensionFragment.SelectRequiredNodeFromPath("$.exts", _logger).AsObject(),
                    openApiSpecification
                );

                InsertNewPaths(
                    openApiExtensionFragment.SelectRequiredNodeFromPath("$.newPaths", _logger).AsObject(),
                    openApiSpecification
                );

                InsertNewSchemas(
                    openApiExtensionFragment.SelectRequiredNodeFromPath("$.newSchemas", _logger).AsObject(),
                    openApiSpecification
                );

                InsertNewTags(
                    openApiExtensionFragment.SelectRequiredNodeFromPath("$.newTags", _logger).AsArray(),
                    openApiSpecification
                );
            }
        }

        return openApiSpecification;
    }
}
