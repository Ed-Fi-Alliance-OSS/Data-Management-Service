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
    /// <c>DocumentStamping</c>, <c>ReferentialIdentityMaintenance</c>, and
    /// <c>AbstractIdentityMaintenance</c> triggers. SQL Server identity-value propagation is
    /// handled by native cascades under MssqlForeignKeyPruningPass; no propagation trigger is
    /// emitted.
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
