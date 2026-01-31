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
    /// The explicit order for the extension table derivation pass.
    /// </summary>
    public int Order { get; } = 10;

    /// <summary>
    /// Executes extension table derivation across all resource-extension schemas.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseResourcesByName = BuildBaseResourceLookup(context.ConcreteResourcesInNameOrder);
        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (!IsResourceExtension(resourceContext))
            {
                continue;
            }

            if (!resourceContext.Project.ProjectSchema.IsExtensionProject)
            {
                throw new InvalidOperationException(
                    $"Resource extension '{FormatResource(new QualifiedResourceName(resourceContext.Project.ProjectSchema.ProjectName, resourceContext.ResourceName))}' "
                        + "must be defined in an extension project."
                );
            }

            if (!baseResourcesByName.TryGetValue(resourceContext.ResourceName, out var baseEntries))
            {
                throw new InvalidOperationException(
                    $"Resource extension '{FormatResource(new QualifiedResourceName(resourceContext.Project.ProjectSchema.ProjectName, resourceContext.ResourceName))}' "
                        + "did not match a concrete base resource."
                );
            }

            if (baseEntries.Count != 1)
            {
                var candidates = string.Join(
                    ", ",
                    baseEntries
                        .Select(entry => FormatResource(entry.Model.ResourceKey.Resource))
                        .OrderBy(name => name, StringComparer.Ordinal)
                );

                throw new InvalidOperationException(
                    $"Resource extension '{FormatResource(new QualifiedResourceName(resourceContext.Project.ProjectSchema.ProjectName, resourceContext.ResourceName))}' "
                        + $"matched multiple concrete resources: {candidates}."
                );
            }

            var baseEntry = baseEntries[0];
            var baseModel = context.ConcreteResourcesInNameOrder[baseEntry.Index];
            var extensionContext = BuildExtensionContext(
                context,
                resourceContext,
                apiSchemaRootsByProjectEndpoint
            );

            var extensionResult = DeriveExtensionTables(
                extensionContext,
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
            resourceContext.Project.EffectiveProject.ProjectSchema
        );

        var resourceKey = new QualifiedResourceName(projectSchema.ProjectName, resourceContext.ResourceName);
        var descriptorPaths = context.GetExtensionDescriptorPathsForResource(resourceKey);

        var builderContext = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = resourceContext.ResourceEndpointName,
            DescriptorPathSource = DescriptorPathSource.Precomputed,
            DescriptorPathsByJsonPath = descriptorPaths,
        };

        new ExtractInputsStep().Execute(builderContext);
        new ValidateJsonSchemaStep().Execute(builderContext);

        return builderContext;
    }

    private static ExtensionDerivationResult DeriveExtensionTables(
        RelationalModelBuilderContext extensionContext,
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

        var baseRootBaseName = RelationalNameConventions.ToPascalCase(baseModel.Resource.ResourceName);
        var extensionRootBaseName = $"{baseRootBaseName}Extension";
        var baseTablesByScope = baseModel.TablesInReadDependencyOrder.ToDictionary(
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
            extensionContext,
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

    private static RelationalResourceModel MergeExtensionTables(
        RelationalResourceModel baseModel,
        ExtensionDerivationResult extensionResult
    )
    {
        var existingTables = baseModel.TablesInReadDependencyOrder;
        var updatedTables = existingTables.Concat(extensionResult.Tables).ToArray();
        var updatedEdges = baseModel
            .DescriptorEdgeSources.Concat(extensionResult.DescriptorEdgeSources)
            .ToArray();

        return baseModel with
        {
            TablesInReadDependencyOrder = updatedTables,
            TablesInWriteDependencyOrder = updatedTables,
            DescriptorEdgeSources = updatedEdges,
        };
    }

    private static void WalkSchema(
        JsonObject schema,
        ExtensionTableBuilder? tableBuilder,
        List<JsonPathSegment> pathSegments,
        List<string> columnSegments,
        string schemaPath,
        bool hasOptionalAncestor,
        RelationalModelBuilderContext context,
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
                    context,
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
                var currentPath = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;
                throw new InvalidOperationException($"Unexpected scalar schema at {currentPath}.");
            default:
                throw new InvalidOperationException("Unknown schema kind while deriving extension tables.");
        }
    }

    private static void WalkObjectSchema(
        JsonObject schema,
        ExtensionTableBuilder? tableBuilder,
        List<JsonPathSegment> pathSegments,
        List<string> columnSegments,
        string schemaPath,
        bool hasOptionalAncestor,
        RelationalModelBuilderContext context,
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
                    context,
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
                        context,
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
                        throw new InvalidOperationException(
                            $"Scalar value '{propertyPath.Canonical}' was found outside an extension scope."
                        );
                    }

                    AddScalarOrDescriptorColumn(
                        tableBuilder,
                        propertySchema,
                        propertyColumnSegments,
                        propertyPath,
                        isNullable,
                        context,
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

    private static void HandleExtensionProperty(
        JsonObject extensionSchema,
        List<JsonPathSegment> owningScopeSegments,
        string schemaPath,
        HashSet<string> requiredProperties,
        bool hasOptionalAncestor,
        RelationalModelBuilderContext context,
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
            extensionProject
        );

        WalkSchema(
            projectSchema,
            extensionTableBuilder,
            projectPathSegments,
            [],
            projectSchemaPath,
            projectHasOptionalAncestor,
            context,
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

    private static void WalkArraySchema(
        JsonObject schema,
        ExtensionTableBuilder? tableBuilder,
        List<JsonPathSegment> propertySegments,
        string schemaPath,
        RelationalModelBuilderContext context,
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

        List<JsonPathSegment> arraySegments = [.. propertySegments, new JsonPathSegment.AnyArrayElement()];
        var arrayScope = JsonPathExpressionCompiler.FromSegments(arraySegments).Canonical;

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
                    context,
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

            throw new InvalidOperationException(
                $"Array schema '{arrayScope}' was found outside an extension scope."
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
                context,
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
                extensionProject
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
                    context,
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

    private static ExtensionTableBuilder GetOrCreateExtensionTableBuilder(
        List<JsonPathSegment> owningScopeSegments,
        string projectKey,
        JsonPathExpression projectPath,
        string baseRootBaseName,
        string extensionRootBaseName,
        IReadOnlyDictionary<string, DbTableModel> baseTablesByScope,
        Dictionary<string, ExtensionTableBuilder> extensionTablesByScope,
        ProjectSchemaInfo extensionProject
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

        var collectionBaseNames = BuildCollectionBaseNames(baseTableScopeSegments);
        var tableName = new DbTableName(
            extensionProject.PhysicalSchema,
            extensionRootBaseName + string.Concat(collectionBaseNames)
        );
        var tableKey =
            collectionBaseNames.Count == 0
                ? BuildRootTableKey()
                : BuildChildTableKey(baseRootBaseName, collectionBaseNames);

        var keyColumns = BuildKeyColumns(tableKey.Columns);
        var fkColumns = tableKey.Columns.Select(column => column.ColumnName).ToArray();
        var fkName = RelationalNameConventions.ForeignKeyName(tableName.Name, fkColumns);

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
        var builder = new ExtensionTableBuilder(table, collectionBaseNames);

        extensionTablesByScope[projectPath.Canonical] = builder;

        return builder;
    }

    private static ExtensionTableBuilder CreateChildTableBuilder(
        ExtensionTableBuilder parent,
        List<JsonPathSegment> propertySegments,
        List<JsonPathSegment> arraySegments,
        string baseRootBaseName,
        string extensionRootBaseName,
        ProjectSchemaInfo extensionProject
    )
    {
        if (propertySegments.Count == 0 || propertySegments[^1] is not JsonPathSegment.Property property)
        {
            throw new InvalidOperationException("Array schema must be rooted at a property segment.");
        }

        var collectionBaseName = RelationalNameConventions.ToCollectionBaseName(property.Name);
        var collectionBaseNames = parent.CollectionBaseNames.Concat(new[] { collectionBaseName }).ToArray();

        var tableName = new DbTableName(
            extensionProject.PhysicalSchema,
            extensionRootBaseName + string.Concat(collectionBaseNames)
        );
        var tableKey = BuildChildTableKey(baseRootBaseName, collectionBaseNames);
        var keyColumns = BuildKeyColumns(tableKey.Columns);

        var parentKeyColumns = BuildParentKeyColumnNames(baseRootBaseName, parent.CollectionBaseNames);
        var fkName = RelationalNameConventions.ForeignKeyName(tableName.Name, parentKeyColumns);

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

        return new ExtensionTableBuilder(table, collectionBaseNames);
    }

    private static IReadOnlyList<string> BuildCollectionBaseNames(IReadOnlyList<JsonPathSegment> segments)
    {
        List<string> baseNames = [];

        for (var index = 0; index < segments.Count - 1; index++)
        {
            if (
                segments[index] is JsonPathSegment.Property property
                && segments[index + 1] is JsonPathSegment.AnyArrayElement
            )
            {
                baseNames.Add(RelationalNameConventions.ToCollectionBaseName(property.Name));
            }
        }

        return baseNames.ToArray();
    }

    private static IReadOnlyList<JsonPathSegment> StripExtensionRootPrefix(
        IReadOnlyList<JsonPathSegment> segments,
        string projectKey
    )
    {
        if (
            segments.Count >= 2
            && segments[0] is JsonPathSegment.Property { Name: ExtensionPropertyName }
            && segments[1] is JsonPathSegment.Property projectSegment
            && string.Equals(projectSegment.Name, projectKey, StringComparison.OrdinalIgnoreCase)
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

    private static string? FindMatchingProjectKey(JsonObject projectKeysObject, string match)
    {
        foreach (var entry in projectKeysObject)
        {
            if (string.Equals(entry.Key, match, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Key;
            }
        }

        return null;
    }

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

    private static void AddDescriptorArrayColumn(
        ExtensionTableBuilder tableBuilder,
        JsonObject itemsSchema,
        List<JsonPathSegment> propertySegments,
        List<JsonPathSegment> arraySegments,
        RelationalModelBuilderContext context,
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
            identityPaths,
            referenceIdentityPaths,
            usedDescriptorPaths,
            descriptorEdgeSources
        );
    }

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

    private static void AddScalarOrDescriptorColumn(
        ExtensionTableBuilder tableBuilder,
        JsonObject schema,
        IReadOnlyList<string> columnSegments,
        JsonPathExpression sourcePath,
        bool isNullable,
        RelationalModelBuilderContext context,
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
            var descriptorBaseName = BuildColumnBaseName(columnSegments);
            var columnName = RelationalNameConventions.DescriptorIdColumnName(descriptorBaseName);
            var column = new DbColumnModel(
                columnName,
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int64),
                isNullable,
                descriptorPathInfo.DescriptorValuePath,
                descriptorPathInfo.DescriptorResource
            );

            tableBuilder.AddColumn(column);
            tableBuilder.AddConstraint(
                new TableConstraint.ForeignKey(
                    RelationalNameConventions.ForeignKeyName(
                        tableBuilder.Definition.Table.Name,
                        new[] { columnName }
                    ),
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
        var scalarColumn = new DbColumnModel(
            new DbColumnName(BuildColumnBaseName(columnSegments)),
            ColumnKind.Scalar,
            scalarType,
            isNullable,
            sourcePath,
            null
        );

        tableBuilder.AddColumn(scalarColumn);
    }

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

    private static bool IsMaxLengthOmissionAllowed(
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        return context.StringMaxLengthOmissionPaths.Contains(sourcePath.Canonical);
    }

    private static RelationalScalarType ResolveIntegerType(JsonObject schema, JsonPathExpression sourcePath)
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        return format switch
        {
            "int64" => new RelationalScalarType(ScalarKind.Int64),
            _ => new RelationalScalarType(ScalarKind.Int32),
        };
    }

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

    private static List<string> BuildPropertyColumnSegments(List<string> columnSegments, string propertyName)
    {
        List<string> propertyColumnSegments = [.. columnSegments, propertyName];

        return propertyColumnSegments;
    }

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

    private static string BuildPropertyPath(List<JsonPathSegment> scopeSegments, string propertyName)
    {
        List<JsonPathSegment> propertySegments =
        [
            .. scopeSegments,
            new JsonPathSegment.Property(propertyName),
        ];

        return JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;
    }

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

    private static int CountArrayDepth(JsonPathExpression scope)
    {
        return scope.Segments.Count(segment => segment is JsonPathSegment.AnyArrayElement);
    }

    private static TableKey BuildRootTableKey()
    {
        return new TableKey(
            new[]
            {
                new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart),
            }
        );
    }

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

    private static Dictionary<string, List<BaseResourceEntry>> BuildBaseResourceLookup(
        IReadOnlyList<ConcreteResourceModel> resources
    )
    {
        Dictionary<string, List<BaseResourceEntry>> lookup = new(StringComparer.Ordinal);

        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            var resourceName = resource.ResourceKey.Resource.ResourceName;

            if (!lookup.TryGetValue(resourceName, out var entries))
            {
                entries = [];
                lookup.Add(resourceName, entries);
            }

            entries.Add(new BaseResourceEntry(index, resource));
        }

        return lookup;
    }

    private static bool IsResourceExtension(ConcreteResourceSchemaContext resourceContext)
    {
        if (
            !resourceContext.ResourceSchema.TryGetPropertyValue(
                "isResourceExtension",
                out var resourceExtensionNode
            ) || resourceExtensionNode is null
        )
        {
            throw new InvalidOperationException(
                $"Expected isResourceExtension to be on ResourceSchema for resource "
                    + $"'{resourceContext.Project.ProjectSchema.ProjectName}:{resourceContext.ResourceName}', "
                    + "invalid ApiSchema."
            );
        }

        return resourceExtensionNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            _ => throw new InvalidOperationException(
                $"Expected isResourceExtension to be a boolean for resource "
                    + $"'{resourceContext.Project.ProjectSchema.ProjectName}:{resourceContext.ResourceName}', "
                    + "invalid ApiSchema."
            ),
        };
    }

    private static JsonObject GetApiSchemaRoot(
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        string projectEndpointName,
        JsonObject projectSchema
    )
    {
        if (apiSchemaRootsByProjectEndpoint.TryGetValue(projectEndpointName, out var apiSchemaRoot))
        {
            return apiSchemaRoot;
        }

        apiSchemaRoot = new JsonObject { ["projectSchema"] = projectSchema };

        apiSchemaRootsByProjectEndpoint[projectEndpointName] = apiSchemaRoot;

        return apiSchemaRoot;
    }

    private sealed record BaseResourceEntry(int Index, ConcreteResourceModel Model);

    private sealed record ExtensionDerivationResult(
        IReadOnlyList<DbTableModel> Tables,
        IReadOnlyList<DescriptorEdgeSource> DescriptorEdgeSources
    );

    private sealed class ExtensionTableBuilder
    {
        private readonly Dictionary<string, JsonPathExpression?> _columnSources = new(StringComparer.Ordinal);

        public ExtensionTableBuilder(DbTableModel table, IReadOnlyList<string> collectionBaseNames)
        {
            Definition = table;
            CollectionBaseNames = collectionBaseNames;
            Columns = new List<DbColumnModel>(table.Columns);
            Constraints = new List<TableConstraint>(table.Constraints);

            foreach (var column in table.Columns)
            {
                _columnSources[column.ColumnName.Value] = column.SourceJsonPath;
            }

            foreach (var keyColumn in table.Key.Columns)
            {
                _columnSources.TryAdd(keyColumn.ColumnName.Value, null);
            }
        }

        public DbTableModel Definition { get; }

        public IReadOnlyList<string> CollectionBaseNames { get; }

        public List<DbColumnModel> Columns { get; }

        public List<TableConstraint> Constraints { get; }

        public void AddColumn(DbColumnModel column)
        {
            if (_columnSources.TryGetValue(column.ColumnName.Value, out var existingSource))
            {
                var tableName = Definition.Table.Name;
                var existingPath = ResolveSourcePath(existingSource);
                var incomingPath = ResolveSourcePath(column.SourceJsonPath);

                throw new InvalidOperationException(
                    $"Column name '{column.ColumnName.Value}' is already defined on table '{tableName}'. "
                        + $"Colliding source paths '{existingPath}' and '{incomingPath}'. "
                        + "Use relational.nameOverrides to resolve the collision."
                );
            }

            _columnSources.Add(column.ColumnName.Value, column.SourceJsonPath);
            Columns.Add(column);
        }

        public void AddConstraint(TableConstraint constraint)
        {
            Constraints.Add(constraint);
        }

        public DbTableModel Build()
        {
            return Definition with { Columns = Columns.ToArray(), Constraints = Constraints.ToArray() };
        }

        private string ResolveSourcePath(JsonPathExpression? sourcePath)
        {
            return (sourcePath ?? Definition.JsonScope).Canonical;
        }
    }
}
