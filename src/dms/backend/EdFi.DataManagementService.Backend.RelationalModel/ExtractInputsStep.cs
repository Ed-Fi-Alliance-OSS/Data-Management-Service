// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public sealed class ExtractInputsStep : IRelationalModelBuilderStep
{
    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var apiSchemaRoot =
            context.ApiSchemaRoot ?? throw new InvalidOperationException("ApiSchema root must be provided.");

        var projectSchema = RequireObject(apiSchemaRoot["projectSchema"], "projectSchema");
        var projectName = RequireString(projectSchema, "projectName");
        var projectEndpointName = RequireString(projectSchema, "projectEndpointName");
        var projectVersion = RequireString(projectSchema, "projectVersion");

        var resourceEndpointName = context.ResourceEndpointName;
        if (string.IsNullOrWhiteSpace(resourceEndpointName))
        {
            throw new InvalidOperationException("Resource endpoint name must be provided.");
        }

        var resourceSchemas = RequireObject(
            projectSchema["resourceSchemas"],
            "projectSchema.resourceSchemas"
        );

        if (
            !resourceSchemas.TryGetPropertyValue(resourceEndpointName, out var resourceSchemaNode)
            || resourceSchemaNode is null
        )
        {
            throw new InvalidOperationException(
                $"Resource schema '{resourceEndpointName}' not found in project schema."
            );
        }

        if (resourceSchemaNode is not JsonObject resourceSchema)
        {
            throw new InvalidOperationException(
                $"Expected resource schema '{resourceEndpointName}' to be an object."
            );
        }

        var resourceName = RequireString(resourceSchema, "resourceName");
        var isDescriptor =
            resourceSchema["isDescriptor"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isDescriptor to be on ResourceSchema, invalid ApiSchema."
            );
        var jsonSchemaForInsert =
            resourceSchema["jsonSchemaForInsert"]
            ?? throw new InvalidOperationException(
                "Expected jsonSchemaForInsert to be on ResourceSchema, invalid ApiSchema."
            );

        var identityJsonPaths = ExtractIdentityJsonPaths(resourceSchema);
        var descriptorPathsByJsonPath = ExtractDescriptorPaths(resourceSchema, projectSchema, projectName);
        var decimalPropertyValidationInfosByPath = ExtractDecimalPropertyValidationInfos(resourceSchema);
        var stringMaxLengthOmissionPaths = ExtractStringMaxLengthOmissionPaths(resourceSchema);

        context.ProjectName = projectName;
        context.ProjectEndpointName = projectEndpointName;
        context.ProjectVersion = projectVersion;
        context.ResourceName = resourceName;
        context.IsDescriptorResource = isDescriptor;
        context.JsonSchemaForInsert = jsonSchemaForInsert;
        context.IdentityJsonPaths = identityJsonPaths;
        context.DescriptorPathsByJsonPath = descriptorPathsByJsonPath;
        context.DecimalPropertyValidationInfosByPath = decimalPropertyValidationInfosByPath;
        context.StringMaxLengthOmissionPaths = stringMaxLengthOmissionPaths;
    }

    private static IReadOnlyList<JsonPathExpression> ExtractIdentityJsonPaths(JsonObject resourceSchema)
    {
        var identityJsonPaths = RequireArray(resourceSchema, "identityJsonPaths");
        List<JsonPathExpression> compiledPaths = new(identityJsonPaths.Count);

        foreach (var identityJsonPath in identityJsonPaths)
        {
            if (identityJsonPath is null)
            {
                throw new InvalidOperationException(
                    "Expected identityJsonPaths to not contain null entries, invalid ApiSchema."
                );
            }

            var identityPath = identityJsonPath.GetValue<string>();
            compiledPaths.Add(JsonPathExpressionCompiler.Compile(identityPath));
        }

        return compiledPaths.ToArray();
    }

    private static Dictionary<string, DescriptorPathInfo> ExtractDescriptorPaths(
        JsonObject resourceSchema,
        JsonObject projectSchema,
        string projectName
    )
    {
        var descriptorPathsByResourceName = BuildDescriptorPathsByResource(projectSchema, projectName);
        var resourceName = RequireString(resourceSchema, "resourceName");
        var resourceKey = new QualifiedResourceName(projectName, resourceName);

        if (!descriptorPathsByResourceName.TryGetValue(resourceKey, out var descriptorPaths))
        {
            return new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);
        }

        return new Dictionary<string, DescriptorPathInfo>(descriptorPaths, StringComparer.Ordinal);
    }

    private sealed record ReferenceJsonPathInfo(
        QualifiedResourceName ReferencedResource,
        JsonPathExpression IdentityPath,
        JsonPathExpression ReferencePath
    );

    private static Dictionary<
        QualifiedResourceName,
        Dictionary<string, DescriptorPathInfo>
    > BuildDescriptorPathsByResource(JsonObject projectSchema, string projectName)
    {
        Dictionary<QualifiedResourceName, Dictionary<string, DescriptorPathInfo>> descriptorPathsByResource =
            new();
        Dictionary<QualifiedResourceName, List<ReferenceJsonPathInfo>> referenceJsonPathsByResource = new();

        var resourceSchemas = RequireObject(
            projectSchema["resourceSchemas"],
            "projectSchema.resourceSchemas"
        );
        AddResourceDescriptors(
            resourceSchemas,
            projectName,
            descriptorPathsByResource,
            referenceJsonPathsByResource,
            "projectSchema.resourceSchemas"
        );

        if (projectSchema["abstractResources"] is JsonObject abstractResources)
        {
            AddResourceDescriptors(
                abstractResources,
                projectName,
                descriptorPathsByResource,
                referenceJsonPathsByResource,
                "projectSchema.abstractResources"
            );
        }

        var updated = true;
        while (updated)
        {
            updated = false;

            foreach (var resourceEntry in referenceJsonPathsByResource)
            {
                var resourceKey = resourceEntry.Key;
                var descriptorPaths = descriptorPathsByResource[resourceKey];

                foreach (var referenceInfo in resourceEntry.Value)
                {
                    if (
                        !descriptorPathsByResource.TryGetValue(
                            referenceInfo.ReferencedResource,
                            out var referencedDescriptorPaths
                        )
                    )
                    {
                        continue;
                    }

                    if (
                        !referencedDescriptorPaths.TryGetValue(
                            referenceInfo.IdentityPath.Canonical,
                            out var descriptorPathInfo
                        )
                    )
                    {
                        continue;
                    }

                    var referencePath = referenceInfo.ReferencePath;

                    if (descriptorPaths.TryGetValue(referencePath.Canonical, out var existingInfo))
                    {
                        if (existingInfo.DescriptorResource != descriptorPathInfo.DescriptorResource)
                        {
                            throw new InvalidOperationException(
                                $"Descriptor path '{referencePath.Canonical}' is already defined."
                            );
                        }

                        continue;
                    }

                    descriptorPaths.Add(
                        referencePath.Canonical,
                        new DescriptorPathInfo(referencePath, descriptorPathInfo.DescriptorResource)
                    );
                    updated = true;
                }
            }
        }

        return descriptorPathsByResource;
    }

    private static void AddResourceDescriptors(
        JsonObject resourceSchemas,
        string projectName,
        Dictionary<QualifiedResourceName, Dictionary<string, DescriptorPathInfo>> descriptorPathsByResource,
        Dictionary<QualifiedResourceName, List<ReferenceJsonPathInfo>> referenceJsonPathsByResource,
        string resourceSchemasPath
    )
    {
        foreach (var resourceSchemaEntry in resourceSchemas)
        {
            if (resourceSchemaEntry.Value is null)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be non-null, invalid ApiSchema."
                );
            }

            if (resourceSchemaEntry.Value is not JsonObject resourceSchema)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be objects, invalid ApiSchema."
                );
            }

            var resourceName = GetResourceName(resourceSchemaEntry.Key, resourceSchema);
            var qualifiedResourceName = new QualifiedResourceName(projectName, resourceName);

            if (descriptorPathsByResource.ContainsKey(qualifiedResourceName))
            {
                throw new InvalidOperationException(
                    $"Descriptor paths for resource '{resourceName}' are already defined."
                );
            }

            descriptorPathsByResource[qualifiedResourceName] = ExtractDescriptorPathsForResource(
                resourceSchema,
                projectName
            );
            referenceJsonPathsByResource[qualifiedResourceName] = ExtractReferenceJsonPaths(resourceSchema);
        }
    }

    private static string GetResourceName(string resourceKey, JsonObject resourceSchema)
    {
        if (resourceSchema.TryGetPropertyValue("resourceName", out var resourceNameNode))
        {
            if (resourceNameNode is null)
            {
                throw new InvalidOperationException(
                    "Expected resourceName to be present, invalid ApiSchema."
                );
            }

            if (resourceNameNode is not JsonValue resourceNameValue)
            {
                throw new InvalidOperationException(
                    "Expected resourceName to be a string, invalid ApiSchema."
                );
            }

            var resourceName = resourceNameValue.GetValue<string>();
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                throw new InvalidOperationException(
                    "Expected resourceName to be non-empty, invalid ApiSchema."
                );
            }

            return resourceName;
        }

        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            throw new InvalidOperationException("Expected resourceName to be present, invalid ApiSchema.");
        }

        return resourceKey;
    }

    private static Dictionary<string, DescriptorPathInfo> ExtractDescriptorPathsForResource(
        JsonObject resourceSchema,
        string projectName
    )
    {
        if (resourceSchema["documentPathsMapping"] is not JsonObject documentPathsMapping)
        {
            return ExtractDescriptorPathsFromIdentityJsonPaths(resourceSchema, projectName);
        }
        Dictionary<string, DescriptorPathInfo> descriptorPathsByJsonPath = new(StringComparer.Ordinal);

        foreach (var mapping in documentPathsMapping)
        {
            if (mapping.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be non-null, invalid ApiSchema."
                );
            }

            if (mapping.Value is not JsonObject mappingObject)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be objects, invalid ApiSchema."
                );
            }

            var isReference =
                mappingObject["isReference"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isReference to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (!isReference)
            {
                continue;
            }

            var isDescriptor =
                mappingObject["isDescriptor"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isDescriptor to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (!isDescriptor)
            {
                continue;
            }

            var descriptorPath = RequireString(mappingObject, "path");
            var descriptorProjectName = RequireString(mappingObject, "projectName");
            var descriptorResourceName = RequireString(mappingObject, "resourceName");
            var descriptorJsonPath = JsonPathExpressionCompiler.Compile(descriptorPath);

            if (
                !descriptorPathsByJsonPath.TryAdd(
                    descriptorJsonPath.Canonical,
                    new DescriptorPathInfo(
                        descriptorJsonPath,
                        new QualifiedResourceName(descriptorProjectName, descriptorResourceName)
                    )
                )
            )
            {
                throw new InvalidOperationException(
                    $"Descriptor path '{descriptorJsonPath.Canonical}' is already defined."
                );
            }
        }

        return descriptorPathsByJsonPath;
    }

    private static Dictionary<string, DescriptorPathInfo> ExtractDescriptorPathsFromIdentityJsonPaths(
        JsonObject resourceSchema,
        string projectName
    )
    {
        if (resourceSchema["identityJsonPaths"] is not JsonArray identityJsonPaths)
        {
            return new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);
        }

        Dictionary<string, DescriptorPathInfo> descriptorPathsByJsonPath = new(StringComparer.Ordinal);

        foreach (var identityJsonPath in identityJsonPaths)
        {
            if (identityJsonPath is null)
            {
                throw new InvalidOperationException(
                    "Expected identityJsonPaths to not contain null entries, invalid ApiSchema."
                );
            }

            var identityPath = JsonPathExpressionCompiler.Compile(identityJsonPath.GetValue<string>());

            if (!TryGetDescriptorResourceName(identityPath, out var descriptorResourceName))
            {
                continue;
            }

            var descriptorResource = new QualifiedResourceName(projectName, descriptorResourceName);

            if (
                !descriptorPathsByJsonPath.TryAdd(
                    identityPath.Canonical,
                    new DescriptorPathInfo(identityPath, descriptorResource)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Descriptor path '{identityPath.Canonical}' is already defined."
                );
            }
        }

        return descriptorPathsByJsonPath;
    }

    private static bool TryGetDescriptorResourceName(
        JsonPathExpression identityPath,
        out string descriptorResourceName
    )
    {
        descriptorResourceName = string.Empty;

        if (identityPath.Segments.Count == 0)
        {
            return false;
        }

        if (identityPath.Segments[^1] is not JsonPathSegment.Property property)
        {
            return false;
        }

        if (!property.Name.EndsWith("Descriptor", StringComparison.Ordinal))
        {
            return false;
        }

        descriptorResourceName = RelationalNameConventions.ToPascalCase(property.Name);
        return true;
    }

    private static List<ReferenceJsonPathInfo> ExtractReferenceJsonPaths(JsonObject resourceSchema)
    {
        if (resourceSchema["documentPathsMapping"] is not JsonObject documentPathsMapping)
        {
            return [];
        }
        List<ReferenceJsonPathInfo> referenceJsonPaths = new();

        foreach (var mapping in documentPathsMapping)
        {
            if (mapping.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be non-null, invalid ApiSchema."
                );
            }

            if (mapping.Value is not JsonObject mappingObject)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be objects, invalid ApiSchema."
                );
            }

            var isReference =
                mappingObject["isReference"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isReference to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (!isReference)
            {
                continue;
            }

            if (!mappingObject.TryGetPropertyValue("referenceJsonPaths", out var referenceJsonPathsNode))
            {
                continue;
            }

            if (referenceJsonPathsNode is null)
            {
                continue;
            }

            if (referenceJsonPathsNode is not JsonArray referenceJsonPathsArray)
            {
                throw new InvalidOperationException(
                    "Expected referenceJsonPaths to be an array on documentPathsMapping entry, invalid ApiSchema."
                );
            }

            var referencedProjectName = RequireString(mappingObject, "projectName");
            var referencedResourceName = RequireString(mappingObject, "resourceName");
            var referencedResource = new QualifiedResourceName(referencedProjectName, referencedResourceName);

            foreach (var referenceJsonPath in referenceJsonPathsArray)
            {
                if (referenceJsonPath is null)
                {
                    throw new InvalidOperationException(
                        "Expected referenceJsonPaths to not contain null entries, invalid ApiSchema."
                    );
                }

                if (referenceJsonPath is not JsonObject referenceJsonPathObject)
                {
                    throw new InvalidOperationException(
                        "Expected referenceJsonPaths entries to be objects, invalid ApiSchema."
                    );
                }

                var identityJsonPath = RequireString(referenceJsonPathObject, "identityJsonPath");
                var referenceJsonPathValue = RequireString(referenceJsonPathObject, "referenceJsonPath");

                referenceJsonPaths.Add(
                    new ReferenceJsonPathInfo(
                        referencedResource,
                        JsonPathExpressionCompiler.Compile(identityJsonPath),
                        JsonPathExpressionCompiler.Compile(referenceJsonPathValue)
                    )
                );
            }
        }

        return referenceJsonPaths;
    }

    private static HashSet<string> ExtractStringMaxLengthOmissionPaths(JsonObject resourceSchema)
    {
        if (resourceSchema["flatteningMetadata"] is not JsonObject flatteningMetadata)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (flatteningMetadata["table"] is not JsonObject table)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        HashSet<string> omissionPaths = new(StringComparer.Ordinal);
        CollectStringMaxLengthOmissionPaths(table, omissionPaths);

        return omissionPaths;
    }

    private static void CollectStringMaxLengthOmissionPaths(
        JsonObject tableNode,
        HashSet<string> omissionPaths
    )
    {
        if (tableNode["columns"] is JsonArray columns)
        {
            foreach (var column in columns)
            {
                if (column is null)
                {
                    throw new InvalidOperationException(
                        "Expected flatteningMetadata.table.columns to not contain null entries."
                    );
                }

                if (column is not JsonObject columnObject)
                {
                    throw new InvalidOperationException(
                        "Expected flatteningMetadata.table.columns entries to be objects."
                    );
                }

                if (!columnObject.TryGetPropertyValue("columnType", out var columnTypeNode))
                {
                    continue;
                }

                if (columnTypeNode is null)
                {
                    continue;
                }

                if (columnTypeNode is not JsonValue columnTypeValue)
                {
                    throw new InvalidOperationException(
                        "Expected flatteningMetadata.table.columns.columnType to be a string."
                    );
                }

                var columnType = columnTypeValue.GetValue<string>();

                if (!columnObject.TryGetPropertyValue("jsonPath", out var jsonPathNode))
                {
                    continue;
                }

                if (jsonPathNode is null)
                {
                    continue;
                }

                if (jsonPathNode is not JsonValue jsonPathValue)
                {
                    throw new InvalidOperationException(
                        "Expected flatteningMetadata.table.columns.jsonPath to be a string."
                    );
                }

                var jsonPath = jsonPathValue.GetValue<string>();

                var hasMaxLength =
                    columnObject.TryGetPropertyValue("maxLength", out var maxLengthNode)
                    && maxLengthNode is not null;

                if (
                    string.Equals(columnType, "duration", StringComparison.Ordinal)
                    || string.Equals(columnType, "enumeration", StringComparison.Ordinal)
                    || (string.Equals(columnType, "string", StringComparison.Ordinal) && !hasMaxLength)
                )
                {
                    var compiledPath = JsonPathExpressionCompiler.Compile(jsonPath);
                    omissionPaths.Add(compiledPath.Canonical);
                }
            }
        }

        if (tableNode["childTables"] is not JsonArray childTables)
        {
            return;
        }

        foreach (var childTable in childTables)
        {
            if (childTable is null)
            {
                throw new InvalidOperationException(
                    "Expected flatteningMetadata.table.childTables to not contain null entries."
                );
            }

            if (childTable is not JsonObject childTableObject)
            {
                throw new InvalidOperationException(
                    "Expected flatteningMetadata.table.childTables entries to be objects."
                );
            }

            CollectStringMaxLengthOmissionPaths(childTableObject, omissionPaths);
        }
    }

    private static Dictionary<string, DecimalPropertyValidationInfo> ExtractDecimalPropertyValidationInfos(
        JsonObject resourceSchema
    )
    {
        if (resourceSchema["decimalPropertyValidationInfos"] is not JsonArray decimalInfos)
        {
            return new Dictionary<string, DecimalPropertyValidationInfo>(StringComparer.Ordinal);
        }

        Dictionary<string, DecimalPropertyValidationInfo> decimalInfosByPath = new(StringComparer.Ordinal);

        foreach (var decimalInfo in decimalInfos)
        {
            if (decimalInfo is null)
            {
                throw new InvalidOperationException(
                    "Expected decimalPropertyValidationInfos to not contain null entries, invalid ApiSchema."
                );
            }

            if (decimalInfo is not JsonObject decimalInfoObject)
            {
                throw new InvalidOperationException(
                    "Expected decimalPropertyValidationInfos entries to be objects, invalid ApiSchema."
                );
            }

            var decimalPath = RequireString(decimalInfoObject, "path");
            var totalDigits = decimalInfoObject["totalDigits"]?.GetValue<short?>();
            var decimalPlaces = decimalInfoObject["decimalPlaces"]?.GetValue<short?>();
            var decimalJsonPath = JsonPathExpressionCompiler.Compile(decimalPath);

            if (
                !decimalInfosByPath.TryAdd(
                    decimalJsonPath.Canonical,
                    new DecimalPropertyValidationInfo(decimalJsonPath, totalDigits, decimalPlaces)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Decimal validation info for '{decimalJsonPath.Canonical}' is already defined."
                );
            }
        }

        return decimalInfosByPath;
    }

    private static JsonObject RequireObject(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be an object, invalid ApiSchema."
            ),
        };
    }

    private static JsonArray RequireArray(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonArray jsonArray => jsonArray,
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be an array, invalid ApiSchema."
            ),
        };
    }

    private static string RequireString(JsonObject node, string propertyName)
    {
        var value = node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string, invalid ApiSchema."
            ),
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be non-empty, invalid ApiSchema."
            );
        }

        return value;
    }
}
