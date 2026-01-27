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
        var descriptorPathsByResourceName = BuildDescriptorPathsByResource(projectSchema, projectName);
        var resourceName = RequireString(resourceSchema, "resourceName");
        var resourceKey = new QualifiedResourceName(projectName, resourceName);

        if (!descriptorPathsByResourceName.TryGetValue(resourceKey, out var descriptorPaths))
        {
            return new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);
        }

        return new Dictionary<string, DescriptorPathInfo>(descriptorPaths, StringComparer.Ordinal);
    }

    /// <summary>
    /// Represents a reference mapping used to propagate descriptor paths from a referenced resource to a
    /// reference JSON path in the current resource.
    /// </summary>
    private sealed record ReferenceJsonPathInfo(
        QualifiedResourceName ReferencedResource,
        JsonPathExpression IdentityPath,
        JsonPathExpression ReferencePath
    );

    /// <summary>
    /// Builds descriptor path maps for all resources in the project schema, including abstract resources,
    /// and then iteratively propagates descriptor paths through reference mappings until no new paths are
    /// discovered.
    /// </summary>
    /// <param name="projectSchema">The project schema containing resource definitions.</param>
    /// <param name="projectName">The current project name.</param>
    /// <returns>
    /// A mapping from qualified resource name to a dictionary of canonical JSON path to descriptor path
    /// information for that resource.
    /// </returns>
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

    /// <summary>
    /// Adds descriptor path mappings and reference path information for each resource schema entry found
    /// in a schema collection (e.g., <c>resourceSchemas</c> or <c>abstractResources</c>).
    /// </summary>
    /// <param name="resourceSchemas">The collection of resource schemas to process.</param>
    /// <param name="projectName">The current project name.</param>
    /// <param name="descriptorPathsByResource">Target dictionary to populate with descriptor paths.</param>
    /// <param name="referenceJsonPathsByResource">
    /// Target dictionary to populate with reference mappings.
    /// </param>
    /// <param name="resourceSchemasPath">A schema path used for exception messages.</param>
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

    /// <summary>
    /// Determines a resource name for a schema entry by preferring the explicit <c>resourceName</c>
    /// property and falling back to the schema dictionary key.
    /// </summary>
    /// <param name="resourceKey">The key used in the resource schema dictionary.</param>
    /// <param name="resourceSchema">The resource schema object.</param>
    /// <returns>The resolved resource name.</returns>
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

    /// <summary>
    /// Extracts descriptor reference paths for a resource schema using <c>documentPathsMapping</c> when
    /// available; otherwise infers descriptor paths from <c>identityJsonPaths</c>.
    /// </summary>
    /// <param name="resourceSchema">The resource schema to inspect.</param>
    /// <param name="projectName">The current project name.</param>
    /// <returns>A mapping of canonical JSON path to descriptor path information.</returns>
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

    /// <summary>
    /// Infers descriptor paths from <c>identityJsonPaths</c> by selecting identity paths whose terminal
    /// property name ends with <c>Descriptor</c>.
    /// </summary>
    /// <param name="resourceSchema">The resource schema to inspect.</param>
    /// <param name="projectName">The current project name used to qualify descriptor resources.</param>
    /// <returns>A mapping of canonical identity JSON path to descriptor path information.</returns>
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

    /// <summary>
    /// Attempts to infer a descriptor resource name from an identity path by inspecting its last segment.
    /// </summary>
    /// <param name="identityPath">The identity path to inspect.</param>
    /// <param name="descriptorResourceName">
    /// When this method returns <see langword="true"/>, contains the inferred descriptor resource name.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a descriptor resource name can be inferred; otherwise
    /// <see langword="false"/>.
    /// </returns>
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

    /// <summary>
    /// Extracts reference identity and reference JSON paths from <c>documentPathsMapping</c> entries,
    /// producing a set of reference mappings used for descriptor path propagation.
    /// </summary>
    /// <param name="resourceSchema">The resource schema containing <c>documentPathsMapping</c>.</param>
    /// <returns>The extracted reference mappings.</returns>
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
                    "Expected referenceJsonPaths to be an array on documentPathsMapping entry, "
                        + "invalid ApiSchema."
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

    /// <summary>
    /// Extracts canonical JSON paths for properties whose string-length constraints should be omitted
    /// based on <c>flatteningMetadata</c> column type and max-length information.
    /// </summary>
    /// <param name="resourceSchema">The resource schema containing <c>flatteningMetadata</c>.</param>
    /// <returns>A set of canonical JSON paths where string max length should be omitted.</returns>
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

    /// <summary>
    /// Recursively traverses a <c>flatteningMetadata</c> table node and accumulates omission paths for
    /// qualifying column definitions, including nested child tables.
    /// </summary>
    /// <param name="tableNode">The flattening metadata table node to inspect.</param>
    /// <param name="omissionPaths">The target set to receive omission paths.</param>
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
