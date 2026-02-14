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
    private const string PropagationFallbackPrefix = "Propagation";

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
                EmitPropagationFallbackTriggers(
                    context,
                    builderContext,
                    resourceModel,
                    rootTable,
                    abstractTablesByResource,
                    resourceContextsByResource,
                    resource
                );
            }
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

                foreach (var identityBinding in binding.IdentityBindings)
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
    /// Emits <see cref="DbTriggerKind.IdentityPropagationFallback"/> triggers for each root-table
    /// reference binding whose target is abstract or allows identity updates. These replace the
    /// <c>ON UPDATE CASCADE</c> that PostgreSQL handles natively but SQL Server rejects due to
    /// multiple cascade paths.
    /// </summary>
    private static void EmitPropagationFallbackTriggers(
        RelationalModelSetBuilderContext context,
        RelationalModelBuilderContext builderContext,
        RelationalResourceModel resourceModel,
        DbTableModel rootTable,
        IReadOnlyDictionary<QualifiedResourceName, AbstractIdentityTableInfo> abstractTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
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
                continue;
            }

            // Only consider root-table bindings.
            if (!binding.Table.Equals(rootTable.Table))
            {
                continue;
            }

            DbTableModel? referencedTableModel;

            if (abstractTablesByResource.TryGetValue(mapping.TargetResource, out var abstractTableInfo))
            {
                referencedTableModel = abstractTableInfo.TableModel;
            }
            else if (
                resourceContextsByResource.TryGetValue(mapping.TargetResource, out var targetResourceContext)
            )
            {
                var targetBuilderContext = context.GetOrCreateResourceBuilderContext(targetResourceContext);

                if (!targetBuilderContext.AllowIdentityUpdates)
                {
                    continue;
                }

                var targetEntry = context.ConcreteResourcesInNameOrder.FirstOrDefault(model =>
                    model.ResourceKey.Resource == mapping.TargetResource
                );

                if (targetEntry is null)
                {
                    continue;
                }

                referencedTableModel = targetEntry.RelationalModel.Root;
            }
            else
            {
                continue;
            }

            var referenceBaseName = ResolveReferenceBaseName(binding.FkColumn);
            var referrerIdentityColumnsByReferencePath = binding.IdentityBindings.ToDictionary(
                identityBinding => identityBinding.ReferenceJsonPath.Canonical,
                identityBinding => identityBinding.Column,
                StringComparer.Ordinal
            );
            var referencedColumnsByIdentityPath = BuildColumnNameLookupBySourceJsonPath(
                referencedTableModel,
                mapping.TargetResource
            );
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
                            + $"resource '{FormatResource(resource)}' did not map to a stored referrer "
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

                identityColumnPairs.Add(
                    new DbIdentityPropagationColumnPair(referrerIdentityColumn, referencedIdentityColumn)
                );
            }

            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName(
                        BuildTriggerName(rootTable.Table, $"{PropagationFallbackPrefix}_{referenceBaseName}")
                    ),
                    rootTable.Table,
                    DbTriggerKind.IdentityPropagationFallback,
                    [],
                    [],
                    PropagationFallback: new DbIdentityPropagationFallbackInfo([
                        new DbIdentityPropagationReferrerAction(
                            rootTable.Table,
                            binding.FkColumn,
                            RelationalNameConventions.DocumentIdColumnName,
                            identityColumnPairs.ToArray()
                        ),
                    ])
                )
            );
        }
    }

    /// <summary>
    /// Resolves the base reference name from a reference FK column by trimming the <c>_DocumentId</c> suffix.
    /// </summary>
    private static string ResolveReferenceBaseName(DbColumnName fkColumn)
    {
        const string DocumentIdSuffix = "_DocumentId";

        return fkColumn.Value.EndsWith(DocumentIdSuffix, StringComparison.Ordinal)
            ? fkColumn.Value[..^DocumentIdSuffix.Length]
            : fkColumn.Value;
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
