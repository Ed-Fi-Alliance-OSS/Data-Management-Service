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

            // Every concrete resource must have at least one identity element for referential identity computation.
            // Empty identity elements would produce a degenerate UUIDv5 hash with no identity data.
            if (identityElements.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Resource '{FormatResource(resource)}' requires at least one identity element "
                        + "for referential identity computation, but none were derived."
                );
            }

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
                    // This invariant is validated here at trigger derivation time rather than during schema
                    // loading because superclassIdentityJsonPath is resolved from the ApiSchema JSON and the
                    // identity element count is derived from the relational model building process.
                    if (identityElements.Count != 1)
                    {
                        throw new InvalidOperationException(
                            $"Subclass resource '{FormatResource(resource)}' with superclassIdentityJsonPath "
                                + $"must have exactly one identity element, but found {identityElements.Count}."
                        );
                    }

                    superclassIdentityElements =
                    [
                        new IdentityElementMapping(
                            identityElements[0].Column,
                            superclassIdentityJsonPath,
                            identityElements[0].ScalarType
                        ),
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
    /// Resolves a reference-bearing identity path to its locally stored identity-part columns
    /// (from <see cref="DocumentReferenceBinding.IdentityBindings"/>). Returns <c>null</c> if the
    /// path does not match a reference binding, allowing the caller to fall through to direct
    /// column lookup.
    /// </summary>
    private static IReadOnlyList<DbColumnName>? ResolveReferenceIdentityPartColumns(
        string canonicalPath,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        DbTableName rootTable,
        QualifiedResourceName resource
    )
    {
        if (!referenceBindingsByIdentityPath.TryGetValue(canonicalPath, out var binding))
        {
            return null;
        }

        if (binding.Table != rootTable)
        {
            throw new InvalidOperationException(
                $"Identity path '{canonicalPath}' on resource '{FormatResource(resource)}' "
                    + "must bind to the root table when resolving reference identity-part columns."
            );
        }

        if (!binding.IsIdentityComponent)
        {
            throw new InvalidOperationException(
                $"Identity path '{canonicalPath}' on resource '{FormatResource(resource)}' "
                    + "mapped to a non-identity reference binding."
            );
        }

        if (binding.IdentityBindings.Count == 0)
        {
            throw new InvalidOperationException(
                $"Identity path '{canonicalPath}' on resource '{FormatResource(resource)}' "
                    + "mapped to a reference binding with no identity-part columns."
            );
        }

        var identityBindingsForPath = binding
            .IdentityBindings.Where(ib => ib.ReferenceJsonPath.Canonical == canonicalPath)
            .ToArray();

        if (identityBindingsForPath.Length == 0)
        {
            throw new InvalidOperationException(
                $"Identity path '{canonicalPath}' on resource '{FormatResource(resource)}' "
                    + "did not resolve to a local identity-part column."
            );
        }

        return identityBindingsForPath.Select(ib => ib.Column).ToArray();
    }

    /// <summary>
    /// Builds the ordered set of root identity projection columns for MSSQL <c>UPDATE()</c> guards
    /// and PostgreSQL <c>IS DISTINCT FROM</c> comparisons. These columns must be physically stored
    /// (writable) columns because SQL Server rejects <c>UPDATE(computedCol)</c> at trigger creation
    /// time (Msg 2114). Under key unification, identity binding columns may be persisted computed
    /// aliases; this method resolves each to its canonical storage column and de-duplicates.
    /// </summary>
    private static IReadOnlyList<DbColumnName> BuildRootIdentityProjectionColumns(
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        var identityElements = BuildIdentityElementMappings(resourceModel, builderContext, resource);

        if (identityElements.Count == 0)
        {
            return [];
        }

        var rootTable = resourceModel.Root;

        // Resolve each identity element column to its canonical stored column.
        // Under key unification, binding columns may be unified aliases (computed);
        // UPDATE() guards and IS DISTINCT FROM comparisons must reference stored columns.
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<DbColumnName> storedColumns = new(identityElements.Count);

        foreach (var element in identityElements)
        {
            var resolved = ResolveToStoredColumn(element.Column, rootTable, resource);

            if (seen.Add(resolved.Value))
            {
                storedColumns.Add(resolved);
            }
        }

        return storedColumns.ToArray();
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
            // Determine trigger table model: abstract identity table OR concrete root table.
            // The table model is needed both for the trigger DDL and for resolving source
            // column names from SourceJsonPath mappings in BuildPropagationColumnMappings.
            DbTableModel triggerTableModel;
            IReadOnlyList<DbColumnName> identityProjectionColumns;

            if (abstractTablesByResource.TryGetValue(targetResource, out var abstractTableInfo))
            {
                triggerTableModel = abstractTableInfo.TableModel;

                // Identity projection columns are all columns with SourceJsonPath (identity columns).
                identityProjectionColumns = triggerTableModel
                    .Columns.Where(c => c.SourceJsonPath is not null)
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

                triggerTableModel = concreteModel.RelationalModel.Root;

                // Identity projection columns from the root table, resolved to stored columns.
                // Under key unification, some identity columns may be unified aliases (computed);
                // SQL Server rejects UPDATE() on computed columns (Msg 2114).
                identityProjectionColumns = ResolveColumnsToStored(
                    triggerTableModel
                        .Columns.Where(c => c.SourceJsonPath is not null)
                        .Select(c => c.ColumnName),
                    triggerTableModel,
                    targetResource
                );
            }
            else
            {
                continue;
            }

            var triggerTable = triggerTableModel.Table;

            // Build referrer updates by mapping source identity columns to referrer stored columns.
            var referrerUpdates = new List<PropagationReferrerTarget>();

            foreach (var entry in referrerEntries)
            {
                var columnMappings = BuildPropagationColumnMappings(
                    entry.Binding,
                    entry.Mapping,
                    entry.ReferrerResource,
                    entry.ReferrerRootTable,
                    triggerTableModel,
                    targetResource
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
    /// Builds column mappings for identity propagation: source identity columns on the trigger
    /// table to referrer stored columns. Resolves source columns from the trigger table model's
    /// <see cref="DbColumnModel.SourceJsonPath"/> mapping instead of guessing from JSON path
    /// segments, matching the approach used by <see cref="BuildIdentityElementMappings"/>.
    /// </summary>
    /// <remarks>
    /// Under key unification, <c>ib.Column</c> from reference identity bindings may be a persisted
    /// computed alias. SQL Server rejects <c>SET r.[computedCol] = ...</c> (Msg 271), so each
    /// target column must be resolved to its canonical storage column via the referrer table model.
    /// </remarks>
    private static IReadOnlyList<TriggerColumnMapping> BuildPropagationColumnMappings(
        DocumentReferenceBinding binding,
        DocumentReferenceMapping mapping,
        QualifiedResourceName referrerResource,
        DbTableModel referrerRootTable,
        DbTableModel triggerTable,
        QualifiedResourceName targetResource
    )
    {
        // Build lookup: identity JSON path → source column on the trigger table.
        var sourceColumnsByPath = BuildColumnNameLookupBySourceJsonPath(triggerTable, targetResource);

        // Build lookup: reference JSON path → identity JSON path.
        var identityPathByReferencePath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in mapping.ReferenceJsonPaths)
        {
            identityPathByReferencePath[entry.ReferenceJsonPath.Canonical] = entry.IdentityJsonPath.Canonical;
        }

        // For each identity binding, map source identity column to referrer stored column.
        // Direction: SourceColumn = trigger table identity column, TargetColumn = referrer stored column.
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

                // Resolve source column from the trigger table model's SourceJsonPath mapping.
                if (!sourceColumnsByPath.TryGetValue(identityPath, out var sourceColumn))
                {
                    throw new InvalidOperationException(
                        $"Propagation fallback trigger derivation for target '{FormatResource(targetResource)}': "
                            + $"identity path '{identityPath}' did not map to a column on trigger table "
                            + $"'{triggerTable.Table.Schema.Value}.{triggerTable.Table.Name}'."
                    );
                }

                // Resolve through storage: ib.Column may be a unified alias (computed) after
                // key unification. The propagation UPDATE must target the canonical stored column.
                var targetColumn = ResolveToStoredColumn(ib.Column, referrerRootTable, referrerResource);

                return new TriggerColumnMapping(sourceColumn, targetColumn);
            })
            .ToArray();
    }

    /// <summary>
    /// Builds identity element mappings for UUIDv5 computation by pairing each identity projection
    /// column with its canonical JSON path. For identity-component references, this resolves to
    /// locally stored identity-part columns (not the FK <c>..._DocumentId</c>) so the computed
    /// UUIDv5 hash matches Core's <c>ReferentialIdCalculator</c>.
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

        // Build a column-name-to-scalar-type lookup for type-aware identity hash formatting.
        var columnScalarTypes = rootTable
            .Columns.Where(c => c.ScalarType is not null)
            .ToDictionary(c => c.ColumnName.Value, c => c.ScalarType!, StringComparer.Ordinal);

        HashSet<string> seenColumns = new(StringComparer.Ordinal);
        List<IdentityElementMapping> mappings = new(builderContext.IdentityJsonPaths.Count);

        foreach (var identityPath in builderContext.IdentityJsonPaths)
        {
            var identityPartColumns = ResolveReferenceIdentityPartColumns(
                identityPath.Canonical,
                referenceBindingsByIdentityPath,
                rootTable.Table,
                resource
            );

            if (identityPartColumns is not null)
            {
                foreach (var col in identityPartColumns)
                {
                    if (seenColumns.Add(col.Value))
                    {
                        mappings.Add(
                            new IdentityElementMapping(
                                col,
                                identityPath.Canonical,
                                LookupColumnScalarType(
                                    columnScalarTypes,
                                    col,
                                    identityPath.Canonical,
                                    resource
                                )
                            )
                        );
                    }
                }
                continue;
            }

            if (!rootColumnsByPath.TryGetValue(identityPath.Canonical, out var columnName))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column during identity element mapping."
                );
            }

            if (seenColumns.Add(columnName.Value))
            {
                mappings.Add(
                    new IdentityElementMapping(
                        columnName,
                        identityPath.Canonical,
                        LookupColumnScalarType(
                            columnScalarTypes,
                            columnName,
                            identityPath.Canonical,
                            resource
                        )
                    )
                );
            }
        }

        return mappings.ToArray();
    }

    /// <summary>
    /// Resolves the <see cref="RelationalScalarType"/> for a given column from the pre-built lookup.
    /// </summary>
    private static RelationalScalarType LookupColumnScalarType(
        Dictionary<string, RelationalScalarType> columnScalarTypes,
        DbColumnName column,
        string identityPath,
        QualifiedResourceName resource
    )
    {
        if (!columnScalarTypes.TryGetValue(column.Value, out var scalarType))
        {
            throw new InvalidOperationException(
                $"Identity column '{column.Value}' for path '{identityPath}' on resource "
                    + $"'{FormatResource(resource)}' has no scalar type metadata."
            );
        }
        return scalarType;
    }

    /// <summary>
    /// Builds column mappings from concrete root table columns to abstract identity table columns
    /// for <see cref="TriggerKindParameters.AbstractIdentityMaintenance"/> triggers. For
    /// identity-component references, resolves to locally stored identity-part columns (not the FK
    /// <c>..._DocumentId</c>).
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

            // Resolve the concrete source column via identity-part columns for references.
            var identityPartColumns = ResolveReferenceIdentityPartColumns(
                concretePath,
                referenceBindingsByIdentityPath,
                rootTable.Table,
                resource
            );

            if (identityPartColumns is not null)
            {
                if (identityPartColumns.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Abstract identity path '{abstractPath}' (concrete path '{concretePath}') "
                            + $"on resource '{FormatResource(resource)}' expected exactly one identity-part column "
                            + $"but found {identityPartColumns.Count}."
                    );
                }

                mappings.Add(new TriggerColumnMapping(identityPartColumns[0], abstractColumn.ColumnName));
                continue;
            }

            if (!rootColumnsByPath.TryGetValue(concretePath, out var columnName))
            {
                throw new InvalidOperationException(
                    $"Abstract identity path '{abstractPath}' (concrete path '{concretePath}') "
                        + $"on resource '{FormatResource(resource)}' did not map to a root table column "
                        + "during abstract identity column mapping."
                );
            }

            mappings.Add(new TriggerColumnMapping(columnName, abstractColumn.ColumnName));
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

    /// <summary>
    /// Resolves a sequence of column names to their canonical stored columns, de-duplicating
    /// by canonical column name. Convenience wrapper around <see cref="ResolveToStoredColumn"/>.
    /// </summary>
    private static IReadOnlyList<DbColumnName> ResolveColumnsToStored(
        IEnumerable<DbColumnName> columns,
        DbTableModel table,
        QualifiedResourceName resource
    )
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<DbColumnName> result = [];

        foreach (var column in columns)
        {
            var resolved = ResolveToStoredColumn(column, table, resource);

            if (seen.Add(resolved.Value))
            {
                result.Add(resolved);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Resolves a column name to its canonical stored column. If the column is a unified alias
    /// (persisted computed column), returns the canonical storage column; otherwise returns the
    /// column itself.
    /// </summary>
    /// <remarks>
    /// This is required for MSSQL trigger guards (<c>UPDATE()</c>) and propagation targets
    /// (<c>SET r.[col]</c>), which SQL Server rejects for computed columns
    /// (Msg 2114 and Msg 271 respectively).
    /// </remarks>
    private static DbColumnName ResolveToStoredColumn(
        DbColumnName column,
        DbTableModel table,
        QualifiedResourceName resource
    )
    {
        var columnModel = table.Columns.FirstOrDefault(c => c.ColumnName == column);

        if (columnModel is null)
        {
            throw new InvalidOperationException(
                $"Trigger derivation for resource '{FormatResource(resource)}': column "
                    + $"'{column.Value}' not found on table "
                    + $"'{table.Table.Schema.Value}.{table.Table.Name}'."
            );
        }

        return columnModel.Storage switch
        {
            ColumnStorage.UnifiedAlias alias => alias.CanonicalColumn,
            _ => columnModel.ColumnName,
        };
    }
}
