// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives reference foreign keys and all-or-none constraints.
/// </summary>
public sealed class ReferenceConstraintRelationalModelSetPass : IRelationalModelSetPass
{
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);
        var baseResourcesByName = BuildBaseResourceLookup(
            context.ConcreteResourcesInNameOrder,
            static (index, model) => new ResourceEntry(index, model)
        );
        var abstractIdentityTablesByResource = context.AbstractIdentityTablesInNameOrder.ToDictionary(table =>
            table.AbstractResourceKey.Resource
        );
        var resourceContextsByResource = BuildResourceContextLookup(context);
        Dictionary<QualifiedResourceName, TargetIdentityInfo> targetIdentityCache = new();
        Dictionary<QualifiedResourceName, ResourceMutation> mutations = new();

        var passContext = new ReferenceConstraintContext(
            context,
            resourcesByKey,
            abstractIdentityTablesByResource,
            resourceContextsByResource,
            targetIdentityCache,
            mutations
        );

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
                var baseResource = baseEntry.Model.ResourceKey.Resource;
                var mutation = GetOrCreateMutation(baseResource, baseEntry, mutations);

                ApplyReferenceConstraintsForResource(
                    passContext,
                    mutation,
                    baseEntry.Model.RelationalModel,
                    builderContext,
                    baseResource
                );

                continue;
            }

            if (!resourcesByKey.TryGetValue(resource, out var entry))
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found for constraint derivation."
                );
            }

            var resourceMutation = GetOrCreateMutation(resource, entry, mutations);

            ApplyReferenceConstraintsForResource(
                passContext,
                resourceMutation,
                entry.Model.RelationalModel,
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

    private static void ApplyReferenceConstraintsForResource(
        ReferenceConstraintContext context,
        ResourceMutation mutation,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        var bindingByReferencePath = resourceModel.DocumentReferenceBindings.ToDictionary(
            binding => binding.ReferenceObjectPath.Canonical,
            StringComparer.Ordinal
        );

        foreach (var mapping in builderContext.DocumentReferenceMappings)
        {
            if (!bindingByReferencePath.TryGetValue(mapping.ReferenceObjectPath.Canonical, out var binding))
            {
                throw new InvalidOperationException(
                    $"Reference object path '{mapping.ReferenceObjectPath.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' was not bound to a table."
                );
            }

            var targetInfo = GetTargetIdentityInfo(mapping.TargetResource, context);
            var identityColumns = BuildReferenceIdentityColumns(mapping, binding, targetInfo, resource);

            EnsureTargetUnique(
                targetInfo,
                identityColumns.TargetColumns,
                context.ResourcesByKey,
                context.Mutations
            );

            var bindingTable = ResolveReferenceBindingTable(binding, resourceModel, resource);
            var tableAccumulator = mutation.GetTableAccumulator(bindingTable, resource);

            if (identityColumns.LocalColumns.Count > 0)
            {
                var allOrNoneName = BuildAllOrNoneConstraintName(
                    tableAccumulator.Definition.Table.Name,
                    binding.FkColumn
                );

                if (!ContainsAllOrNoneConstraint(tableAccumulator.Constraints, allOrNoneName))
                {
                    tableAccumulator.AddConstraint(
                        new TableConstraint.AllOrNoneNullability(
                            allOrNoneName,
                            binding.FkColumn,
                            identityColumns.LocalColumns
                        )
                    );
                    mutation.MarkTableMutated(bindingTable);
                }
            }

            var localColumns = new List<DbColumnName>(1 + identityColumns.LocalColumns.Count)
            {
                binding.FkColumn,
            };
            localColumns.AddRange(identityColumns.LocalColumns);

            var targetColumns = new List<DbColumnName>(1 + identityColumns.TargetColumns.Count)
            {
                RelationalNameConventions.DocumentIdColumnName,
            };
            targetColumns.AddRange(identityColumns.TargetColumns);

            var fkName = RelationalNameConventions.ForeignKeyName(
                tableAccumulator.Definition.Table.Name,
                localColumns
            );

            if (!ContainsForeignKeyConstraint(tableAccumulator.Constraints, fkName))
            {
                var onUpdate =
                    targetInfo.IsAbstract ? ReferentialAction.Cascade
                    : targetInfo.AllowIdentityUpdates ? ReferentialAction.Cascade
                    : ReferentialAction.NoAction;

                tableAccumulator.AddConstraint(
                    new TableConstraint.ForeignKey(
                        fkName,
                        localColumns.ToArray(),
                        targetInfo.Table,
                        targetColumns.ToArray(),
                        OnDelete: ReferentialAction.NoAction,
                        OnUpdate: onUpdate
                    )
                );
                mutation.MarkTableMutated(bindingTable);
            }
        }
    }

    private static ReferenceIdentityColumnSet BuildReferenceIdentityColumns(
        DocumentReferenceMapping mapping,
        DocumentReferenceBinding binding,
        TargetIdentityInfo targetInfo,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, JsonPathExpression> referencePathsByIdentityPath = new(StringComparer.Ordinal);
        Dictionary<string, DbColumnName> localColumnsByIdentityPath = new(StringComparer.Ordinal);

        if (mapping.ReferenceJsonPaths.Count != binding.IdentityBindings.Count)
        {
            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + "did not align referenceJsonPaths with identity bindings."
            );
        }

        for (var index = 0; index < mapping.ReferenceJsonPaths.Count; index++)
        {
            var path = mapping.ReferenceJsonPaths[index];
            var identityBinding = binding.IdentityBindings[index];

            if (
                !string.Equals(
                    path.ReferenceJsonPath.Canonical,
                    identityBinding.ReferenceJsonPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"did not align reference path '{path.ReferenceJsonPath.Canonical}' with "
                        + $"binding '{identityBinding.ReferenceJsonPath.Canonical}'."
                );
            }

            if (!referencePathsByIdentityPath.TryAdd(path.IdentityJsonPath.Canonical, path.ReferenceJsonPath))
            {
                var existing = referencePathsByIdentityPath[path.IdentityJsonPath.Canonical];

                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"contains duplicate identity path '{path.IdentityJsonPath.Canonical}' bound to "
                        + $"'{existing.Canonical}' and '{path.ReferenceJsonPath.Canonical}'."
                );
            }

            if (!localColumnsByIdentityPath.TryAdd(path.IdentityJsonPath.Canonical, identityBinding.Column))
            {
                var existing = localColumnsByIdentityPath[path.IdentityJsonPath.Canonical];

                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"contains duplicate identity path '{path.IdentityJsonPath.Canonical}' bound to "
                        + $"columns '{existing.Value}' and '{identityBinding.Column.Value}'."
                );
            }
        }

        Dictionary<string, DbColumnName> targetColumnsByIdentityPath = new(StringComparer.Ordinal);

        for (var index = 0; index < targetInfo.IdentityJsonPaths.Count; index++)
        {
            targetColumnsByIdentityPath[targetInfo.IdentityJsonPaths[index].Canonical] =
                targetInfo.IdentityColumns[index];
        }

        List<DbColumnName> localColumns = new(targetInfo.IdentityJsonPaths.Count);
        List<DbColumnName> targetColumns = new(targetInfo.IdentityJsonPaths.Count);
        List<JsonPathExpression> missingIdentityPaths = new();

        foreach (var identityPath in targetInfo.IdentityJsonPaths)
        {
            if (!localColumnsByIdentityPath.TryGetValue(identityPath.Canonical, out var localColumn))
            {
                missingIdentityPaths.Add(identityPath);
                continue;
            }

            if (!targetColumnsByIdentityPath.TryGetValue(identityPath.Canonical, out var targetColumn))
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"did not resolve identity path '{identityPath.Canonical}' on target "
                        + $"'{FormatResource(mapping.TargetResource)}'."
                );
            }

            localColumns.Add(localColumn);
            targetColumns.Add(targetColumn);
        }

        if (missingIdentityPaths.Count > 0)
        {
            var missingPaths = string.Join(", ", missingIdentityPaths.Select(path => $"'{path.Canonical}'"));

            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + $"did not include identity path(s) {missingPaths} required by target "
                    + $"'{FormatResource(mapping.TargetResource)}'."
            );
        }

        return new ReferenceIdentityColumnSet(localColumns.ToArray(), targetColumns.ToArray());
    }

    private static void EnsureTargetUnique(
        TargetIdentityInfo targetInfo,
        IReadOnlyList<DbColumnName> targetIdentityColumns,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IDictionary<QualifiedResourceName, ResourceMutation> mutations
    )
    {
        if (targetInfo.IsAbstract || targetIdentityColumns.Count == 0)
        {
            return;
        }

        if (!resourcesByKey.TryGetValue(targetInfo.Resource, out var entry))
        {
            throw new InvalidOperationException(
                $"Concrete target resource '{FormatResource(targetInfo.Resource)}' was not found "
                    + "when deriving reference constraints."
            );
        }

        var mutation = GetOrCreateMutation(targetInfo.Resource, entry, mutations);
        var tableAccumulator = mutation.GetTableAccumulator(
            entry.Model.RelationalModel.Root,
            targetInfo.Resource
        );
        var uniqueColumns = new List<DbColumnName>(1 + targetIdentityColumns.Count)
        {
            RelationalNameConventions.DocumentIdColumnName,
        };
        uniqueColumns.AddRange(targetIdentityColumns);

        var uniqueName = BuildUniqueConstraintName(tableAccumulator.Definition.Table.Name, uniqueColumns);

        if (!ContainsUniqueConstraint(tableAccumulator.Constraints, uniqueName))
        {
            tableAccumulator.AddConstraint(new TableConstraint.Unique(uniqueName, uniqueColumns.ToArray()));
            mutation.MarkTableMutated(entry.Model.RelationalModel.Root);
        }
    }

    private static TargetIdentityInfo GetTargetIdentityInfo(
        QualifiedResourceName targetResource,
        ReferenceConstraintContext context
    )
    {
        if (context.TargetIdentityCache.TryGetValue(targetResource, out var cached))
        {
            return cached;
        }

        if (context.AbstractIdentityTablesByResource.TryGetValue(targetResource, out var abstractTable))
        {
            var abstractIdentityColumns = abstractTable
                .TableModel.Columns.Where(column => column.SourceJsonPath is not null)
                .ToArray();
            var identityPaths = abstractIdentityColumns
                .Select(column => column.SourceJsonPath!.Value)
                .ToArray();
            var columnNames = abstractIdentityColumns.Select(column => column.ColumnName).ToArray();

            var abstractInfo = new TargetIdentityInfo(
                targetResource,
                abstractTable.TableModel.Table,
                identityPaths,
                columnNames,
                AllowIdentityUpdates: false,
                IsAbstract: true
            );

            context.TargetIdentityCache[targetResource] = abstractInfo;
            return abstractInfo;
        }

        if (!context.ResourcesByKey.TryGetValue(targetResource, out var entry))
        {
            throw new InvalidOperationException(
                $"Reference target resource '{FormatResource(targetResource)}' was not found "
                    + "for constraint derivation."
            );
        }

        if (!context.ResourceContextsByResource.TryGetValue(targetResource, out var resourceContext))
        {
            throw new InvalidOperationException(
                $"Reference target resource '{FormatResource(targetResource)}' was not found "
                    + "in schema inputs."
            );
        }

        var builderContext = context.SetContext.GetOrCreateResourceBuilderContext(resourceContext);
        var identityColumns = BuildIdentityValueColumns(
            entry.Model.RelationalModel,
            builderContext,
            targetResource
        );
        var info = new TargetIdentityInfo(
            targetResource,
            entry.Model.RelationalModel.Root.Table,
            builderContext.IdentityJsonPaths,
            identityColumns,
            builderContext.AllowIdentityUpdates,
            IsAbstract: false
        );

        context.TargetIdentityCache[targetResource] = info;
        return info;
    }

    private static DbTableModel ResolveReferenceBindingTable(
        DocumentReferenceBinding binding,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        var candidates = resourceModel
            .TablesInReadDependencyOrder.Where(table => table.Table.Equals(binding.Table))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not map to table '{binding.Table}'."
            );
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        var orderedCandidates = candidates
            .OrderBy(table => table.JsonScope.Canonical, StringComparer.Ordinal)
            .ToArray();
        DbTableModel? bestMatch = null;

        foreach (var candidate in orderedCandidates)
        {
            if (!IsPrefixOf(candidate.JsonScope.Segments, binding.ReferenceObjectPath.Segments))
            {
                continue;
            }

            if (bestMatch is null || candidate.JsonScope.Segments.Count > bestMatch.JsonScope.Segments.Count)
            {
                bestMatch = candidate;
            }
        }

        if (bestMatch is null)
        {
            var scopeList = string.Join(", ", orderedCandidates.Select(table => table.JsonScope.Canonical));

            throw new InvalidOperationException(
                $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not match any table scope for '{binding.Table}'. "
                    + $"Candidates: {scopeList}."
            );
        }

        if (
            binding.ReferenceObjectPath.Segments.Any(segment =>
                segment is JsonPathSegment.Property { Name: "_ext" }
            )
            && !bestMatch.JsonScope.Segments.Any(segment =>
                segment is JsonPathSegment.Property { Name: "_ext" }
            )
        )
        {
            throw new InvalidOperationException(
                $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' requires an extension table scope, but none was found."
            );
        }

        return bestMatch;
    }

    private static IReadOnlyList<DbColumnName> BuildIdentityValueColumns(
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        if (builderContext.IdentityJsonPaths.Count == 0)
        {
            return Array.Empty<DbColumnName>();
        }

        var rootTable = resourceModel.Root;
        var rootColumnsByPath = BuildColumnNameLookupBySourceJsonPath(rootTable, resource);

        List<DbColumnName> identityColumns = new(builderContext.IdentityJsonPaths.Count);

        foreach (var identityPath in builderContext.IdentityJsonPaths)
        {
            if (identityPath.Segments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "must not include array segments when deriving reference constraints."
                );
            }

            if (!rootColumnsByPath.TryGetValue(identityPath.Canonical, out var columnName))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column."
                );
            }

            identityColumns.Add(columnName);
        }

        return identityColumns.ToArray();
    }

    private static IReadOnlyDictionary<
        QualifiedResourceName,
        ConcreteResourceSchemaContext
    > BuildResourceContextLookup(RelationalModelSetBuilderContext context)
    {
        Dictionary<QualifiedResourceName, ConcreteResourceSchemaContext> lookup = new();

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (IsResourceExtension(resourceContext))
            {
                continue;
            }

            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );

            lookup[resource] = resourceContext;
        }

        return lookup;
    }

    private sealed record ReferenceConstraintContext(
        RelationalModelSetBuilderContext SetContext,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> ResourcesByKey,
        IReadOnlyDictionary<
            QualifiedResourceName,
            AbstractIdentityTableInfo
        > AbstractIdentityTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> ResourceContextsByResource,
        IDictionary<QualifiedResourceName, TargetIdentityInfo> TargetIdentityCache,
        IDictionary<QualifiedResourceName, ResourceMutation> Mutations
    );

    private sealed record TargetIdentityInfo(
        QualifiedResourceName Resource,
        DbTableName Table,
        IReadOnlyList<JsonPathExpression> IdentityJsonPaths,
        IReadOnlyList<DbColumnName> IdentityColumns,
        bool AllowIdentityUpdates,
        bool IsAbstract
    );

    private sealed record ReferenceIdentityColumnSet(
        IReadOnlyList<DbColumnName> LocalColumns,
        IReadOnlyList<DbColumnName> TargetColumns
    );
}
