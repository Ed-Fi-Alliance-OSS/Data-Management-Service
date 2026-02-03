// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    /// Executes reference binding across all concrete resources and resource extensions.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseResourcesByName = BuildBaseResourceLookup(
            context.ConcreteResourcesInNameOrder,
            static (index, model) => new BaseResourceEntry(index, model)
        );
        var resourceIndexByKey = context
            .ConcreteResourcesInNameOrder.Select(
                (resource, index) => new { resource.ResourceKey.Resource, Index = index }
            )
            .ToDictionary(entry => entry.Resource, entry => entry.Index);
        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);

            if (builderContext.DocumentReferenceMappings.Count == 0)
            {
                continue;
            }

            if (IsResourceExtension(resourceContext))
            {
                var baseEntry = ResolveBaseResourceForExtension(
                    resourceContext.ResourceName,
                    resource,
                    baseResourcesByName,
                    static entry => entry.Model.ResourceKey.Resource
                );
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

            if (!resourceIndexByKey.TryGetValue(resource, out var index))
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found for reference binding."
                );
            }

            var concrete = context.ConcreteResourcesInNameOrder[index];
            var updated = ApplyReferenceMappings(context, concrete.RelationalModel, builderContext, resource);

            context.ConcreteResourcesInNameOrder[index] = concrete with { RelationalModel = updated };
        }
    }

    /// <summary>
    /// Applies the resource's document reference mappings by adding FK and propagated identity columns to the
    /// owning tables and emitting <see cref="DocumentReferenceBinding"/> metadata for runtime use.
    /// </summary>
    private static RelationalResourceModel ApplyReferenceMappings(
        RelationalModelSetBuilderContext context,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        var tableBuilders = resourceModel
            .TablesInDependencyOrder.Select(table => new TableColumnAccumulator(table))
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
                    resource,
                    "Reference"
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

                var scalarType = RelationalScalarTypeResolver.ResolveScalarType(
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
            .TablesInDependencyOrder.Select(table => tableBuilders[table.JsonScope.Canonical].Build())
            .ToArray();
        var updatedRoot = tableBuilders[resourceModel.Root.JsonScope.Canonical].Build();

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInDependencyOrder = updatedTables,
            DocumentReferenceBindings = documentReferenceBindings.ToArray(),
            DescriptorEdgeSources = descriptorEdgeSources.ToArray(),
        };
    }

    /// <summary>
    /// Resolves the table accumulator that owns the reference object path by selecting the deepest matching
    /// table scope prefix.
    /// </summary>
    private static TableColumnAccumulator ResolveOwningTableBuilder(
        JsonPathExpression referenceObjectPath,
        IReadOnlyList<TableScopeEntry> tableScopes,
        QualifiedResourceName resource
    )
    {
        var bestMatches = new List<TableScopeEntry>();
        var bestSegmentCount = -1;

        foreach (var scope in tableScopes)
        {
            if (!IsPrefixOf(scope.Segments, referenceObjectPath.Segments))
            {
                continue;
            }

            var segmentCount = scope.Segments.Count;
            if (segmentCount > bestSegmentCount)
            {
                bestSegmentCount = segmentCount;
                bestMatches.Clear();
                bestMatches.Add(scope);
            }
            else if (segmentCount == bestSegmentCount)
            {
                bestMatches.Add(scope);
            }
        }

        if (bestMatches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Reference object path '{referenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not match any table scope."
            );
        }

        var candidateScopes = bestMatches
            .Select(entry => entry.Canonical)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();

        if (candidateScopes.Length > 1)
        {
            throw new InvalidOperationException(
                $"Reference object path '{referenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' matched multiple table scopes with the same depth: "
                    + $"{string.Join(", ", candidateScopes.Select(scope => $"'{scope}'"))}."
            );
        }

        var bestMatch = bestMatches[0];
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

    /// <summary>
    /// Resolves the reference base name, applying any supported <c>relational.nameOverrides</c> entry first.
    /// </summary>
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

    /// <summary>
    /// Builds the FK column name used to represent the reference object as a single referenced document id.
    /// </summary>
    private static DbColumnName BuildReferenceDocumentIdColumnName(string referenceBaseName)
    {
        if (string.IsNullOrWhiteSpace(referenceBaseName))
        {
            throw new InvalidOperationException("Reference base name must be non-empty.");
        }

        return new DbColumnName($"{referenceBaseName}_DocumentId");
    }

    /// <summary>
    /// Resolves descriptor identity metadata for a target resource identity path when the identity ends in a
    /// descriptor segment.
    /// </summary>
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

        if (IsDescriptorIdentityPath(identityJsonPath))
        {
            throw new InvalidOperationException(
                $"Descriptor identity path '{identityJsonPath.Canonical}' on resource "
                    + $"'{FormatResource(targetResource)}' was not found in descriptor path map."
            );
        }

        descriptorPathInfo = default;
        return false;
    }

    /// <summary>
    /// Returns true when an identity path appears to represent a descriptor URI identity component.
    /// </summary>
    private static bool IsDescriptorIdentityPath(JsonPathExpression identityJsonPath)
    {
        return identityJsonPath.Segments.Count > 0
            && identityJsonPath.Segments[^1] is JsonPathSegment.Property property
            && property.Name.EndsWith("Descriptor", StringComparison.Ordinal);
    }

    /// <summary>
    /// Captures the index and model for a base (non-extension) resource used when resolving extensions.
    /// </summary>
    private sealed record BaseResourceEntry(int Index, ConcreteResourceModel Model);

    /// <summary>
    /// Captures a table scope and its accumulator for prefix matching against reference object paths.
    /// </summary>
    private sealed record TableScopeEntry(
        string Canonical,
        IReadOnlyList<JsonPathSegment> Segments,
        TableColumnAccumulator Builder
    );
}
