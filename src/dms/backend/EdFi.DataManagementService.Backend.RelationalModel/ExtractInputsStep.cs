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
        var jsonSchemaForInsert =
            resourceSchema["jsonSchemaForInsert"]
            ?? throw new InvalidOperationException(
                "Expected jsonSchemaForInsert to be on ResourceSchema, invalid ApiSchema."
            );

        var identityJsonPaths = ExtractIdentityJsonPaths(resourceSchema);
        var descriptorPathsByJsonPath = ExtractDescriptorPaths(resourceSchema);
        var decimalPropertyValidationInfosByPath = ExtractDecimalPropertyValidationInfos(resourceSchema);

        context.ProjectName = projectName;
        context.ProjectEndpointName = projectEndpointName;
        context.ProjectVersion = projectVersion;
        context.ResourceName = resourceName;
        context.JsonSchemaForInsert = jsonSchemaForInsert;
        context.IdentityJsonPaths = identityJsonPaths;
        context.DescriptorPathsByJsonPath = descriptorPathsByJsonPath;
        context.DecimalPropertyValidationInfosByPath = decimalPropertyValidationInfosByPath;
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

    private static Dictionary<string, DescriptorPathInfo> ExtractDescriptorPaths(JsonObject resourceSchema)
    {
        var documentPathsMapping = RequireObject(
            resourceSchema["documentPathsMapping"],
            "documentPathsMapping"
        );
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
