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
        var isResourceExtension = TryGetOptionalBoolean(
            resourceSchema,
            "isResourceExtension",
            defaultValue: false
        );
        var superclassResourceName = RelationalModelSetSchemaHelpers.TryGetOptionalString(
            resourceSchema,
            "superclassResourceName"
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
        ValidateArrayUniquenessReferenceIdentityCompleteness(
            arrayUniquenessConstraints,
            documentPathsMapping.ReferenceMappings,
            projectName,
            resourceName
        );
        var relationalObject = GetRelationalObject(resourceSchema, projectName, resourceName, isDescriptor);
        var rootTableNameOverride = ExtractRootTableNameOverride(
            relationalObject,
            isResourceExtension,
            projectName,
            resourceName
        );
        var nameOverrides = ExtractNameOverrides(
            relationalObject,
            documentPathsMapping.ReferenceMappings,
            isResourceExtension,
            jsonSchemaForInsert,
            projectEndpointName,
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
        context.SuperclassResourceName = superclassResourceName;
        context.RootTableNameOverride = rootTableNameOverride;
        context.IsDescriptorResource = isDescriptor;
        context.JsonSchemaForInsert = jsonSchemaForInsert;
        context.IdentityJsonPaths = identityJsonPaths;
        context.AllowIdentityUpdates = allowIdentityUpdates;
        context.DocumentReferenceMappings = documentPathsMapping.ReferenceMappings;
        context.ArrayUniquenessConstraints = arrayUniquenessConstraints;
        context.NameOverridesByPath = nameOverrides;
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
        var documentPathsMapping = GetDocumentPathsMappingOrEmpty(resourceSchema);
        var state = new DocumentReferenceMappingExtractionState();

        foreach (var mapping in documentPathsMapping.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            ProcessDocumentPathsMappingEntry(
                mapping.Key,
                mapping.Value,
                projectName,
                resourceName,
                identityJsonPaths,
                state
            );
        }

        ValidateIdentityJsonPathsCovered(projectName, resourceName, identityJsonPaths, state);
        return state.ToResult();
    }

    /// <summary>
    /// Accumulates extracted document reference mappings and derived path sets while iterating
    /// <c>documentPathsMapping</c> entries.
    /// </summary>
    private sealed class DocumentReferenceMappingExtractionState
    {
        public List<DocumentReferenceMapping> ReferenceMappings { get; } = [];

        public HashSet<string> MappedIdentityPaths { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ReferenceObjectPaths { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Builds the final <see cref="DocumentPathsMappingResult"/> for the caller.
        /// </summary>
        public DocumentPathsMappingResult ToResult()
        {
            return new DocumentPathsMappingResult(
                ReferenceMappings.ToArray(),
                MappedIdentityPaths,
                ReferenceObjectPaths
            );
        }
    }

    /// <summary>
    /// Reads <c>documentPathsMapping</c> from the resource schema, returning an empty object when the
    /// property is missing or null, and throwing when the property is not an object.
    /// </summary>
    private static JsonObject GetDocumentPathsMappingOrEmpty(JsonObject resourceSchema)
    {
        if (!resourceSchema.TryGetPropertyValue("documentPathsMapping", out var documentPathsMappingNode))
        {
            return new JsonObject();
        }

        return documentPathsMappingNode switch
        {
            null => new JsonObject(),
            JsonObject mappingObject => mappingObject,
            _ => throw new InvalidOperationException(
                "Expected documentPathsMapping to be an object, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Validates and processes a single <c>documentPathsMapping</c> entry, updating the extraction state.
    /// </summary>
    private static void ProcessDocumentPathsMappingEntry(
        string mappingKey,
        JsonNode? mappingNode,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var mappingObject = RequireDocumentPathsMappingEntryObject(mappingNode);

        var isReference =
            mappingObject["isReference"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isReference to be on documentPathsMapping entry, invalid ApiSchema."
            );

        if (!isReference)
        {
            ProcessDocumentPathsMappingPathEntry(
                mappingKey,
                mappingObject,
                projectName,
                resourceName,
                identityJsonPaths,
                state
            );
            return;
        }

        var isDescriptor =
            mappingObject["isDescriptor"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isDescriptor to be on documentPathsMapping entry, invalid ApiSchema."
            );

        if (isDescriptor)
        {
            ProcessDocumentPathsMappingDescriptorEntry(
                mappingKey,
                mappingObject,
                projectName,
                resourceName,
                identityJsonPaths,
                state
            );
            return;
        }

        ProcessDocumentPathsMappingReferenceEntry(
            mappingKey,
            mappingObject,
            projectName,
            resourceName,
            identityJsonPaths,
            state
        );
    }

    /// <summary>
    /// Ensures the mapping value for a <c>documentPathsMapping</c> entry is a non-null object, and
    /// throws a schema validation exception otherwise.
    /// </summary>
    private static JsonObject RequireDocumentPathsMappingEntryObject(JsonNode? mappingNode)
    {
        return mappingNode switch
        {
            null => throw new InvalidOperationException(
                "Expected documentPathsMapping entries to be non-null, invalid ApiSchema."
            ),
            JsonObject mappingObject => mappingObject,
            _ => throw new InvalidOperationException(
                "Expected documentPathsMapping entries to be objects, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Processes a non-reference <c>documentPathsMapping</c> entry with a single <c>path</c> property.
    /// </summary>
    private static void ProcessDocumentPathsMappingPathEntry(
        string mappingKey,
        JsonObject mappingObject,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var path = RequireString(mappingObject, "path");
        var pathExpression = JsonPathExpressionCompiler.Compile(path);

        state.MappedIdentityPaths.Add(pathExpression.Canonical);
    }

    /// <summary>
    /// Processes a descriptor <c>documentPathsMapping</c> entry with a single <c>path</c> property.
    /// </summary>
    private static void ProcessDocumentPathsMappingDescriptorEntry(
        string mappingKey,
        JsonObject mappingObject,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var path = RequireString(mappingObject, "path");
        var pathExpression = JsonPathExpressionCompiler.Compile(path);

        state.MappedIdentityPaths.Add(pathExpression.Canonical);
    }

    /// <summary>
    /// Processes a reference <c>documentPathsMapping</c> entry, extracting JSONPath bindings, validating
    /// identity component completeness, and adding the resulting <see cref="DocumentReferenceMapping"/> to
    /// the extraction state.
    /// </summary>
    private static void ProcessDocumentPathsMappingReferenceEntry(
        string mappingKey,
        JsonObject mappingObject,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var referenceJsonPathsNode = GetReferenceJsonPathsNode(
            mappingKey,
            mappingObject,
            projectName,
            resourceName
        );
        var referenceJsonPaths = ExtractReferenceJsonPaths(
            mappingKey,
            referenceJsonPathsNode,
            projectName,
            resourceName,
            out var referenceObjectPath
        );

        foreach (var referenceJsonPath in referenceJsonPaths)
        {
            state.MappedIdentityPaths.Add(referenceJsonPath.ReferenceJsonPath.Canonical);
        }

        var referencePaths = referenceJsonPaths
            .Select(binding => binding.ReferenceJsonPath.Canonical)
            .ToArray();
        var referenceIsPartOfIdentity = referencePaths.Any(identityJsonPaths.Contains);
        ValidateReferenceIdentityCompleteness(
            mappingKey,
            projectName,
            resourceName,
            referenceObjectPath,
            referenceIsPartOfIdentity,
            referenceJsonPaths,
            identityJsonPaths
        );

        var targetProjectName = RequireString(mappingObject, "projectName");
        var targetResourceName = RequireString(mappingObject, "resourceName");
        var isRequired = mappingObject["isRequired"]?.GetValue<bool>() ?? false;

        if (referenceIsPartOfIdentity && !isRequired)
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' is "
                    + "mapped to identityJsonPaths but isRequired is false. "
                    + "Identity references must be required."
            );
        }

        state.ReferenceObjectPaths.Add(referenceObjectPath.Canonical);
        state.ReferenceMappings.Add(
            new DocumentReferenceMapping(
                mappingKey,
                new QualifiedResourceName(targetProjectName, targetResourceName),
                isRequired,
                referenceIsPartOfIdentity,
                referenceObjectPath,
                referenceJsonPaths
            )
        );
    }

    /// <summary>
    /// Validates that every identity JSONPath defined by <c>identityJsonPaths</c> is represented by at least
    /// one <c>documentPathsMapping</c> entry.
    /// </summary>
    private static void ValidateIdentityJsonPathsCovered(
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var missingIdentityPaths = identityJsonPaths
            .Where(path => !state.MappedIdentityPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missingIdentityPaths.Length > 0)
        {
            throw new InvalidOperationException(
                $"identityJsonPaths on resource '{projectName}:{resourceName}' were not found in "
                    + $"documentPathsMapping: {string.Join(", ", missingIdentityPaths)}."
            );
        }
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

    /// <summary>
    /// Extracts and compiles array uniqueness constraints from a JSON array, recursively processing nested
    /// constraints.
    /// </summary>
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
    /// Resolves the <c>relational</c> block for a resource, validating descriptor restrictions.
    /// </summary>
    private static JsonObject? GetRelationalObject(
        JsonObject resourceSchema,
        string projectName,
        string resourceName,
        bool isDescriptor
    )
    {
        if (!resourceSchema.TryGetPropertyValue("relational", out var relationalNode))
        {
            return null;
        }

        if (isDescriptor)
        {
            throw new InvalidOperationException(
                $"Descriptor resource '{projectName}:{resourceName}' must not define relational overrides."
            );
        }

        if (relationalNode is null)
        {
            return null;
        }

        if (relationalNode is not JsonObject relationalObject)
        {
            throw new InvalidOperationException(
                $"Expected relational to be an object for resource '{projectName}:{resourceName}'."
            );
        }

        return relationalObject;
    }

    /// <summary>
    /// Extracts and normalizes <c>rootTableNameOverride</c> from the relational block.
    /// </summary>
    private static string? ExtractRootTableNameOverride(
        JsonObject? relationalObject,
        bool isResourceExtension,
        string projectName,
        string resourceName
    )
    {
        if (relationalObject is null)
        {
            return null;
        }

        if (!relationalObject.TryGetPropertyValue("rootTableNameOverride", out var overrideNode))
        {
            return null;
        }

        if (overrideNode is null)
        {
            throw new InvalidOperationException(
                $"relational.rootTableNameOverride must be non-empty on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        if (overrideNode is not JsonValue overrideValue)
        {
            throw new InvalidOperationException(
                $"relational.rootTableNameOverride must be a string on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        var overrideText = overrideValue.GetValue<string>();

        if (string.IsNullOrWhiteSpace(overrideText))
        {
            throw new InvalidOperationException(
                $"relational.rootTableNameOverride must be non-empty on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        var normalized = RelationalNameConventions.ToPascalCase(overrideText);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(
                $"relational.rootTableNameOverride must normalize to a non-empty name on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        if (isResourceExtension)
        {
            var expectedExtensionName = $"{RelationalNameConventions.ToPascalCase(resourceName)}Extension";

            if (!string.Equals(normalized, expectedExtensionName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"relational.rootTableNameOverride is not supported for resource extension "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            return null;
        }

        return normalized;
    }

    /// <summary>
    /// Extracts and normalizes <c>resourceSchema.relational.nameOverrides</c> entries.
    /// </summary>
    private static IReadOnlyDictionary<string, NameOverrideEntry> ExtractNameOverrides(
        JsonObject? relationalObject,
        IReadOnlyList<DocumentReferenceMapping> referenceMappings,
        bool isResourceExtension,
        JsonNode jsonSchemaForInsert,
        string projectEndpointName,
        string projectName,
        string resourceName
    )
    {
        if (relationalObject is null)
        {
            return new Dictionary<string, NameOverrideEntry>(StringComparer.Ordinal);
        }

        if (!relationalObject.TryGetPropertyValue("nameOverrides", out var nameOverridesNode))
        {
            return new Dictionary<string, NameOverrideEntry>(StringComparer.Ordinal);
        }

        if (nameOverridesNode is null)
        {
            return new Dictionary<string, NameOverrideEntry>(StringComparer.Ordinal);
        }

        if (nameOverridesNode is not JsonObject nameOverridesObject)
        {
            throw new InvalidOperationException(
                $"Expected relational.nameOverrides to be an object for resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        Dictionary<string, NameOverrideEntry> overrides = new(StringComparer.Ordinal);
        string? extensionProjectKey = null;
        var referenceIdentityPaths = RelationalModelSetSchemaHelpers.BuildReferenceIdentityPathSet(
            referenceMappings
        );

        foreach (var overrideEntry in nameOverridesObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            JsonPathExpression compiledPath;
            var overrideKey = overrideEntry.Key;

            try
            {
                compiledPath = JsonPathExpressionCompiler.Compile(overrideKey);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' on resource "
                        + $"'{projectName}:{resourceName}' is not a valid JSONPath.",
                    ex
                );
            }

            var resolvedPath = compiledPath;

            if (isResourceExtension && !IsExtensionRootPath(compiledPath))
            {
                extensionProjectKey ??= ResolveExtensionProjectKey(
                    jsonSchemaForInsert,
                    projectEndpointName,
                    projectName,
                    resourceName
                );
                resolvedPath = PrefixExtensionRoot(compiledPath, extensionProjectKey);
            }

            var overrideNode = overrideEntry.Value;

            if (overrideNode is null)
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' is null on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            if (overrideNode is not JsonValue overrideValue)
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' must be a string on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            var overrideText = overrideValue.GetValue<string>();

            if (string.IsNullOrWhiteSpace(overrideText))
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' must be non-empty on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            var normalizedOverride = RelationalNameConventions.ToPascalCase(overrideText);

            if (string.IsNullOrWhiteSpace(normalizedOverride))
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' must normalize to a non-empty "
                        + $"name on resource '{projectName}:{resourceName}'."
                );
            }

            if (IsInsideReferenceObjectPath(resolvedPath, referenceMappings, out var referencePath))
            {
                if (!referenceIdentityPaths.Contains(resolvedPath.Canonical))
                {
                    throw new InvalidOperationException(
                        $"relational.nameOverrides entry '{overrideKey}' (canonical '{resolvedPath.Canonical}') "
                            + $"on resource '{projectName}:{resourceName}' targets a non-identity path inside "
                            + $"reference object '{referencePath}'. Only reference identity paths may be overridden."
                    );
                }
            }

            var overrideKind =
                resolvedPath.Segments.Count > 0
                && resolvedPath.Segments[^1] is JsonPathSegment.AnyArrayElement
                    ? NameOverrideKind.Collection
                    : NameOverrideKind.Column;

            if (
                !overrides.TryAdd(
                    resolvedPath.Canonical,
                    new NameOverrideEntry(
                        overrideKey,
                        resolvedPath.Canonical,
                        normalizedOverride,
                        overrideKind
                    )
                )
            )
            {
                var existing = overrides[resolvedPath.Canonical];

                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' (canonical '{resolvedPath.Canonical}') "
                        + $"duplicates '{existing.RawKey}' on resource '{projectName}:{resourceName}'."
                );
            }
        }

        return overrides;
    }

    private static bool IsExtensionRootPath(JsonPathExpression path)
    {
        return path.Segments.Count > 0 && path.Segments[0] is JsonPathSegment.Property { Name: "_ext" };
    }

    private static JsonPathExpression PrefixExtensionRoot(JsonPathExpression path, string projectKey)
    {
        List<JsonPathSegment> segments =
        [
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property(projectKey),
        ];

        segments.AddRange(path.Segments);

        return JsonPathExpressionCompiler.FromSegments(segments);
    }

    private static string ResolveExtensionProjectKey(
        JsonNode jsonSchemaForInsert,
        string projectEndpointName,
        string projectName,
        string resourceName
    )
    {
        if (jsonSchemaForInsert is not JsonObject rootSchema)
        {
            throw new InvalidOperationException(
                $"Expected jsonSchemaForInsert to be an object on resource '{projectName}:{resourceName}'."
            );
        }

        if (!rootSchema.TryGetPropertyValue("properties", out var propertiesNode))
        {
            throw new InvalidOperationException(
                $"Extension resource '{projectName}:{resourceName}' is missing jsonSchemaForInsert.properties."
            );
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException(
                $"Expected jsonSchemaForInsert.properties to be an object on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        if (!propertiesObject.TryGetPropertyValue("_ext", out var extNode) || extNode is null)
        {
            throw new InvalidOperationException(
                $"Extension resource '{projectName}:{resourceName}' is missing jsonSchemaForInsert.properties._ext."
            );
        }

        if (extNode is not JsonObject extSchema)
        {
            throw new InvalidOperationException(
                $"Expected jsonSchemaForInsert.properties._ext to be an object on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        if (!extSchema.TryGetPropertyValue("properties", out var projectKeysNode))
        {
            throw new InvalidOperationException(
                $"Extension resource '{projectName}:{resourceName}' is missing "
                    + "jsonSchemaForInsert.properties._ext.properties."
            );
        }

        if (projectKeysNode is not JsonObject projectKeysObject)
        {
            throw new InvalidOperationException(
                $"Expected jsonSchemaForInsert.properties._ext.properties to be an object on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        var endpointKey = FindMatchingProjectKey(projectKeysObject, projectEndpointName);

        if (endpointKey is not null)
        {
            return endpointKey;
        }

        var nameKey = FindMatchingProjectKey(projectKeysObject, projectName);

        if (nameKey is not null)
        {
            return nameKey;
        }

        throw new InvalidOperationException(
            $"Extension project key '{projectEndpointName}' not found under jsonSchemaForInsert "
                + $"._ext on resource '{projectName}:{resourceName}'."
        );
    }

    private static string? FindMatchingProjectKey(JsonObject projectKeysObject, string match)
    {
        foreach (var entry in projectKeysObject)
        {
            if (string.Equals(entry.Key, match, StringComparison.Ordinal))
            {
                return entry.Key;
            }
        }

        return null;
    }

    private static bool IsInsideReferenceObjectPath(
        JsonPathExpression path,
        IReadOnlyList<DocumentReferenceMapping> referenceMappings,
        out string referencePath
    )
    {
        foreach (var mapping in referenceMappings)
        {
            var referenceObjectPath = mapping.ReferenceObjectPath;

            if (path.Segments.Count <= referenceObjectPath.Segments.Count)
            {
                continue;
            }

            if (IsSegmentPrefix(referenceObjectPath.Segments, path.Segments))
            {
                referencePath = referenceObjectPath.Canonical;
                return true;
            }
        }

        referencePath = string.Empty;
        return false;
    }

    private static bool IsSegmentPrefix(
        IReadOnlyList<JsonPathSegment> prefix,
        IReadOnlyList<JsonPathSegment> candidate
    )
    {
        if (prefix.Count > candidate.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            var prefixSegment = prefix[index];
            var candidateSegment = candidate[index];

            if (prefixSegment is JsonPathSegment.Property prefixProperty)
            {
                if (
                    candidateSegment is not JsonPathSegment.Property candidateProperty
                    || !string.Equals(prefixProperty.Name, candidateProperty.Name, StringComparison.Ordinal)
                )
                {
                    return false;
                }

                continue;
            }

            if (prefixSegment is JsonPathSegment.AnyArrayElement)
            {
                if (candidateSegment is not JsonPathSegment.AnyArrayElement)
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates and returns the <c>referenceJsonPaths</c> array for a document reference mapping entry.
    /// </summary>
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

    /// <summary>
    /// Extracts the identity/reference JSONPath bindings for a document reference mapping and returns the
    /// compiled reference object path.
    /// </summary>
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

    /// <summary>
    /// Extracts the reference object path prefix from a reference JSONPath by removing the terminal property
    /// segment.
    /// </summary>
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

    /// <summary>
    /// Validates that all identity-component reference paths are present in the resource's
    /// <c>identityJsonPaths</c>.
    /// </summary>
    private static void ValidateReferenceIdentityCompleteness(
        string mappingKey,
        string projectName,
        string resourceName,
        JsonPathExpression referenceObjectPath,
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
            $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' has "
                + $"reference identity paths for '{referenceObjectPath.Canonical}' but identityJsonPaths "
                + "is missing reference path(s): "
                + string.Join(", ", missing)
        );
    }

    /// <summary>
    /// Validates that array uniqueness constraints referencing any identity path from a reference object include
    /// all of that reference object's identity paths.
    /// </summary>
    private static void ValidateArrayUniquenessReferenceIdentityCompleteness(
        IReadOnlyList<ArrayUniquenessConstraintInput> constraints,
        IReadOnlyList<DocumentReferenceMapping> referenceMappings,
        string projectName,
        string resourceName
    )
    {
        if (constraints.Count == 0 || referenceMappings.Count == 0)
        {
            return;
        }

        var referenceGroups = referenceMappings
            .Select(mapping => new ReferenceIdentityGroup(
                mapping.ReferenceObjectPath,
                mapping
                    .ReferenceJsonPaths.Select(binding => binding.ReferenceJsonPath.Canonical)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()
            ))
            .ToArray();
        var resourceKey = $"{projectName}:{resourceName}";

        foreach (var constraint in constraints)
        {
            ValidateArrayUniquenessReferenceIdentityCompleteness(constraint, referenceGroups, resourceKey);
        }
    }

    /// <summary>
    /// Validates a single array uniqueness constraint (and nested constraints) for reference identity coverage
    /// within each array scope.
    /// </summary>
    private static void ValidateArrayUniquenessReferenceIdentityCompleteness(
        ArrayUniquenessConstraintInput constraint,
        IReadOnlyList<ReferenceIdentityGroup> referenceGroups,
        string resourceKey
    )
    {
        var resolvedPaths = constraint
            .Paths.Select(path => ResolveConstraintPath(constraint.BasePath, path))
            .ToArray();
        var pathsByScope = GroupPathsByArrayScope(resolvedPaths, resourceKey);
        var basePath = constraint.BasePath?.Canonical;

        foreach (var scopeGroup in pathsByScope.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var scope = scopeGroup.Key;
            var scopePaths = scopeGroup.Value;
            var scopePath = GetArrayScope(scopePaths[0], resourceKey);
            var matched = ValidateArrayUniquenessReferenceIdentityCoverage(
                scopePaths,
                referenceGroups,
                resourceKey,
                scope,
                basePath,
                alignedScope: null,
                alignedBasePath: null
            );

            if (!matched)
            {
                if (
                    TryStripExtensionRootPrefix(scopePath, out var alignedScope)
                    && TryStripExtensionRootPrefix(scopePaths, out var alignedPaths)
                )
                {
                    var alignedBasePath = TryStripExtensionRootPrefix(
                        constraint.BasePath,
                        out var alignedBase
                    )
                        ? alignedBase.Canonical
                        : null;

                    _ = ValidateArrayUniquenessReferenceIdentityCoverage(
                        alignedPaths,
                        referenceGroups,
                        resourceKey,
                        scope,
                        basePath,
                        alignedScope.Canonical,
                        alignedBasePath
                    );
                }
            }
        }

        foreach (var nested in constraint.NestedConstraints)
        {
            ValidateArrayUniquenessReferenceIdentityCompleteness(nested, referenceGroups, resourceKey);
        }
    }

    /// <summary>
    /// Validates that when any reference identity path is included in a constraint scope, all identity paths for
    /// that reference object are included.
    /// </summary>
    private static bool ValidateArrayUniquenessReferenceIdentityCoverage(
        IReadOnlyList<JsonPathExpression> constraintPaths,
        IReadOnlyList<ReferenceIdentityGroup> referenceGroups,
        string resourceKey,
        string scope,
        string? basePath,
        string? alignedScope,
        string? alignedBasePath
    )
    {
        HashSet<string> constraintPathSet = new(
            constraintPaths.Select(path => path.Canonical),
            StringComparer.Ordinal
        );
        var matchedAny = false;

        foreach (var referenceGroup in referenceGroups)
        {
            if (!referenceGroup.ReferenceIdentityPaths.Any(constraintPathSet.Contains))
            {
                continue;
            }

            matchedAny = true;
            var missing = referenceGroup
                .ReferenceIdentityPaths.Where(path => !constraintPathSet.Contains(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            if (missing.Length == 0)
            {
                continue;
            }

            var basePathMessage = basePath is null ? string.Empty : $" basePath '{basePath}'";
            var alignedMessage =
                alignedScope is null ? string.Empty
                : alignedBasePath is null ? $" alignedScope '{alignedScope}'"
                : $" alignedScope '{alignedScope}' basePath '{alignedBasePath}'";

            throw new InvalidOperationException(
                $"arrayUniquenessConstraints scope '{scope}' on resource '{resourceKey}'"
                    + basePathMessage
                    + alignedMessage
                    + $" includes reference identity path(s) under '{referenceGroup.ReferenceObjectPath.Canonical}' "
                    + "but is missing reference identity path(s): "
                    + string.Join(", ", missing)
            );
        }

        return matchedAny;
    }

    /// <summary>
    /// Groups constraint paths by the canonical JSONPath of their owning array scope.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<JsonPathExpression>> GroupPathsByArrayScope(
        IReadOnlyList<JsonPathExpression> paths,
        string resourceKey
    )
    {
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("arrayUniquenessConstraints must include at least one path.");
        }

        Dictionary<string, List<JsonPathExpression>> grouped = new(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var arrayScope = GetArrayScope(path, resourceKey);
            var scope = arrayScope.Canonical;

            if (!grouped.TryGetValue(scope, out var scopePaths))
            {
                scopePaths = [];
                grouped.Add(scope, scopePaths);
            }

            scopePaths.Add(path);
        }

        return grouped.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<JsonPathExpression>)entry.Value,
            StringComparer.Ordinal
        );
    }

    /// <summary>
    /// Returns the owning array scope for a path by taking the prefix through its last wildcard array segment.
    /// </summary>
    private static JsonPathExpression GetArrayScope(JsonPathExpression path, string resourceKey)
    {
        var lastArrayIndex = -1;

        for (var index = 0; index < path.Segments.Count; index++)
        {
            if (path.Segments[index] is JsonPathSegment.AnyArrayElement)
            {
                lastArrayIndex = index;
            }
        }

        if (lastArrayIndex < 0)
        {
            throw new InvalidOperationException(
                $"arrayUniquenessConstraints path '{path.Canonical}' on resource '{resourceKey}' "
                    + "must include an array wildcard segment."
            );
        }

        var scopeSegments = path.Segments.Take(lastArrayIndex + 1).ToArray();
        return JsonPathExpressionCompiler.FromSegments(scopeSegments);
    }

    /// <summary>
    /// Resolves a constraint path relative to its optional base path.
    /// </summary>
    private static JsonPathExpression ResolveConstraintPath(
        JsonPathExpression? basePath,
        JsonPathExpression path
    )
    {
        return basePath is null ? path : ResolveRelativePath(basePath.Value, path);
    }

    /// <summary>
    /// Resolves a path relative to a base array path by concatenating JSONPath segments.
    /// </summary>
    private static JsonPathExpression ResolveRelativePath(
        JsonPathExpression basePath,
        JsonPathExpression relativePath
    )
    {
        if (relativePath.Segments.Count == 0)
        {
            return basePath;
        }

        var combinedSegments = basePath.Segments.Concat(relativePath.Segments).ToArray();
        return JsonPathExpressionCompiler.FromSegments(combinedSegments);
    }

    /// <summary>
    /// Attempts to strip a leading <c>._ext.{project}</c> prefix from an optional path.
    /// </summary>
    private static bool TryStripExtensionRootPrefix(JsonPathExpression? path, out JsonPathExpression stripped)
    {
        if (path is null)
        {
            stripped = default;
            return false;
        }

        return TryStripExtensionRootPrefix(path.Value, out stripped);
    }

    /// <summary>
    /// Attempts to strip a leading <c>._ext.{project}</c> prefix from a path.
    /// </summary>
    private static bool TryStripExtensionRootPrefix(JsonPathExpression path, out JsonPathExpression stripped)
    {
        if (
            path.Segments.Count >= 2
            && path.Segments[0] is JsonPathSegment.Property { Name: "_ext" }
            && path.Segments[1] is JsonPathSegment.Property
        )
        {
            var remainingSegments = path.Segments.Skip(2).ToArray();
            stripped = JsonPathExpressionCompiler.FromSegments(remainingSegments);
            return true;
        }

        stripped = default;
        return false;
    }

    /// <summary>
    /// Attempts to strip a leading <c>._ext.{project}</c> prefix from all paths in the list.
    /// </summary>
    private static bool TryStripExtensionRootPrefix(
        IReadOnlyList<JsonPathExpression> paths,
        out IReadOnlyList<JsonPathExpression> stripped
    )
    {
        List<JsonPathExpression> strippedPaths = new(paths.Count);

        foreach (var path in paths)
        {
            if (!TryStripExtensionRootPrefix(path, out var strippedPath))
            {
                stripped = Array.Empty<JsonPathExpression>();
                return false;
            }

            strippedPaths.Add(strippedPath);
        }

        stripped = strippedPaths.ToArray();
        return true;
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

    /// <summary>
    /// Reads a named optional boolean property with a default when absent.
    /// </summary>
    private static bool TryGetOptionalBoolean(JsonObject node, string propertyName, bool defaultValue)
    {
        if (!node.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a boolean, invalid ApiSchema."
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

    /// <summary>
    /// Holds the extracted document reference mappings and the derived path sets used by later derivation
    /// steps.
    /// </summary>
    private sealed record DocumentPathsMappingResult(
        IReadOnlyList<DocumentReferenceMapping> ReferenceMappings,
        IReadOnlySet<string> MappedIdentityPaths,
        IReadOnlySet<string> ReferenceObjectPaths
    )
    {
        /// <summary>
        /// An empty result used when <c>documentPathsMapping</c> is not present.
        /// </summary>
        public static DocumentPathsMappingResult Empty { get; } =
            new(
                Array.Empty<DocumentReferenceMapping>(),
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)
            );
    }

    /// <summary>
    /// Captures a reference object path and its ordered set of identity path canonical strings.
    /// </summary>
    private sealed record ReferenceIdentityGroup(
        JsonPathExpression ReferenceObjectPath,
        IReadOnlyList<string> ReferenceIdentityPaths
    );
}
