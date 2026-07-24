// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Binds document references from <c>documentPathsMapping.referenceJsonPaths</c> into derived tables by
/// adding FK/identity columns and emitting <see cref="DocumentReferenceBinding"/> metadata.
/// </summary>
public sealed class ReferenceBindingPass : IRelationalModelSetPass
{
    /// <summary>
    /// Executes reference binding across all concrete resources and resource extensions.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseResourcesByName = SetPassHelpers.BuildExtensionBaseResourceLookup(
            context,
            static (index, model) => new ResourceEntry(index, model)
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
            .TablesInDependencyOrder.Select(table => new TableColumnAccumulator(
                table,
                FormatResource(resource)
            ))
            .ToDictionary(builder => builder.Definition.JsonScope.Canonical, StringComparer.Ordinal);

        var tableScopes = tableBuilders
            .Select(entry => new TableScopeEntry(entry.Value.Definition.JsonScope, entry.Value))
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

            // MappingKey is the documentPathsMapping entry key from the ApiSchema — the bare
            // reference/role name (e.g. "Student", "Program", "AdministrationPointOfContact.Education-
            // Organization"). MetaEd-generated schemas never suffix it with "Reference", so ToPascalCase
            // yields the resource-based column base directly (e.g. Student_StudentUniqueId).
            var originalReferenceBaseName = RelationalNameConventions.ToPascalCase(mapping.MappingKey);
            var referenceBaseName = ResolveReferenceBaseName(mapping, builderContext);
            var tableBuilder = ReferenceObjectPathScopeResolver
                .ResolveOwningTableScope(
                    mapping.ReferenceObjectPath,
                    tableScopes,
                    static scope => scope.Scope,
                    resource
                )
                .Builder;
            var isNullable = !mapping.IsRequired;
            var referenceIdentityFieldBaseNameCounts = BuildReferenceIdentityFieldBaseNameCounts(
                mapping.ReferenceObjectPath,
                mapping.ReferenceJsonPaths
            );

            var fkColumnName = BuildReferenceDocumentIdColumnName(referenceBaseName);
            var originalFkColumnName = BuildReferenceDocumentIdColumnName(originalReferenceBaseName);
            var fkColumn = new DbColumnModel(
                fkColumnName,
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                isNullable,
                mapping.ReferenceObjectPath,
                mapping.TargetResource
            );

            tableBuilder.AddColumn(fkColumn, originalFkColumnName.Value);

            List<ReferenceIdentityBinding> identityBindings = new(mapping.ReferenceJsonPaths.Count);

            foreach (var identityBinding in mapping.ReferenceJsonPaths)
            {
                var conventionIdentityPartBaseName = ResolveReferenceIdentityPartBaseName(
                    mapping.ReferenceObjectPath,
                    identityBinding,
                    referenceIdentityFieldBaseNameCounts
                );

                // identityPartBaseName starts at the override-free convention name; may be replaced
                // by a nameOverride. The convention name is preserved separately in the builder context.
                var identityPartBaseName = conventionIdentityPartBaseName;

                if (
                    builderContext.TryGetNameOverride(
                        identityBinding.ReferenceJsonPath,
                        NameOverrideKind.Column,
                        out var identityOverride
                    )
                )
                {
                    identityPartBaseName = identityOverride;
                }

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
                    var originalDescriptorColumnName = RelationalNameConventions.DescriptorIdColumnName(
                        $"{originalReferenceBaseName}_{identityPartBaseName}"
                    );
                    // Override-free, MappingKey-derived convention name (matches concrete's override-free
                    // naming exactly, including role-named references where MappingKey != path segment).
                    var conventionDescriptorColumnName = RelationalNameConventions.DescriptorIdColumnName(
                        $"{originalReferenceBaseName}_{conventionIdentityPartBaseName}"
                    );
                    var descriptorColumn = new DbColumnModel(
                        descriptorColumnName,
                        ColumnKind.DescriptorFk,
                        new RelationalScalarType(ScalarKind.Int64),
                        isNullable,
                        identityBinding.ReferenceJsonPath,
                        descriptorPath.DescriptorResource
                    );

                    tableBuilder.AddColumn(descriptorColumn, originalDescriptorColumnName.Value);

                    var isIdentityComponent = identityPaths.Contains(
                        identityBinding.ReferenceJsonPath.Canonical
                    );
                    descriptorEdgeSources.Add(
                        new DescriptorEdgeSource(
                            isIdentityComponent,
                            identityBinding.ReferenceJsonPath,
                            tableBuilder.Definition.Table,
                            descriptorColumnName,
                            descriptorPath.DescriptorResource,
                            IsRequired: mapping.IsRequired,
                            IsRoleNamed: ReferenceRoleNameConventions.IsDocumentReferenceRoleNamed(
                                mapping.ReferenceObjectPath,
                                mapping.TargetResource
                            )
                        )
                    );

                    context.RegisterReferenceIdentityConventionColumn(
                        resource,
                        mapping.ReferenceObjectPath,
                        identityBinding.ReferenceJsonPath,
                        identityBinding.IdentityJsonPath,
                        conventionDescriptorColumnName
                    );

                    identityBindings.Add(
                        new ReferenceIdentityBinding(
                            identityBinding.IdentityJsonPath,
                            identityBinding.ReferenceJsonPath,
                            descriptorColumnName
                        )
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
                var originalColumnName = new DbColumnName(
                    $"{originalReferenceBaseName}_{identityPartBaseName}"
                );
                // Override-free, MappingKey-derived convention name (matches concrete's override-free
                // naming exactly, including role-named references where MappingKey != path segment).
                var conventionColumnName = new DbColumnName(
                    $"{originalReferenceBaseName}_{conventionIdentityPartBaseName}"
                );
                var scalarColumn = new DbColumnModel(
                    columnName,
                    ColumnKind.Scalar,
                    scalarType,
                    isNullable,
                    identityBinding.ReferenceJsonPath,
                    null
                );

                tableBuilder.AddColumn(scalarColumn, originalColumnName.Value);
                context.RegisterReferenceIdentityConventionColumn(
                    resource,
                    mapping.ReferenceObjectPath,
                    identityBinding.ReferenceJsonPath,
                    identityBinding.IdentityJsonPath,
                    conventionColumnName
                );

                identityBindings.Add(
                    new ReferenceIdentityBinding(
                        identityBinding.IdentityJsonPath,
                        identityBinding.ReferenceJsonPath,
                        columnName
                    )
                );
            }

            documentReferenceBindings.Add(
                new DocumentReferenceBinding(
                    mapping.IsPartOfIdentity,
                    mapping.ReferenceObjectPath,
                    tableBuilder.Definition.Table,
                    fkColumnName,
                    mapping.TargetResource,
                    identityBindings.ToArray(),
                    IsRequired: mapping.IsRequired,
                    IsRoleNamed: ReferenceRoleNameConventions.IsDocumentReferenceRoleNamed(
                        mapping.ReferenceObjectPath,
                        mapping.TargetResource
                    )
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
    /// Resolves the reference base name, applying any supported <c>relational.nameOverrides</c> entry first.
    /// </summary>
    private static string ResolveReferenceBaseName(
        DocumentReferenceMapping mapping,
        RelationalModelBuilderContext builderContext
    )
    {
        if (
            builderContext.TryGetNameOverride(
                mapping.ReferenceObjectPath,
                NameOverrideKind.Column,
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
        return DocumentIdentity.IsDescriptorIdentityPath(identityJsonPath.Canonical);
    }

    /// <summary>
    /// Captures a table scope and its accumulator for prefix matching against reference object paths.
    /// </summary>
    private sealed record TableScopeEntry(JsonPathExpression Scope, TableColumnAccumulator Builder);
}
