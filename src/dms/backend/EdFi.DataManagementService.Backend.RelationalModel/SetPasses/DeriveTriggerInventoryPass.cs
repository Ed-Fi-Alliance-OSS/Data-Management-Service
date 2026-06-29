// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;
using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.SetPasses.IdentityProjectionResolver;

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
    private const string PropagationTriggerPrefix = "PropagateIdentity";

    // The shared dms.Descriptor stamping trigger. Defined as local constants because DmsTableNames is
    // internal to Backend.Ddl and RelationalModel must not depend on the DDL project; CoreDdlEmitterTests
    // and DeriveTriggerInventoryPassTests each independently pin the same literal, so drift on either side
    // fails that side's tests (no single cross-project assertion exists). Matches the existing
    // local-constant convention in this layer (e.g. IdentifierCollisionDetector,
    // AbstractIdentityTableAndUnionViewDerivationPass).
    private const string DescriptorStampingTriggerName = "TR_Descriptor_Stamp_Document";
    private static readonly DbTableName _descriptorTable = new(new DbSchemaName("dms"), "Descriptor");

    /// <summary>
    /// Populates <see cref="RelationalModelSetBuilderContext.TriggerInventory"/> with
    /// <c>DocumentStamping</c>, <c>ReferentialIdentityMaintenance</c>,
    /// <c>AbstractIdentityMaintenance</c>, and (MSSQL only) <c>MssqlIdentityPropagationTrigger</c>
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

            // Build identity element mappings once — shared by both identity projection columns
            // and the referential identity trigger below.
            var identityElements = BuildIdentityElementMappings(resourceModel, builderContext, resource);

            // Resolve identity projection columns for the root table.
            var identityProjectionColumns = BuildRootIdentityProjectionColumns(
                resourceModel,
                identityElements,
                resource
            );

            // DocumentStamping trigger for each table (root, child, extension).
            foreach (var table in resourceModel.TablesInDependencyOrder)
            {
                var documentIdKeyColumn = ResolveRootScopeLocatorColumn(table, rootTable);

                if (documentIdKeyColumn is null)
                {
                    throw new InvalidOperationException(
                        $"DocumentStamping trigger derivation requires a root DocumentId locator column, "
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
                        new TriggerKindParameters.DocumentStamping(),
                        // The mirror UPDATE targets the owning resource root: the source table itself for
                        // the root trigger, the resource root for child / collection / _ext triggers.
                        MirrorStampTargetTable: rootTable.Table
                    )
                );
            }

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
                            identityElements[0].ScalarType,
                            identityElements[0].IsDescriptorReference
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

        // Shared dms.Descriptor stamping trigger. Descriptor resources are skipped above (they get no
        // per-resource trigger); the single shared trigger is derived once per model set regardless of
        // whether any descriptor resource exists, because core DDL always emits dms.Descriptor and its
        // stamping trigger. The ChangeTracking attachment (tombstones / key-change rows) is added later by
        // DeriveTrackedChangeInventoryPass, and only when a shared descriptor tracked-change table exists.
        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName(DescriptorStampingTriggerName),
                _descriptorTable,
                [RelationalNameConventions.DocumentIdColumnName],
                // Descriptor identity (Namespace + CodeValue) is immutable, so there are no identity
                // projection columns to diff and the shared descriptor table never receives key-change
                // rows (only tombstones). This empty workset is safe only because descriptor writes are
                // handled by DescriptorWriteHandler, not DefaultRelationalWriteExecutor — the latter's
                // RelationalWriteIdentityStability guard throws on a DocumentStamping trigger with zero
                // IdentityProjectionColumns. If descriptors are ever routed through that executor, this
                // workset must be populated first.
                [],
                new TriggerKindParameters.DocumentStamping(),
                MirrorStampTargetTable: _descriptorTable
            )
        );

        // MssqlIdentityPropagationTrigger — MSSQL only: emits triggers on referenced entities to propagate
        // identity updates to all referrers. This replaces ON UPDATE CASCADE which SQL Server rejects
        // due to multiple cascade paths.
        if (context.Dialect == SqlDialect.Mssql)
        {
            var resourceContextsByResource = SetPassHelpers.BuildResourceContextLookup(context);

            EmitMssqlIdentityPropagationTriggers(
                context,
                abstractTablesByResource,
                resourceContextsByResource,
                resourcesByKey
            );
        }
    }

    /// <summary>
    /// Helper record for reverse reference index entries. <see cref="BindingTable"/> is the
    /// physical referrer table that stores projected reference identity columns — this is the
    /// referrer resource's root table for root bindings, the owning child collection table for
    /// child bindings, or the owning <c>_ext</c> table for extension bindings.
    /// </summary>
    private sealed record ReverseReferenceEntry(
        QualifiedResourceName ReferrerResource,
        DbTableModel BindingTable,
        DocumentReferenceBinding Binding
    );

    /// <summary>
    /// Emits <see cref="TriggerKindParameters.MssqlIdentityPropagationTrigger"/> triggers on referenced
    /// entities to propagate identity updates to all referrers. These replace the <c>ON UPDATE CASCADE</c>
    /// that PostgreSQL handles natively but SQL Server rejects due to multiple cascade paths.
    /// </summary>
    private static void EmitMssqlIdentityPropagationTriggers(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, AbstractIdentityTableInfo> abstractTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> concreteResourcesByName
    )
    {
        // Build reverse reference index: for each target resource, collect all referrer entries.
        var reverseIndex = BuildReverseReferenceIndex(context, concreteResourcesByName);

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
                    || !context
                        .GetOrCreateResourceBuilderContext(targetContext)
                        .TransitivelyAllowIdentityUpdates
                )
                {
                    continue;
                }

                triggerTableModel = concreteModel.RelationalModel.Root;

                // Identity projection columns from the root table, resolved to stored columns.
                // Under key unification, some identity columns may be unified aliases that are computed,
                // and SQL Server rejects UPDATE() on computed columns (Msg 2114).
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
                    entry.ReferrerResource,
                    entry.BindingTable,
                    triggerTableModel,
                    targetResource
                );

                referrerUpdates.Add(
                    new PropagationReferrerTarget(
                        entry.BindingTable.Table,
                        entry.Binding.FkColumn,
                        columnMappings
                    )
                );
            }

            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName(BuildTriggerName(triggerTable, PropagationTriggerPrefix)),
                    triggerTable,
                    [RelationalNameConventions.DocumentIdColumnName],
                    identityProjectionColumns,
                    new TriggerKindParameters.MssqlIdentityPropagationTrigger(referrerUpdates)
                )
            );
        }
    }

    /// <summary>
    /// Builds a reverse reference index mapping target resources to their referrer entries.
    /// Resource extension contexts contribute their <c>_ext</c>-scoped reference mappings against
    /// the base resource's merged binding set, so extension tables appear as referrers on the
    /// referenced resource's propagation trigger.
    /// </summary>
    private static Dictionary<QualifiedResourceName, List<ReverseReferenceEntry>> BuildReverseReferenceIndex(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> concreteResourcesByName
    )
    {
        var reverseIndex = new Dictionary<QualifiedResourceName, List<ReverseReferenceEntry>>();
        var baseResourcesByName = SetPassHelpers.BuildExtensionBaseResourceLookup(
            context,
            static (_, model) => model
        );

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var contextResource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );

            ConcreteResourceModel referrerModel;
            QualifiedResourceName referrerResource;

            if (IsResourceExtension(resourceContext))
            {
                // ReferenceBindingPass merges extension reference bindings into the base
                // resource's RelationalModel.DocumentReferenceBindings (the binding's Table
                // points at the _ext table). Skipping the extension context here would drop
                // those _ext-bound referrers from the propagation reverse index, leaving the
                // extension table's projected identity columns stale on cross-resource
                // identity updates and preventing its stamp trigger from firing.
                referrerModel = ResolveBaseResourceForExtension(
                    resourceContext.ResourceName,
                    contextResource,
                    baseResourcesByName,
                    static model => model.ResourceKey.Resource
                );
                referrerResource = referrerModel.ResourceKey.Resource;
            }
            else
            {
                referrerResource = contextResource;

                // Continue intentionally filters abstract and unmodeled resources — they have
                // schema entries but no concrete resource model (e.g., abstract base resources
                // or resources that do not produce relational tables).
                if (!concreteResourcesByName.TryGetValue(referrerResource, out var resolved))
                {
                    continue;
                }

                if (resolved.StorageKind == ResourceStorageKind.SharedDescriptorTable)
                {
                    continue;
                }

                referrerModel = resolved;
            }

            var referrerBuilderContext = context.GetOrCreateResourceBuilderContext(resourceContext);

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

                // Resolve binding.Table to its DbTableModel via the shared scope-aware helper:
                // the same DbTableName can occur in multiple scopes on a resource (e.g., a base
                // table and its _ext counterpart), so a name-only dictionary lookup is unsafe.
                // Resolving against the wrong model would silently mis-project unified-alias columns.
                var bindingTableModel = ResolveReferenceBindingTable(
                    binding,
                    referrerModel.RelationalModel,
                    referrerResource
                );

                var targetResource = mapping.TargetResource;

                if (!reverseIndex.TryGetValue(targetResource, out var entries))
                {
                    entries = [];
                    reverseIndex[targetResource] = entries;
                }

                entries.Add(new ReverseReferenceEntry(referrerResource, bindingTableModel, binding));
            }
        }

        return reverseIndex;
    }

    /// <summary>
    /// Builds column mappings for identity propagation: source identity columns on the trigger
    /// table to referrer stored columns on the binding table. The binding table may be the
    /// referrer's root, a child collection table, or an extension table; <c>ib.Column</c> by
    /// construction lives on that binding table, so its storage-column resolution must use the
    /// binding table's <see cref="DbTableModel"/>. Resolving against any other model is unsound,
    /// even when the storage column name happens to match.
    /// </summary>
    /// <remarks>
    /// Source columns are resolved from the referenced trigger table model using target identity
    /// JSON paths. Update target columns are resolved from the referrer binding table model using
    /// <see cref="DocumentReferenceBinding.IdentityBindings"/>. In <see cref="TriggerColumnMapping"/>,
    /// <c>TargetColumn</c> means the column being updated on the referrer binding table — not the
    /// referenced resource's target table.
    /// </remarks>
    internal static IReadOnlyList<TriggerColumnMapping> BuildPropagationColumnMappings(
        DocumentReferenceBinding binding,
        QualifiedResourceName referrerResource,
        DbTableModel bindingTableModel,
        DbTableModel triggerTable,
        QualifiedResourceName targetResource
    )
    {
        // Build lookup: identity JSON path → source column on the trigger table.
        var sourceColumnsByPath = BuildColumnNameLookupBySourceJsonPath(triggerTable, targetResource);

        // Group identity bindings by their resolved (stored) target column, preserving first-appearance
        // order for the SET clause. Key unification can map several identity bindings to one stored target
        // column — including grouped duplicate ReferenceJsonPath bindings that map one reference field to
        // several key-unified target identity fields — and collapsing them avoids invalid SQL (SQL Server
        // Error 264: duplicate columns in SET clause). ib.Column may be a unified-alias computed column, so
        // resolve it to its canonical storage column before grouping.
        var orderedTargets = new List<DbColumnName>();
        var bindingsByTarget = new Dictionary<string, List<ReferenceIdentityBinding>>(StringComparer.Ordinal);

        foreach (var ib in binding.IdentityBindings)
        {
            var targetColumn = ResolveToStoredColumn(ib.Column, bindingTableModel, referrerResource);

            if (!bindingsByTarget.TryGetValue(targetColumn.Value, out var grouped))
            {
                grouped = [];
                bindingsByTarget[targetColumn.Value] = grouped;
                orderedTargets.Add(targetColumn);
            }

            grouped.Add(ib);
        }

        List<TriggerColumnMapping> mappings = new(orderedTargets.Count);

        // Resolve a binding's IdentityJsonPath to its source column on the trigger table; each binding
        // expects the source it names, so a missing mapping is a derivation fault.
        DbColumnName ResolveCandidateSource(ReferenceIdentityBinding ib)
        {
            if (!sourceColumnsByPath.TryGetValue(ib.IdentityJsonPath.Canonical, out var candidateSource))
            {
                throw new InvalidOperationException(
                    $"Propagation trigger derivation for target '{FormatResource(targetResource)}': "
                        + $"identity path '{ib.IdentityJsonPath.Canonical}' did not map to a column on "
                        + $"trigger table '{triggerTable.Table.Schema.Value}.{triggerTable.Table.Name}'."
                );
            }

            return candidateSource;
        }

        foreach (var targetColumn in orderedTargets)
        {
            var groupBindings = bindingsByTarget[targetColumn.Value];

            // Resolve every binding in the group to its own source column. Collapsing the group onto one
            // referrer column is only sound when those sources read the same effective value.
            var candidates = groupBindings
                .Select(ib => (ib.IdentityJsonPath, SourceColumn: ResolveCandidateSource(ib)))
                .ToArray();

            // The target-side grouping already proved the referrer columns collapse to one stored column,
            // but emitting a single representative source is only correct when every candidate source is
            // value-equivalent. Guard the source side and fail loudly rather than propagate a wrong value.
            ValidatePropagationSourceEquivalence(
                triggerTable,
                targetResource,
                referrerResource,
                targetColumn,
                candidates
            );

            // Choose the source from the field-name-matched binding (the rule that names abstract identity
            // columns), so the emitted source is independent of binding order rather than reflecting
            // whichever grouped duplicate happened to be first. Equivalence is validated above, so the
            // representative is value-correct for the whole group.
            var chosen = SelectReferenceFieldMatchedBinding(binding.ReferenceObjectPath, groupBindings);

            mappings.Add(
                new TriggerColumnMapping(sourceColumnsByPath[chosen.IdentityJsonPath.Canonical], targetColumn)
            );
        }

        return mappings.ToArray();
    }

    /// <summary>
    /// Chooses one identity binding from a group that resolves to the same stored column. Prefers the
    /// binding whose reference field name matches its target identity field name — the same rule that names
    /// abstract identity columns — with a deterministic tie-break, so trigger source selection does not
    /// depend on binding order. Grouped duplicate bindings are key-unified, so every candidate carries the
    /// same stored value; this only fixes which equivalent column name is emitted.
    /// </summary>
    private static ReferenceIdentityBinding SelectReferenceFieldMatchedBinding(
        JsonPathExpression referenceObjectPath,
        IReadOnlyList<ReferenceIdentityBinding> candidates
    )
    {
        return OrderByRepresentativeReferenceBinding(
                candidates,
                referenceObjectPath,
                ib => ib.ReferenceJsonPath,
                ib => ib.IdentityJsonPath
            )
            .First();
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
                {
                    throw new InvalidOperationException(
                        $"Resource '{FormatResource(resource)}' has superclassIdentityJsonPath set but no identity JSON paths."
                    );
                }

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
                // A logical reference field can fan out to multiple identity-part columns when
                // duplicate ReferenceJsonPath bindings map one reference field to several target
                // identity fields (the same grouped-duplicate shape the abstract identity table accepts).
                // They must converge to one stored column; SelectIdentityElementColumn verifies that and
                // returns the representative member, throwing only on genuine divergence.
                var sourceColumn = SelectIdentityElementColumn(
                    identityPartColumns,
                    rootTable,
                    concretePath,
                    resource
                );

                mappings.Add(new TriggerColumnMapping(sourceColumn, abstractColumn.ColumnName));
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
    /// Builds a trigger name following the <c>TR_{TableName}_{Purpose}</c> convention.
    /// </summary>
    private static string BuildTriggerName(DbTableName table, string purposeToken)
    {
        return $"TR_{table.Name}_{purposeToken}";
    }

    /// <summary>
    /// Resolves the root document locator column used by document-stamping triggers. Stable-key collection
    /// tables surface this through explicit identity metadata instead of through PK shape.
    /// </summary>
    private static DbKeyColumn? ResolveRootScopeLocatorColumn(DbTableModel table, DbTableModel rootTable)
    {
        if (table.Table.Equals(rootTable.Table))
        {
            return new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart);
        }

        var metadataLocator = table.IdentityMetadata.RootScopeLocatorColumns.FirstOrDefault(column =>
            RelationalNameConventions.IsDocumentIdColumn(column)
        );

        if (metadataLocator != default)
        {
            return new DbKeyColumn(metadataLocator, ColumnKind.ParentKeyPart);
        }

        return table.Key.Columns.FirstOrDefault(column =>
            column.Kind == ColumnKind.ParentKeyPart
            && RelationalNameConventions.IsDocumentIdColumn(column.ColumnName)
        );
    }
}
