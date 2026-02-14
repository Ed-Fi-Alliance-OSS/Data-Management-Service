// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;
using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives the trigger inventory for all schema-derived tables (concrete resources with
/// <see cref="ResourceStorageKind.RelationalTables"/> storage). Descriptor resources are skipped.
/// </summary>
public sealed class DeriveTriggerInventoryPass : IRelationalModelSetPass
{
    private const string StampToken = "Stamp";
    private const string ReferentialIdentityToken = "ReferentialIdentity";
    private const string AbstractIdentityToken = "AbstractIdentity";
    private const string PropagationFallbackPrefix = "PropagateIdentity";

    /// <summary>
    /// Populates <see cref="RelationalModelSetBuilderContext.TriggerInventory"/> with
    /// <c>DocumentStamping</c>, <c>ReferentialIdentityMaintenance</c>,
    /// <c>AbstractIdentityMaintenance</c>, and (MSSQL only) <c>IdentityPropagationFallback</c>
    /// triggers.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);
        Dictionary<
            DbTableName,
            List<DbIdentityPropagationReferrerAction>
        > propagationFallbackActionsByTriggerTable = new();

        var abstractTablesByResource = context.AbstractIdentityTablesInNameOrder.ToDictionary(table =>
            table.AbstractResourceKey.Resource
        );

        var resourceContextsByResource = BuildResourceContextLookup(context);

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
                    $"Concrete resource '{FormatResource(resource)}' was not found for trigger derivation."
                );
            }

            var concreteModel = entry.Model;

            if (concreteModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            var resourceModel = concreteModel.RelationalModel;
            var rootTable = resourceModel.Root;
            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);

            // Resolve identity projection columns for the root table.
            var identityProjectionColumns = BuildRootIdentityProjectionColumns(
                resourceModel,
                builderContext,
                resource
            );

            // DocumentStamping trigger for each table (root, child, extension).
            foreach (var table in resourceModel.TablesInDependencyOrder)
            {
                var documentIdKeyColumn = table.Key.Columns.FirstOrDefault(c =>
                    c.Kind == ColumnKind.ParentKeyPart
                    && RelationalNameConventions.IsDocumentIdColumn(c.ColumnName)
                );

                if (documentIdKeyColumn is null)
                {
                    throw new InvalidOperationException(
                        $"DocumentStamping trigger derivation requires a DocumentId key column, "
                            + $"but none was found on table '{table.Table.Schema.Value}.{table.Table.Name}' "
                            + $"for resource '{FormatResource(resource)}'."
                    );
                }

                IReadOnlyList<DbColumnName> keyColumns = [documentIdKeyColumn.ColumnName];
                var isRootTable = table.Table.Equals(rootTable.Table);

                context.TriggerInventory.Add(
                    new DbTriggerInfo(
                        new DbTriggerName(BuildTriggerName(table.Table, StampToken)),
                        table.Table,
                        DbTriggerKind.DocumentStamping,
                        keyColumns,
                        isRootTable ? identityProjectionColumns : []
                    )
                );
            }

            // ReferentialIdentityMaintenance trigger on the root table.
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName(BuildTriggerName(rootTable.Table, ReferentialIdentityToken)),
                    rootTable.Table,
                    DbTriggerKind.ReferentialIdentityMaintenance,
                    [RelationalNameConventions.DocumentIdColumnName],
                    identityProjectionColumns
                )
            );

            // AbstractIdentityMaintenance triggers — one per abstract identity table this
            // resource contributes to (discovered via subclass metadata).
            var isSubclass = ApiSchemaNodeRequirements.TryGetOptionalBoolean(
                resourceContext.ResourceSchema,
                "isSubclass",
                defaultValue: false
            );

            if (isSubclass)
            {
                var superclassProjectName = RequireString(
                    resourceContext.ResourceSchema,
                    "superclassProjectName"
                );
                var superclassResourceName = RequireString(
                    resourceContext.ResourceSchema,
                    "superclassResourceName"
                );
                var superclassResource = new QualifiedResourceName(
                    superclassProjectName,
                    superclassResourceName
                );

                if (abstractTablesByResource.TryGetValue(superclassResource, out var abstractTable))
                {
                    context.TriggerInventory.Add(
                        new DbTriggerInfo(
                            new DbTriggerName(BuildTriggerName(rootTable.Table, AbstractIdentityToken)),
                            rootTable.Table,
                            DbTriggerKind.AbstractIdentityMaintenance,
                            [RelationalNameConventions.DocumentIdColumnName],
                            identityProjectionColumns,
                            MaintenanceTargetTable: abstractTable.TableModel.Table
                        )
                    );
                }
            }

            // IdentityPropagationFallback — MSSQL only: emits triggers for reference FKs that
            // would use ON UPDATE CASCADE on PostgreSQL but must use NO ACTION on SQL Server.
            if (context.Dialect == SqlDialect.Mssql && builderContext.DocumentReferenceMappings.Count > 0)
            {
                CollectPropagationFallbackActions(
                    context,
                    builderContext,
                    resourceModel,
                    resourcesByKey,
                    abstractTablesByResource,
                    resourceContextsByResource,
                    resource,
                    propagationFallbackActionsByTriggerTable
                );
            }
        }

        if (context.Dialect == SqlDialect.Mssql)
        {
            EmitPropagationFallbackTriggers(context, propagationFallbackActionsByTriggerTable);
        }
    }

    /// <summary>
    /// Builds the ordered set of root identity projection columns by resolving
    /// <c>identityJsonPaths</c> to physical column names. For identity-component references, this
    /// projects locally stored identity-part columns (from
    /// <see cref="DocumentReferenceBinding.IdentityBindings"/>) so trigger compare sets detect value
    /// changes even when <c>..._DocumentId</c> remains stable.
    /// </summary>
    private static IReadOnlyList<DbColumnName> BuildRootIdentityProjectionColumns(
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        if (builderContext.IdentityJsonPaths.Count == 0)
        {
            return [];
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
            if (referenceBindingsByIdentityPath.TryGetValue(identityPath.Canonical, out var binding))
            {
                if (binding.Table != rootTable.Table)
                {
                    throw new InvalidOperationException(
                        $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                            + "must bind to the root table when deriving trigger identity projections."
                    );
                }

                if (!binding.IsIdentityComponent)
                {
                    throw new InvalidOperationException(
                        $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                            + "mapped to a non-identity reference binding during trigger derivation."
                    );
                }

                if (binding.IdentityBindings.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                            + "mapped to a reference binding with no identity-part columns."
                    );
                }

                var identityBindingsForPath = binding
                    .IdentityBindings.Where(identityBinding =>
                        identityBinding.ReferenceJsonPath.Canonical == identityPath.Canonical
                    )
                    .ToArray();

                if (identityBindingsForPath.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                            + "did not resolve to a local identity-part column for trigger derivation."
                    );
                }

                foreach (var identityBinding in identityBindingsForPath)
                {
                    AddUniqueColumn(identityBinding.Column, uniqueColumns, seenColumns);
                }

                continue;
            }

            if (!rootColumnsByPath.TryGetValue(identityPath.Canonical, out var columnName))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column during trigger derivation."
                );
            }

            AddUniqueColumn(columnName, uniqueColumns, seenColumns);
        }

        return uniqueColumns.ToArray();
    }

    /// <summary>
    /// Collects <see cref="DbTriggerKind.IdentityPropagationFallback"/> fan-out actions keyed by
    /// referenced table. This replaces PostgreSQL <c>ON UPDATE CASCADE</c> behavior for SQL Server,
    /// which must use <c>NO ACTION</c> due to multiple cascade path restrictions.
    /// </summary>
    private static void CollectPropagationFallbackActions(
        RelationalModelSetBuilderContext context,
        RelationalModelBuilderContext builderContext,
        RelationalResourceModel resourceModel,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IReadOnlyDictionary<QualifiedResourceName, AbstractIdentityTableInfo> abstractTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        QualifiedResourceName resource,
        IDictionary<DbTableName, List<DbIdentityPropagationReferrerAction>> actionsByTriggerTable
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
                continue;
            }

            if (
                !TryResolvePropagationTargetTable(
                    context,
                    mapping.TargetResource,
                    resourcesByKey,
                    abstractTablesByResource,
                    resourceContextsByResource,
                    out var referencedTableModel
                )
            )
            {
                continue;
            }

            var bindingTable = ResolveReferenceBindingTable(binding, resourceModel, resource);
            var referrerIdentityColumnsByReferencePath = binding.IdentityBindings.ToDictionary(
                identityBinding => identityBinding.ReferenceJsonPath.Canonical,
                identityBinding => identityBinding.Column,
                StringComparer.Ordinal
            );
            var referencedColumnsByIdentityPath = BuildColumnNameLookupBySourceJsonPath(
                referencedTableModel,
                mapping.TargetResource
            );
            var referrerColumnsByName = bindingTable.Columns.ToDictionary(
                column => column.ColumnName,
                column => column
            );
            var referencedColumnsByName = referencedTableModel.Columns.ToDictionary(
                column => column.ColumnName,
                column => column
            );
            var referrerPresenceColumns = BuildPresenceColumnSet(bindingTable);
            var referencedPresenceColumns = BuildPresenceColumnSet(referencedTableModel);
            HashSet<(
                DbColumnName ReferrerStorageColumn,
                DbColumnName ReferencedStorageColumn
            )> seenIdentityColumnPairs = [];
            List<DbIdentityPropagationColumnPair> identityColumnPairs = new(mapping.ReferenceJsonPaths.Count);

            foreach (var identityPathBinding in mapping.ReferenceJsonPaths)
            {
                if (
                    !referrerIdentityColumnsByReferencePath.TryGetValue(
                        identityPathBinding.ReferenceJsonPath.Canonical,
                        out var referrerIdentityColumn
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Reference identity path '{identityPathBinding.ReferenceJsonPath.Canonical}' on "
                            + $"resource '{FormatResource(resource)}' did not map to a referrer "
                            + $"identity column during trigger derivation."
                    );
                }

                if (
                    !referencedColumnsByIdentityPath.TryGetValue(
                        identityPathBinding.IdentityJsonPath.Canonical,
                        out var referencedIdentityColumn
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Target identity path '{identityPathBinding.IdentityJsonPath.Canonical}' on "
                            + $"resource '{FormatResource(mapping.TargetResource)}' did not map to a "
                            + $"referenced table column during trigger derivation."
                    );
                }

                var referrerStorageColumn = ResolveStorageColumn(
                    referrerIdentityColumn,
                    referrerColumnsByName,
                    referrerPresenceColumns,
                    bindingTable.Table,
                    resource,
                    mapping,
                    "referrer identity column"
                );
                var referencedStorageColumn = ResolveStorageColumn(
                    referencedIdentityColumn,
                    referencedColumnsByName,
                    referencedPresenceColumns,
                    referencedTableModel.Table,
                    mapping.TargetResource,
                    mapping,
                    "referenced identity column"
                );

                AddIdentityColumnPair(
                    referrerStorageColumn,
                    referencedStorageColumn,
                    identityColumnPairs,
                    seenIdentityColumnPairs
                );
            }

            var referrerAction = new DbIdentityPropagationReferrerAction(
                bindingTable.Table,
                binding.FkColumn,
                RelationalNameConventions.DocumentIdColumnName,
                identityColumnPairs.ToArray()
            );

            if (!actionsByTriggerTable.TryGetValue(referencedTableModel.Table, out var referrerActions))
            {
                referrerActions = [];
                actionsByTriggerTable[referencedTableModel.Table] = referrerActions;
            }

            AddPropagationReferrerAction(referrerActions, referrerAction);
        }
    }

    /// <summary>
    /// Emits one <see cref="DbTriggerKind.IdentityPropagationFallback"/> trigger per referenced table.
    /// </summary>
    private static void EmitPropagationFallbackTriggers(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<DbTableName, List<DbIdentityPropagationReferrerAction>> actionsByTriggerTable
    )
    {
        foreach (
            var (triggerTable, referrerActions) in actionsByTriggerTable
                .OrderBy(entry => entry.Key.Schema.Value, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key.Name, StringComparer.Ordinal)
        )
        {
            var orderedReferrerActions = referrerActions
                .OrderBy(action => action.ReferrerTable.Schema.Value, StringComparer.Ordinal)
                .ThenBy(action => action.ReferrerTable.Name, StringComparer.Ordinal)
                .ThenBy(action => action.ReferrerDocumentIdColumn.Value, StringComparer.Ordinal)
                .ThenBy(
                    action => BuildIdentityColumnPairSignature(action.IdentityColumnPairs),
                    StringComparer.Ordinal
                )
                .ToArray();

            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName(BuildTriggerName(triggerTable, PropagationFallbackPrefix)),
                    triggerTable,
                    DbTriggerKind.IdentityPropagationFallback,
                    [],
                    [],
                    PropagationFallback: new DbIdentityPropagationFallbackInfo(orderedReferrerActions)
                )
            );
        }
    }

    /// <summary>
    /// Resolves the propagation trigger target table for a reference mapping.
    /// </summary>
    private static bool TryResolvePropagationTargetTable(
        RelationalModelSetBuilderContext context,
        QualifiedResourceName targetResource,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IReadOnlyDictionary<QualifiedResourceName, AbstractIdentityTableInfo> abstractTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        out DbTableModel referencedTableModel
    )
    {
        if (abstractTablesByResource.TryGetValue(targetResource, out var abstractTable))
        {
            referencedTableModel = abstractTable.TableModel;
            return true;
        }

        if (!resourceContextsByResource.TryGetValue(targetResource, out var targetResourceContext))
        {
            referencedTableModel = default!;
            return false;
        }

        var targetBuilderContext = context.GetOrCreateResourceBuilderContext(targetResourceContext);

        if (!targetBuilderContext.AllowIdentityUpdates)
        {
            referencedTableModel = default!;
            return false;
        }

        if (!resourcesByKey.TryGetValue(targetResource, out var targetEntry))
        {
            throw new InvalidOperationException(
                $"Reference target resource '{FormatResource(targetResource)}' was not found "
                    + "for trigger propagation derivation."
            );
        }

        referencedTableModel = targetEntry.Model.RelationalModel.Root;
        return true;
    }

    /// <summary>
    /// Resolves the concrete table model for a reference binding, selecting the best matching JSON scope
    /// when a table name appears in multiple scopes.
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

        return ReferenceObjectPathScopeResolver.ResolveDeepestMatchingScope(
            binding.ReferenceObjectPath,
            candidates,
            static table => table.JsonScope,
            candidateScopes =>
            {
                var scopeList = string.Join(", ", candidateScopes);

                return new InvalidOperationException(
                    $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' did not match any table scope for '{binding.Table}'. "
                        + $"Candidates: {scopeList}."
                );
            },
            candidateScopes => new InvalidOperationException(
                $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' matched multiple table scopes with the same depth "
                    + $"for '{binding.Table}': "
                    + $"{string.Join(", ", candidateScopes.Select(scope => $"'{scope}'"))}."
            ),
            () =>
                new InvalidOperationException(
                    $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' requires an extension table scope, but none was found."
                )
        );
    }

    /// <summary>
    /// Resolves a column to its canonical stored column using storage metadata.
    /// </summary>
    private static DbColumnName ResolveStorageColumn(
        DbColumnName column,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnsByName,
        IReadOnlySet<DbColumnName> presenceColumns,
        DbTableName table,
        QualifiedResourceName resource,
        DocumentReferenceMapping mapping,
        string role
    )
    {
        if (!columnsByName.TryGetValue(column, out var columnModel))
        {
            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + $"resolved {role} '{column.Value}' that does not exist on table '{table}'."
            );
        }

        if (presenceColumns.Contains(column))
        {
            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + $"resolved {role} '{column.Value}' to a synthetic presence column on table '{table}'."
            );
        }

        switch (columnModel.Storage)
        {
            case ColumnStorage.Stored:
                return column;
            case ColumnStorage.UnifiedAlias unifiedAlias:
                if (!columnsByName.TryGetValue(unifiedAlias.CanonicalColumn, out var canonicalColumn))
                {
                    throw new InvalidOperationException(
                        $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                            + $"resolved {role} '{column.Value}' to missing canonical storage column "
                            + $"'{unifiedAlias.CanonicalColumn.Value}' on table '{table}'."
                    );
                }

                if (presenceColumns.Contains(unifiedAlias.CanonicalColumn))
                {
                    throw new InvalidOperationException(
                        $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                            + $"resolved {role} '{column.Value}' to synthetic presence column "
                            + $"'{unifiedAlias.CanonicalColumn.Value}' on table '{table}'."
                    );
                }

                if (canonicalColumn.Storage is not ColumnStorage.Stored)
                {
                    throw new InvalidOperationException(
                        $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                            + $"resolved {role} '{column.Value}' to non-stored canonical column "
                            + $"'{unifiedAlias.CanonicalColumn.Value}' on table '{table}'."
                    );
                }

                return unifiedAlias.CanonicalColumn;
            default:
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"resolved {role} '{column.Value}' to unsupported storage metadata "
                        + $"'{columnModel.Storage.GetType().Name}' on table '{table}'."
                );
        }
    }

    /// <summary>
    /// Builds the set of synthetic presence columns referenced by unified aliases on a table.
    /// </summary>
    private static IReadOnlySet<DbColumnName> BuildPresenceColumnSet(DbTableModel table)
    {
        HashSet<DbColumnName> presenceColumns = [];

        foreach (var column in table.Columns)
        {
            if (column.Storage is ColumnStorage.UnifiedAlias { PresenceColumn: { } presenceColumn })
            {
                presenceColumns.Add(presenceColumn);
            }
        }

        return presenceColumns;
    }

    /// <summary>
    /// Adds an identity column pair once, preserving first-seen ordering.
    /// </summary>
    private static void AddIdentityColumnPair(
        DbColumnName referrerStorageColumn,
        DbColumnName referencedStorageColumn,
        ICollection<DbIdentityPropagationColumnPair> pairs,
        ISet<(DbColumnName ReferrerStorageColumn, DbColumnName ReferencedStorageColumn)> seenPairs
    )
    {
        var key = (referrerStorageColumn, referencedStorageColumn);

        if (!seenPairs.Add(key))
        {
            return;
        }

        pairs.Add(new DbIdentityPropagationColumnPair(referrerStorageColumn, referencedStorageColumn));
    }

    /// <summary>
    /// Adds a propagation action exactly once using semantic equality on referrer + identity mappings.
    /// </summary>
    private static void AddPropagationReferrerAction(
        ICollection<DbIdentityPropagationReferrerAction> referrerActions,
        DbIdentityPropagationReferrerAction candidate
    )
    {
        if (
            referrerActions.Any(existing =>
                existing.ReferrerTable.Equals(candidate.ReferrerTable)
                && existing.ReferrerDocumentIdColumn.Equals(candidate.ReferrerDocumentIdColumn)
                && existing.ReferencedDocumentIdColumn.Equals(candidate.ReferencedDocumentIdColumn)
                && existing.IdentityColumnPairs.SequenceEqual(candidate.IdentityColumnPairs)
            )
        )
        {
            return;
        }

        referrerActions.Add(candidate);
    }

    /// <summary>
    /// Builds a deterministic signature for an ordered identity-column-pair sequence.
    /// </summary>
    private static string BuildIdentityColumnPairSignature(
        IReadOnlyList<DbIdentityPropagationColumnPair> identityColumnPairs
    )
    {
        return string.Join(
            "|",
            identityColumnPairs.Select(pair =>
                $"{pair.ReferrerStorageColumn.Value}>{pair.ReferencedStorageColumn.Value}"
            )
        );
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
    /// Builds a trigger name following the <c>TR_{TableName}_{Purpose}</c> convention.
    /// </summary>
    private static string BuildTriggerName(DbTableName table, string purposeToken)
    {
        return $"TR_{table.Name}_{purposeToken}";
    }
}
