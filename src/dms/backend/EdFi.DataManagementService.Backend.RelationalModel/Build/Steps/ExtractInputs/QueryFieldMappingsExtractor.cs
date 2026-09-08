// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

/// <summary>
/// Extracts normalized <c>queryFieldMapping</c> metadata from a resource schema.
/// </summary>
internal static class QueryFieldMappingsExtractor
{
    public static IReadOnlyDictionary<string, RelationalQueryFieldMapping> ExtractQueryFieldMappings(
        JsonObject resourceSchema,
        QualifiedResourceName resourceName
    )
    {
        ArgumentNullException.ThrowIfNull(resourceSchema);

        var queryFieldMappingObject = resourceSchema["queryFieldMapping"] switch
        {
            JsonObject jsonObject => jsonObject,
            null => null,
            _ => throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + "expected queryFieldMapping to be an object."
            ),
        };

        if (queryFieldMappingObject is null)
        {
            return new Dictionary<string, RelationalQueryFieldMapping>(StringComparer.Ordinal);
        }

        var mappingsByField = queryFieldMappingObject
            .OrderBy(static queryFieldMappingEntry => queryFieldMappingEntry.Key, StringComparer.Ordinal)
            .Select(queryFieldMappingEntry =>
                CreateQueryFieldMapping(
                    resourceName,
                    queryFieldMappingEntry.Key,
                    queryFieldMappingEntry.Value
                )
            )
            .ToFrozenDictionary(
                static queryFieldMapping => queryFieldMapping.QueryFieldName,
                static queryFieldMapping => queryFieldMapping,
                StringComparer.Ordinal
            );

        return mappingsByField;
    }

    private static RelationalQueryFieldMapping CreateQueryFieldMapping(
        QualifiedResourceName resourceName,
        string queryFieldName,
        JsonNode? queryFieldNode
    )
    {
        if (string.IsNullOrWhiteSpace(queryFieldName))
        {
            throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + "queryFieldMapping contains an empty query field name."
            );
        }

        if (queryFieldNode is not JsonArray queryPathArray)
        {
            throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + $"expected queryFieldMapping['{queryFieldName}'] to be an array."
            );
        }

        if (queryPathArray.Count == 0)
        {
            throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + $"expected queryFieldMapping['{queryFieldName}'] to contain at least one path entry."
            );
        }

        var paths = queryPathArray
            .Select(queryPathNode => CreateQueryFieldPath(resourceName, queryFieldName, queryPathNode))
            .ToArray();

        return new RelationalQueryFieldMapping(queryFieldName, paths);
    }

    private static RelationalQueryFieldPath CreateQueryFieldPath(
        QualifiedResourceName resourceName,
        string queryFieldName,
        JsonNode? queryPathNode
    )
    {
        if (queryPathNode is not JsonObject queryPathObject)
        {
            throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + $"expected queryFieldMapping['{queryFieldName}'] entries to be objects."
            );
        }

        var path = queryPathObject["path"] switch
        {
            JsonValue pathValue => pathValue.GetValue<string>(),
            null => throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + $"expected queryFieldMapping['{queryFieldName}'].path to be present."
            ),
            _ => throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + $"expected queryFieldMapping['{queryFieldName}'].path to be a string."
            ),
        };

        var type = queryPathObject["type"] switch
        {
            JsonValue typeValue => typeValue.GetValue<string>(),
            null => throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + $"expected queryFieldMapping['{queryFieldName}'].type to be present."
            ),
            _ => throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + $"expected queryFieldMapping['{queryFieldName}'].type to be a string."
            ),
        };

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new InvalidOperationException(
                $"Malformed ApiSchema for resource '{resourceName.ProjectName}.{resourceName.ResourceName}': "
                    + $"expected queryFieldMapping['{queryFieldName}'].type to be non-empty."
            );
        }

        return new RelationalQueryFieldPath(JsonPathExpressionCompiler.Compile(path), type);
    }
}
