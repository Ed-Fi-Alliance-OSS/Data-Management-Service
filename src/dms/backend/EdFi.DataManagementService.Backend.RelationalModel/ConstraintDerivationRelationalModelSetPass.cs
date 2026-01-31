// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives root-level uniqueness constraints and descriptor-system columns from identity metadata.
/// </summary>
public sealed class ConstraintDerivationRelationalModelSetPass : IRelationalModelSetPass
{
    private const string UriColumnLabel = "Uri";
    private const string DiscriminatorColumnLabel = "Discriminator";
    private const int UriMaxLength = 306;
    private const int DiscriminatorMaxLength = 128;

    /// <summary>
    /// The explicit order for the constraint derivation pass.
    /// </summary>
    public int Order { get; } = 40;

    /// <summary>
    /// Derives root-table unique constraints for each concrete resource.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ApplyRootConstraints(context);
        ApplyReferenceConstraints(context);
    }

    private static void ApplyRootConstraints(RelationalModelSetBuilderContext context)
    {
        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);

        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);

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

            var builderContext = BuildResourceContext(resourceContext, apiSchemaRootsByProjectEndpoint);
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

    private static void ApplyReferenceConstraints(RelationalModelSetBuilderContext context)
    {
        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);
        var baseResourcesByName = BuildBaseResourceLookup(context.ConcreteResourcesInNameOrder);
        var abstractIdentityTablesByResource = context.AbstractIdentityTablesInNameOrder.ToDictionary(table =>
            table.AbstractResourceKey.Resource
        );
        var resourceContextsByResource = BuildResourceContextLookup(context);
        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);
        Dictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource = new();
        Dictionary<QualifiedResourceName, TargetIdentityInfo> targetIdentityCache = new();
        Dictionary<QualifiedResourceName, ResourceMutation> mutations = new();

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = GetOrCreateBuilderContext(
                resourceContext,
                apiSchemaRootsByProjectEndpoint,
                builderContextsByResource
            );

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
                var baseResource = baseEntry.Model.ResourceKey.Resource;
                var mutation = GetOrCreateMutation(baseResource, baseEntry, mutations);

                ApplyReferenceConstraintsForResource(
                    mutation,
                    baseEntry.Model.RelationalModel,
                    builderContext,
                    baseResource,
                    resourcesByKey,
                    abstractIdentityTablesByResource,
                    resourceContextsByResource,
                    apiSchemaRootsByProjectEndpoint,
                    builderContextsByResource,
                    targetIdentityCache,
                    mutations
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
                resourceMutation,
                entry.Model.RelationalModel,
                builderContext,
                resource,
                resourcesByKey,
                abstractIdentityTablesByResource,
                resourceContextsByResource,
                apiSchemaRootsByProjectEndpoint,
                builderContextsByResource,
                targetIdentityCache,
                mutations
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

    private static RelationalResourceModel ApplyRootConstraints(
        RelationalModelBuilderContext builderContext,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        var rootTable = resourceModel.Root;
        var tableBuilder = new TableBuilder(rootTable);
        var mutated = false;

        if (resourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            mutated |= EnsureDescriptorColumn(tableBuilder, rootTable, BuildUriColumn(), UriColumnLabel);
            mutated |= EnsureDescriptorColumn(
                tableBuilder,
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
                tableBuilder.AddConstraint(
                    new TableConstraint.Unique(descriptorUniqueName, descriptorUniqueColumns)
                );
                mutated = true;
            }

            if (!mutated)
            {
                return resourceModel;
            }

            var updatedRoot = CanonicalizeTable(tableBuilder.Build());

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
            tableBuilder.AddConstraint(new TableConstraint.Unique(rootUniqueName, identityColumns));
            mutated = true;
        }

        if (!mutated)
        {
            return resourceModel;
        }

        var updatedRootTable = CanonicalizeTable(tableBuilder.Build());

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
            TablesInWriteDependencyOrder = updatedTables,
        };
    }

    private static RelationalResourceModel UpdateResourceModel(
        RelationalResourceModel resourceModel,
        ResourceMutation mutation
    )
    {
        var updatedTables = resourceModel
            .TablesInReadDependencyOrder.Select(table => mutation.BuildTable(table))
            .ToArray();
        var updatedRoot = updatedTables.Single(table => table.Table.Equals(resourceModel.Root.Table));

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInReadDependencyOrder = updatedTables,
            TablesInWriteDependencyOrder = updatedTables,
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
        var rootColumnsByPath = rootTable
            .Columns.Where(column => column.SourceJsonPath is not null)
            .GroupBy(column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ColumnName, StringComparer.Ordinal);
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
        var rootColumnsByPath = rootTable
            .Columns.Where(column => column.SourceJsonPath is not null)
            .GroupBy(column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ColumnName, StringComparer.Ordinal);

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

    private static IReadOnlyDictionary<string, DocumentReferenceBinding> BuildReferenceIdentityBindings(
        IReadOnlyList<DocumentReferenceBinding> bindings,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, DocumentReferenceBinding> lookup = new(StringComparer.Ordinal);

        foreach (var binding in bindings)
        {
            foreach (var identityBinding in binding.IdentityBindings)
            {
                if (!lookup.TryAdd(identityBinding.ReferenceJsonPath.Canonical, binding))
                {
                    var existing = lookup[identityBinding.ReferenceJsonPath.Canonical];

                    if (existing.ReferenceObjectPath.Canonical == binding.ReferenceObjectPath.Canonical)
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Identity path '{identityBinding.ReferenceJsonPath.Canonical}' on resource "
                            + $"'{FormatResource(resource)}' was bound to multiple references."
                    );
                }
            }
        }

        return lookup;
    }

    private static void ApplyReferenceConstraintsForResource(
        ResourceMutation mutation,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IReadOnlyDictionary<
            QualifiedResourceName,
            AbstractIdentityTableInfo
        > abstractIdentityTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        IDictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource,
        IDictionary<QualifiedResourceName, TargetIdentityInfo> targetIdentityCache,
        IDictionary<QualifiedResourceName, ResourceMutation> mutations
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

            var targetInfo = GetTargetIdentityInfo(
                mapping.TargetResource,
                resourcesByKey,
                abstractIdentityTablesByResource,
                resourceContextsByResource,
                apiSchemaRootsByProjectEndpoint,
                builderContextsByResource,
                targetIdentityCache
            );
            var identityColumns = BuildReferenceIdentityColumns(mapping, binding, targetInfo, resource);

            var hasCompleteIdentity = identityColumns.TargetColumns.Count == targetInfo.IdentityColumns.Count;

            if (targetInfo.IsAbstract && !hasCompleteIdentity)
            {
                continue;
            }

            EnsureTargetUnique(targetInfo, identityColumns.TargetColumns, resourcesByKey, mutations);

            var tableBuilder = mutation.GetTableBuilder(binding.Table, resource);

            if (identityColumns.LocalColumns.Count > 0)
            {
                var allOrNoneName = BuildAllOrNoneConstraintName(
                    tableBuilder.Definition.Table.Name,
                    binding.FkColumn
                );

                if (!ContainsAllOrNoneConstraint(tableBuilder.Constraints, allOrNoneName))
                {
                    tableBuilder.AddConstraint(
                        new TableConstraint.AllOrNoneNullability(
                            allOrNoneName,
                            binding.FkColumn,
                            identityColumns.LocalColumns
                        )
                    );
                    mutation.MarkTableMutated(tableBuilder.Definition.Table);
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
                tableBuilder.Definition.Table.Name,
                localColumns
            );

            if (!ContainsForeignKeyConstraint(tableBuilder.Constraints, fkName))
            {
                var onUpdate =
                    targetInfo.IsAbstract ? ReferentialAction.NoAction
                    : targetInfo.AllowIdentityUpdates ? ReferentialAction.Cascade
                    : ReferentialAction.NoAction;

                tableBuilder.AddConstraint(
                    new TableConstraint.ForeignKey(
                        fkName,
                        localColumns.ToArray(),
                        targetInfo.Table,
                        targetColumns.ToArray(),
                        OnDelete: ReferentialAction.NoAction,
                        OnUpdate: onUpdate
                    )
                );
                mutation.MarkTableMutated(tableBuilder.Definition.Table);
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

        foreach (var path in mapping.ReferenceJsonPaths)
        {
            if (!referencePathsByIdentityPath.TryAdd(path.IdentityJsonPath.Canonical, path.ReferenceJsonPath))
            {
                var existing = referencePathsByIdentityPath[path.IdentityJsonPath.Canonical];

                if (string.CompareOrdinal(existing.Canonical, path.ReferenceJsonPath.Canonical) > 0)
                {
                    referencePathsByIdentityPath[path.IdentityJsonPath.Canonical] = path.ReferenceJsonPath;
                }
            }
        }

        Dictionary<string, DbColumnName> columnsByReferencePath = new(StringComparer.Ordinal);

        foreach (var identityBinding in binding.IdentityBindings)
        {
            if (
                !columnsByReferencePath.TryAdd(
                    identityBinding.ReferenceJsonPath.Canonical,
                    identityBinding.Column
                )
            )
            {
                var existing = columnsByReferencePath[identityBinding.ReferenceJsonPath.Canonical];

                if (string.CompareOrdinal(existing.Value, identityBinding.Column.Value) > 0)
                {
                    columnsByReferencePath[identityBinding.ReferenceJsonPath.Canonical] =
                        identityBinding.Column;
                }
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

        foreach (var identityPath in targetInfo.IdentityJsonPaths)
        {
            if (!referencePathsByIdentityPath.TryGetValue(identityPath.Canonical, out var referencePath))
            {
                continue;
            }

            if (!columnsByReferencePath.TryGetValue(referencePath.Canonical, out var localColumn))
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"did not bind reference path '{referencePath.Canonical}' to a column."
                );
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
        var tableBuilder = mutation.GetTableBuilder(
            entry.Model.RelationalModel.Root.Table,
            targetInfo.Resource
        );
        var uniqueColumns = new List<DbColumnName>(1 + targetIdentityColumns.Count)
        {
            RelationalNameConventions.DocumentIdColumnName,
        };
        uniqueColumns.AddRange(targetIdentityColumns);

        var uniqueName = BuildUniqueConstraintName(tableBuilder.Definition.Table.Name, uniqueColumns);

        if (!ContainsUniqueConstraint(tableBuilder.Constraints, uniqueName))
        {
            tableBuilder.AddConstraint(new TableConstraint.Unique(uniqueName, uniqueColumns.ToArray()));
            mutation.MarkTableMutated(tableBuilder.Definition.Table);
        }
    }

    private static TargetIdentityInfo GetTargetIdentityInfo(
        QualifiedResourceName targetResource,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IReadOnlyDictionary<
            QualifiedResourceName,
            AbstractIdentityTableInfo
        > abstractIdentityTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        IDictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource,
        IDictionary<QualifiedResourceName, TargetIdentityInfo> targetIdentityCache
    )
    {
        if (targetIdentityCache.TryGetValue(targetResource, out var cached))
        {
            return cached;
        }

        if (abstractIdentityTablesByResource.TryGetValue(targetResource, out var abstractTable))
        {
            var abstractIdentityColumns = abstractTable
                .ColumnsInIdentityOrder.Where(column => column.SourceJsonPath is not null)
                .ToArray();
            var identityPaths = abstractIdentityColumns
                .Select(column => column.SourceJsonPath!.Value)
                .ToArray();
            var columnNames = abstractIdentityColumns.Select(column => column.ColumnName).ToArray();

            var abstractInfo = new TargetIdentityInfo(
                targetResource,
                abstractTable.Table,
                identityPaths,
                columnNames,
                AllowIdentityUpdates: false,
                IsAbstract: true
            );

            targetIdentityCache[targetResource] = abstractInfo;
            return abstractInfo;
        }

        if (!resourcesByKey.TryGetValue(targetResource, out var entry))
        {
            throw new InvalidOperationException(
                $"Reference target resource '{FormatResource(targetResource)}' was not found "
                    + "for constraint derivation."
            );
        }

        if (!resourceContextsByResource.TryGetValue(targetResource, out var resourceContext))
        {
            throw new InvalidOperationException(
                $"Reference target resource '{FormatResource(targetResource)}' was not found "
                    + "in schema inputs."
            );
        }

        var builderContext = GetOrCreateBuilderContext(
            resourceContext,
            apiSchemaRootsByProjectEndpoint,
            builderContextsByResource
        );
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

        targetIdentityCache[targetResource] = info;
        return info;
    }

    private static ResourceMutation GetOrCreateMutation(
        QualifiedResourceName resource,
        ResourceEntry entry,
        IDictionary<QualifiedResourceName, ResourceMutation> mutations
    )
    {
        if (mutations.TryGetValue(resource, out var existing))
        {
            return existing;
        }

        var mutation = new ResourceMutation(entry);
        mutations[resource] = mutation;
        return mutation;
    }

    private static void AddUniqueColumn(
        DbColumnName columnName,
        ICollection<DbColumnName> columns,
        ISet<string> seenColumns
    )
    {
        if (!seenColumns.Add(columnName.Value))
        {
            return;
        }

        columns.Add(columnName);
    }

    private static bool EnsureDescriptorColumn(
        TableBuilder tableBuilder,
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

        tableBuilder.AddColumn(column);

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

    private static string BuildUniqueConstraintName(string tableName, IReadOnlyList<DbColumnName> columns)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Unique constraint must include at least one column.");
        }

        return $"UX_{tableName}_{string.Join("_", columns.Select(column => column.Value))}";
    }

    private static bool ContainsUniqueConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.Unique>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    private static bool ContainsForeignKeyConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.ForeignKey>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    private static bool ContainsAllOrNoneConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.AllOrNoneNullability>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    private static string BuildAllOrNoneConstraintName(string tableName, DbColumnName fkColumn)
    {
        return $"CK_{tableName}_{fkColumn.Value}_AllOrNone";
    }

    private static DbTableModel CanonicalizeTable(DbTableModel table)
    {
        var keyColumnOrder = BuildKeyColumnOrder(table.Key.Columns);

        var orderedColumns = table
            .Columns.OrderBy(column => GetColumnGroup(column, keyColumnOrder))
            .ThenBy(column => GetColumnKeyIndex(column, keyColumnOrder))
            .ThenBy(column => column.ColumnName.Value, StringComparer.Ordinal)
            .ToArray();

        var orderedConstraints = table
            .Constraints.OrderBy(GetConstraintGroup)
            .ThenBy(GetConstraintName, StringComparer.Ordinal)
            .ToArray();

        return table with
        {
            Columns = orderedColumns,
            Constraints = orderedConstraints,
        };
    }

    private static Dictionary<string, int> BuildKeyColumnOrder(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        Dictionary<string, int> keyOrder = new(StringComparer.Ordinal);

        for (var index = 0; index < keyColumns.Count; index++)
        {
            keyOrder[keyColumns[index].ColumnName.Value] = index;
        }

        return keyOrder;
    }

    private static int GetColumnGroup(DbColumnModel column, IReadOnlyDictionary<string, int> keyColumnOrder)
    {
        if (keyColumnOrder.ContainsKey(column.ColumnName.Value))
        {
            return 0;
        }

        return column.Kind switch
        {
            ColumnKind.DescriptorFk => 1,
            ColumnKind.Scalar => 2,
            _ => 3,
        };
    }

    private static int GetColumnKeyIndex(
        DbColumnModel column,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        return keyColumnOrder.TryGetValue(column.ColumnName.Value, out var index) ? index : int.MaxValue;
    }

    private static int GetConstraintGroup(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique => 1,
            TableConstraint.ForeignKey => 2,
            TableConstraint.AllOrNoneNullability => 3,
            _ => 99,
        };
    }

    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            _ => string.Empty,
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

    private static RelationalModelBuilderContext GetOrCreateBuilderContext(
        ConcreteResourceSchemaContext resourceContext,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        IDictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource
    )
    {
        var resource = new QualifiedResourceName(
            resourceContext.Project.ProjectSchema.ProjectName,
            resourceContext.ResourceName
        );

        if (builderContextsByResource.TryGetValue(resource, out var cached))
        {
            return cached;
        }

        var builderContext = BuildResourceContext(resourceContext, apiSchemaRootsByProjectEndpoint);
        builderContextsByResource[resource] = builderContext;
        return builderContext;
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

    private static Dictionary<string, List<ResourceEntry>> BuildBaseResourceLookup(
        IReadOnlyList<ConcreteResourceModel> resources
    )
    {
        Dictionary<string, List<ResourceEntry>> lookup = new(StringComparer.Ordinal);

        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            var resourceName = resource.ResourceKey.Resource.ResourceName;

            if (!lookup.TryGetValue(resourceName, out var entries))
            {
                entries = [];
                lookup.Add(resourceName, entries);
            }

            entries.Add(new ResourceEntry(index, resource));
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

    private sealed record ResourceEntry(int Index, ConcreteResourceModel Model);

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

    private sealed class ResourceMutation
    {
        private readonly HashSet<DbTableName> _mutatedTables = new();

        public ResourceMutation(ResourceEntry entry)
        {
            Entry = entry;
            TableBuilders = new Dictionary<DbTableName, TableBuilder>();

            foreach (var table in entry.Model.RelationalModel.TablesInReadDependencyOrder)
            {
                if (TableBuilders.ContainsKey(table.Table))
                {
                    continue;
                }

                TableBuilders[table.Table] = new TableBuilder(table);
            }
        }

        public ResourceEntry Entry { get; }

        public Dictionary<DbTableName, TableBuilder> TableBuilders { get; }

        public bool HasChanges => _mutatedTables.Count > 0;

        public TableBuilder GetTableBuilder(DbTableName tableName, QualifiedResourceName resource)
        {
            if (TableBuilders.TryGetValue(tableName, out var builder))
            {
                return builder;
            }

            throw new InvalidOperationException(
                $"Table '{tableName}' for resource '{FormatResource(resource)}' "
                    + "was not found for constraint derivation."
            );
        }

        public void MarkTableMutated(DbTableName tableName)
        {
            _mutatedTables.Add(tableName);
        }

        public DbTableModel BuildTable(DbTableModel original)
        {
            if (!TableBuilders.TryGetValue(original.Table, out var builder))
            {
                throw new InvalidOperationException(
                    $"Table '{original.Table}' was not found for constraint derivation."
                );
            }

            var built = builder.Build();
            return _mutatedTables.Contains(original.Table) ? CanonicalizeTable(built) : built;
        }
    }

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
