// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles natural-key probe metadata from the final, dialect-specific derived relational model set.
/// </summary>
/// <remarks>
/// Probes are derived from the relational model only: identity path order, column source paths, column
/// storage and reference bindings. Trigger metadata, constraint names and discriminators are never read.
/// </remarks>
internal static class NaturalKeyProbeCompiler
{
    /// <summary>
    /// The compiled reference-target and own natural-key probes.
    /// </summary>
    internal sealed record Result(
        IReadOnlyDictionary<QualifiedResourceName, NaturalKeyProbeTarget> Targets,
        IReadOnlyDictionary<QualifiedResourceName, OwnNaturalKeyProbe> OwnKeys
    );

    /// <summary>
    /// Compiles and validates the natural-key probes for a derived relational model set.
    /// </summary>
    public static Result Compile(DerivedRelationalModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        HashSet<QualifiedResourceName> referencedResources =
        [
            .. modelSet.ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.DocumentReferenceBindings.Select(binding => binding.TargetResource)
            ),
        ];

        Dictionary<QualifiedResourceName, NaturalKeyProbeTarget> targets = [];
        Dictionary<QualifiedResourceName, OwnNaturalKeyProbe> ownKeys = [];

        foreach (var concreteResource in modelSet.ConcreteResourcesInNameOrder)
        {
            if (concreteResource.StorageKind is not ResourceStorageKind.RelationalTables)
            {
                continue;
            }

            var resource = concreteResource.RelationalModel.Resource;

            if (concreteResource.IdentityJsonPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: resource '{Format(resource)}' has no identity JSON paths, "
                        + "so no natural-key probe can be compiled."
                );
            }

            ownKeys.Add(resource, BuildOwnKeyProbe(concreteResource));

            if (referencedResources.Contains(resource))
            {
                targets.Add(resource, BuildConcreteTarget(concreteResource));
            }
        }

        foreach (var abstractTable in modelSet.AbstractIdentityTablesInNameOrder)
        {
            targets.Add(abstractTable.AbstractResourceKey.Resource, BuildAbstractTarget(abstractTable));
        }

        var result = new Result(targets.ToFrozenDictionary(), ownKeys.ToFrozenDictionary());

        Validate(modelSet, result.Targets, result.OwnKeys);

        return result;
    }

    /// <summary>
    /// Validates natural-key probes against the derived relational model set. A reference-target probe must
    /// have RefKey inventory (a unique constraint on its distinct key columns, in order, followed by its
    /// document id column) and no document-reference keys; every probe must have keys; every own-key
    /// document-reference key must index a reference binding whose FK column is the key column.
    /// </summary>
    internal static void Validate(
        DerivedRelationalModelSet modelSet,
        IReadOnlyDictionary<QualifiedResourceName, NaturalKeyProbeTarget> targets,
        IReadOnlyDictionary<QualifiedResourceName, OwnNaturalKeyProbe> ownKeys
    )
    {
        var concreteResourcesByResource = modelSet.GetConcreteResourceModelsByResource();
        var abstractTablesByResource = modelSet.AbstractIdentityTablesInNameOrder.ToDictionary(table =>
            table.AbstractResourceKey.Resource
        );

        foreach (var target in targets.Values)
        {
            RequireKeys(target.Resource, target.KeyColumns, "reference-target");

            if (target.KeyColumns.Any(key => key.Binding is NaturalKeyProbeKeyBinding.DocumentReference))
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: reference-target probe for resource '{Format(target.Resource)}' "
                        + "contains a document-reference key; document-reference keys are valid only in "
                        + "own natural-key probes."
                );
            }

            DbTableModel? table = target.Kind switch
            {
                NaturalKeyProbeTargetKind.Abstract => abstractTablesByResource.TryGetValue(
                    target.Resource,
                    out var abstractTable
                )
                    ? abstractTable.TableModel
                    : null,
                _ => concreteResourcesByResource.TryGetValue(target.Resource, out var concreteResource)
                    ? concreteResource.RelationalModel.Root
                    : null,
            };

            if (table is null || table.Table != target.Table)
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: reference-target probe for resource '{Format(target.Resource)}' "
                        + $"names table '{target.Table}', which is not the resource's {target.Kind} probe table."
                );
            }

            DbColumnName[] refKeyColumns =
            [
                .. target.KeyColumns.Select(key => key.Column).Distinct(),
                target.DocumentIdColumn,
            ];

            if (
                !table
                    .Constraints.OfType<TableConstraint.Unique>()
                    .Any(unique => unique.Columns.SequenceEqual(refKeyColumns))
            )
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: reference-target probe for resource '{Format(target.Resource)}' "
                        + $"has no RefKey inventory on table '{target.Table}': no unique constraint on "
                        + $"({string.Join(", ", refKeyColumns.Select(column => column.Value))})."
                );
            }
        }

        foreach (var ownKey in ownKeys.Values)
        {
            RequireKeys(ownKey.Resource, ownKey.KeyColumns, "own natural-key");

            if (!concreteResourcesByResource.TryGetValue(ownKey.Resource, out var concreteResource))
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: own natural-key probe names unknown resource "
                        + $"'{Format(ownKey.Resource)}'."
                );
            }

            var bindings = concreteResource.RelationalModel.DocumentReferenceBindings;

            foreach (var key in ownKey.KeyColumns)
            {
                if (
                    key.Binding is NaturalKeyProbeKeyBinding.DocumentReference(var index)
                    && (index < 0 || index >= bindings.Count || bindings[index].FkColumn != key.Column)
                )
                {
                    throw new InvalidOperationException(
                        $"Cannot compile mapping set: own natural-key probe for resource "
                            + $"'{Format(ownKey.Resource)}' binds '{key.IdentityJsonPath.Canonical}' to "
                            + $"document reference binding {index}, which does not supply column "
                            + $"'{key.Column.Value}'."
                    );
                }
            }
        }
    }

    /// <summary>
    /// Builds the own natural-key probe. It binds the emitted <c>UX_&lt;R&gt;_NK</c> column contract: a
    /// reference-sourced identity part binds the reference's <c>..._DocumentId</c>, and any other part binds
    /// its path column without storage resolution.
    /// </summary>
    private static OwnNaturalKeyProbe BuildOwnKeyProbe(ConcreteResourceModel concreteResource)
    {
        var resourceModel = concreteResource.RelationalModel;
        var root = resourceModel.Root;
        var bindings = resourceModel.DocumentReferenceBindings;
        List<NaturalKeyProbeKeyColumn> keys = new(concreteResource.IdentityJsonPaths.Count);

        foreach (var identityPath in concreteResource.IdentityJsonPaths)
        {
            var referenceSites = Enumerable
                .Range(0, bindings.Count)
                .Where(index =>
                    bindings[index]
                        .IdentityBindings.Any(identityBinding =>
                            identityBinding.ReferenceJsonPath.Canonical == identityPath.Canonical
                        )
                )
                .ToArray();

            if (referenceSites.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: identity path '{identityPath.Canonical}' on resource "
                        + $"'{Format(resourceModel.Resource)}' is supplied by multiple reference sites."
                );
            }

            if (referenceSites.Length == 1)
            {
                var index = referenceSites[0];
                var binding = bindings[index];

                if (!binding.IsIdentityComponent || binding.Table != root.Table)
                {
                    throw new InvalidOperationException(
                        $"Cannot compile mapping set: identity path '{identityPath.Canonical}' on resource "
                            + $"'{Format(resourceModel.Resource)}' is supplied by reference "
                            + $"'{binding.ReferenceObjectPath.Canonical}', which is not a root-table "
                            + "identity component."
                    );
                }

                keys.Add(
                    new NaturalKeyProbeKeyColumn(
                        identityPath,
                        binding.FkColumn,
                        new NaturalKeyProbeKeyBinding.DocumentReference(index)
                    )
                );
                continue;
            }

            var pathColumns = GetPathColumns(root, identityPath, resourceModel.Resource);

            if (pathColumns.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: identity path '{identityPath.Canonical}' on resource "
                        + $"'{Format(resourceModel.Resource)}' maps to multiple root columns "
                        + $"({string.Join(", ", pathColumns.Select(column => column.ColumnName.Value))})."
                );
            }

            var column = pathColumns[0];
            keys.Add(
                new NaturalKeyProbeKeyColumn(
                    identityPath,
                    column.ColumnName,
                    BuildValueBinding(column, resourceModel.Resource)
                )
            );
        }

        return new OwnNaturalKeyProbe(
            resourceModel.Resource,
            root.Table,
            keys,
            RelationalNameConventions.DocumentIdColumnName
        );
    }

    /// <summary>
    /// Builds a concrete reference-target probe. Each identity part binds its storage-resolved column, so
    /// the probe's distinct key columns match <c>UX_&lt;R&gt;_RefKey</c>.
    /// </summary>
    private static NaturalKeyProbeTarget BuildConcreteTarget(ConcreteResourceModel concreteResource)
    {
        var resourceModel = concreteResource.RelationalModel;
        var root = resourceModel.Root;
        List<NaturalKeyProbeKeyColumn> keys = new(concreteResource.IdentityJsonPaths.Count);

        foreach (var identityPath in concreteResource.IdentityJsonPaths)
        {
            var pathColumns = GetPathColumns(root, identityPath, resourceModel.Resource);
            var storageColumns = pathColumns
                .Select(column =>
                    column.Storage is ColumnStorage.UnifiedAlias alias
                        ? alias.CanonicalColumn
                        : column.ColumnName
                )
                .Distinct()
                .ToArray();

            if (storageColumns.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: identity path '{identityPath.Canonical}' on resource "
                        + $"'{Format(resourceModel.Resource)}' resolves to multiple storage columns "
                        + $"({string.Join(", ", storageColumns.Select(column => column.Value))})."
                );
            }

            keys.Add(
                new NaturalKeyProbeKeyColumn(
                    identityPath,
                    storageColumns[0],
                    BuildValueBinding(pathColumns[0], resourceModel.Resource)
                )
            );
        }

        return new NaturalKeyProbeTarget(
            resourceModel.Resource,
            NaturalKeyProbeTargetKind.Concrete,
            root.Table,
            keys,
            RelationalNameConventions.DocumentIdColumnName
        );
    }

    /// <summary>
    /// Builds an abstract reference-target probe over the abstract identity table's identity columns, in
    /// table order, carrying the concrete member <c>ResourceKeyId</c> column.
    /// </summary>
    private static NaturalKeyProbeTarget BuildAbstractTarget(AbstractIdentityTableInfo abstractTable)
    {
        var resource = abstractTable.AbstractResourceKey.Resource;
        var table = abstractTable.TableModel;
        NaturalKeyProbeKeyColumn[] keys =
        [
            .. table
                .Columns.Where(column => column.SourceJsonPath is not null)
                .Select(column => new NaturalKeyProbeKeyColumn(
                    column.SourceJsonPath!.Value,
                    column.ColumnName,
                    BuildValueBinding(column, resource)
                )),
        ];

        if (keys.Length == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile mapping set: abstract identity table '{table.Table}' for resource "
                    + $"'{Format(resource)}' has no identity columns, so no natural-key probe can be compiled."
            );
        }

        var resourceKeyIdColumn = RelationalNameConventions.ResourceKeyIdColumnName;

        if (!table.Columns.Any(column => column.ColumnName == resourceKeyIdColumn))
        {
            throw new InvalidOperationException(
                $"Cannot compile mapping set: abstract identity table '{table.Table}' for resource "
                    + $"'{Format(resource)}' has no '{resourceKeyIdColumn.Value}' column."
            );
        }

        return new NaturalKeyProbeTarget(
            resource,
            NaturalKeyProbeTargetKind.Abstract,
            table.Table,
            keys,
            RelationalNameConventions.DocumentIdColumnName,
            resourceKeyIdColumn
        );
    }

    /// <summary>
    /// Returns the table's columns whose source path is the identity path, throwing when there are none.
    /// </summary>
    private static DbColumnModel[] GetPathColumns(
        DbTableModel table,
        JsonPathExpression identityPath,
        QualifiedResourceName resource
    )
    {
        DbColumnModel[] columns =
        [
            .. table.Columns.Where(column => column.SourceJsonPath?.Canonical == identityPath.Canonical),
        ];

        if (columns.Length == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile mapping set: identity path '{identityPath.Canonical}' on resource "
                    + $"'{Format(resource)}' did not map to a column on table '{table.Table}'."
            );
        }

        return columns;
    }

    /// <summary>
    /// Builds the binding for a value column from its column kind.
    /// </summary>
    private static NaturalKeyProbeKeyBinding BuildValueBinding(
        DbColumnModel column,
        QualifiedResourceName resource
    )
    {
        return column switch
        {
            { Kind: ColumnKind.DescriptorFk, TargetResource: { } descriptorResource } =>
                new NaturalKeyProbeKeyBinding.Descriptor(descriptorResource),
            { Kind: ColumnKind.Scalar, ScalarType: { } scalarType } => new NaturalKeyProbeKeyBinding.Scalar(
                scalarType
            ),
            _ => throw new InvalidOperationException(
                $"Cannot compile mapping set: identity column '{column.ColumnName.Value}' on resource "
                    + $"'{Format(resource)}' has kind '{column.Kind}', which cannot bind a natural-key value."
            ),
        };
    }

    private static void RequireKeys(
        QualifiedResourceName resource,
        IReadOnlyList<NaturalKeyProbeKeyColumn> keys,
        string probeKind
    )
    {
        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile mapping set: {probeKind} probe for resource '{Format(resource)}' has no keys."
            );
        }
    }

    private static string Format(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";
}
