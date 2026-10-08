// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives root-table unique constraints for each concrete resource.
/// </summary>
public sealed class RootIdentityConstraintPass : IRelationalModelSetPass
{
    /// <summary>
    /// Applies root-table uniqueness constraints for each concrete resource model in the set.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);

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

            if (!resourcesByKey.TryGetValue(resource, out var entry))
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found for constraint derivation."
                );
            }

            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);
            var updatedModel = ApplyRootConstraints(builderContext, entry.Model.RelationalModel, resource);

            if (ReferenceEquals(updatedModel, entry.Model.RelationalModel))
            {
                continue;
            }

            context.ConcreteResourcesInNameOrder[entry.Index] = entry.Model with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    /// <summary>
    /// Builds and attaches root-table unique constraints derived from <c>identityJsonPaths</c>, using FK columns
    /// for identity components sourced from document references.
    /// </summary>
    private static RelationalResourceModel ApplyRootConstraints(
        RelationalModelBuilderContext builderContext,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        // Core DDL owns provider-specific reconstructed-URI uniqueness for the shared descriptor table.
        if (resourceModel.StorageKind is ResourceStorageKind.SharedDescriptorTable)
        {
            return resourceModel;
        }

        var rootTable = resourceModel.Root;
        var tableAccumulator = new TableColumnAccumulator(rootTable);
        var mutated = false;

        var identityColumns = BuildRootIdentityColumns(resourceModel, builderContext, resource);

        if (identityColumns.Count == 0)
        {
            return resourceModel;
        }

        if (!ContainsUniqueConstraint(rootTable.Constraints, rootTable.Table, identityColumns))
        {
            var rootUniqueName = ConstraintNaming.BuildNaturalKeyUniqueName(rootTable.Table);
            tableAccumulator.AddConstraint(new TableConstraint.Unique(rootUniqueName, identityColumns));
            mutated = true;
        }

        if (!mutated)
        {
            return resourceModel;
        }

        var updatedRootTable = RelationalModelOrdering.CanonicalizeTable(tableAccumulator.Build());

        return UpdateResourceModel(resourceModel, updatedRootTable);
    }

    /// <summary>
    /// Updates the resource model by replacing the root table with an updated definition.
    /// </summary>
    private static RelationalResourceModel UpdateResourceModel(
        RelationalResourceModel resourceModel,
        DbTableModel updatedRoot
    )
    {
        var updatedTables = resourceModel
            .TablesInDependencyOrder.Select(table =>
                table.JsonScope.Canonical == updatedRoot.JsonScope.Canonical ? updatedRoot : table
            )
            .ToArray();

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInDependencyOrder = updatedTables,
        };
    }

    /// <summary>
    /// Builds the ordered set of root identity columns used by the natural-key unique constraint.
    /// </summary>
    private static IReadOnlyList<DbColumnName> BuildRootIdentityColumns(
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
        var referenceBindingsByIdentityPath = BuildReferenceIdentityBindings(
            resourceModel.DocumentReferenceBindings,
            resource
        );

        HashSet<string> seenColumns = new(StringComparer.Ordinal);
        List<DbColumnName> uniqueColumns = new(builderContext.IdentityJsonPaths.Count);

        foreach (var identityPath in builderContext.IdentityJsonPaths)
        {
            if (identityPath.Segments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "must not include array segments when deriving root unique constraints."
                );
            }

            if (referenceBindingsByIdentityPath.TryGetValue(identityPath.Canonical, out var binding))
            {
                if (binding.Table != rootTable.Table)
                {
                    throw new InvalidOperationException(
                        $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                            + "must bind to the root table when deriving unique constraints."
                    );
                }

                AddUniqueColumn(binding.FkColumn, uniqueColumns, seenColumns);
                continue;
            }

            if (!rootColumnsByPath.TryGetValue(identityPath.Canonical, out var columnName))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column."
                );
            }

            AddUniqueColumn(columnName, uniqueColumns, seenColumns);
        }

        return uniqueColumns.ToArray();
    }
}
