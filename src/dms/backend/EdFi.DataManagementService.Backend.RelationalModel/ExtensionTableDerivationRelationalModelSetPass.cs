// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives extension tables (<c>_ext</c>) from resource-extension schemas and aligns them to base scopes.
/// </summary>
public sealed class ExtensionTableDerivationRelationalModelSetPass : IRelationalModelSetPass
{
    private const string ExtensionPropertyName = "_ext";
    private static readonly DbSchemaName _dmsSchemaName = new("dms");
    private static readonly DbTableName _descriptorTableName = new(_dmsSchemaName, "Descriptor");

    /// <summary>
    /// Executes extension table derivation across all resource-extension schemas.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseResourcesByName = BuildBaseResourceLookup(
            context.ConcreteResourcesInNameOrder,
            static (index, model) => new BaseResourceEntry(index, model)
        );
        var baseSchemasByName = context
            .EnumerateConcreteResourceSchemasInNameOrder()
            .Where(resourceContext => !IsResourceExtension(resourceContext))
            .ToDictionary(resourceContext => resourceContext.ResourceName, StringComparer.Ordinal);
        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (!IsResourceExtension(resourceContext))
            {
                continue;
            }

            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );

            if (!resourceContext.Project.ProjectSchema.IsExtensionProject)
            {
                throw new InvalidOperationException(
                    $"Resource extension '{FormatResource(resource)}' "
                        + "must be defined in an extension project."
                );
            }

            var baseEntry = ResolveBaseResourceForExtension(
                resourceContext.ResourceName,
                resource,
                baseResourcesByName,
                static entry => entry.Model.ResourceKey.Resource
            );
            var baseModel = context.ConcreteResourcesInNameOrder[baseEntry.Index];
            if (!baseSchemasByName.TryGetValue(resourceContext.ResourceName, out var baseSchemaContext))
            {
                throw new InvalidOperationException(
                    $"Base resource schema not found for extension '{FormatResource(resource)}'."
                );
            }

            var baseOverrideContext = context.GetOrCreateResourceBuilderContext(baseSchemaContext);
            var extensionOverrideContext = context.GetOrCreateResourceBuilderContext(resourceContext);
            var overrideProvider = new CompositeNameOverrideProvider(
                extensionOverrideContext,
                baseOverrideContext
            );
            var extensionContext = BuildExtensionContext(
                context,
                resourceContext,
                apiSchemaRootsByProjectEndpoint
            );

            var extensionResult = DeriveExtensionTables(
                extensionContext,
                overrideProvider,
                $"{resource.ProjectName}:{resource.ResourceName}",
                baseModel.RelationalModel,
                resourceContext.Project.ProjectSchema
            );

            if (extensionResult.Tables.Count == 0 && extensionResult.DescriptorEdgeSources.Count == 0)
            {
                continue;
            }

            var updatedModel = MergeExtensionTables(baseModel.RelationalModel, extensionResult);
            context.ConcreteResourcesInNameOrder[baseEntry.Index] = baseModel with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    /// <summary>
    /// Builds a relational model builder context for a resource extension using the extension project's schema
    /// and descriptor-path inventory.
    /// </summary>
    private static RelationalModelBuilderContext BuildExtensionContext(
        RelationalModelSetBuilderContext context,
        ConcreteResourceSchemaContext resourceContext,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint
    )
    {
        var projectSchema = resourceContext.Project.ProjectSchema;
        var apiSchemaRoot = GetApiSchemaRoot(
            apiSchemaRootsByProjectEndpoint,
            projectSchema.ProjectEndpointName,
            resourceContext.Project.EffectiveProject.ProjectSchema,
            cloneProjectSchema: true
        );

        var resourceKey = new QualifiedResourceName(projectSchema.ProjectName, resourceContext.ResourceName);
        var descriptorPaths = context.GetExtensionDescriptorPathsForResource(resourceKey);

        var builderContext = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = resourceContext.ResourceEndpointName,
            DescriptorPathSource = DescriptorPathSource.Precomputed,
            DescriptorPathsByJsonPath = descriptorPaths,
            OverrideCollisionDetector = context.OverrideCollisionDetector,
        };

        new ExtractInputsStep().Execute(builderContext);
        new ValidateJsonSchemaStep().Execute(builderContext);

        return builderContext;
    }

    /// <summary>
    /// Derives extension tables and descriptor edge sources from a resource extension schema.
    /// </summary>
    private static ExtensionDerivationResult DeriveExtensionTables(
        RelationalModelBuilderContext extensionContext,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        RelationalResourceModel baseModel,
        ProjectSchemaInfo extensionProject
    )
    {
        var jsonSchemaForInsert =
            extensionContext.JsonSchemaForInsert
            ?? throw new InvalidOperationException(
                "JsonSchemaForInsert must be provided before deriving extension tables."
            );

        if (jsonSchemaForInsert is not JsonObject rootSchema)
        {
            throw new InvalidOperationException("Json schema root must be an object.");
        }

        JsonSchemaUnsupportedKeywordValidator.Validate(rootSchema, "$");

        var baseRootBaseName = baseModel.Root.Table.Name;
        var extensionRootBaseName = $"{baseRootBaseName}Extension";
        var baseTablesByScope = baseModel.TablesInDependencyOrder.ToDictionary(
            table => table.JsonScope.Canonical,
            StringComparer.Ordinal
        );
        var extensionTablesByScope = new Dictionary<string, ExtensionTableBuilder>(StringComparer.Ordinal);

        var identityPaths = new HashSet<string>(
            extensionContext.IdentityJsonPaths.Select(path => path.Canonical),
            StringComparer.Ordinal
        );
        var referenceIdentityPaths = BuildReferenceIdentityPathSet(
            extensionContext.DocumentReferenceMappings
        );
        var referenceObjectPaths = BuildReferenceObjectPathSet(extensionContext.DocumentReferenceMappings);
        var usedDescriptorPaths = new HashSet<string>(StringComparer.Ordinal);
        List<DescriptorEdgeSource> descriptorEdgeSources = [];

        WalkSchema(
            rootSchema,
            tableBuilder: null,
            [],
            [],
            "$",
            hasOptionalAncestor: false,
            hasOverriddenAncestor: false,
            extensionContext,
            overrideProvider,
            resourceLabel,
            extensionProject,
            baseRootBaseName,
            extensionRootBaseName,
            baseTablesByScope,
            extensionTablesByScope,
            identityPaths,
            referenceIdentityPaths,
            referenceObjectPaths,
            usedDescriptorPaths,
            descriptorEdgeSources
        );

        EnsureAllDescriptorPathsUsed(extensionContext, usedDescriptorPaths);

        var orderedTables = extensionTablesByScope
            .Values.Select(builder => builder.Build())
            .OrderBy(table => CountArrayDepth(table.JsonScope))
            .ThenBy(table => table.JsonScope.Canonical, StringComparer.Ordinal)
            .ToArray();

        return new ExtensionDerivationResult(orderedTables, descriptorEdgeSources.ToArray());
    }

    /// <summary>
    /// Merges derived extension tables and descriptor edge sources into the base resource model.
    /// </summary>
    private static RelationalResourceModel MergeExtensionTables(
        RelationalResourceModel baseModel,
        ExtensionDerivationResult extensionResult
    )
    {
        var existingTables = baseModel.TablesInDependencyOrder;
        var updatedTables = existingTables.Concat(extensionResult.Tables).ToArray();
        var updatedEdges = baseModel
            .DescriptorEdgeSources.Concat(extensionResult.DescriptorEdgeSources)
            .ToArray();

        return baseModel with
        {
            TablesInDependencyOrder = updatedTables,
            DescriptorEdgeSources = updatedEdges,
        };
    }

    /// <summary>
    /// Walks a schema node and dispatches to object/array handlers during extension table derivation.
    /// </summary>
    private static void WalkSchema(
        JsonObject schema,
        ExtensionTableBuilder? tableBuilder,
        List<JsonPathSegment> pathSegments,
        List<string> columnSegments,
        string schemaPath,
        bool hasOptionalAncestor,
        bool hasOverriddenAncestor,
        RelationalModelBuilderContext context,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        ProjectSchemaInfo extensionProject,
        string baseRootBaseName,
        string extensionRootBaseName,
        IReadOnlyDictionary<string, DbTableModel> baseTablesByScope,
        Dictionary<string, ExtensionTableBuilder> extensionTablesByScope,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        IReadOnlySet<string> referenceObjectPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(schema);

        switch (schemaKind)
        {
            case SchemaKind.Object:
                WalkObjectSchema(
                    schema,
                    tableBuilder,
                    pathSegments,
                    columnSegments,
                    schemaPath,
                    hasOptionalAncestor,
                    hasOverriddenAncestor,
                    context,
                    overrideProvider,
                    resourceLabel,
                    extensionProject,
                    baseRootBaseName,
                    extensionRootBaseName,
                    baseTablesByScope,
                    extensionTablesByScope,
                    identityPaths,
                    referenceIdentityPaths,
                    referenceObjectPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                break;
            case SchemaKind.Array:
                WalkArraySchema(
                    schema,
                    tableBuilder,
                    pathSegments,
                    schemaPath,
                    context,
                    overrideProvider,
                    resourceLabel,
                    extensionProject,
                    baseRootBaseName,
                    extensionRootBaseName,
                    baseTablesByScope,
                    extensionTablesByScope,
                    identityPaths,
                    referenceIdentityPaths,
                    referenceObjectPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources,
                    hasOverriddenAncestor
                );
                break;
            case SchemaKind.Scalar:
                var currentPath = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;
                throw new InvalidOperationException($"Unexpected scalar schema at {currentPath}.");
            default:
                throw new InvalidOperationException("Unknown schema kind while deriving extension tables.");
        }
    }

    /// <summary>
    /// Walks an object schema node, adding scalar/descriptor columns and recursing into nested objects/arrays.
    /// </summary>
    private static void WalkObjectSchema(
        JsonObject schema,
        ExtensionTableBuilder? tableBuilder,
        List<JsonPathSegment> pathSegments,
        List<string> columnSegments,
        string schemaPath,
        bool hasOptionalAncestor,
        bool hasOverriddenAncestor,
        RelationalModelBuilderContext context,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        ProjectSchemaInfo extensionProject,
        string baseRootBaseName,
        string extensionRootBaseName,
        IReadOnlyDictionary<string, DbTableModel> baseTablesByScope,
        Dictionary<string, ExtensionTableBuilder> extensionTablesByScope,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        IReadOnlySet<string> referenceObjectPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            return;
        }

        var scopePath = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException($"Expected properties to be an object at {scopePath}.");
        }

        var requiredProperties = GetRequiredProperties(schema, pathSegments);
        var isReferenceScope = referenceObjectPaths.Contains(scopePath);

        foreach (var property in propertiesObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
            {
                var propertyPathForError = BuildPropertyPath(pathSegments, property.Key);

                throw new InvalidOperationException(
                    $"Expected property schema to be an object at {propertyPathForError}."
                );
            }

            if (string.Equals(property.Key, ExtensionPropertyName, StringComparison.Ordinal))
            {
                HandleExtensionProperty(
                    propertySchema,
                    pathSegments,
                    schemaPath,
                    requiredProperties,
                    hasOptionalAncestor,
                    hasOverriddenAncestor,
                    context,
                    overrideProvider,
                    resourceLabel,
                    extensionProject,
                    baseRootBaseName,
                    extensionRootBaseName,
                    baseTablesByScope,
                    extensionTablesByScope,
                    identityPaths,
                    referenceIdentityPaths,
                    referenceObjectPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                continue;
            }

            if (isReferenceScope && string.Equals(property.Key, "link", StringComparison.Ordinal))
            {
                continue;
            }

            var propertyPathSegments = BuildPropertySegments(pathSegments, property.Key);
            var propertyColumnSegments = BuildPropertyColumnSegments(columnSegments, property.Key);
            var propertyPath = JsonPathExpressionCompiler.FromSegments(propertyPathSegments);
            var propertySchemaPath = $"{schemaPath}.properties.{property.Key}";
            var isRequired = requiredProperties.Contains(property.Key);
            var isXNullable = IsXNullable(propertySchema, propertyPath.Canonical);
            var isOptional = !isRequired;
            var isNullable = hasOptionalAncestor || isOptional || isXNullable;
            var nextHasOptionalAncestor = hasOptionalAncestor || isOptional || isXNullable;

            JsonSchemaUnsupportedKeywordValidator.Validate(propertySchema, propertySchemaPath);

            var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(propertySchema);
            switch (schemaKind)
            {
                case SchemaKind.Object:
                case SchemaKind.Array:
                    WalkSchema(
                        propertySchema,
                        tableBuilder,
                        propertyPathSegments,
                        propertyColumnSegments,
                        propertySchemaPath,
                        nextHasOptionalAncestor,
                        hasOverriddenAncestor,
                        context,
                        overrideProvider,
                        resourceLabel,
                        extensionProject,
                        baseRootBaseName,
                        extensionRootBaseName,
                        baseTablesByScope,
                        extensionTablesByScope,
                        identityPaths,
                        referenceIdentityPaths,
                        referenceObjectPaths,
                        usedDescriptorPaths,
                        descriptorEdgeSources
                    );
                    break;
                case SchemaKind.Scalar:
                    if (tableBuilder is null)
                    {
                        if (context.TryGetDescriptorPath(propertyPath, out _))
                        {
                            usedDescriptorPaths.Add(propertyPath.Canonical);
                        }

                        continue;
                    }

                    AddScalarOrDescriptorColumn(
                        tableBuilder,
                        propertySchema,
                        propertyColumnSegments,
                        propertyPath,
                        isNullable,
                        context,
                        overrideProvider,
                        resourceLabel,
                        identityPaths,
                        referenceIdentityPaths,
                        usedDescriptorPaths,
                        descriptorEdgeSources
                    );
                    break;
                default:
                    throw new InvalidOperationException($"Unknown schema kind at {propertyPath.Canonical}.");
            }
        }
    }

    /// <summary>
    /// Handles an <c>_ext</c> property by selecting the matching extension project subtree and walking it into
    /// an extension table aligned to the owning base scope.
    /// </summary>
    private static void HandleExtensionProperty(
        JsonObject extensionSchema,
        List<JsonPathSegment> owningScopeSegments,
        string schemaPath,
        HashSet<string> requiredProperties,
        bool hasOptionalAncestor,
        bool hasOverriddenAncestor,
        RelationalModelBuilderContext context,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        ProjectSchemaInfo extensionProject,
        string baseRootBaseName,
        string extensionRootBaseName,
        IReadOnlyDictionary<string, DbTableModel> baseTablesByScope,
        Dictionary<string, ExtensionTableBuilder> extensionTablesByScope,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        IReadOnlySet<string> referenceObjectPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        if (
            !extensionSchema.TryGetPropertyValue("properties", out var projectKeysNode)
            || projectKeysNode is null
        )
        {
            throw new InvalidOperationException(
                $"Expected extension site properties at {schemaPath}.properties.{ExtensionPropertyName}."
            );
        }

        if (projectKeysNode is not JsonObject projectKeysObject)
        {
            throw new InvalidOperationException(
                $"Expected extension site properties to be an object at {schemaPath}.properties.{ExtensionPropertyName}."
            );
        }

        var projectKey = ResolveExtensionProjectKey(projectKeysObject, extensionProject, schemaPath);

        if (!projectKeysObject.TryGetPropertyValue(projectKey, out var projectSchemaNode))
        {
            throw new InvalidOperationException(
                $"Extension project key '{projectKey}' not found at {schemaPath}.properties.{ExtensionPropertyName}."
            );
        }

        if (projectSchemaNode is null)
        {
            throw new InvalidOperationException(
                $"Expected extension project schema to be non-null at {schemaPath}.properties.{ExtensionPropertyName}.{projectKey}."
            );
        }

        if (projectSchemaNode is not JsonObject projectSchema)
        {
            throw new InvalidOperationException(
                $"Expected extension project schema to be an object at {schemaPath}.properties.{ExtensionPropertyName}.{projectKey}."
            );
        }

        var extensionPathSegments = BuildPropertySegments(owningScopeSegments, ExtensionPropertyName);
        var extensionPath = JsonPathExpressionCompiler.FromSegments(extensionPathSegments);
        var isExtensionRequired = requiredProperties.Contains(ExtensionPropertyName);
        var isExtensionXNullable = IsXNullable(extensionSchema, extensionPath.Canonical);
        var extensionHasOptionalAncestor =
            hasOptionalAncestor || !isExtensionRequired || isExtensionXNullable;

        var extensionRequiredProperties = GetRequiredProperties(extensionSchema, extensionPathSegments);
        var projectPathSegments = BuildPropertySegments(extensionPathSegments, projectKey);
        var projectPath = JsonPathExpressionCompiler.FromSegments(projectPathSegments);
        var projectSchemaPath = $"{schemaPath}.properties.{ExtensionPropertyName}.properties.{projectKey}";
        var isProjectRequired = extensionRequiredProperties.Contains(projectKey);
        var isProjectXNullable = IsXNullable(projectSchema, projectPath.Canonical);
        var projectHasOptionalAncestor =
            extensionHasOptionalAncestor || !isProjectRequired || isProjectXNullable;

        JsonSchemaUnsupportedKeywordValidator.Validate(projectSchema, projectSchemaPath);

        var extensionTableBuilder = GetOrCreateExtensionTableBuilder(
            owningScopeSegments,
            projectKey,
            projectPath,
            baseRootBaseName,
            extensionRootBaseName,
            baseTablesByScope,
            extensionTablesByScope,
            extensionProject,
            overrideProvider,
            resourceLabel,
            context.OverrideCollisionDetector,
            string.IsNullOrWhiteSpace(context.SuperclassResourceName)
                ? null
                : RelationalNameConventions.ToPascalCase(context.SuperclassResourceName)
        );

        WalkSchema(
            projectSchema,
            extensionTableBuilder,
            projectPathSegments,
            [],
            projectSchemaPath,
            projectHasOptionalAncestor,
            hasOverriddenAncestor,
            context,
            overrideProvider,
            resourceLabel,
            extensionProject,
            baseRootBaseName,
            extensionRootBaseName,
            baseTablesByScope,
            extensionTablesByScope,
            identityPaths,
            referenceIdentityPaths,
            referenceObjectPaths,
            usedDescriptorPaths,
            descriptorEdgeSources
        );
    }

    /// <summary>
    /// Walks an array schema, deriving child extension tables for array-of-object shapes and handling
    /// scalar/descriptor-array special cases.
    /// </summary>
    private static void WalkArraySchema(
        JsonObject schema,
        ExtensionTableBuilder? tableBuilder,
        List<JsonPathSegment> propertySegments,
        string schemaPath,
        RelationalModelBuilderContext context,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        ProjectSchemaInfo extensionProject,
        string baseRootBaseName,
        string extensionRootBaseName,
        IReadOnlyDictionary<string, DbTableModel> baseTablesByScope,
        Dictionary<string, ExtensionTableBuilder> extensionTablesByScope,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        IReadOnlySet<string> referenceObjectPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources,
        bool hasOverriddenAncestor
    )
    {
        var arrayPath = JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;

        if (!schema.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
        {
            throw new InvalidOperationException($"Array schema items must be an object at {arrayPath}.");
        }

        if (itemsNode is not JsonObject itemsSchema)
        {
            throw new InvalidOperationException($"Array schema items must be an object at {arrayPath}.");
        }

        var itemsSchemaPath = $"{schemaPath}.items";

        JsonSchemaUnsupportedKeywordValidator.Validate(itemsSchema, itemsSchemaPath);

        if (propertySegments.Count == 0 || propertySegments[^1] is not JsonPathSegment.Property property)
        {
            throw new InvalidOperationException("Array schema must be rooted at a property segment.");
        }

        List<JsonPathSegment> arraySegments = [.. propertySegments, new JsonPathSegment.AnyArrayElement()];
        var arrayPathExpression = JsonPathExpressionCompiler.FromSegments(arraySegments);
        var arrayScope = arrayPathExpression.Canonical;
        var defaultCollectionBaseName = RelationalNameConventions.ToCollectionBaseName(property.Name);
        var parentSuffix = tableBuilder is null
            ? string.Empty
            : string.Concat(tableBuilder.CollectionBaseNames);
        var hasOverride = overrideProvider.TryGetNameOverride(
            arrayPathExpression,
            NameOverrideKind.Collection,
            out var overrideName
        );
        var nextHasOverriddenAncestor = hasOverriddenAncestor || hasOverride;

        var itemsKind = JsonSchemaTraversalConventions.DetermineSchemaKind(itemsSchema);

        if (tableBuilder is null)
        {
            if (itemsKind == SchemaKind.Object)
            {
                WalkSchema(
                    itemsSchema,
                    tableBuilder: null,
                    arraySegments,
                    [],
                    itemsSchemaPath,
                    hasOptionalAncestor: false,
                    hasOverriddenAncestor: nextHasOverriddenAncestor,
                    context,
                    overrideProvider,
                    resourceLabel,
                    extensionProject,
                    baseRootBaseName,
                    extensionRootBaseName,
                    baseTablesByScope,
                    extensionTablesByScope,
                    identityPaths,
                    referenceIdentityPaths,
                    referenceObjectPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                return;
            }

            if (itemsKind == SchemaKind.Scalar)
            {
                var elementPath = JsonPathExpressionCompiler.FromSegments(arraySegments);

                if (context.TryGetDescriptorPath(elementPath, out _))
                {
                    usedDescriptorPaths.Add(elementPath.Canonical);
                }
            }

            return;
        }

        var collectionBaseName = defaultCollectionBaseName;

        if (hasOverride)
        {
            var superclassBaseName = string.IsNullOrWhiteSpace(context.SuperclassResourceName)
                ? null
                : RelationalNameConventions.ToPascalCase(context.SuperclassResourceName);
            var includeSuperclass =
                !string.IsNullOrWhiteSpace(superclassBaseName)
                && !string.Equals(overrideName, defaultCollectionBaseName, StringComparison.Ordinal);
            var impliedPrefixes = RelationalModelSetSchemaHelpers.BuildCollectionOverridePrefixes(
                extensionRootBaseName,
                parentSuffix,
                baseRootBaseName,
                includeSuperclass ? superclassBaseName : null
            );

            collectionBaseName = RelationalModelSetSchemaHelpers.ResolveCollectionOverrideBaseName(
                overrideName,
                impliedPrefixes,
                arrayPathExpression.Canonical,
                resourceLabel
            );
        }

        if (itemsKind == SchemaKind.Object && HasExtensionProperty(itemsSchema))
        {
            WalkSchema(
                itemsSchema,
                tableBuilder: null,
                arraySegments,
                [],
                itemsSchemaPath,
                hasOptionalAncestor: false,
                hasOverriddenAncestor: nextHasOverriddenAncestor,
                context,
                overrideProvider,
                resourceLabel,
                extensionProject,
                baseRootBaseName,
                extensionRootBaseName,
                baseTablesByScope,
                extensionTablesByScope,
                identityPaths,
                referenceIdentityPaths,
                referenceObjectPaths,
                usedDescriptorPaths,
                descriptorEdgeSources
            );
            return;
        }

        if (!extensionTablesByScope.TryGetValue(arrayScope, out var childTable))
        {
            childTable = CreateChildTableBuilder(
                tableBuilder,
                propertySegments,
                arraySegments,
                baseRootBaseName,
                extensionRootBaseName,
                extensionProject,
                collectionBaseName,
                defaultCollectionBaseName,
                context.OverrideCollisionDetector,
                resourceLabel
            );
            extensionTablesByScope[arrayScope] = childTable;
        }

        switch (itemsKind)
        {
            case SchemaKind.Object:
                WalkSchema(
                    itemsSchema,
                    childTable,
                    arraySegments,
                    [],
                    itemsSchemaPath,
                    hasOptionalAncestor: false,
                    hasOverriddenAncestor: nextHasOverriddenAncestor,
                    context,
                    overrideProvider,
                    resourceLabel,
                    extensionProject,
                    baseRootBaseName,
                    extensionRootBaseName,
                    baseTablesByScope,
                    extensionTablesByScope,
                    identityPaths,
                    referenceIdentityPaths,
                    referenceObjectPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                break;
            case SchemaKind.Scalar:
                AddDescriptorArrayColumn(
                    childTable,
                    itemsSchema,
                    propertySegments,
                    arraySegments,
                    context,
                    overrideProvider,
                    resourceLabel,
                    identityPaths,
                    referenceIdentityPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                break;
            case SchemaKind.Array:
                throw new InvalidOperationException($"Array schema items must be an object at {arrayPath}.");
            default:
                throw new InvalidOperationException($"Unknown schema kind at {arrayPath}.");
        }
    }

    /// <summary>
    /// Gets an existing extension table builder for the given extension project scope or creates one aligned to
    /// the corresponding base table scope.
    /// </summary>
    private static ExtensionTableBuilder GetOrCreateExtensionTableBuilder(
        List<JsonPathSegment> owningScopeSegments,
        string projectKey,
        JsonPathExpression projectPath,
        string baseRootBaseName,
        string extensionRootBaseName,
        IReadOnlyDictionary<string, DbTableModel> baseTablesByScope,
        Dictionary<string, ExtensionTableBuilder> extensionTablesByScope,
        ProjectSchemaInfo extensionProject,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        OverrideCollisionDetector? collisionDetector,
        string? superclassBaseName
    )
    {
        if (extensionTablesByScope.TryGetValue(projectPath.Canonical, out var existing))
        {
            return existing;
        }

        var baseScopeSegments = StripExtensionRootPrefix(owningScopeSegments, projectKey);
        var baseTableScopeSegments = TrimToTableScope(baseScopeSegments);
        var baseTableScope = JsonPathExpressionCompiler.FromSegments(baseTableScopeSegments).Canonical;

        if (!baseTablesByScope.TryGetValue(baseTableScope, out var baseTable))
        {
            throw new InvalidOperationException(
                $"Extension scope '{projectPath.Canonical}' maps to base scope '{baseTableScope}', "
                    + "but no base table was found for that scope."
            );
        }

        var (collectionBaseNames, defaultCollectionBaseNames) = BuildCollectionBaseNames(
            baseTableScopeSegments,
            overrideProvider,
            resourceLabel,
            baseRootBaseName,
            extensionRootBaseName,
            superclassBaseName
        );
        var tableName = new DbTableName(
            extensionProject.PhysicalSchema,
            extensionRootBaseName + string.Concat(collectionBaseNames)
        );
        var tableKey =
            collectionBaseNames.Count == 0
                ? BuildRootTableKey()
                : BuildChildTableKey(baseRootBaseName, collectionBaseNames);
        var originalKey =
            defaultCollectionBaseNames.Count == 0
                ? BuildRootTableKey()
                : BuildChildTableKey(baseRootBaseName, defaultCollectionBaseNames);

        var keyColumns = BuildKeyColumns(tableKey.Columns);
        var fkColumns = tableKey.Columns.Select(column => column.ColumnName).ToArray();
        var fkName = ConstraintNaming.BuildForeignKeyName(tableName, baseTable.Table.Name);

        TableConstraint[] constraints =
        [
            new TableConstraint.ForeignKey(
                fkName,
                fkColumns,
                baseTable.Table,
                baseTable.Key.Columns.Select(column => column.ColumnName).ToArray(),
                OnDelete: ReferentialAction.Cascade
            ),
        ];

        var table = new DbTableModel(tableName, projectPath, tableKey, keyColumns, constraints);
        var originalTableName = extensionRootBaseName + string.Concat(defaultCollectionBaseNames);

        collisionDetector?.RegisterTable(
            tableName,
            originalTableName,
            BuildTableOrigin(tableName, resourceLabel, projectPath)
        );

        for (var index = 0; index < tableKey.Columns.Count; index++)
        {
            var finalColumn = tableKey.Columns[index].ColumnName;
            var originalColumn = originalKey.Columns[index].ColumnName;

            collisionDetector?.RegisterColumn(
                tableName,
                finalColumn,
                originalColumn.Value,
                BuildColumnOrigin(tableName, finalColumn, resourceLabel, projectPath)
            );
        }

        var builder = new ExtensionTableBuilder(table, collectionBaseNames, defaultCollectionBaseNames);

        extensionTablesByScope[projectPath.Canonical] = builder;

        return builder;
    }

    /// <summary>
    /// Creates a child extension table builder for an array-of-object extension property under a parent table.
    /// </summary>
    private static ExtensionTableBuilder CreateChildTableBuilder(
        ExtensionTableBuilder parent,
        List<JsonPathSegment> propertySegments,
        List<JsonPathSegment> arraySegments,
        string baseRootBaseName,
        string extensionRootBaseName,
        ProjectSchemaInfo extensionProject,
        string collectionBaseName,
        string defaultCollectionBaseName,
        OverrideCollisionDetector? collisionDetector,
        string resourceLabel
    )
    {
        var collectionBaseNames = parent.CollectionBaseNames.Concat(new[] { collectionBaseName }).ToArray();
        var defaultCollectionBaseNames = parent
            .DefaultCollectionBaseNames.Concat(new[] { defaultCollectionBaseName })
            .ToArray();

        var tableName = new DbTableName(
            extensionProject.PhysicalSchema,
            extensionRootBaseName + string.Concat(collectionBaseNames)
        );
        var tableKey = BuildChildTableKey(baseRootBaseName, collectionBaseNames);
        var originalKey = BuildChildTableKey(baseRootBaseName, defaultCollectionBaseNames);
        var keyColumns = BuildKeyColumns(tableKey.Columns);

        var parentKeyColumns = BuildParentKeyColumnNames(baseRootBaseName, parent.CollectionBaseNames);
        var fkName = ConstraintNaming.BuildForeignKeyName(tableName, parent.Definition.Table.Name);

        TableConstraint[] constraints =
        [
            new TableConstraint.ForeignKey(
                fkName,
                parentKeyColumns,
                parent.Definition.Table,
                parent.Definition.Key.Columns.Select(column => column.ColumnName).ToArray(),
                OnDelete: ReferentialAction.Cascade
            ),
        ];

        var jsonScope = JsonPathExpressionCompiler.FromSegments(arraySegments);
        var table = new DbTableModel(tableName, jsonScope, tableKey, keyColumns, constraints);

        var originalTableName = extensionRootBaseName + string.Concat(defaultCollectionBaseNames);

        collisionDetector?.RegisterTable(
            tableName,
            originalTableName,
            BuildTableOrigin(tableName, resourceLabel, jsonScope)
        );

        for (var index = 0; index < tableKey.Columns.Count; index++)
        {
            var finalColumn = tableKey.Columns[index].ColumnName;
            var originalColumn = originalKey.Columns[index].ColumnName;

            collisionDetector?.RegisterColumn(
                tableName,
                finalColumn,
                originalColumn.Value,
                BuildColumnOrigin(tableName, finalColumn, resourceLabel, jsonScope)
            );
        }

        return new ExtensionTableBuilder(table, collectionBaseNames, defaultCollectionBaseNames);
    }

    /// <summary>
    /// Builds the ordered collection base names for a table scope from its property/array segments.
    /// </summary>
    private static (IReadOnlyList<string> Effective, IReadOnlyList<string> Default) BuildCollectionBaseNames(
        IReadOnlyList<JsonPathSegment> segments,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        string baseRootBaseName,
        string extensionRootBaseName,
        string? superclassBaseName
    )
    {
        List<string> effective = [];
        List<string> defaults = [];
        for (var index = 0; index < segments.Count - 1; index++)
        {
            if (
                segments[index] is JsonPathSegment.Property property
                && segments[index + 1] is JsonPathSegment.AnyArrayElement
            )
            {
                var defaultBaseName = RelationalNameConventions.ToCollectionBaseName(property.Name);
                var arraySegments = segments.Take(index + 2).ToArray();
                var arrayPath = JsonPathExpressionCompiler.FromSegments(arraySegments);
                var parentSuffix = string.Concat(effective);
                var hasOverride = overrideProvider.TryGetNameOverride(
                    arrayPath,
                    NameOverrideKind.Collection,
                    out var overrideName
                );

                var baseName = defaultBaseName;

                if (hasOverride)
                {
                    var includeSuperclass =
                        !string.IsNullOrWhiteSpace(superclassBaseName)
                        && !string.Equals(overrideName, defaultBaseName, StringComparison.Ordinal);
                    var impliedPrefixes = RelationalModelSetSchemaHelpers.BuildCollectionOverridePrefixes(
                        extensionRootBaseName,
                        parentSuffix,
                        baseRootBaseName,
                        includeSuperclass ? superclassBaseName : null
                    );

                    baseName = RelationalModelSetSchemaHelpers.ResolveCollectionOverrideBaseName(
                        overrideName,
                        impliedPrefixes,
                        arrayPath.Canonical,
                        resourceLabel
                    );
                }

                effective.Add(baseName);
                defaults.Add(defaultBaseName);
            }
        }

        return (effective.ToArray(), defaults.ToArray());
    }

    private static IdentifierCollisionOrigin BuildTableOrigin(
        DbTableName tableName,
        string resourceLabel,
        JsonPathExpression jsonScope
    )
    {
        var description = $"table {tableName.Schema.Value}.{tableName.Name}";

        return new IdentifierCollisionOrigin(description, resourceLabel, jsonScope.Canonical);
    }

    private static IdentifierCollisionOrigin BuildColumnOrigin(
        DbTableName tableName,
        DbColumnName columnName,
        string resourceLabel,
        JsonPathExpression jsonScope
    )
    {
        var description = $"column {tableName.Schema.Value}.{tableName.Name}.{columnName.Value}";

        return new IdentifierCollisionOrigin(description, resourceLabel, jsonScope.Canonical);
    }

    /// <summary>
    /// Strips the leading <c>_ext.{project}</c> prefix from a scope segment list.
    /// </summary>
    private static IReadOnlyList<JsonPathSegment> StripExtensionRootPrefix(
        IReadOnlyList<JsonPathSegment> segments,
        string projectKey
    )
    {
        if (
            segments.Count >= 2
            && segments[0] is JsonPathSegment.Property { Name: ExtensionPropertyName }
            && segments[1] is JsonPathSegment.Property projectSegment
            && string.Equals(projectSegment.Name, projectKey, StringComparison.Ordinal)
        )
        {
            return segments.Skip(2).ToArray();
        }

        if (segments.Count == 0)
        {
            return segments;
        }

        throw new InvalidOperationException(
            $"Expected extension scope to start with '{ExtensionPropertyName}.{projectKey}'."
        );
    }

    /// <summary>
    /// Trims a segment list down to the nearest owning table scope (through the last array wildcard segment).
    /// </summary>
    private static IReadOnlyList<JsonPathSegment> TrimToTableScope(IReadOnlyList<JsonPathSegment> segments)
    {
        var lastArrayIndex = -1;

        for (var index = segments.Count - 1; index >= 0; index--)
        {
            if (segments[index] is JsonPathSegment.AnyArrayElement)
            {
                lastArrayIndex = index;
                break;
            }
        }

        if (lastArrayIndex < 0)
        {
            return Array.Empty<JsonPathSegment>();
        }

        return segments.Take(lastArrayIndex + 1).ToArray();
    }

    /// <summary>
    /// Resolves which extension project key is present under an <c>_ext</c> object using endpoint name or
    /// project name matching.
    /// </summary>
    private static string ResolveExtensionProjectKey(
        JsonObject projectKeysObject,
        ProjectSchemaInfo extensionProject,
        string schemaPath
    )
    {
        var endpointKey = FindMatchingProjectKey(projectKeysObject, extensionProject.ProjectEndpointName);

        if (endpointKey is not null)
        {
            return endpointKey;
        }

        var nameKey = FindMatchingProjectKey(projectKeysObject, extensionProject.ProjectName);

        if (nameKey is not null)
        {
            return nameKey;
        }

        throw new InvalidOperationException(
            $"Extension project key '{extensionProject.ProjectEndpointName}' not found at "
                + $"{schemaPath}.properties.{ExtensionPropertyName}."
        );
    }

    /// <summary>
    /// Finds a matching project key in a <c>_ext</c> object using ordinal string comparison.
    /// </summary>
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

    /// <summary>
    /// Builds a set of canonical reference identity JSONPaths from <c>documentPathsMapping.referenceJsonPaths</c>.
    /// </summary>
    private static HashSet<string> BuildReferenceIdentityPathSet(
        IReadOnlyList<DocumentReferenceMapping> mappings
    )
    {
        HashSet<string> referenceIdentityPaths = new(StringComparer.Ordinal);

        if (mappings.Count == 0)
        {
            return referenceIdentityPaths;
        }

        foreach (var mapping in mappings)
        {
            foreach (var binding in mapping.ReferenceJsonPaths)
            {
                referenceIdentityPaths.Add(binding.ReferenceJsonPath.Canonical);
            }
        }

        return referenceIdentityPaths;
    }

    /// <summary>
    /// Builds a set of canonical reference object JSONPaths from the document reference mappings.
    /// </summary>
    private static HashSet<string> BuildReferenceObjectPathSet(
        IReadOnlyList<DocumentReferenceMapping> mappings
    )
    {
        HashSet<string> referenceObjectPaths = new(StringComparer.Ordinal);

        if (mappings.Count == 0)
        {
            return referenceObjectPaths;
        }

        foreach (var mapping in mappings)
        {
            referenceObjectPaths.Add(mapping.ReferenceObjectPath.Canonical);
        }

        return referenceObjectPaths;
    }

    /// <summary>
    /// Adds a descriptor FK column for an array whose items are scalar descriptor values.
    /// </summary>
    private static void AddDescriptorArrayColumn(
        ExtensionTableBuilder tableBuilder,
        JsonObject itemsSchema,
        List<JsonPathSegment> propertySegments,
        List<JsonPathSegment> arraySegments,
        RelationalModelBuilderContext context,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        var elementPath = JsonPathExpressionCompiler.FromSegments(arraySegments);

        if (!context.TryGetDescriptorPath(elementPath, out _))
        {
            var arrayPath = JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;

            throw new InvalidOperationException($"Array schema items must be an object at {arrayPath}.");
        }

        var columnSegments = BuildDescriptorArrayColumnSegments(propertySegments);
        var isNullable = IsXNullable(itemsSchema, elementPath.Canonical);

        AddScalarOrDescriptorColumn(
            tableBuilder,
            itemsSchema,
            columnSegments,
            elementPath,
            isNullable,
            context,
            overrideProvider,
            resourceLabel,
            identityPaths,
            referenceIdentityPaths,
            usedDescriptorPaths,
            descriptorEdgeSources
        );
    }

    /// <summary>
    /// Returns true when the schema contains an <c>_ext</c> property under its properties object.
    /// </summary>
    private static bool HasExtensionProperty(JsonObject schema)
    {
        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            return false;
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException("Expected properties to be an object.");
        }

        return propertiesObject.ContainsKey(ExtensionPropertyName);
    }

    /// <summary>
    /// Adds either a scalar column or a bound descriptor FK column for the given schema node at the source path.
    /// </summary>
    private static void AddScalarOrDescriptorColumn(
        ExtensionTableBuilder tableBuilder,
        JsonObject schema,
        IReadOnlyList<string> columnSegments,
        JsonPathExpression sourcePath,
        bool isNullable,
        RelationalModelBuilderContext context,
        INameOverrideProvider overrideProvider,
        string resourceLabel,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        if (referenceIdentityPaths.Contains(sourcePath.Canonical))
        {
            if (context.TryGetDescriptorPath(sourcePath, out _))
            {
                usedDescriptorPaths.Add(sourcePath.Canonical);
            }

            return;
        }

        if (context.TryGetDescriptorPath(sourcePath, out var descriptorPathInfo))
        {
            var descriptorBaseName = ResolveColumnBaseName(
                overrideProvider,
                sourcePath,
                columnSegments,
                out var originalDescriptorBaseName
            );
            var columnName = RelationalNameConventions.DescriptorIdColumnName(descriptorBaseName);
            var originalColumnName = RelationalNameConventions.DescriptorIdColumnName(
                originalDescriptorBaseName
            );
            var column = new DbColumnModel(
                columnName,
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int64),
                isNullable,
                descriptorPathInfo.DescriptorValuePath,
                descriptorPathInfo.DescriptorResource
            );

            tableBuilder.AddColumn(
                column,
                originalColumnName.Value,
                BuildCollisionOrigin(
                    tableBuilder.Definition.Table,
                    columnName,
                    descriptorPathInfo.DescriptorValuePath,
                    resourceLabel
                )
            );
            context.OverrideCollisionDetector?.RegisterColumn(
                tableBuilder.Definition.Table,
                columnName,
                originalColumnName.Value,
                BuildCollisionOrigin(
                    tableBuilder.Definition.Table,
                    columnName,
                    descriptorPathInfo.DescriptorValuePath,
                    resourceLabel
                )
            );
            tableBuilder.AddConstraint(
                new TableConstraint.ForeignKey(
                    ConstraintNaming.BuildDescriptorForeignKeyName(tableBuilder.Definition.Table, columnName),
                    new[] { columnName },
                    _descriptorTableName,
                    new[] { RelationalNameConventions.DocumentIdColumnName },
                    OnDelete: ReferentialAction.NoAction,
                    OnUpdate: ReferentialAction.NoAction
                )
            );

            var isIdentityComponent = identityPaths.Contains(sourcePath.Canonical);
            descriptorEdgeSources.Add(
                new DescriptorEdgeSource(
                    isIdentityComponent,
                    descriptorPathInfo.DescriptorValuePath,
                    tableBuilder.Definition.Table,
                    columnName,
                    descriptorPathInfo.DescriptorResource
                )
            );

            usedDescriptorPaths.Add(sourcePath.Canonical);
            return;
        }

        var scalarType = ResolveScalarType(schema, sourcePath, context);
        var scalarBaseName = ResolveColumnBaseName(
            overrideProvider,
            sourcePath,
            columnSegments,
            out var originalBaseName
        );
        var scalarColumn = new DbColumnModel(
            new DbColumnName(scalarBaseName),
            ColumnKind.Scalar,
            scalarType,
            isNullable,
            sourcePath,
            null
        );

        tableBuilder.AddColumn(
            scalarColumn,
            originalBaseName,
            BuildCollisionOrigin(
                tableBuilder.Definition.Table,
                scalarColumn.ColumnName,
                sourcePath,
                resourceLabel
            )
        );
        context.OverrideCollisionDetector?.RegisterColumn(
            tableBuilder.Definition.Table,
            scalarColumn.ColumnName,
            originalBaseName,
            BuildCollisionOrigin(
                tableBuilder.Definition.Table,
                scalarColumn.ColumnName,
                sourcePath,
                resourceLabel
            )
        );
    }

    private static string ResolveColumnBaseName(
        INameOverrideProvider overrideProvider,
        JsonPathExpression sourcePath,
        IReadOnlyList<string> columnSegments,
        out string originalBaseName
    )
    {
        originalBaseName = BuildColumnBaseName(columnSegments);

        return overrideProvider.TryGetNameOverride(sourcePath, NameOverrideKind.Column, out var overrideName)
            ? overrideName
            : originalBaseName;
    }

    private static IdentifierCollisionOrigin BuildCollisionOrigin(
        DbTableName tableName,
        DbColumnName columnName,
        JsonPathExpression sourcePath,
        string resourceLabel
    )
    {
        var description = $"column {tableName.Schema.Value}.{tableName.Name}.{columnName.Value}";

        return new IdentifierCollisionOrigin(description, resourceLabel, sourcePath.Canonical);
    }

    /// <summary>
    /// Resolves the relational scalar type for an extension schema node at the supplied path.
    /// </summary>
    private static RelationalScalarType ResolveScalarType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        var schemaType = GetSchemaType(schema, sourcePath.Canonical);

        return schemaType switch
        {
            "string" => ResolveStringType(schema, sourcePath, context),
            "integer" => ResolveIntegerType(schema, sourcePath),
            "number" => ResolveDecimalType(sourcePath, context),
            "boolean" => new RelationalScalarType(ScalarKind.Boolean),
            _ => throw new InvalidOperationException(
                $"Unsupported scalar type '{schemaType}' at {sourcePath.Canonical}."
            ),
        };
    }

    /// <summary>
    /// Resolves a string schema to a relational type, using format hints when present.
    /// </summary>
    private static RelationalScalarType ResolveStringType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        if (!string.IsNullOrWhiteSpace(format))
        {
            return format switch
            {
                "date" => new RelationalScalarType(ScalarKind.Date),
                "date-time" => new RelationalScalarType(ScalarKind.DateTime),
                "time" => new RelationalScalarType(ScalarKind.Time),
                _ => BuildStringType(schema, sourcePath, context),
            };
        }

        return BuildStringType(schema, sourcePath, context);
    }

    /// <summary>
    /// Resolves an unformatted string schema to a relational string type, enforcing max length when required.
    /// </summary>
    private static RelationalScalarType BuildStringType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        if (!schema.TryGetPropertyValue("maxLength", out var maxLengthNode) || maxLengthNode is null)
        {
            if (IsMaxLengthOmissionAllowed(sourcePath, context))
            {
                return new RelationalScalarType(ScalarKind.String);
            }

            throw new InvalidOperationException(
                $"String schema maxLength is required at {sourcePath.Canonical}. "
                    + "Set maxLength in MetaEd for string/sharedString."
            );
        }

        if (maxLengthNode is not JsonValue maxLengthValue)
        {
            throw new InvalidOperationException(
                $"Expected maxLength to be a number at {sourcePath.Canonical}."
            );
        }

        var maxLength = maxLengthValue.GetValue<int>();
        if (maxLength <= 0)
        {
            throw new InvalidOperationException(
                $"String schema maxLength must be positive at {sourcePath.Canonical}."
            );
        }

        return new RelationalScalarType(ScalarKind.String, maxLength);
    }

    /// <summary>
    /// Returns true when maxLength may be omitted for the given string path.
    /// </summary>
    private static bool IsMaxLengthOmissionAllowed(
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        return context.StringMaxLengthOmissionPaths.Contains(sourcePath.Canonical);
    }

    /// <summary>
    /// Resolves an integer schema to a 32-bit or 64-bit relational type based on format.
    /// </summary>
    private static RelationalScalarType ResolveIntegerType(JsonObject schema, JsonPathExpression sourcePath)
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        return format switch
        {
            "int64" => new RelationalScalarType(ScalarKind.Int64),
            _ => new RelationalScalarType(ScalarKind.Int32),
        };
    }

    /// <summary>
    /// Resolves a decimal schema to a relational decimal type using the required totalDigits/decimalPlaces
    /// metadata.
    /// </summary>
    private static RelationalScalarType ResolveDecimalType(
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        if (!context.TryGetDecimalPropertyValidationInfo(sourcePath, out var validationInfo))
        {
            throw new InvalidOperationException(
                $"Decimal property validation info is required for number properties at {sourcePath.Canonical}."
            );
        }

        if (validationInfo.TotalDigits is null || validationInfo.DecimalPlaces is null)
        {
            throw new InvalidOperationException(
                $"Decimal property validation info must include totalDigits and decimalPlaces at {sourcePath.Canonical}."
            );
        }

        if (validationInfo.TotalDigits <= 0 || validationInfo.DecimalPlaces < 0)
        {
            throw new InvalidOperationException(
                $"Decimal property validation info must be positive for {sourcePath.Canonical}."
            );
        }

        if (validationInfo.DecimalPlaces > validationInfo.TotalDigits)
        {
            throw new InvalidOperationException(
                $"Decimal places cannot exceed total digits for {sourcePath.Canonical}."
            );
        }

        return new RelationalScalarType(
            ScalarKind.Decimal,
            Decimal: (validationInfo.TotalDigits.Value, validationInfo.DecimalPlaces.Value)
        );
    }

    /// <summary>
    /// Reads <c>x-nullable</c> (an OpenAPI extension commonly used in Ed-Fi schemas) as an override for
    /// JSON Schema required-ness.
    /// </summary>
    private static bool IsXNullable(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("x-nullable", out var nullableNode) || nullableNode is null)
        {
            return false;
        }

        if (nullableNode is not JsonValue jsonValue)
        {
            throw new InvalidOperationException($"Expected x-nullable to be a boolean at {path}.");
        }

        return jsonValue.GetValue<bool>();
    }

    /// <summary>
    /// Returns the JSON Schema <c>type</c> for the node, throwing when missing or non-string.
    /// </summary>
    private static string GetSchemaType(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("type", out var typeNode) || typeNode is null)
        {
            throw new InvalidOperationException($"Schema type must be specified at {path}.");
        }

        return typeNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            _ => throw new InvalidOperationException($"Expected type to be a string at {path}.type."),
        };
    }

    /// <summary>
    /// Reads an optional string-valued schema property, returning null when absent.
    /// </summary>
    private static string? GetOptionalString(JsonObject schema, string propertyName, string path)
    {
        if (!schema.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        return valueNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string at {path}.{propertyName}."
            ),
        };
    }

    /// <summary>
    /// Builds a deterministic column base name by PascalCasing each segment.
    /// </summary>
    private static string BuildColumnBaseName(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Column path must contain at least one segment.");
        }

        StringBuilder builder = new();

        foreach (var segment in segments)
        {
            builder.Append(RelationalNameConventions.ToPascalCase(segment));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds column name segments for a descriptor array property.
    /// </summary>
    private static List<string> BuildDescriptorArrayColumnSegments(
        IReadOnlyList<JsonPathSegment> propertySegments
    )
    {
        if (propertySegments.Count == 0 || propertySegments[^1] is not JsonPathSegment.Property property)
        {
            throw new InvalidOperationException("Array schema must be rooted at a property segment.");
        }

        var singular = RelationalNameConventions.SingularizeCollectionSegment(property.Name);
        List<string> columnSegments = [singular];

        return columnSegments;
    }

    /// <summary>
    /// Builds a property path segment list by appending the property name to the current scope segments.
    /// </summary>
    private static List<JsonPathSegment> BuildPropertySegments(
        List<JsonPathSegment> pathSegments,
        string propertyName
    )
    {
        List<JsonPathSegment> propertySegments =
        [
            .. pathSegments,
            new JsonPathSegment.Property(propertyName),
        ];

        return propertySegments;
    }

    /// <summary>
    /// Builds column name segments by appending the property name to the current column segments.
    /// </summary>
    private static List<string> BuildPropertyColumnSegments(List<string> columnSegments, string propertyName)
    {
        List<string> propertyColumnSegments = [.. columnSegments, propertyName];

        return propertyColumnSegments;
    }

    /// <summary>
    /// Returns the required property name set for an object schema at the provided scope.
    /// </summary>
    private static HashSet<string> GetRequiredProperties(
        JsonObject schema,
        List<JsonPathSegment> pathSegments
    )
    {
        var path = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;

        if (!schema.TryGetPropertyValue("required", out var requiredNode) || requiredNode is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (requiredNode is not JsonArray requiredArray)
        {
            throw new InvalidOperationException($"Expected required to be an array at {path}.required.");
        }

        HashSet<string> requiredProperties = new(StringComparer.Ordinal);

        foreach (var requiredEntry in requiredArray)
        {
            if (requiredEntry is null)
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be non-null at {path}.required."
                );
            }

            if (requiredEntry is not JsonValue jsonValue)
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be strings at {path}.required."
                );
            }

            var name = jsonValue.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be non-empty at {path}.required."
                );
            }

            requiredProperties.Add(name);
        }

        return requiredProperties;
    }

    /// <summary>
    /// Builds a canonical property JSONPath string for diagnostics.
    /// </summary>
    private static string BuildPropertyPath(List<JsonPathSegment> scopeSegments, string propertyName)
    {
        List<JsonPathSegment> propertySegments =
        [
            .. scopeSegments,
            new JsonPathSegment.Property(propertyName),
        ];

        return JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;
    }

    /// <summary>
    /// Validates that all descriptor paths were observed during schema traversal.
    /// </summary>
    private static void EnsureAllDescriptorPathsUsed(
        RelationalModelBuilderContext context,
        HashSet<string> usedDescriptorPaths
    )
    {
        if (context.DescriptorPathsByJsonPath.Count == 0)
        {
            return;
        }

        if (usedDescriptorPaths.Count == context.DescriptorPathsByJsonPath.Count)
        {
            return;
        }

        var missingPaths = context
            .DescriptorPathsByJsonPath.Keys.Where(path => !usedDescriptorPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missingPaths.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Descriptor paths were not found in JSON schema: {string.Join(", ", missingPaths)}."
        );
    }

    /// <summary>
    /// Counts the number of wildcard array segments in a scope path.
    /// </summary>
    private static int CountArrayDepth(JsonPathExpression scope)
    {
        return scope.Segments.Count(segment => segment is JsonPathSegment.AnyArrayElement);
    }

    /// <summary>
    /// Builds the root table key for an extension table aligned to the base root scope.
    /// </summary>
    private static TableKey BuildRootTableKey()
    {
        return new TableKey(
            new[]
            {
                new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart),
            }
        );
    }

    /// <summary>
    /// Builds a child table key for an extension collection table aligned to a base collection scope.
    /// </summary>
    private static TableKey BuildChildTableKey(
        string baseRootBaseName,
        IReadOnlyList<string> collectionBaseNames
    )
    {
        List<DbKeyColumn> keyColumns =
        [
            new DbKeyColumn(
                RelationalNameConventions.RootDocumentIdColumnName(baseRootBaseName),
                ColumnKind.ParentKeyPart
            ),
        ];

        for (var index = 0; index < collectionBaseNames.Count - 1; index++)
        {
            keyColumns.Add(
                new DbKeyColumn(
                    RelationalNameConventions.ParentCollectionOrdinalColumnName(collectionBaseNames[index]),
                    ColumnKind.ParentKeyPart
                )
            );
        }

        keyColumns.Add(new DbKeyColumn(RelationalNameConventions.OrdinalColumnName, ColumnKind.Ordinal));

        return new TableKey(keyColumns.ToArray());
    }

    /// <summary>
    /// Builds the parent key column name list for a child extension table FK to its parent table.
    /// </summary>
    private static DbColumnName[] BuildParentKeyColumnNames(
        string baseRootBaseName,
        IReadOnlyList<string> parentCollectionBaseNames
    )
    {
        List<DbColumnName> keyColumns =
        [
            RelationalNameConventions.RootDocumentIdColumnName(baseRootBaseName),
        ];

        foreach (var collectionBaseName in parentCollectionBaseNames)
        {
            keyColumns.Add(RelationalNameConventions.ParentCollectionOrdinalColumnName(collectionBaseName));
        }

        return keyColumns.ToArray();
    }

    /// <summary>
    /// Builds physical column models for the table key columns.
    /// </summary>
    private static DbColumnModel[] BuildKeyColumns(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        DbColumnModel[] columns = new DbColumnModel[keyColumns.Count];

        for (var index = 0; index < keyColumns.Count; index++)
        {
            var keyColumn = keyColumns[index];
            var scalarType = ResolveKeyColumnScalarType(keyColumn);

            columns[index] = new DbColumnModel(
                keyColumn.ColumnName,
                keyColumn.Kind,
                scalarType,
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            );
        }

        return columns;
    }

    /// <summary>
    /// Resolves the scalar type for a key column based on its kind and name.
    /// </summary>
    private static RelationalScalarType ResolveKeyColumnScalarType(DbKeyColumn keyColumn)
    {
        return keyColumn.Kind switch
        {
            ColumnKind.Ordinal => new RelationalScalarType(ScalarKind.Int32),
            ColumnKind.ParentKeyPart => IsDocumentIdColumn(keyColumn.ColumnName)
                ? new RelationalScalarType(ScalarKind.Int64)
                : new RelationalScalarType(ScalarKind.Int32),
            ColumnKind.DocumentFk => new RelationalScalarType(ScalarKind.Int64),
            _ => throw new InvalidOperationException(
                $"Unsupported key column kind '{keyColumn.Kind}' for {keyColumn.ColumnName.Value}."
            ),
        };
    }

    /// <summary>
    /// Returns true when the column name represents a document id key part.
    /// </summary>
    private static bool IsDocumentIdColumn(DbColumnName columnName)
    {
        if (
            string.Equals(
                columnName.Value,
                RelationalNameConventions.DocumentIdColumnName.Value,
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }

        return columnName.Value.EndsWith("_DocumentId", StringComparison.Ordinal);
    }

    /// <summary>
    /// Captures the index and model for a base (non-extension) resource used when resolving extensions.
    /// </summary>
    private sealed record BaseResourceEntry(int Index, ConcreteResourceModel Model);

    /// <summary>
    /// Captures the derived extension table models and descriptor edge sources for an extension resource.
    /// </summary>
    private sealed record ExtensionDerivationResult(
        IReadOnlyList<DbTableModel> Tables,
        IReadOnlyList<DescriptorEdgeSource> DescriptorEdgeSources
    );

    /// <summary>
    /// Accumulates columns and constraints for a derived extension table.
    /// </summary>
    private sealed class ExtensionTableBuilder
    {
        private readonly TableColumnAccumulator _accumulator;

        /// <summary>
        /// Creates a builder for the supplied table definition and collection scope.
        /// </summary>
        public ExtensionTableBuilder(
            DbTableModel table,
            IReadOnlyList<string> collectionBaseNames,
            IReadOnlyList<string> defaultCollectionBaseNames
        )
        {
            _accumulator = new TableColumnAccumulator(table);
            CollectionBaseNames = collectionBaseNames;
            DefaultCollectionBaseNames = defaultCollectionBaseNames;
        }

        /// <summary>
        /// The current table definition for this builder.
        /// </summary>
        public DbTableModel Definition => _accumulator.Definition;

        /// <summary>
        /// The collection base name chain used when constructing nested extension table names.
        /// </summary>
        public IReadOnlyList<string> CollectionBaseNames { get; }

        /// <summary>
        /// The default collection base name chain used for pre-override collision detection.
        /// </summary>
        public IReadOnlyList<string> DefaultCollectionBaseNames { get; }

        /// <summary>
        /// Adds a column to the table being built.
        /// </summary>
        public void AddColumn(
            DbColumnModel column,
            string? originalName = null,
            IdentifierCollisionOrigin? origin = null
        )
        {
            _accumulator.AddColumn(column, originalName, origin);
        }

        /// <summary>
        /// Adds a constraint to the table being built.
        /// </summary>
        public void AddConstraint(TableConstraint constraint)
        {
            _accumulator.AddConstraint(constraint);
        }

        /// <summary>
        /// Builds the final table model with accumulated columns and constraints.
        /// </summary>
        public DbTableModel Build()
        {
            return _accumulator.Build();
        }
    }
}
