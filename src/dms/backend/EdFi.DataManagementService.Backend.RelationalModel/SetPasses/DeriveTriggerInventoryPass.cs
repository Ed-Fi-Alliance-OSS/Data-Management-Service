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
/// <remarks>
/// <para>
/// <b>MSSQL trigger ordering:</b> Multiple AFTER triggers may be emitted for the same table
/// (e.g., DocumentStamping, AbstractIdentityMaintenance, ReferentialIdentityMaintenance). SQL Server
/// does not guarantee a deterministic firing order for multiple AFTER triggers unless
/// <c>sp_settriggerorder</c> is used. The current triggers are designed to be order-independent:
/// each writes to a different target table and has no dependency on another trigger's side effects.
/// If a future trigger introduces such a dependency, explicit ordering via <c>sp_settriggerorder</c>
/// must be emitted.
/// </para>
/// </remarks>
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

        var resourcesByKey = context.ConcreteResourcesInNameOrder.ToDictionary(model =>
            model.ResourceKey.Resource
        );

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

            if (!resourcesByKey.TryGetValue(resource, out var concreteModel))
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found for trigger derivation."
                );
            }

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
                        keyColumns,
                        isRootTable ? identityProjectionColumns : [],
                        new TriggerKindParameters.DocumentStamping()
                    )
                );
            }

            // Build identity element mappings for UUIDv5 computation.
            var identityElements = BuildIdentityElementMappings(resourceModel, builderContext, resource);

            // Resolve the resource key entry for referential identity metadata.
            var resourceKeyEntry = context.GetResourceKeyEntry(resource);

            // AbstractIdentityMaintenance triggers — one per abstract identity table this
            // resource contributes to (discovered via subclass metadata).
            var isSubclass = ApiSchemaNodeRequirements.TryGetOptionalBoolean(
                resourceContext.ResourceSchema,
                "isSubclass",
                defaultValue: false
            );

            SuperclassAliasInfo? superclassAlias = null;

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

                var superclassResourceKeyEntry = context.GetResourceKeyEntry(superclassResource);

                // Build superclass identity element mappings, handling superclassIdentityJsonPath remapping.
                var superclassIdentityJsonPath = TryGetOptionalString(
                    resourceContext.ResourceSchema,
                    "superclassIdentityJsonPath"
                );

                IReadOnlyList<IdentityElementMapping> superclassIdentityElements;
                if (superclassIdentityJsonPath is not null)
                {
                    // When superclassIdentityJsonPath is set, the subclass has exactly one identity path
                    // that maps to the superclass's single identity path.
                    if (identityElements.Count != 1)
                    {
                        throw new InvalidOperationException(
                            $"Subclass resource '{FormatResource(resource)}' with superclassIdentityJsonPath "
                                + $"must have exactly one identity element, but found {identityElements.Count}."
                        );
                    }

                    superclassIdentityElements =
                    [
                        new IdentityElementMapping(identityElements[0].Column, superclassIdentityJsonPath),
                    ];
                }
                else
                {
                    // Same identity paths — reuse the concrete identity elements.
                    superclassIdentityElements = identityElements;
                }

                superclassAlias = new SuperclassAliasInfo(
                    superclassResourceKeyEntry.ResourceKeyId,
                    superclassProjectName,
                    superclassResourceName,
                    superclassIdentityElements
                );

                if (abstractTablesByResource.TryGetValue(superclassResource, out var abstractTable))
                {
                    var targetColumnMappings = BuildAbstractIdentityColumnMappings(
                        abstractTable.TableModel,
                        resourceModel,
                        builderContext,
                        resource,
                        superclassIdentityJsonPath
                    );

                    context.TriggerInventory.Add(
                        new DbTriggerInfo(
                            new DbTriggerName(BuildTriggerName(rootTable.Table, AbstractIdentityToken)),
                            rootTable.Table,
                            [RelationalNameConventions.DocumentIdColumnName],
                            identityProjectionColumns,
                            new TriggerKindParameters.AbstractIdentityMaintenance(
                                abstractTable.TableModel.Table,
                                targetColumnMappings,
                                $"{resource.ProjectName}:{resource.ResourceName}"
                            )
                        )
                    );
                }
            }

            // ReferentialIdentityMaintenance trigger on the root table.
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName(BuildTriggerName(rootTable.Table, ReferentialIdentityToken)),
                    rootTable.Table,
                    [RelationalNameConventions.DocumentIdColumnName],
                    identityProjectionColumns,
                    new TriggerKindParameters.ReferentialIdentityMaintenance(
                        resourceKeyEntry.ResourceKeyId,
                        resource.ProjectName,
                        resource.ResourceName,
                        identityElements,
                        superclassAlias
                    )
                )
            );
        }

        // IdentityPropagationFallback — MSSQL only: emits triggers on referenced entities to propagate
        // identity updates to all referrers. This replaces ON UPDATE CASCADE which SQL Server rejects
        // due to multiple cascade paths.
        if (context.Dialect == SqlDialect.Mssql)
        {
            EmitPropagationFallbackTriggers(
                context,
                abstractTablesByResource,
                resourceContextsByResource,
                resourcesByKey
            );
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
    /// Helper record for reverse reference index entries.
    /// </summary>
    private sealed record ReverseReferenceEntry(
        QualifiedResourceName ReferrerResource,
        DbTableModel ReferrerRootTable,
        DocumentReferenceBinding Binding,
        DocumentReferenceMapping Mapping
    );

    /// <summary>
    /// Emits <see cref="TriggerKindParameters.IdentityPropagationFallback"/> triggers on referenced
    /// entities to propagate identity updates to all referrers. These replace the <c>ON UPDATE CASCADE</c>
    /// that PostgreSQL handles natively but SQL Server rejects due to multiple cascade paths.
    /// </summary>
    private static void EmitPropagationFallbackTriggers(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, AbstractIdentityTableInfo> abstractTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> concreteResourcesByName
    )
    {
        // Build reverse reference index: for each target resource, collect all referrer entries.
        var reverseIndex = BuildReverseReferenceIndex(
            context,
            resourceContextsByResource,
            concreteResourcesByName
        );

        // For each target resource in the reverse index, emit a single trigger with all referrers.
        foreach (var (targetResource, referrerEntries) in reverseIndex)
        {
            // Determine trigger table: abstract identity table OR concrete root table.
            DbTableName triggerTable;
            IReadOnlyList<DbColumnName> identityProjectionColumns;

            if (abstractTablesByResource.TryGetValue(targetResource, out var abstractTableInfo))
            {
                triggerTable = abstractTableInfo.TableModel.Table;

                // Identity projection columns are all columns with SourceJsonPath (identity columns).
                identityProjectionColumns = abstractTableInfo
                    .TableModel.Columns.Where(c => c.SourceJsonPath is not null)
                    .Select(c => c.ColumnName)
                    .ToArray();
            }
            else if (concreteResourcesByName.TryGetValue(targetResource, out var concreteModel))
            {
                // Concrete target must allow identity updates.
                if (
                    !resourceContextsByResource.TryGetValue(targetResource, out var targetContext)
                    || !context.GetOrCreateResourceBuilderContext(targetContext).AllowIdentityUpdates
                )
                {
                    continue;
                }

                triggerTable = concreteModel.RelationalModel.Root.Table;

                // Identity projection columns from the root table.
                identityProjectionColumns = concreteModel
                    .RelationalModel.Root.Columns.Where(c => c.SourceJsonPath is not null)
                    .Select(c => c.ColumnName)
                    .ToArray();
            }
            else
            {
                continue;
            }

            // Build referrer updates by mapping source identity columns to referrer stored columns.
            var referrerUpdates = new List<PropagationReferrerTarget>();

            foreach (var entry in referrerEntries)
            {
                var columnMappings = BuildPropagationColumnMappings(
                    entry.Binding,
                    entry.Mapping,
                    entry.ReferrerResource
                );

                referrerUpdates.Add(
                    new PropagationReferrerTarget(
                        entry.ReferrerRootTable.Table,
                        entry.Binding.FkColumn,
                        columnMappings
                    )
                );
            }

            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName(BuildTriggerName(triggerTable, PropagationFallbackPrefix)),
                    triggerTable,
                    [RelationalNameConventions.DocumentIdColumnName],
                    identityProjectionColumns,
                    new TriggerKindParameters.IdentityPropagationFallback(referrerUpdates)
                )
            );
        }
    }

    /// <summary>
    /// Builds a reverse reference index mapping target resources to their referrer entries.
    /// </summary>
    private static Dictionary<QualifiedResourceName, List<ReverseReferenceEntry>> BuildReverseReferenceIndex(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> concreteResourcesByName
    )
    {
        var reverseIndex = new Dictionary<QualifiedResourceName, List<ReverseReferenceEntry>>();

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (IsResourceExtension(resourceContext))
            {
                continue;
            }

            var referrerResource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );

            if (!concreteResourcesByName.TryGetValue(referrerResource, out var referrerModel))
            {
                continue;
            }

            if (referrerModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            var referrerBuilderContext = context.GetOrCreateResourceBuilderContext(resourceContext);
            var referrerRootTable = referrerModel.RelationalModel.Root;

            var bindingByReferencePath = referrerModel.RelationalModel.DocumentReferenceBindings.ToDictionary(
                binding => binding.ReferenceObjectPath.Canonical,
                StringComparer.Ordinal
            );

            foreach (var mapping in referrerBuilderContext.DocumentReferenceMappings)
            {
                if (
                    !bindingByReferencePath.TryGetValue(
                        mapping.ReferenceObjectPath.Canonical,
                        out var binding
                    )
                )
                {
                    continue;
                }

                // Only consider root-table bindings (references from the root table).
                if (!binding.Table.Equals(referrerRootTable.Table))
                {
                    continue;
                }

                var targetResource = mapping.TargetResource;

                if (!reverseIndex.TryGetValue(targetResource, out var entries))
                {
                    entries = [];
                    reverseIndex[targetResource] = entries;
                }

                entries.Add(new ReverseReferenceEntry(referrerResource, referrerRootTable, binding, mapping));
            }
        }

        return reverseIndex;
    }

    /// <summary>
    /// Builds column mappings for identity propagation: source identity columns → referrer stored columns.
    /// The direction is now source (trigger table) → referrer (what we update).
    /// </summary>
    private static IReadOnlyList<TriggerColumnMapping> BuildPropagationColumnMappings(
        DocumentReferenceBinding binding,
        DocumentReferenceMapping mapping,
        QualifiedResourceName referrerResource
    )
    {
        // Build lookup: reference JSON path → identity JSON path.
        var identityPathByReferencePath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in mapping.ReferenceJsonPaths)
        {
            identityPathByReferencePath[entry.ReferenceJsonPath.Canonical] = entry.IdentityJsonPath.Canonical;
        }

        // For each identity binding, map source identity column to referrer stored column.
        // Direction: SourceColumn = target identity column, TargetColumn = referrer stored column.
        return binding
            .IdentityBindings.Select(ib =>
            {
                if (
                    !identityPathByReferencePath.TryGetValue(
                        ib.ReferenceJsonPath.Canonical,
                        out var identityPath
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Propagation fallback trigger derivation for referrer '{FormatResource(referrerResource)}': "
                            + $"reference JSON path '{ib.ReferenceJsonPath.Canonical}' did not map to "
                            + "a target identity path."
                    );
                }

                // Extract column name from identity path: $.schoolId → SchoolId
                var sourceColumnName = ExtractColumnNameFromIdentityPath(identityPath);
                var sourceColumn = new DbColumnName(sourceColumnName);

                // Target column is the referrer's stored identity column (e.g., School_SchoolId).
                var targetColumn = ib.Column;

                return new TriggerColumnMapping(sourceColumn, targetColumn);
            })
            .ToArray();
    }

    /// <summary>
    /// Extracts a column name from an identity JSON path by converting the last path segment to PascalCase.
    /// </summary>
    private static string ExtractColumnNameFromIdentityPath(string identityPath)
    {
        // Identity path format: $.fieldName or $.path.to.fieldName
        var lastDotIndex = identityPath.LastIndexOf('.');
        var fieldName = lastDotIndex >= 0 ? identityPath[(lastDotIndex + 1)..] : identityPath;

        // Convert first char to uppercase (camelCase → PascalCase).
        if (fieldName.Length > 0 && char.IsLower(fieldName[0]))
        {
            return char.ToUpperInvariant(fieldName[0]) + fieldName[1..];
        }

        return fieldName;
    }

    /// <summary>
    /// Builds identity element mappings for UUIDv5 computation by pairing each identity projection
    /// column with its canonical JSON path.
    /// </summary>
    private static IReadOnlyList<IdentityElementMapping> BuildIdentityElementMappings(
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
        List<IdentityElementMapping> mappings = new(builderContext.IdentityJsonPaths.Count);

        foreach (var identityPath in builderContext.IdentityJsonPaths)
        {
            DbColumnName column;

            if (referenceBindingsByIdentityPath.TryGetValue(identityPath.Canonical, out var binding))
            {
                column = binding.FkColumn;
            }
            else if (rootColumnsByPath.TryGetValue(identityPath.Canonical, out var columnName))
            {
                column = columnName;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column during identity element mapping."
                );
            }

            if (seenColumns.Add(column.Value))
            {
                mappings.Add(new IdentityElementMapping(column, identityPath.Canonical));
            }
        }

        return mappings.ToArray();
    }

    /// <summary>
    /// Builds column mappings from concrete root table columns to abstract identity table columns
    /// for <see cref="TriggerKindParameters.AbstractIdentityMaintenance"/> triggers.
    /// </summary>
    private static IReadOnlyList<TriggerColumnMapping> BuildAbstractIdentityColumnMappings(
        DbTableModel abstractTable,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource,
        string? superclassIdentityJsonPath
    )
    {
        var rootTable = resourceModel.Root;
        var rootColumnsByPath = BuildColumnNameLookupBySourceJsonPath(rootTable, resource);
        var referenceBindingsByIdentityPath = BuildReferenceIdentityBindings(
            resourceModel.DocumentReferenceBindings,
            resource
        );

        List<TriggerColumnMapping> mappings = [];

        // Iterate over abstract identity table columns that have a source JSON path
        // (these are the identity columns, excluding DocumentId and Discriminator).
        foreach (var abstractColumn in abstractTable.Columns)
        {
            if (abstractColumn.SourceJsonPath is null)
            {
                continue;
            }

            var abstractPath = abstractColumn.SourceJsonPath.Value.Canonical;

            // Determine which concrete identity path maps to this abstract path.
            string concretePath;
            if (
                superclassIdentityJsonPath is not null
                && string.Equals(abstractPath, superclassIdentityJsonPath, StringComparison.Ordinal)
            )
            {
                // When superclassIdentityJsonPath is set, the first concrete identity path
                // maps to the superclass identity path.
                if (builderContext.IdentityJsonPaths.Count == 0)
                    throw new InvalidOperationException(
                        $"Resource '{FormatResource(resource)}' has superclassIdentityJsonPath set but no identity JSON paths."
                    );

                concretePath = builderContext.IdentityJsonPaths[0].Canonical;
            }
            else
            {
                // Normal case: the concrete path is the same as the abstract path.
                concretePath = abstractPath;
            }

            // Resolve the concrete source column.
            DbColumnName sourceColumn;
            if (referenceBindingsByIdentityPath.TryGetValue(concretePath, out var binding))
            {
                sourceColumn = binding.FkColumn;
            }
            else if (rootColumnsByPath.TryGetValue(concretePath, out var columnName))
            {
                sourceColumn = columnName;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Abstract identity path '{abstractPath}' (concrete path '{concretePath}') "
                        + $"on resource '{FormatResource(resource)}' did not map to a root table column "
                        + "during abstract identity column mapping."
                );
            }

            mappings.Add(new TriggerColumnMapping(sourceColumn, abstractColumn.ColumnName));
        }

        return mappings.ToArray();
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
