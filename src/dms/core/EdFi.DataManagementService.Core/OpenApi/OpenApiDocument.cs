// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Provides information from a loaded ApiSchema.json document
/// </summary>
internal class OpenApiDocument(
    JsonNode _coreApiSchemaRootNode,
    JsonNode[] _extensionApiSchemaRootNodes,
    ILogger _logger
)
{
    /// <summary>
    /// Inserts exts from extension OpenAPI fragments into the _ext section of the corresponding
    /// core OpenAPI endpoint.
    /// </summary>
    private void InsertExts(JsonObject extList, JsonNode openApiSpecification)
    {
        foreach ((string componentSchemaName, JsonNode? extObject) in extList)
        {
            if (extObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty exts schema name '{componentSchemaName}'. Extension fragment validation failed?"
                );
            }

            // Get the component.schema location for _ext insert
            // TODO: This is hardcoding EdFi_ prefix - should be in ApiSchema from MetaEd
            JsonObject locationForExt =
                openApiSpecification
                    .SelectNodeFromPath(
                        $"$.components.schemas.EdFi_{componentSchemaName}.properties",
                        _logger
                    )
                    ?.AsObject()
                ?? throw new InvalidOperationException(
                    $"OpenAPI extension fragment expects Core to have '$.components.schemas.EdFi_{componentSchemaName}.properties'. Extension fragment validation failed?"
                );

            // If _ext has already been added by another extension, we don't support a second one
            if (locationForExt["_ext"] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second _ext to '$.components.schemas.EdFi_{componentSchemaName}.properties', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForExt.Add("_ext", extObject.DeepClone());
        }
    }

    /// <summary>
    /// Inserts new endpoint paths from extension OpenAPI fragments into the paths section of the corresponding
    /// core OpenAPI endpoint.
    /// </summary>
    private void InsertNewPaths(JsonObject newPaths, JsonNode openApiSpecification)
    {
        foreach ((string pathName, JsonNode? pathObject) in newPaths)
        {
            if (pathObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newPaths path name '{pathName}'. Extension fragment validation failed?"
                );
            }

            JsonObject locationForPaths = openApiSpecification
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
    private void InsertNewSchemas(JsonObject newSchemas, JsonNode openApiSpecification)
    {
        foreach ((string schemaName, JsonNode? schemaObject) in newSchemas)
        {
            if (schemaObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newSchemas path name '{schemaName}'. Extension fragment validation failed?"
                );
            }

            JsonObject locationForSchemas = openApiSpecification
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
    /// Returns the OpenAPI specification derived from core and extension ApiSchemas
    /// </summary>
    public JsonNode GetDocument()
    {
        ApiSchemaDocument coreApiSchemaDocument = new(_coreApiSchemaRootNode, _logger);

        // Get the core OpenAPI spec
        JsonNode openApiSpecification =
            coreApiSchemaDocument.FindCoreOpenApiSpecification()
            ?? throw new InvalidOperationException("Expected CoreOpenApiSpecification node to exist.");

        // Get each extension OpenAPI fragment to insert into core OpenAPI spec
        foreach (JsonNode extensionApiSchemaRootNode in _extensionApiSchemaRootNodes)
        {
            ApiSchemaDocument extensionApiSchemaDocument = new(extensionApiSchemaRootNode, _logger);
            JsonNode extensionFragments =
                extensionApiSchemaDocument.FindOpenApiExtensionFragments()
                ?? throw new InvalidOperationException("Expected OpenApiExtensionFragments node to exist.");

            InsertExts(
                extensionFragments.SelectRequiredNodeFromPath("$.exts", _logger).AsObject(),
                openApiSpecification
            );

            InsertNewPaths(
                extensionFragments.SelectRequiredNodeFromPath("$.newPaths", _logger).AsObject(),
                openApiSpecification
            );

            InsertNewSchemas(
                extensionFragments.SelectRequiredNodeFromPath("$.newSchemas", _logger).AsObject(),
                openApiSpecification
            );
        }

        return openApiSpecification;
    }
}
