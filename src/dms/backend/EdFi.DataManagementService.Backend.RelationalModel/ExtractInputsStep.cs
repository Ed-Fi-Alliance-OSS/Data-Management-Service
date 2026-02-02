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

        var identityJsonPaths = ExtractIdentityJsonPaths(resourceSchema, projectName, resourceName);
        var identityJsonPathSet = new HashSet<string>(
            identityJsonPaths.Select(path => path.Canonical),
            StringComparer.Ordinal
        );
        var allowIdentityUpdates = RequireBoolean(resourceSchema, "allowIdentityUpdates");
        var documentPathsMapping = ExtractDocumentReferenceMappings(
            resourceSchema,
            projectName,
            resourceName,
            identityJsonPathSet
        );
        var arrayUniquenessConstraints = ExtractArrayUniquenessConstraints(
            resourceSchema,
            projectName,
            resourceName
        );
        var referenceNameOverrides = ExtractReferenceNameOverrides(
            resourceSchema,
            documentPathsMapping.ReferenceObjectPaths,
            projectName,
            resourceName
        );
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
        context.AllowIdentityUpdates = allowIdentityUpdates;
        context.DocumentReferenceMappings = documentPathsMapping.ReferenceMappings;
        context.ArrayUniquenessConstraints = arrayUniquenessConstraints;
        context.ReferenceNameOverridesByPath = referenceNameOverrides;
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
    private static IReadOnlyList<JsonPathExpression> ExtractIdentityJsonPaths(
        JsonObject resourceSchema,
        string projectName,
        string resourceName
    )
    {
        var identityJsonPaths = RequireArray(resourceSchema, "identityJsonPaths");
        List<JsonPathExpression> compiledPaths = new(identityJsonPaths.Count);
        HashSet<string> seenPaths = new(StringComparer.Ordinal);
        HashSet<string> duplicatePaths = new(StringComparer.Ordinal);

        foreach (var identityJsonPath in identityJsonPaths)
        {
            if (identityJsonPath is null)
            {
                throw new InvalidOperationException(
                    "Expected identityJsonPaths to not contain null entries, invalid ApiSchema."
                );
            }

            var identityPath = identityJsonPath.GetValue<string>();
            var compiledPath = JsonPathExpressionCompiler.Compile(identityPath);
            compiledPaths.Add(compiledPath);

            if (!seenPaths.Add(compiledPath.Canonical))
            {
                duplicatePaths.Add(compiledPath.Canonical);
            }
        }

        if (duplicatePaths.Count > 0)
        {
            var duplicates = string.Join(", ", duplicatePaths.OrderBy(path => path, StringComparer.Ordinal));

            throw new InvalidOperationException(
                $"identityJsonPaths on resource '{projectName}:{resourceName}' contains duplicate JSONPaths: {duplicates}."
            );
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
    /// Extracts document reference mappings and validates identity component usage.
    /// </summary>
    private static DocumentPathsMappingResult ExtractDocumentReferenceMappings(
        JsonObject resourceSchema,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths
    )
    {
        JsonObject documentPathsMapping = new();

        if (resourceSchema.TryGetPropertyValue("documentPathsMapping", out var documentPathsMappingNode))
        {
            if (documentPathsMappingNode is null)
            {
                documentPathsMapping = new JsonObject();
            }
            else if (documentPathsMappingNode is JsonObject mappingObject)
            {
                documentPathsMapping = mappingObject;
            }
            else
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping to be an object, invalid ApiSchema."
                );
            }
        }

        List<DocumentReferenceMapping> referenceMappings = [];
        HashSet<string> mappedIdentityPaths = new(StringComparer.Ordinal);
        HashSet<string> referenceObjectPaths = new(StringComparer.Ordinal);

        foreach (var mapping in documentPathsMapping.OrderBy(entry => entry.Key, StringComparer.Ordinal))
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
            var isPartOfIdentity = TryGetOptionalBoolean(mappingObject, "isPartOfIdentity");

            if (!isReference)
            {
                var path = RequireString(mappingObject, "path");
                var pathExpression = JsonPathExpressionCompiler.Compile(path);
                var pathIsPartOfIdentity = identityJsonPaths.Contains(pathExpression.Canonical);
                _ = ResolveIsPartOfIdentity(
                    mapping.Key,
                    projectName,
                    resourceName,
                    isPartOfIdentity,
                    pathIsPartOfIdentity,
                    new[] { pathExpression.Canonical }
                );

                mappedIdentityPaths.Add(pathExpression.Canonical);
                continue;
            }

            var isDescriptor =
                mappingObject["isDescriptor"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isDescriptor to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (isDescriptor)
            {
                var path = RequireString(mappingObject, "path");
                var pathExpression = JsonPathExpressionCompiler.Compile(path);
                var descriptorIsPartOfIdentity = identityJsonPaths.Contains(pathExpression.Canonical);
                _ = ResolveIsPartOfIdentity(
                    mapping.Key,
                    projectName,
                    resourceName,
                    isPartOfIdentity,
                    descriptorIsPartOfIdentity,
                    new[] { pathExpression.Canonical }
                );

                mappedIdentityPaths.Add(pathExpression.Canonical);
                continue;
            }

            var referenceJsonPathsNode = GetReferenceJsonPathsNode(
                mapping.Key,
                mappingObject,
                projectName,
                resourceName
            );
            var referenceJsonPaths = ExtractReferenceJsonPaths(
                mapping.Key,
                referenceJsonPathsNode,
                projectName,
                resourceName,
                out var referenceObjectPath
            );

            foreach (var referenceJsonPath in referenceJsonPaths)
            {
                mappedIdentityPaths.Add(referenceJsonPath.ReferenceJsonPath.Canonical);
            }

            var referencePaths = referenceJsonPaths
                .Select(binding => binding.ReferenceJsonPath.Canonical)
                .ToArray();
            var referenceIsPartOfIdentity = referencePaths.Any(identityJsonPaths.Contains);
            var effectiveIsPartOfIdentity = ResolveIsPartOfIdentity(
                mapping.Key,
                projectName,
                resourceName,
                isPartOfIdentity,
                referenceIsPartOfIdentity,
                referencePaths
            );
            ValidateReferenceIdentityCompleteness(
                mapping.Key,
                projectName,
                resourceName,
                referenceIsPartOfIdentity,
                referenceJsonPaths,
                identityJsonPaths
            );

            var targetProjectName = RequireString(mappingObject, "projectName");
            var targetResourceName = RequireString(mappingObject, "resourceName");
            var isRequired = mappingObject["isRequired"]?.GetValue<bool>() ?? false;

            if (effectiveIsPartOfIdentity && !isRequired)
            {
                throw new InvalidOperationException(
                    $"documentPathsMapping entry '{mapping.Key}' on resource '{projectName}:{resourceName}' is "
                        + "marked as isPartOfIdentity but isRequired is false. "
                        + "Identity references must be required."
                );
            }

            referenceObjectPaths.Add(referenceObjectPath.Canonical);
            referenceMappings.Add(
                new DocumentReferenceMapping(
                    mapping.Key,
                    new QualifiedResourceName(targetProjectName, targetResourceName),
                    isRequired,
                    effectiveIsPartOfIdentity,
                    referenceObjectPath,
                    referenceJsonPaths
                )
            );
        }

        var missingIdentityPaths = identityJsonPaths
            .Where(path => !mappedIdentityPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missingIdentityPaths.Length > 0)
        {
            throw new InvalidOperationException(
                $"identityJsonPaths on resource '{projectName}:{resourceName}' were not found in "
                    + $"documentPathsMapping: {string.Join(", ", missingIdentityPaths)}."
            );
        }

        return new DocumentPathsMappingResult(
            referenceMappings.ToArray(),
            mappedIdentityPaths,
            referenceObjectPaths
        );
    }

    /// <summary>
    /// Extracts array uniqueness constraints defined on the resource schema.
    /// </summary>
    private static IReadOnlyList<ArrayUniquenessConstraintInput> ExtractArrayUniquenessConstraints(
        JsonObject resourceSchema,
        string projectName,
        string resourceName
    )
    {
        if (!resourceSchema.TryGetPropertyValue("arrayUniquenessConstraints", out var constraintsNode))
        {
            return Array.Empty<ArrayUniquenessConstraintInput>();
        }

        if (constraintsNode is null)
        {
            return Array.Empty<ArrayUniquenessConstraintInput>();
        }

        if (constraintsNode is not JsonArray constraintsArray)
        {
            throw new InvalidOperationException(
                "Expected arrayUniquenessConstraints to be an array, invalid ApiSchema."
            );
        }

        return ExtractArrayUniquenessConstraints(
            constraintsArray,
            projectName,
            resourceName,
            isNested: false
        );
    }

    private static IReadOnlyList<ArrayUniquenessConstraintInput> ExtractArrayUniquenessConstraints(
        JsonArray constraintsArray,
        string projectName,
        string resourceName,
        bool isNested
    )
    {
        List<ArrayUniquenessConstraintInput> constraints = [];

        foreach (var constraint in constraintsArray)
        {
            if (constraint is null)
            {
                throw new InvalidOperationException(
                    "Expected arrayUniquenessConstraints to not contain null entries, invalid ApiSchema."
                );
            }

            if (constraint is not JsonObject constraintObject)
            {
                throw new InvalidOperationException(
                    "Expected arrayUniquenessConstraints entries to be objects, invalid ApiSchema."
                );
            }

            JsonPathExpression? basePath = null;

            if (constraintObject.TryGetPropertyValue("basePath", out var basePathNode))
            {
                if (basePathNode is null)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.basePath to be non-null, invalid ApiSchema."
                    );
                }

                if (basePathNode is not JsonValue basePathValue)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.basePath to be a string, invalid ApiSchema."
                    );
                }

                basePath = JsonPathExpressionCompiler.Compile(basePathValue.GetValue<string>());
            }
            else if (isNested)
            {
                throw new InvalidOperationException(
                    $"arrayUniquenessConstraints nestedConstraints entry is missing basePath on "
                        + $"resource '{projectName}:{resourceName}'."
                );
            }

            var pathsNode = constraintObject["paths"];

            if (pathsNode is not JsonArray pathsArray)
            {
                throw new InvalidOperationException(
                    "Expected arrayUniquenessConstraints.paths to be an array, invalid ApiSchema."
                );
            }

            if (pathsArray.Count == 0)
            {
                throw new InvalidOperationException(
                    "Expected arrayUniquenessConstraints.paths to contain entries, invalid ApiSchema."
                );
            }

            List<JsonPathExpression> paths = new(pathsArray.Count);

            foreach (var pathNode in pathsArray)
            {
                if (pathNode is null)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.paths to not contain null entries, "
                            + "invalid ApiSchema."
                    );
                }

                if (pathNode is not JsonValue pathValue)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.paths entries to be strings, invalid ApiSchema."
                    );
                }

                paths.Add(JsonPathExpressionCompiler.Compile(pathValue.GetValue<string>()));
            }

            IReadOnlyList<ArrayUniquenessConstraintInput> nestedConstraints =
                Array.Empty<ArrayUniquenessConstraintInput>();

            if (
                constraintObject.TryGetPropertyValue("nestedConstraints", out var nestedConstraintsNode)
                && nestedConstraintsNode is not null
            )
            {
                if (nestedConstraintsNode is not JsonArray nestedConstraintsArray)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.nestedConstraints to be an array, "
                            + "invalid ApiSchema."
                    );
                }

                nestedConstraints = ExtractArrayUniquenessConstraints(
                    nestedConstraintsArray,
                    projectName,
                    resourceName,
                    isNested: true
                );
            }

            constraints.Add(new ArrayUniquenessConstraintInput(basePath, paths.ToArray(), nestedConstraints));
        }

        return constraints.ToArray();
    }

    /// <summary>
    /// Extracts reference base-name overrides from <c>resourceSchema.relational.nameOverrides</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExtractReferenceNameOverrides(
        JsonObject resourceSchema,
        IReadOnlySet<string> referenceObjectPaths,
        string projectName,
        string resourceName
    )
    {
        if (!resourceSchema.TryGetPropertyValue("relational", out var relationalNode))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (relationalNode is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (relationalNode is not JsonObject relationalObject)
        {
            throw new InvalidOperationException(
                $"Expected relational to be an object for resource '{projectName}:{resourceName}'."
            );
        }

        if (!relationalObject.TryGetPropertyValue("nameOverrides", out var nameOverridesNode))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (nameOverridesNode is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (nameOverridesNode is not JsonObject nameOverridesObject)
        {
            throw new InvalidOperationException(
                $"Expected relational.nameOverrides to be an object for resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        Dictionary<string, string> overrides = new(StringComparer.Ordinal);

        foreach (var referenceObjectPath in referenceObjectPaths)
        {
            if (!nameOverridesObject.TryGetPropertyValue(referenceObjectPath, out var overrideNode))
            {
                continue;
            }

            if (overrideNode is null)
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{referenceObjectPath}' is null on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            if (overrideNode is not JsonValue overrideValue)
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{referenceObjectPath}' must be a string on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            var overrideText = overrideValue.GetValue<string>();

            if (string.IsNullOrWhiteSpace(overrideText))
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{referenceObjectPath}' must be non-empty on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            overrides[referenceObjectPath] = overrideText;
        }

        return overrides;
    }

    private static JsonArray GetReferenceJsonPathsNode(
        string mappingKey,
        JsonObject mappingObject,
        string projectName,
        string resourceName
    )
    {
        if (!mappingObject.TryGetPropertyValue("referenceJsonPaths", out var referenceJsonPathsNode))
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                    + "is missing referenceJsonPaths."
            );
        }

        if (referenceJsonPathsNode is null)
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                    + "has null referenceJsonPaths."
            );
        }

        if (referenceJsonPathsNode is not JsonArray referenceJsonPathsArray)
        {
            throw new InvalidOperationException(
                "Expected referenceJsonPaths to be an array on documentPathsMapping entry, "
                    + "invalid ApiSchema."
            );
        }

        if (referenceJsonPathsArray.Count == 0)
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                    + "has no referenceJsonPaths entries."
            );
        }

        return referenceJsonPathsArray;
    }

    private static IReadOnlyList<ReferenceJsonPathBinding> ExtractReferenceJsonPaths(
        string mappingKey,
        JsonArray referenceJsonPathsArray,
        string projectName,
        string resourceName,
        out JsonPathExpression referenceObjectPath
    )
    {
        List<ReferenceJsonPathBinding> referenceJsonPaths = new(referenceJsonPathsArray.Count);
        JsonPathExpression? referencePrefix = null;
        Dictionary<string, string> referencePathsByIdentityPath = new(StringComparer.Ordinal);

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
            var identityPath = JsonPathExpressionCompiler.Compile(identityJsonPath);
            var referencePath = JsonPathExpressionCompiler.Compile(referenceJsonPathValue);
            var prefixPath = ExtractReferencePrefixPath(mappingKey, projectName, resourceName, referencePath);

            if (referencePrefix is null)
            {
                referencePrefix = prefixPath;
            }
            else if (
                !string.Equals(
                    referencePrefix.Value.Canonical,
                    prefixPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                        + $"has inconsistent referenceJsonPaths prefix '{referencePrefix.Value.Canonical}' "
                        + $"and '{prefixPath.Canonical}'."
                );
            }

            if (!referencePathsByIdentityPath.TryAdd(identityPath.Canonical, referencePath.Canonical))
            {
                var existingReferencePath = referencePathsByIdentityPath[identityPath.Canonical];

                throw new InvalidOperationException(
                    $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                        + $"has duplicate identityJsonPath '{identityPath.Canonical}' mapped to "
                        + $"'{existingReferencePath}' and '{referencePath.Canonical}'."
                );
            }

            referenceJsonPaths.Add(new ReferenceJsonPathBinding(identityPath, referencePath));
        }

        referenceObjectPath =
            referencePrefix
            ?? throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                    + "has no referenceJsonPaths entries."
            );

        return referenceJsonPaths.ToArray();
    }

    private static JsonPathExpression ExtractReferencePrefixPath(
        string mappingKey,
        string projectName,
        string resourceName,
        JsonPathExpression referencePath
    )
    {
        if (referencePath.Segments.Count == 0 || referencePath.Segments[^1] is not JsonPathSegment.Property)
        {
            throw new InvalidOperationException(
                $"referenceJsonPath '{referencePath.Canonical}' on documentPathsMapping entry '{mappingKey}' "
                    + $"for resource '{projectName}:{resourceName}' must end with a property segment."
            );
        }

        var prefixSegments = referencePath.Segments.Take(referencePath.Segments.Count - 1).ToArray();
        return JsonPathExpressionCompiler.FromSegments(prefixSegments);
    }

    private static bool ResolveIsPartOfIdentity(
        string mappingKey,
        string projectName,
        string resourceName,
        bool? isPartOfIdentity,
        bool derivedIsPartOfIdentity,
        IReadOnlyList<string> mappingPaths
    )
    {
        if (!derivedIsPartOfIdentity)
        {
            if (isPartOfIdentity is true)
            {
                var orderedPaths = mappingPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();

                throw new InvalidOperationException(
                    $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' is "
                        + "marked as isPartOfIdentity but identityJsonPaths does not include path(s): "
                        + string.Join(", ", orderedPaths)
                );
            }

            return isPartOfIdentity ?? false;
        }

        if (isPartOfIdentity is false)
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' is "
                    + "not marked as isPartOfIdentity but identityJsonPaths includes it."
            );
        }

        return true;
    }

    private static void ValidateReferenceIdentityCompleteness(
        string mappingKey,
        string projectName,
        string resourceName,
        bool derivedIsPartOfIdentity,
        IReadOnlyList<ReferenceJsonPathBinding> referenceJsonPaths,
        IReadOnlySet<string> identityJsonPaths
    )
    {
        if (!derivedIsPartOfIdentity)
        {
            return;
        }

        var referencePaths = referenceJsonPaths
            .Select(binding => binding.ReferenceJsonPath.Canonical)
            .ToArray();
        var missing = referencePaths
            .Where(path => !identityJsonPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' is "
                + "marked isPartOfIdentity but identityJsonPaths is missing reference path(s): "
                + string.Join(", ", missing)
        );
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
    /// Ensures a named property on an object is a boolean value.
    /// </summary>
    private static bool RequireBoolean(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a boolean, invalid ApiSchema."
            ),
        };
    }

    private static bool? TryGetOptionalBoolean(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var value))
        {
            return null;
        }

        if (value is null)
        {
            return null;
        }

        if (value is not JsonValue jsonValue)
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be a boolean, invalid ApiSchema."
            );
        }

        return jsonValue.GetValue<bool>();
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

    private sealed record DocumentPathsMappingResult(
        IReadOnlyList<DocumentReferenceMapping> ReferenceMappings,
        IReadOnlySet<string> MappedIdentityPaths,
        IReadOnlySet<string> ReferenceObjectPaths
    )
    {
        public static DocumentPathsMappingResult Empty { get; } =
            new(
                Array.Empty<DocumentReferenceMapping>(),
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)
            );
    }
}
