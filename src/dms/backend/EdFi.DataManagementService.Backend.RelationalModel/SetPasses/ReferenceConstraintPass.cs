// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives reference foreign keys and all-or-none constraints.
/// </summary>
public sealed class ReferenceConstraintPass : IRelationalModelSetPass
{
    /// <summary>
    /// Applies reference foreign keys and all-or-none constraints for all concrete resources and resource
    /// extensions.
    /// </summary>
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

    /// <summary>
    /// Applies reference constraints for a single resource by attaching all-or-none checks and composite foreign
    /// keys for each bound document reference.
    /// </summary>
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
            var identityColumns = BuildReferenceIdentityColumns(
                mapping,
                binding,
                targetInfo,
                resource,
                builderContext
            );
            var referenceBaseName = ResolveReferenceBaseName(binding.FkColumn, mapping, resource);

            var bindingTable = ResolveReferenceBindingTable(binding, resourceModel, resource);
            var tableAccumulator = mutation.GetTableAccumulator(bindingTable, resource);

            if (identityColumns.LocalColumns.Count > 0)
            {
                if (
                    !ContainsAllOrNoneConstraint(
                        tableAccumulator.Constraints,
                        tableAccumulator.Definition.Table,
                        binding.FkColumn,
                        identityColumns.LocalColumns
                    )
                )
                {
                    var allOrNoneName = ConstraintNaming.BuildAllOrNoneName(
                        tableAccumulator.Definition.Table,
                        referenceBaseName
                    );
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

            var mappedIdentityColumns = MapReferenceIdentityColumnsToStorage(
                identityColumns,
                bindingTable,
                targetInfo.TableModel,
                mapping,
                resource
            );

            EnsureTargetUnique(
                targetInfo,
                mappedIdentityColumns.TargetColumns,
                context.ResourcesByKey,
                context.Mutations
            );

            var localTableMetadata = UnifiedAliasStorageResolver.BuildTableMetadata(
                bindingTable,
                new UnifiedAliasStorageResolver.PresenceGateMetadataOptions(
                    ThrowIfPresenceColumnMissing: false,
                    ThrowIfInvalidStrictSyntheticCandidate: false,
                    UnifiedAliasStorageResolver.ScalarPresenceGateClassification.AnyScalarPresenceGate
                )
            );
            var localReferenceFkColumn = UnifiedAliasStorageResolver.ResolveStorageColumn(
                binding.FkColumn,
                localTableMetadata,
                UnifiedAliasStorageResolver.PresenceGateRejectionPolicy.RejectSyntheticScalarPresence,
                BuildReferenceMappingContext(mapping, resource),
                "reference fk column",
                "foreign keys"
            );

            var targetTableMetadata = UnifiedAliasStorageResolver.BuildTableMetadata(
                targetInfo.TableModel,
                new UnifiedAliasStorageResolver.PresenceGateMetadataOptions(
                    ThrowIfPresenceColumnMissing: false,
                    ThrowIfInvalidStrictSyntheticCandidate: false,
                    UnifiedAliasStorageResolver.ScalarPresenceGateClassification.AnyScalarPresenceGate
                )
            );
            var targetDocumentIdColumn = UnifiedAliasStorageResolver.ResolveStorageColumn(
                RelationalNameConventions.DocumentIdColumnName,
                targetTableMetadata,
                UnifiedAliasStorageResolver.PresenceGateRejectionPolicy.RejectSyntheticScalarPresence,
                BuildReferenceMappingContext(mapping, targetInfo.Resource),
                "target document id column",
                "foreign keys"
            );

            var localColumns = new List<DbColumnName>(1 + mappedIdentityColumns.LocalColumns.Count)
            {
                localReferenceFkColumn,
            };
            localColumns.AddRange(mappedIdentityColumns.LocalColumns);

            var targetColumns = new List<DbColumnName>(1 + mappedIdentityColumns.TargetColumns.Count)
            {
                targetDocumentIdColumn,
            };
            targetColumns.AddRange(mappedIdentityColumns.TargetColumns);

            var onUpdate =
                context.SetContext.Dialect == SqlDialect.Mssql ? ReferentialAction.NoAction
                : targetInfo.IsAbstract ? ReferentialAction.Cascade
                : targetInfo.AllowIdentityUpdates ? ReferentialAction.Cascade
                : ReferentialAction.NoAction;

            if (
                !ContainsForeignKeyConstraint(
                    tableAccumulator.Constraints,
                    tableAccumulator.Definition.Table,
                    localColumns,
                    targetInfo.Table,
                    targetColumns,
                    ReferentialAction.NoAction,
                    onUpdate
                )
            )
            {
                var fkName = ConstraintNaming.BuildReferenceForeignKeyName(
                    tableAccumulator.Definition.Table,
                    referenceBaseName,
                    localColumns.Count > 1
                );

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

    /// <summary>
    /// Maps local and target identity columns to canonical storage columns and de-duplicates pairs after
    /// storage mapping while preserving first-seen identity-path order.
    /// </summary>
    private static ReferenceIdentityColumnSet MapReferenceIdentityColumnsToStorage(
        ReferenceIdentityColumnSet identityColumns,
        DbTableModel localTable,
        DbTableModel targetTable,
        DocumentReferenceMapping mapping,
        QualifiedResourceName resource
    )
    {
        if (identityColumns.LocalColumns.Count != identityColumns.TargetColumns.Count)
        {
            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + "produced mismatched local and target identity-column counts."
            );
        }

        var localTableMetadata = UnifiedAliasStorageResolver.BuildTableMetadata(
            localTable,
            new UnifiedAliasStorageResolver.PresenceGateMetadataOptions(
                ThrowIfPresenceColumnMissing: false,
                ThrowIfInvalidStrictSyntheticCandidate: false,
                UnifiedAliasStorageResolver.ScalarPresenceGateClassification.AnyScalarPresenceGate
            )
        );
        var targetTableMetadata = UnifiedAliasStorageResolver.BuildTableMetadata(
            targetTable,
            new UnifiedAliasStorageResolver.PresenceGateMetadataOptions(
                ThrowIfPresenceColumnMissing: false,
                ThrowIfInvalidStrictSyntheticCandidate: false,
                UnifiedAliasStorageResolver.ScalarPresenceGateClassification.AnyScalarPresenceGate
            )
        );
        var localMappingContext = BuildReferenceMappingContext(mapping, resource);
        var targetMappingContext = BuildReferenceMappingContext(mapping, mapping.TargetResource);
        Dictionary<DbColumnName, DbColumnName> targetByLocalStorageColumn = new();
        HashSet<(DbColumnName LocalStorageColumn, DbColumnName TargetStorageColumn)> seenPairs = [];
        List<DbColumnName> localStorageColumns = [];
        List<DbColumnName> targetStorageColumns = [];

        for (var index = 0; index < identityColumns.LocalColumns.Count; index++)
        {
            var localStorageColumn = UnifiedAliasStorageResolver.ResolveStorageColumn(
                identityColumns.LocalColumns[index],
                localTableMetadata,
                UnifiedAliasStorageResolver.PresenceGateRejectionPolicy.RejectSyntheticScalarPresence,
                localMappingContext,
                "local identity column",
                "foreign keys"
            );
            var targetStorageColumn = UnifiedAliasStorageResolver.ResolveStorageColumn(
                identityColumns.TargetColumns[index],
                targetTableMetadata,
                UnifiedAliasStorageResolver.PresenceGateRejectionPolicy.RejectSyntheticScalarPresence,
                targetMappingContext,
                "target identity column",
                "foreign keys"
            );

            if (
                targetByLocalStorageColumn.TryGetValue(
                    localStorageColumn,
                    out var existingTargetStorageColumn
                ) && !existingTargetStorageColumn.Equals(targetStorageColumn)
            )
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"mapped storage column '{localStorageColumn.Value}' to multiple target "
                        + $"storage columns ('{existingTargetStorageColumn.Value}', '{targetStorageColumn.Value}')."
                );
            }

            targetByLocalStorageColumn[localStorageColumn] = targetStorageColumn;

            if (seenPairs.Add((localStorageColumn, targetStorageColumn)))
            {
                localStorageColumns.Add(localStorageColumn);
                targetStorageColumns.Add(targetStorageColumn);
            }
        }

        return new ReferenceIdentityColumnSet(localStorageColumns.ToArray(), targetStorageColumns.ToArray());
    }

    /// <summary>
    /// Builds the local and target identity column sets required for a composite reference FK, validating that
    /// mapping and binding identity paths align.
    /// </summary>
    private static ReferenceIdentityColumnSet BuildReferenceIdentityColumns(
        DocumentReferenceMapping mapping,
        DocumentReferenceBinding binding,
        TargetIdentityInfo targetInfo,
        QualifiedResourceName resource,
        RelationalModelBuilderContext builderContext
    )
    {
        var referenceBaseName = ResolveReferenceBaseName(binding.FkColumn, mapping, resource);

        Dictionary<string, ReferenceIdentityBinding> identityBindingsByColumnName = new(
            StringComparer.Ordinal
        );
        Dictionary<string, int> bindingCountsByReferencePath = new(StringComparer.Ordinal);

        foreach (var identityBinding in binding.IdentityBindings)
        {
            if (!identityBindingsByColumnName.TryAdd(identityBinding.Column.Value, identityBinding))
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"contains duplicate identity binding column '{identityBinding.Column.Value}'."
                );
            }

            var referencePath = identityBinding.ReferenceJsonPath.Canonical;
            bindingCountsByReferencePath[referencePath] = bindingCountsByReferencePath.TryGetValue(
                referencePath,
                out var count
            )
                ? count + 1
                : 1;
        }

        Dictionary<string, int> mappingCountsByReferencePath = new(StringComparer.Ordinal);

        foreach (var path in mapping.ReferenceJsonPaths)
        {
            var referencePath = path.ReferenceJsonPath.Canonical;
            mappingCountsByReferencePath[referencePath] = mappingCountsByReferencePath.TryGetValue(
                referencePath,
                out var count
            )
                ? count + 1
                : 1;
        }

        List<string> missingReferencePaths = new();
        List<string> extraReferencePaths = new();

        foreach (var entry in mappingCountsByReferencePath)
        {
            if (!bindingCountsByReferencePath.TryGetValue(entry.Key, out var bindingCount))
            {
                missingReferencePaths.Add(FormatReferencePathCount(entry.Key, entry.Value, 0));
                continue;
            }

            if (bindingCount < entry.Value)
            {
                missingReferencePaths.Add(FormatReferencePathCount(entry.Key, entry.Value, bindingCount));
            }
            else if (bindingCount > entry.Value)
            {
                extraReferencePaths.Add(FormatReferencePathCount(entry.Key, entry.Value, bindingCount));
            }
        }

        foreach (var entry in bindingCountsByReferencePath)
        {
            if (!mappingCountsByReferencePath.ContainsKey(entry.Key))
            {
                extraReferencePaths.Add(FormatReferencePathCount(entry.Key, 0, entry.Value));
            }
        }

        if (missingReferencePaths.Count > 0 || extraReferencePaths.Count > 0)
        {
            var missingSummary =
                missingReferencePaths.Count > 0
                    ? $"missing bindings for {string.Join(
                        ", ",
                        missingReferencePaths.OrderBy(path => path, StringComparer.Ordinal)
                    )}"
                    : null;
            var extraSummary =
                extraReferencePaths.Count > 0
                    ? $"extra bindings for {string.Join(
                        ", ",
                        extraReferencePaths.OrderBy(path => path, StringComparer.Ordinal)
                    )}"
                    : null;
            var details = string.Join(
                "; ",
                new[] { missingSummary, extraSummary }.Where(entry => entry is not null)
            );

            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + $"did not align referenceJsonPaths with identity bindings: {details}."
            );
        }

        Dictionary<string, JsonPathExpression> referencePathsByIdentityPath = new(StringComparer.Ordinal);
        Dictionary<string, DbColumnName> localColumnsByIdentityPath = new(StringComparer.Ordinal);
        var referenceIdentityFieldBaseNameCounts = BuildReferenceIdentityFieldBaseNameCounts(
            mapping.ReferenceObjectPath,
            mapping.ReferenceJsonPaths
        );

        foreach (var path in mapping.ReferenceJsonPaths)
        {
            var identityPartBaseName = ResolveReferenceIdentityPartBaseName(
                mapping.ReferenceObjectPath,
                path,
                referenceIdentityFieldBaseNameCounts
            );

            if (
                builderContext.TryGetNameOverride(
                    path.ReferenceJsonPath,
                    NameOverrideKind.Column,
                    out var identityOverride
                )
            )
            {
                identityPartBaseName = identityOverride;
            }

            var identityColumnBaseName = $"{referenceBaseName}_{identityPartBaseName}";

            if (
                !TryResolveIdentityBindingColumn(
                    identityBindingsByColumnName,
                    identityColumnBaseName,
                    out var identityBinding
                ) || identityBinding is null
            )
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"did not resolve identity column for path '{path.IdentityJsonPath.Canonical}' "
                        + $"under reference path '{path.ReferenceJsonPath.Canonical}'."
                );
            }

            if (
                !string.Equals(
                    identityBinding.ReferenceJsonPath.Canonical,
                    path.ReferenceJsonPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"did not align identity column '{identityBinding.Column.Value}' with "
                        + $"reference path '{path.ReferenceJsonPath.Canonical}'."
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

    /// <summary>
    /// Resolves the base reference name from a reference FK column by trimming the <c>_DocumentId</c> suffix.
    /// </summary>
    private static string ResolveReferenceBaseName(
        DbColumnName fkColumn,
        DocumentReferenceMapping mapping,
        QualifiedResourceName resource
    )
    {
        const string DocumentIdSuffix = "_DocumentId";

        if (!fkColumn.Value.EndsWith(DocumentIdSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + $"expected FK column '{fkColumn.Value}' to end with '{DocumentIdSuffix}'."
            );
        }

        return fkColumn.Value[..^DocumentIdSuffix.Length];
    }

    /// <summary>
    /// Formats a diagnostic summary for a reference path count mismatch.
    /// </summary>
    private static string FormatReferencePathCount(string path, int expected, int actual)
    {
        return $"'{path}' (expected {expected}, found {actual})";
    }

    /// <summary>
    /// Attempts to resolve the binding column for a propagated identity part, allowing for descriptor FK column
    /// naming.
    /// </summary>
    private static bool TryResolveIdentityBindingColumn(
        IReadOnlyDictionary<string, ReferenceIdentityBinding> identityBindingsByColumnName,
        string identityColumnBaseName,
        out ReferenceIdentityBinding? identityBinding
    )
    {
        if (identityBindingsByColumnName.TryGetValue(identityColumnBaseName, out identityBinding))
        {
            return true;
        }

        var descriptorColumnName = RelationalNameConventions.DescriptorIdColumnName(identityColumnBaseName);

        if (identityBindingsByColumnName.TryGetValue(descriptorColumnName.Value, out identityBinding))
        {
            return true;
        }

        identityBinding = default!;
        return false;
    }

    /// <summary>
    /// Builds a consistent reference-mapping context prefix for invariant errors.
    /// </summary>
    private static string BuildReferenceMappingContext(
        DocumentReferenceMapping mapping,
        QualifiedResourceName resource
    )
    {
        return $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}'";
    }

    /// <summary>
    /// Ensures that a concrete target resource root has a unique constraint on its document id and identity
    /// columns so referencing composite foreign keys are valid.
    /// </summary>
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

        // Cross-resource side effect: ensure target root has a unique constraint when another resource references it.
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

        if (
            !ContainsUniqueConstraint(
                tableAccumulator.Constraints,
                tableAccumulator.Definition.Table,
                uniqueColumns
            )
        )
        {
            var uniqueName = ConstraintNaming.BuildReferenceKeyUniqueName(tableAccumulator.Definition.Table);
            tableAccumulator.AddConstraint(new TableConstraint.Unique(uniqueName, uniqueColumns.ToArray()));
            mutation.MarkTableMutated(entry.Model.RelationalModel.Root);
        }
    }

    /// <summary>
    /// Gets identity metadata for a target resource, using an abstract identity table when the target is
    /// abstract and caching results for reuse.
    /// </summary>
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
                abstractTable.TableModel,
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
            entry.Model.RelationalModel.Root,
            builderContext.IdentityJsonPaths,
            identityColumns,
            builderContext.AllowIdentityUpdates,
            IsAbstract: false
        );

        context.TargetIdentityCache[targetResource] = info;
        return info;
    }

    /// <summary>
    /// Resolves the concrete table model for a reference binding, selecting the best matching JSON scope when a
    /// table name appears in multiple scopes (e.g., extensions).
    /// </summary>
    private static DbTableModel ResolveReferenceBindingTable(
        DocumentReferenceBinding binding,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        var candidates = resourceModel
            .TablesInDependencyOrder.Where(table => table.Table.Equals(binding.Table))
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

    /// <summary>
    /// Builds the ordered identity column list for a resource root table based on <c>identityJsonPaths</c>.
    /// </summary>
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

    /// <summary>
    /// Builds a lookup from qualified resource name to its concrete schema context (excluding extensions).
    /// </summary>
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

    /// <summary>
    /// Holds shared lookups, caches, and mutation accumulators used during reference constraint derivation.
    /// </summary>
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

    /// <summary>
    /// Captures the target table and identity metadata used to build composite reference foreign keys.
    /// </summary>
    private sealed record TargetIdentityInfo(
        QualifiedResourceName Resource,
        DbTableName Table,
        DbTableModel TableModel,
        IReadOnlyList<JsonPathExpression> IdentityJsonPaths,
        IReadOnlyList<DbColumnName> IdentityColumns,
        bool AllowIdentityUpdates,
        bool IsAbstract
    );

    /// <summary>
    /// Captures the local and target column lists required to build a composite reference foreign key.
    /// </summary>
    private sealed record ReferenceIdentityColumnSet(
        IReadOnlyList<DbColumnName> LocalColumns,
        IReadOnlyList<DbColumnName> TargetColumns
    );
}
