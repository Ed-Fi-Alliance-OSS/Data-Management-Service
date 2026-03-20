// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Naming;
using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Compiles persisted collection semantic identity into table metadata before downstream consumers derive
/// constraints, indexes, manifests, or triggers from it.
/// </summary>
public sealed class SemanticIdentityCompilationPass : IRelationalModelSetPass
{
    /// <summary>
    /// Executes semantic-identity compilation across all concrete resources and resource extensions.
    /// </summary>
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
        Dictionary<QualifiedResourceName, ResourceMutation> mutations = new();

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);

            if (
                builderContext.ArrayUniquenessConstraints.Count == 0
                && builderContext.DocumentReferenceMappings.Count == 0
            )
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
                var baseResource = baseEntry.Model.ResourceKey.Resource;
                var mutation = GetOrCreateMutation(baseResource, baseEntry, mutations);

                CompileSemanticIdentityForResource(
                    mutation,
                    baseEntry.Model.RelationalModel,
                    builderContext,
                    baseResource
                );

                continue;
            }

            if (!resourceIndexByKey.TryGetValue(resource, out var index))
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found for semantic identity compilation."
                );
            }

            var concrete = context.ConcreteResourcesInNameOrder[index];
            var resourceMutation = GetOrCreateMutation(
                resource,
                new ResourceEntry(index, concrete),
                mutations
            );

            CompileSemanticIdentityForResource(
                resourceMutation,
                concrete.RelationalModel,
                builderContext,
                resource
            );
        }

        foreach (var mutation in mutations.Values)
        {
            if (!mutation.HasChanges)
            {
                continue;
            }

            var updatedModel = UpdateResourceModel(mutation.Entry.Model.RelationalModel, mutation);
            context.ConcreteResourcesInNameOrder[mutation.Entry.Index] = mutation.Entry.Model with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    /// <summary>
    /// Compiles semantic identity from array uniqueness constraints first, then from a single qualifying
    /// scope-local document reference binding when a persisted collection scope still has no compiled identity.
    /// </summary>
    private static void CompileSemanticIdentityForResource(
        ResourceMutation mutation,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        if (builderContext.ArrayUniquenessConstraints.Count > 0)
        {
            ArrayUniquenessConstraintPass.ApplyArrayUniquenessConstraintsForResource(
                mutation,
                resourceModel,
                builderContext,
                resource,
                emitUniqueConstraints: false
            );
        }

        foreach (var table in resourceModel.TablesInDependencyOrder)
        {
            if (!SupportsReferenceDerivedSemanticIdentity(table))
            {
                continue;
            }

            var tableAccumulator = mutation.GetTableAccumulator(table, resource);

            if (tableAccumulator.IdentityMetadata.SemanticIdentityBindings.Count > 0)
            {
                continue;
            }

            var candidateBindings = resourceModel
                .DocumentReferenceBindings.Select(binding =>
                    TryCompileReferenceDerivedSemanticIdentity(table, binding, out var bindings)
                        ? new ReferenceSemanticIdentityCandidate(binding, bindings)
                        : null
                )
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .OrderBy(candidate => candidate.Binding.ReferenceObjectPath.Canonical, StringComparer.Ordinal)
                .ToArray();

            if (candidateBindings.Length != 1)
            {
                continue;
            }

            ArrayUniquenessConstraintPass.ApplySemanticIdentityBindings(
                mutation,
                table,
                candidateBindings[0].Bindings,
                resource
            );
        }
    }

    /// <summary>
    /// Returns whether a table can consume reference-derived semantic identity.
    /// </summary>
    private static bool SupportsReferenceDerivedSemanticIdentity(DbTableModel table)
    {
        var supportsTableKind =
            table.IdentityMetadata.TableKind is DbTableKind.Collection or DbTableKind.ExtensionCollection;

        return supportsTableKind
            && table.Columns.Any(column =>
                column.Kind == ColumnKind.Ordinal
                && column.ColumnName.Equals(RelationalNameConventions.OrdinalColumnName)
            );
    }

    /// <summary>
    /// Attempts to compile semantic identity for a table from one scope-local document reference binding.
    /// </summary>
    private static bool TryCompileReferenceDerivedSemanticIdentity(
        DbTableModel table,
        DocumentReferenceBinding binding,
        out IReadOnlyList<CollectionSemanticIdentityBinding> bindings
    )
    {
        bindings = [];

        if (!binding.Table.Equals(table.Table) || binding.IdentityBindings.Count == 0)
        {
            return false;
        }

        List<CollectionSemanticIdentityBinding> compiledBindings = new(binding.IdentityBindings.Count);

        foreach (var identityBinding in binding.IdentityBindings)
        {
            if (
                !TryDeriveScopeRelativePath(
                    table.JsonScope,
                    identityBinding.ReferenceJsonPath,
                    out var relativePath
                )
            )
            {
                return false;
            }

            compiledBindings.Add(new CollectionSemanticIdentityBinding(relativePath, binding.FkColumn));
        }

        bindings = compiledBindings.ToArray();
        return true;
    }

    /// <summary>
    /// Attempts to derive a table-scope-relative JSON path without crossing into descendant array scopes.
    /// </summary>
    private static bool TryDeriveScopeRelativePath(
        JsonPathExpression jsonScope,
        JsonPathExpression path,
        out JsonPathExpression relativePath
    )
    {
        relativePath = default;

        if (!IsPrefixOf(jsonScope.Segments, path.Segments))
        {
            return false;
        }

        var relativeSegments = path.Segments.Skip(jsonScope.Segments.Count).ToArray();

        if (relativeSegments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
        {
            return false;
        }

        relativePath = JsonPathExpressionCompiler.FromSegments(relativeSegments);
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="prefix"/> matches the left-most path segments of
    /// <paramref name="path"/>.
    /// </summary>
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

    /// <summary>
    /// Captures one qualifying reference binding and its compiled semantic-identity bindings.
    /// </summary>
    private sealed record ReferenceSemanticIdentityCandidate(
        DocumentReferenceBinding Binding,
        IReadOnlyList<CollectionSemanticIdentityBinding> Bindings
    );
}
