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

    /// <summary>
    /// Populates <see cref="RelationalModelSetBuilderContext.TriggerInventory"/> with
    /// <c>DocumentStamping</c>, <c>ReferentialIdentityMaintenance</c>, and
    /// <c>AbstractIdentityMaintenance</c> triggers. <c>IdentityPropagationFallback</c> is
    /// stubbed as empty (full cascade-path analysis deferred).
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

            // Resolve identity projection columns for the root table.
            var identityProjectionColumns = BuildRootIdentityProjectionColumns(
                resourceModel,
                context.GetOrCreateResourceBuilderContext(resourceContext),
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
                            + $"but none was found on table '{table.Table.Name}'."
                    );
                }

                var keyColumns = new[] { documentIdKeyColumn.ColumnName };
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
                            abstractTable.TableModel.Table
                        )
                    );
                }
            }

            // IdentityPropagationFallback — stubbed as empty (TODO: full cascade-path analysis).
        }
    }

    /// <summary>
    /// Builds the ordered set of root identity projection columns by resolving
    /// <c>identityJsonPaths</c> to physical column names, using the same logic as
    /// <see cref="RootIdentityConstraintPass"/>.
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
                AddUniqueColumn(binding.FkColumn, uniqueColumns, seenColumns);
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
    /// Builds a trigger name following the <c>TR_{TableName}_{Purpose}</c> convention.
    /// </summary>
    private static string BuildTriggerName(DbTableName table, string purposeToken)
    {
        return $"TR_{table.Name}_{purposeToken}";
    }
}
