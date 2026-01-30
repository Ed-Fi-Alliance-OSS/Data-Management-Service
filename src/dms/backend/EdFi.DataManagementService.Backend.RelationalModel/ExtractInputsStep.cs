// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Extracts the API schema inputs required to build a relational resource model and populates the
/// <see cref="RelationalModelBuilderContext"/> with normalized values and precompiled paths.
/// </summary>
public sealed class ExtractInputsStep : IRelationalModelBuilderStep
{
    /// <summary>
    /// Reads <see cref="RelationalModelBuilderContext.ApiSchemaRoot"/> and the current resource endpoint
    /// name, then populates the context with project metadata, resource metadata, and derived path maps
    /// consumed by subsequent relational model builder steps.
    /// </summary>
    /// <param name="context">The builder context holding the API schema and target resource endpoint.</param>
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
        var descriptorPathsByJsonPath = context.DescriptorPathSource switch
        {
            DescriptorPathSource.Precomputed => context.DescriptorPathsByJsonPath,
            _ => ExtractDescriptorPaths(resourceSchema, projectSchema, projectName),
        };
        var decimalPropertyValidationInfosByPath = ExtractDecimalPropertyValidationInfos(resourceSchema);
        var stringMaxLengthOmissionPaths = new HashSet<string>(StringComparer.Ordinal);

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

    /// <summary>
    /// Compiles identity JSON path strings defined by a resource schema into canonical
    /// <see cref="JsonPathExpression"/> instances.
    /// </summary>
    /// <param name="resourceSchema">The resource schema containing <c>identityJsonPaths</c>.</param>
    /// <returns>The compiled identity paths.</returns>
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

    /// <summary>
    /// Resolves descriptor reference paths for the current resource, using the project schema to locate
    /// descriptor mappings and reference-based propagation rules.
    /// </summary>
    /// <param name="resourceSchema">The resource schema for the current resource.</param>
    /// <param name="projectSchema">The project schema containing all resource schemas.</param>
    /// <param name="projectName">The current project name.</param>
    /// <returns>A mapping of canonical JSON path to descriptor path information.</returns>
    private static Dictionary<string, DescriptorPathInfo> ExtractDescriptorPaths(
        JsonObject resourceSchema,
        JsonObject projectSchema,
        string projectName
    )
    {
        var descriptorPathsByResourceName = DescriptorPathInference.BuildDescriptorPathsByResource(
            new[] { new DescriptorPathInference.ProjectDescriptorSchema(projectName, projectSchema) }
        );
        var resourceName = RequireString(resourceSchema, "resourceName");
        var resourceKey = new QualifiedResourceName(projectName, resourceName);

        if (!descriptorPathsByResourceName.TryGetValue(resourceKey, out var descriptorPaths))
        {
            return new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);
        }

        return new Dictionary<string, DescriptorPathInfo>(descriptorPaths, StringComparer.Ordinal);
    }

    /// <summary>
    /// Extracts decimal validation metadata from <c>decimalPropertyValidationInfos</c>, keyed by the
    /// canonical JSON path.
    /// </summary>
    /// <param name="resourceSchema">The resource schema containing decimal validation metadata.</param>
    /// <returns>A mapping of canonical JSON path to decimal validation information.</returns>
    private static Dictionary<string, DecimalPropertyValidationInfo> ExtractDecimalPropertyValidationInfos(
        JsonObject resourceSchema
    )
    {
        Dictionary<string, DecimalPropertyValidationInfo> decimalInfosByPath = new(StringComparer.Ordinal);

        if (resourceSchema["decimalPropertyValidationInfos"] is JsonArray decimalInfos)
        {
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
        }

        return decimalInfosByPath;
    }

    /// <summary>
    /// Ensures a node is a <see cref="JsonObject"/> and throws a schema validation exception otherwise.
    /// </summary>
    /// <param name="node">The node to validate.</param>
    /// <param name="propertyName">The schema property path used for exception messages.</param>
    /// <returns>The validated object.</returns>
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

    /// <summary>
    /// Ensures a named property on an object is a <see cref="JsonArray"/> and throws a schema validation
    /// exception otherwise.
    /// </summary>
    /// <param name="node">The node holding the property.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The validated array.</returns>
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

    /// <summary>
    /// Ensures a named property on an object is a non-empty string and throws a schema validation
    /// exception otherwise.
    /// </summary>
    /// <param name="node">The node holding the property.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The validated string value.</returns>
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
