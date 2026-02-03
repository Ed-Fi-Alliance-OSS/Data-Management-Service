// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives root-table unique constraints for each concrete resource.
/// </summary>
public sealed class RootIdentityConstraintRelationalModelSetPass : IRelationalModelSetPass
{
    private const string UriColumnLabel = "Uri";
    private const string DiscriminatorColumnLabel = "Discriminator";
    private const int UriMaxLength = 306;
    private const int DiscriminatorMaxLength = 128;

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

    private static RelationalResourceModel ApplyRootConstraints(
        RelationalModelBuilderContext builderContext,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        var rootTable = resourceModel.Root;
        var tableAccumulator = new TableColumnAccumulator(rootTable);
        var mutated = false;

        if (resourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            mutated |= EnsureDescriptorColumn(tableAccumulator, rootTable, BuildUriColumn(), UriColumnLabel);
            mutated |= EnsureDescriptorColumn(
                tableAccumulator,
                rootTable,
                BuildDiscriminatorColumn(),
                DiscriminatorColumnLabel
            );

            var descriptorUniqueColumns = new DbColumnName[]
            {
                new(UriColumnLabel),
                new(DiscriminatorColumnLabel),
            };
            var descriptorUniqueName = BuildUniqueConstraintName(
                rootTable.Table.Name,
                descriptorUniqueColumns
            );

            if (!ContainsUniqueConstraint(rootTable.Constraints, descriptorUniqueName))
            {
                tableAccumulator.AddConstraint(
                    new TableConstraint.Unique(descriptorUniqueName, descriptorUniqueColumns)
                );
                mutated = true;
            }

            if (!mutated)
            {
                return resourceModel;
            }

            var updatedRoot = RelationalModelOrdering.CanonicalizeTable(tableAccumulator.Build());

            return UpdateResourceModel(resourceModel, updatedRoot);
        }

        var identityColumns = BuildRootIdentityColumns(resourceModel, builderContext, resource);

        if (identityColumns.Count == 0)
        {
            return resourceModel;
        }

        var rootUniqueName = BuildUniqueConstraintName(rootTable.Table.Name, identityColumns);

        if (!ContainsUniqueConstraint(rootTable.Constraints, rootUniqueName))
        {
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

    private static RelationalResourceModel UpdateResourceModel(
        RelationalResourceModel resourceModel,
        DbTableModel updatedRoot
    )
    {
        var updatedTables = resourceModel
            .TablesInReadDependencyOrder.Select(table =>
                table.JsonScope.Canonical == updatedRoot.JsonScope.Canonical ? updatedRoot : table
            )
            .ToArray();

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInReadDependencyOrder = updatedTables,
        };
    }

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

    private static bool EnsureDescriptorColumn(
        TableColumnAccumulator tableAccumulator,
        DbTableModel rootTable,
        DbColumnModel column,
        string columnName
    )
    {
        if (
            rootTable.Columns.Any(existing =>
                string.Equals(existing.ColumnName.Value, columnName, StringComparison.Ordinal)
            )
        )
        {
            return false;
        }

        tableAccumulator.AddColumn(column);

        return true;
    }

    private static DbColumnModel BuildUriColumn()
    {
        return new DbColumnModel(
            new DbColumnName(UriColumnLabel),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, UriMaxLength),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static DbColumnModel BuildDiscriminatorColumn()
    {
        return new DbColumnModel(
            new DbColumnName(DiscriminatorColumnLabel),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, DiscriminatorMaxLength),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }
}
