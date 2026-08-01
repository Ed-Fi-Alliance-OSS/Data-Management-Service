// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// The three cross-resource natural-key probe maps produced in one pass over the derived model set.
/// </summary>
internal sealed record NaturalKeyProbeCompilation(
    FrozenDictionary<QualifiedResourceName, NaturalKeyProbeTarget> NaturalKeyProbeTargets,
    FrozenDictionary<QualifiedResourceName, OwnNaturalKeyProbe> OwnNaturalKeyProbesByResource,
    DescriptorProbeTarget DescriptorProbeTarget
);

/// <summary>
/// Compiles the natural-key probe metadata hung off <see cref="MappingSet"/>.
/// </summary>
/// <remarks>
/// <para>
/// The probe column lists must reproduce, byte for byte, the column lists that the model-build passes
/// baked into the physical unique constraints — <c>ReferenceConstraintPass.EnsureTargetUnique</c> for
/// <c>UX_&lt;T&gt;_RefKey</c> and <c>RootIdentityConstraintPass.BuildRootIdentityColumns</c> for
/// <c>UX_&lt;R&gt;_NK</c> — or a probe binds a different arity than the index it is meant to seek.
/// The derivation is therefore replicated from the same inputs those passes use: the resource's
/// <c>identityJsonPaths</c> ordering (promoted onto
/// <see cref="ConcreteResourceModel.IdentityJsonPaths"/>), the root table's
/// <see cref="DbColumnModel.SourceJsonPath"/> lookup, and the resource's
/// <see cref="DocumentReferenceBinding"/> inventory.
/// </para>
/// <para>
/// Two things are deliberately NOT done here. The probes are not derived from
/// <c>TriggerKindParameters.ReferentialIdentityMaintenance.IdentityElements</c>, even though that block
/// already carries an ordered, typed, descriptor-flagged identity element list: it belongs to the
/// <c>ReferentialIdentity</c> trigger that is being removed. And constraints are never located by NAME —
/// <c>ApplyDialectIdentifierShorteningPass</c> hash-truncates long identifiers, so the <c>_RefKey</c> /
/// <c>_NK</c> tokens are not reliably present in the emitted names.
/// </para>
/// </remarks>
internal static class NaturalKeyProbeCompiler
{
    /// <summary>The shared descriptor table probed for descriptor URI resolution.</summary>
    internal static readonly DbTableName DescriptorTable = new(new DbSchemaName("dms"), "Descriptor");

    /// <summary>
    /// The persisted lower-cased descriptor URI column. Compiled as a constant here; the column itself is
    /// added to <c>dms.Descriptor</c> by a later step, so nothing may query it yet.
    /// </summary>
    internal static readonly DbColumnName DescriptorUriLoweredColumn = new("UriLowered");

    /// <summary>The descriptor discriminator column.</summary>
    internal static readonly DbColumnName DescriptorDiscriminatorColumn = new("Discriminator");

    /// <summary>
    /// Compiles the target probes, own-identity probes, and the shared descriptor probe target.
    /// </summary>
    public static NaturalKeyProbeCompilation Compile(DerivedRelationalModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        var targetsByResource = new Dictionary<QualifiedResourceName, NaturalKeyProbeTarget>();
        var ownProbesByResource = new Dictionary<QualifiedResourceName, OwnNaturalKeyProbe>();
        var discriminatorLiteralByResource = new Dictionary<QualifiedResourceName, string>();

        foreach (var concreteResource in modelSet.ConcreteResourcesInNameOrder)
        {
            var resource = concreteResource.RelationalModel.Resource;

            if (concreteResource.StorageKind is ResourceStorageKind.SharedDescriptorTable)
            {
                // The discriminator literal is the BARE resource name, matching what
                // DescriptorWriteBodyExtractor persists. It is NOT the "{Project}:{Resource}" form used by
                // link injection and abstract identity tables.
                if (!discriminatorLiteralByResource.TryAdd(resource, resource.ResourceName))
                {
                    throw DuplicateEntry("descriptor discriminator literal", resource);
                }

                continue;
            }

            if (concreteResource.StorageKind is not ResourceStorageKind.RelationalTables)
            {
                continue;
            }

            var rootTable = concreteResource.RelationalModel.Root;
            var columnModelsByName = BuildColumnModelsByName(rootTable);
            var columnNamesBySourceJsonPath = BuildColumnNameLookupBySourceJsonPath(rootTable, resource);
            var referenceBindingsByIdentityPath = BuildReferenceIdentityBindings(
                concreteResource.RelationalModel.DocumentReferenceBindings,
                resource
            );

            if (
                !targetsByResource.TryAdd(
                    resource,
                    BuildConcreteProbeTarget(
                        concreteResource,
                        rootTable,
                        columnModelsByName,
                        columnNamesBySourceJsonPath,
                        resource
                    )
                )
            )
            {
                throw DuplicateEntry("natural-key probe target", resource);
            }

            if (
                !ownProbesByResource.TryAdd(
                    resource,
                    BuildOwnNaturalKeyProbe(
                        concreteResource,
                        rootTable,
                        columnModelsByName,
                        columnNamesBySourceJsonPath,
                        referenceBindingsByIdentityPath,
                        resource
                    )
                )
            )
            {
                throw DuplicateEntry("own natural-key probe", resource);
            }
        }

        foreach (var abstractIdentityTable in modelSet.AbstractIdentityTablesInNameOrder)
        {
            var resource = abstractIdentityTable.AbstractResourceKey.Resource;

            if (!targetsByResource.TryAdd(resource, BuildAbstractProbeTarget(abstractIdentityTable)))
            {
                throw DuplicateEntry("natural-key probe target", resource);
            }
        }

        return new NaturalKeyProbeCompilation(
            targetsByResource.ToFrozenDictionary(),
            ownProbesByResource.ToFrozenDictionary(),
            new DescriptorProbeTarget(
                DescriptorTable,
                DescriptorUriLoweredColumn,
                DescriptorDiscriminatorColumn,
                discriminatorLiteralByResource.ToFrozenDictionary()
            )
        );
    }

    /// <summary>
    /// Builds the concrete-resource target probe: identity paths resolved to root columns, storage-resolved
    /// through unified aliases, de-duplicated by storage column in first-seen order — the column list of
    /// <c>UX_&lt;T&gt;_RefKey</c> minus its trailing <c>DocumentId</c>.
    /// </summary>
    private static NaturalKeyProbeTarget BuildConcreteProbeTarget(
        ConcreteResourceModel concreteResource,
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnModelsByName,
        IReadOnlyDictionary<string, DbColumnName> columnNamesBySourceJsonPath,
        QualifiedResourceName resource
    )
    {
        List<NaturalKeyProbeColumn> probeColumns = new(concreteResource.IdentityJsonPaths.Count);
        HashSet<string> seenStorageColumns = new(StringComparer.Ordinal);

        foreach (var identityPath in concreteResource.IdentityJsonPaths)
        {
            RejectArraySegments(identityPath, resource);

            var identityColumn = ResolveRootColumn(columnNamesBySourceJsonPath, identityPath, resource);
            var storageColumn = ResolveToStoredColumn(
                identityColumn,
                columnModelsByName,
                rootTable.Table,
                resource
            );

            if (!seenStorageColumns.Add(storageColumn.Value))
            {
                continue;
            }

            var storageColumnModel = RequireColumnModel(
                columnModelsByName,
                storageColumn,
                rootTable.Table,
                resource
            );

            probeColumns.Add(
                new NaturalKeyProbeColumn(
                    storageColumn,
                    identityPath,
                    RequireScalarType(storageColumnModel, identityPath, rootTable.Table, resource),
                    DescriptorResourceOf(storageColumnModel, rootTable.Table, resource)
                )
            );
        }

        return new NaturalKeyProbeTarget(
            rootTable.Table,
            RelationalNameConventions.DocumentIdColumnName,
            IsAbstract: false,
            probeColumns
        );
    }

    /// <summary>
    /// Builds the abstract-resource target probe over the <c>{Abstract}Identity</c> table — never the
    /// abstract union view, which carries no index. The table's identity columns are exactly its columns
    /// with a source JSONPath, in the same order the derivation pass used for
    /// <c>UX_&lt;Abstract&gt;Identity_RefKey</c>.
    /// </summary>
    private static NaturalKeyProbeTarget BuildAbstractProbeTarget(
        AbstractIdentityTableInfo abstractIdentityTable
    )
    {
        var resource = abstractIdentityTable.AbstractResourceKey.Resource;
        var table = abstractIdentityTable.TableModel;

        List<NaturalKeyProbeColumn> probeColumns = [];

        foreach (var column in table.Columns)
        {
            if (column.SourceJsonPath is not { } sourceJsonPath)
            {
                continue;
            }

            probeColumns.Add(
                new NaturalKeyProbeColumn(
                    column.ColumnName,
                    sourceJsonPath,
                    RequireScalarType(column, sourceJsonPath, table.Table, resource),
                    DescriptorResourceOf(column, table.Table, resource)
                )
            );
        }

        return new NaturalKeyProbeTarget(
            table.Table,
            RelationalNameConventions.DocumentIdColumnName,
            IsAbstract: true,
            probeColumns
        );
    }

    /// <summary>
    /// Builds the own-identity probe: the column list of <c>UX_&lt;R&gt;_NK</c>. Reference-sourced identity
    /// paths collapse onto the reference site's <c>..._DocumentId</c> FK column (no storage resolution —
    /// the constraint names the FK column itself); every other path resolves to its root column.
    /// </summary>
    private static OwnNaturalKeyProbe BuildOwnNaturalKeyProbe(
        ConcreteResourceModel concreteResource,
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnModelsByName,
        IReadOnlyDictionary<string, DbColumnName> columnNamesBySourceJsonPath,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        QualifiedResourceName resource
    )
    {
        List<OwnNaturalKeyProbeColumn> probeColumns = new(concreteResource.IdentityJsonPaths.Count);
        HashSet<string> seenColumns = new(StringComparer.Ordinal);

        foreach (var identityPath in concreteResource.IdentityJsonPaths)
        {
            RejectArraySegments(identityPath, resource);

            if (referenceBindingsByIdentityPath.TryGetValue(identityPath.Canonical, out var binding))
            {
                if (!binding.Table.Equals(rootTable.Table))
                {
                    throw new InvalidOperationException(
                        $"Cannot compile natural-key probes: identity path '{identityPath.Canonical}' on "
                            + $"resource '{FormatResource(resource)}' must bind to the root table."
                    );
                }

                if (!seenColumns.Add(binding.FkColumn.Value))
                {
                    continue;
                }

                var fkColumnModel = RequireColumnModel(
                    columnModelsByName,
                    binding.FkColumn,
                    rootTable.Table,
                    resource
                );

                probeColumns.Add(
                    new OwnNaturalKeyProbeColumn(
                        binding.FkColumn,
                        RequireScalarType(fkColumnModel, identityPath, rootTable.Table, resource),
                        ScalarSourceJsonPath: null,
                        ReferenceIdentityJsonPath: identityPath,
                        DescriptorResource: DescriptorResourceOf(fkColumnModel, rootTable.Table, resource)
                    )
                );

                continue;
            }

            var identityColumn = ResolveRootColumn(columnNamesBySourceJsonPath, identityPath, resource);

            if (!seenColumns.Add(identityColumn.Value))
            {
                continue;
            }

            var identityColumnModel = RequireColumnModel(
                columnModelsByName,
                identityColumn,
                rootTable.Table,
                resource
            );

            probeColumns.Add(
                new OwnNaturalKeyProbeColumn(
                    identityColumn,
                    RequireScalarType(identityColumnModel, identityPath, rootTable.Table, resource),
                    ScalarSourceJsonPath: identityPath,
                    ReferenceIdentityJsonPath: null,
                    DescriptorResource: DescriptorResourceOf(identityColumnModel, rootTable.Table, resource)
                )
            );
        }

        return new OwnNaturalKeyProbe(rootTable.Table, probeColumns)
        {
            IdentityJsonPathsInOrder = concreteResource.IdentityJsonPaths,
        };
    }

    /// <summary>
    /// Replicates <c>ConstraintDerivationHelpers.BuildColumnNameLookupBySourceJsonPath</c>: same-kind
    /// duplicates arise from key unification (a canonical column and its unified aliases share one source
    /// path) and the ordinally first column name wins; mixed kinds for one path are a model defect.
    /// </summary>
    private static IReadOnlyDictionary<string, DbColumnName> BuildColumnNameLookupBySourceJsonPath(
        DbTableModel table,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, DbColumnName> lookup = new(StringComparer.Ordinal);

        foreach (
            var group in table
                .Columns.Where(column => column.SourceJsonPath is not null)
                .GroupBy(column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
        )
        {
            var ordered = group
                .OrderBy(candidate => candidate.ColumnName.Value, StringComparer.Ordinal)
                .ToArray();

            if (ordered.Select(column => column.Kind).Distinct().Skip(1).Any())
            {
                var columnDetails = string.Join(
                    ", ",
                    ordered.Select(column => $"{column.ColumnName.Value} ({column.Kind})")
                );

                throw new InvalidOperationException(
                    $"Cannot compile natural-key probes: table '{table.Table}' on resource "
                        + $"'{FormatResource(resource)}' has multiple column kinds for source path "
                        + $"'{group.Key}': {columnDetails}."
                );
            }

            lookup[group.Key] = ordered[0].ColumnName;
        }

        return lookup;
    }

    /// <summary>
    /// Replicates <c>ConstraintDerivationHelpers.BuildReferenceIdentityBindings</c>: maps each identity
    /// value path under a reference object to the reference binding that supplies it.
    /// </summary>
    private static IReadOnlyDictionary<string, DocumentReferenceBinding> BuildReferenceIdentityBindings(
        IReadOnlyList<DocumentReferenceBinding> bindings,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, DocumentReferenceBinding> lookup = new(StringComparer.Ordinal);

        foreach (var binding in bindings)
        {
            foreach (
                var canonical in binding.IdentityBindings.Select(identityBinding =>
                    identityBinding.ReferenceJsonPath.Canonical
                )
            )
            {
                if (lookup.TryAdd(canonical, binding))
                {
                    continue;
                }

                if (lookup[canonical].ReferenceObjectPath.Canonical == binding.ReferenceObjectPath.Canonical)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Cannot compile natural-key probes: identity path '{canonical}' on resource "
                        + $"'{FormatResource(resource)}' was bound to multiple references."
                );
            }
        }

        return lookup;
    }

    private static IReadOnlyDictionary<DbColumnName, DbColumnModel> BuildColumnModelsByName(
        DbTableModel table
    )
    {
        Dictionary<DbColumnName, DbColumnModel> columnModelsByName = new();

        foreach (var column in table.Columns)
        {
            columnModelsByName[column.ColumnName] = column;
        }

        return columnModelsByName;
    }

    /// <summary>
    /// Unwraps a unified-alias column to its canonical stored column, mirroring
    /// <c>IdentityProjectionResolver.ResolveToStoredColumn</c>. A probe bound to an alias is semantically
    /// correct but cannot seek the <c>*_RefKey</c> index, which names the canonical column.
    /// </summary>
    private static DbColumnName ResolveToStoredColumn(
        DbColumnName column,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnModelsByName,
        DbTableName table,
        QualifiedResourceName resource
    )
    {
        var columnModel = RequireColumnModel(columnModelsByName, column, table, resource);

        return columnModel.Storage switch
        {
            ColumnStorage.UnifiedAlias alias => alias.CanonicalColumn,
            _ => columnModel.ColumnName,
        };
    }

    private static DbColumnName ResolveRootColumn(
        IReadOnlyDictionary<string, DbColumnName> columnNamesBySourceJsonPath,
        JsonPathExpression identityPath,
        QualifiedResourceName resource
    )
    {
        if (!columnNamesBySourceJsonPath.TryGetValue(identityPath.Canonical, out var columnName))
        {
            throw new InvalidOperationException(
                $"Cannot compile natural-key probes: identity path '{identityPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not map to a root table column."
            );
        }

        return columnName;
    }

    private static DbColumnModel RequireColumnModel(
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnModelsByName,
        DbColumnName column,
        DbTableName table,
        QualifiedResourceName resource
    )
    {
        if (!columnModelsByName.TryGetValue(column, out var columnModel))
        {
            throw new InvalidOperationException(
                $"Cannot compile natural-key probes: column '{column.Value}' for resource "
                    + $"'{FormatResource(resource)}' was not found on table '{table}'."
            );
        }

        return columnModel;
    }

    private static RelationalScalarType RequireScalarType(
        DbColumnModel columnModel,
        JsonPathExpression identityPath,
        DbTableName table,
        QualifiedResourceName resource
    )
    {
        return columnModel.ScalarType
            ?? throw new InvalidOperationException(
                $"Cannot compile natural-key probes: identity column '{columnModel.ColumnName.Value}' for "
                    + $"path '{identityPath.Canonical}' on table '{table}' of resource "
                    + $"'{FormatResource(resource)}' has no scalar type metadata."
            );
    }

    private static QualifiedResourceName? DescriptorResourceOf(
        DbColumnModel columnModel,
        DbTableName table,
        QualifiedResourceName resource
    )
    {
        if (columnModel.Kind is not ColumnKind.DescriptorFk)
        {
            return null;
        }

        return columnModel.TargetResource
            ?? throw new InvalidOperationException(
                $"Cannot compile natural-key probes: descriptor column '{columnModel.ColumnName.Value}' on "
                    + $"table '{table}' of resource '{FormatResource(resource)}' has no target resource."
            );
    }

    private static void RejectArraySegments(JsonPathExpression identityPath, QualifiedResourceName resource)
    {
        if (identityPath.Segments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
        {
            throw new InvalidOperationException(
                $"Cannot compile natural-key probes: identity path '{identityPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' must not include array segments."
            );
        }
    }

    private static InvalidOperationException DuplicateEntry(
        string entryDescription,
        QualifiedResourceName resource
    ) =>
        new(
            $"Cannot compile mapping set: duplicate {entryDescription} for resource "
                + $"'{FormatResource(resource)}'."
        );

    private static string FormatResource(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";
}
