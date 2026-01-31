// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Binds document references from <c>documentPathsMapping.referenceJsonPaths</c> into derived tables by
/// adding FK/identity columns and emitting <see cref="DocumentReferenceBinding"/> metadata.
/// </summary>
public sealed class ReferenceBindingRelationalModelSetPass : IRelationalModelSetPass
{
    private static readonly DbSchemaName _dmsSchemaName = new("dms");
    private static readonly DbTableName _descriptorTableName = new(_dmsSchemaName, "Descriptor");

    /// <summary>
    /// The explicit order for the reference binding pass.
    /// </summary>
    public int Order { get; } = 30;

    /// <summary>
    /// Executes reference binding across all concrete resources and resource extensions.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseResourcesByName = BuildBaseResourceLookup(context.ConcreteResourcesInNameOrder);
        var resourcesByKey = context.ConcreteResourcesInNameOrder.ToDictionary(resource =>
            resource.ResourceKey.Resource
        );
        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = BuildResourceContext(resourceContext, apiSchemaRootsByProjectEndpoint);

            if (builderContext.DocumentReferenceMappings.Count == 0)
            {
                continue;
            }

            if (IsResourceExtension(resourceContext))
            {
                if (!baseResourcesByName.TryGetValue(resourceContext.ResourceName, out var baseEntries))
                {
                    throw new InvalidOperationException(
                        $"Resource extension '{FormatResource(resource)}' did not match a concrete base resource."
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
                        $"Resource extension '{FormatResource(resource)}' matched multiple concrete resources: "
                            + $"{candidates}."
                    );
                }

                var baseEntry = baseEntries[0];
                var baseModel = context.ConcreteResourcesInNameOrder[baseEntry.Index];
                var updatedModel = ApplyReferenceMappings(
                    context,
                    baseModel.RelationalModel,
                    builderContext,
                    baseModel.ResourceKey.Resource
                );

                context.ConcreteResourcesInNameOrder[baseEntry.Index] = baseModel with
                {
                    RelationalModel = updatedModel,
                };

                continue;
            }

            if (!resourcesByKey.TryGetValue(resource, out var concrete))
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found for reference binding."
                );
            }

            var updated = ApplyReferenceMappings(context, concrete.RelationalModel, builderContext, resource);

            var index = context.ConcreteResourcesInNameOrder.FindIndex(entry =>
                entry.ResourceKey.Resource == resource
            );

            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found in derived inventory."
                );
            }

            context.ConcreteResourcesInNameOrder[index] = concrete with { RelationalModel = updated };
        }
    }

    private static RelationalResourceModel ApplyReferenceMappings(
        RelationalModelSetBuilderContext context,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        var tableBuilders = resourceModel
            .TablesInReadDependencyOrder.Select(table => new TableBuilder(table))
            .ToDictionary(builder => builder.Definition.JsonScope.Canonical, StringComparer.Ordinal);

        var tableScopes = tableBuilders
            .Select(entry => new TableScopeEntry(
                entry.Key,
                entry.Value.Definition.JsonScope.Segments,
                entry.Value
            ))
            .ToArray();

        var identityPaths = new HashSet<string>(
            builderContext.IdentityJsonPaths.Select(path => path.Canonical),
            StringComparer.Ordinal
        );
        var documentReferenceBindings = new List<DocumentReferenceBinding>(
            resourceModel.DocumentReferenceBindings
        );
        var referenceObjectPaths = new HashSet<string>(
            resourceModel.DocumentReferenceBindings.Select(binding => binding.ReferenceObjectPath.Canonical),
            StringComparer.Ordinal
        );
        var descriptorEdgeSources = new List<DescriptorEdgeSource>(resourceModel.DescriptorEdgeSources);

        foreach (var mapping in builderContext.DocumentReferenceMappings)
        {
            if (!referenceObjectPaths.Add(mapping.ReferenceObjectPath.Canonical))
            {
                throw new InvalidOperationException(
                    $"Reference object path '{mapping.ReferenceObjectPath.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' is already bound."
                );
            }

            var referenceBaseName = ResolveReferenceBaseName(mapping, builderContext);
            var tableBuilder = ResolveOwningTableBuilder(mapping.ReferenceObjectPath, tableScopes, resource);
            var isNullable = !mapping.IsRequired;

            var fkColumnName = BuildReferenceDocumentIdColumnName(referenceBaseName);
            var fkColumn = new DbColumnModel(
                fkColumnName,
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                isNullable,
                mapping.ReferenceObjectPath,
                mapping.TargetResource
            );

            tableBuilder.AddColumn(fkColumn);

            List<ReferenceIdentityBinding> identityBindings = new(mapping.ReferenceJsonPaths.Count);

            foreach (var identityBinding in mapping.ReferenceJsonPaths)
            {
                var identityPartBaseName = BuildIdentityPartBaseName(identityBinding.IdentityJsonPath);

                if (
                    TryResolveDescriptorIdentity(
                        context,
                        mapping.TargetResource,
                        identityBinding.IdentityJsonPath,
                        out var descriptorPath
                    )
                )
                {
                    var descriptorColumnName = RelationalNameConventions.DescriptorIdColumnName(
                        $"{referenceBaseName}_{identityPartBaseName}"
                    );
                    var descriptorColumn = new DbColumnModel(
                        descriptorColumnName,
                        ColumnKind.DescriptorFk,
                        new RelationalScalarType(ScalarKind.Int64),
                        isNullable,
                        identityBinding.ReferenceJsonPath,
                        descriptorPath.DescriptorResource
                    );

                    tableBuilder.AddColumn(descriptorColumn);
                    tableBuilder.AddConstraint(
                        new TableConstraint.ForeignKey(
                            RelationalNameConventions.ForeignKeyName(
                                tableBuilder.Definition.Table.Name,
                                new[] { descriptorColumnName }
                            ),
                            new[] { descriptorColumnName },
                            _descriptorTableName,
                            new[] { RelationalNameConventions.DocumentIdColumnName },
                            OnDelete: ReferentialAction.NoAction,
                            OnUpdate: ReferentialAction.NoAction
                        )
                    );

                    var isIdentityComponent = identityPaths.Contains(
                        identityBinding.ReferenceJsonPath.Canonical
                    );
                    descriptorEdgeSources.Add(
                        new DescriptorEdgeSource(
                            isIdentityComponent,
                            identityBinding.ReferenceJsonPath,
                            tableBuilder.Definition.Table,
                            descriptorColumnName,
                            descriptorPath.DescriptorResource
                        )
                    );

                    identityBindings.Add(
                        new ReferenceIdentityBinding(identityBinding.ReferenceJsonPath, descriptorColumnName)
                    );

                    continue;
                }

                var schemaNode = ResolveSchemaForPath(
                    builderContext.JsonSchemaForInsert,
                    identityBinding.ReferenceJsonPath,
                    resource
                );
                var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(
                    schemaNode,
                    identityBinding.ReferenceJsonPath.Canonical,
                    includeTypePathInErrors: true
                );

                if (schemaKind != SchemaKind.Scalar)
                {
                    throw new InvalidOperationException(
                        $"Reference identity path '{identityBinding.ReferenceJsonPath.Canonical}' on resource "
                            + $"'{FormatResource(resource)}' must resolve to a scalar schema."
                    );
                }

                var scalarType = ResolveScalarType(
                    schemaNode,
                    identityBinding.ReferenceJsonPath,
                    builderContext
                );
                var columnName = new DbColumnName($"{referenceBaseName}_{identityPartBaseName}");
                var scalarColumn = new DbColumnModel(
                    columnName,
                    ColumnKind.Scalar,
                    scalarType,
                    isNullable,
                    identityBinding.ReferenceJsonPath,
                    null
                );

                tableBuilder.AddColumn(scalarColumn);
                identityBindings.Add(
                    new ReferenceIdentityBinding(identityBinding.ReferenceJsonPath, columnName)
                );
            }

            documentReferenceBindings.Add(
                new DocumentReferenceBinding(
                    mapping.IsPartOfIdentity,
                    mapping.ReferenceObjectPath,
                    tableBuilder.Definition.Table,
                    fkColumnName,
                    mapping.TargetResource,
                    identityBindings.ToArray()
                )
            );
        }

        var updatedTables = resourceModel
            .TablesInReadDependencyOrder.Select(table => tableBuilders[table.JsonScope.Canonical].Build())
            .ToArray();
        var updatedRoot = tableBuilders[resourceModel.Root.JsonScope.Canonical].Build();

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInReadDependencyOrder = updatedTables,
            TablesInWriteDependencyOrder = updatedTables,
            DocumentReferenceBindings = documentReferenceBindings.ToArray(),
            DescriptorEdgeSources = descriptorEdgeSources.ToArray(),
        };
    }

    private static RelationalModelBuilderContext BuildResourceContext(
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

        var builderContext = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = resourceContext.ResourceEndpointName,
        };

        new ExtractInputsStep().Execute(builderContext);

        return builderContext;
    }

    private static TableBuilder ResolveOwningTableBuilder(
        JsonPathExpression referenceObjectPath,
        IReadOnlyList<TableScopeEntry> tableScopes,
        QualifiedResourceName resource
    )
    {
        TableScopeEntry? bestMatch = null;

        foreach (var scope in tableScopes)
        {
            if (!IsPrefixOf(scope.Segments, referenceObjectPath.Segments))
            {
                continue;
            }

            if (bestMatch is null || scope.Segments.Count > bestMatch.Segments.Count)
            {
                bestMatch = scope;
            }
            else if (
                bestMatch is not null
                && scope.Segments.Count == bestMatch.Segments.Count
                && string.CompareOrdinal(scope.Canonical, bestMatch.Canonical) < 0
            )
            {
                bestMatch = scope;
            }
        }

        if (bestMatch is null)
        {
            throw new InvalidOperationException(
                $"Reference object path '{referenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not match any table scope."
            );
        }

        if (
            referenceObjectPath.Segments.Any(segment => segment is JsonPathSegment.Property { Name: "_ext" })
            && !bestMatch.Segments.Any(segment => segment is JsonPathSegment.Property { Name: "_ext" })
        )
        {
            throw new InvalidOperationException(
                $"Reference object path '{referenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' requires an extension table scope, but none was found."
            );
        }

        return bestMatch.Builder;
    }

    private static bool IsPrefixOf(IReadOnlyList<JsonPathSegment> prefix, IReadOnlyList<JsonPathSegment> path)
    {
        if (prefix.Count > path.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            var prefixSegment = prefix[index];
            var pathSegment = path[index];

            if (prefixSegment.GetType() != pathSegment.GetType())
            {
                return false;
            }

            if (
                prefixSegment is JsonPathSegment.Property prefixProperty
                && pathSegment is JsonPathSegment.Property pathProperty
                && !string.Equals(prefixProperty.Name, pathProperty.Name, StringComparison.Ordinal)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveReferenceBaseName(
        DocumentReferenceMapping mapping,
        RelationalModelBuilderContext builderContext
    )
    {
        if (
            builderContext.ReferenceNameOverridesByPath.TryGetValue(
                mapping.ReferenceObjectPath.Canonical,
                out var overrideName
            )
        )
        {
            return overrideName;
        }

        return RelationalNameConventions.ToPascalCase(mapping.MappingKey);
    }

    private static DbColumnName BuildReferenceDocumentIdColumnName(string referenceBaseName)
    {
        if (string.IsNullOrWhiteSpace(referenceBaseName))
        {
            throw new InvalidOperationException("Reference base name must be non-empty.");
        }

        return new DbColumnName($"{referenceBaseName}_DocumentId");
    }

    private static string BuildIdentityPartBaseName(JsonPathExpression identityJsonPath)
    {
        List<string> segments = [];

        foreach (var segment in identityJsonPath.Segments)
        {
            switch (segment)
            {
                case JsonPathSegment.Property property:
                    segments.Add(property.Name);
                    break;
                case JsonPathSegment.AnyArrayElement:
                    throw new InvalidOperationException(
                        $"Identity path '{identityJsonPath.Canonical}' must not include array segments."
                    );
            }
        }

        if (segments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Identity path '{identityJsonPath.Canonical}' must include at least one property segment."
            );
        }

        StringBuilder builder = new();

        foreach (var segment in segments)
        {
            builder.Append(RelationalNameConventions.ToPascalCase(segment));
        }

        return builder.ToString();
    }

    private static bool TryResolveDescriptorIdentity(
        RelationalModelSetBuilderContext context,
        QualifiedResourceName targetResource,
        JsonPathExpression identityJsonPath,
        out DescriptorPathInfo descriptorPathInfo
    )
    {
        var descriptorPaths = context.GetAllDescriptorPathsForResource(targetResource);

        if (descriptorPaths.TryGetValue(identityJsonPath.Canonical, out descriptorPathInfo))
        {
            return true;
        }

        if (
            identityJsonPath.Segments.Count > 0
            && identityJsonPath.Segments[^1] is JsonPathSegment.Property property
            && property.Name.EndsWith("Descriptor", StringComparison.Ordinal)
        )
        {
            descriptorPathInfo = new DescriptorPathInfo(
                identityJsonPath,
                new QualifiedResourceName(targetResource.ProjectName, property.Name)
            );
            return true;
        }

        descriptorPathInfo = default;
        return false;
    }

    private static JsonObject ResolveSchemaForPath(
        JsonNode? rootSchemaNode,
        JsonPathExpression path,
        QualifiedResourceName resource
    )
    {
        if (rootSchemaNode is not JsonObject rootSchema)
        {
            throw new InvalidOperationException("Json schema root must be an object.");
        }

        var current = rootSchema;

        foreach (var segment in path.Segments)
        {
            var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(current);

            switch (segment)
            {
                case JsonPathSegment.Property property:
                    if (schemaKind != SchemaKind.Object)
                    {
                        throw new InvalidOperationException(
                            $"Expected object schema for '{path.Canonical}' while resolving "
                                + $"'{property.Name}' on resource '{FormatResource(resource)}'."
                        );
                    }

                    if (
                        !current.TryGetPropertyValue("properties", out var propertiesNode)
                        || propertiesNode is null
                    )
                    {
                        throw new InvalidOperationException(
                            $"Expected properties to be present for '{path.Canonical}' on resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    if (propertiesNode is not JsonObject propertiesObject)
                    {
                        throw new InvalidOperationException(
                            $"Expected properties to be an object for '{path.Canonical}' on resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    if (
                        !propertiesObject.TryGetPropertyValue(property.Name, out var propertyNode)
                        || propertyNode is null
                    )
                    {
                        throw new InvalidOperationException(
                            $"Reference path '{path.Canonical}' was not found in jsonSchemaForInsert for "
                                + $"resource '{FormatResource(resource)}'."
                        );
                    }

                    if (propertyNode is not JsonObject propertySchema)
                    {
                        throw new InvalidOperationException(
                            $"Expected schema object at '{path.Canonical}' for resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    current = propertySchema;
                    break;
                case JsonPathSegment.AnyArrayElement:
                    if (schemaKind != SchemaKind.Array)
                    {
                        throw new InvalidOperationException(
                            $"Expected array schema for '{path.Canonical}' on resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    if (!current.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
                    {
                        throw new InvalidOperationException(
                            $"Expected array items for '{path.Canonical}' on resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    if (itemsNode is not JsonObject itemsSchema)
                    {
                        throw new InvalidOperationException(
                            $"Expected array items schema to be an object for '{path.Canonical}' on "
                                + $"resource '{FormatResource(resource)}'."
                        );
                    }

                    current = itemsSchema;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported JSONPath segment for '{path.Canonical}' on resource "
                            + $"'{FormatResource(resource)}'."
                    );
            }
        }

        return current;
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

        var detachedSchema = projectSchema.DeepClone();

        if (detachedSchema is not JsonObject detachedObject)
        {
            throw new InvalidOperationException("Project schema must be an object.");
        }

        apiSchemaRoot = new JsonObject { ["projectSchema"] = detachedObject };

        apiSchemaRootsByProjectEndpoint[projectEndpointName] = apiSchemaRoot;

        return apiSchemaRoot;
    }

    private sealed record BaseResourceEntry(int Index, ConcreteResourceModel Model);

    private sealed record TableScopeEntry(
        string Canonical,
        IReadOnlyList<JsonPathSegment> Segments,
        TableBuilder Builder
    );

    private sealed class TableBuilder
    {
        private readonly Dictionary<string, JsonPathExpression?> _columnSources = new(StringComparer.Ordinal);

        public TableBuilder(DbTableModel table)
        {
            Definition = table;
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
