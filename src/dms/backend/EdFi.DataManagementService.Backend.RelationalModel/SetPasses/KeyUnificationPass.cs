// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Applies ApiSchema equality constraints as table-local key-unification classes.
/// </summary>
public sealed class KeyUnificationPass : IRelationalModelSetPass
{
    private static readonly IComparer<IReadOnlyList<string>> _connectedComponentOrderingComparer = Comparer<
        IReadOnlyList<string>
    >.Create(CompareConnectedComponents);

    /// <summary>
    /// Executes key unification for all concrete resources and extensions.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseResourcesByName = BuildBaseResourceLookup(
            context.ConcreteResourcesInNameOrder,
            static (index, model) => new BaseResourceEntry(index, model)
        );
        var resourceIndexByKey = context
            .ConcreteResourcesInNameOrder.Select(
                (resource, index) => new { resource.ResourceKey.Resource, Index = index }
            )
            .ToDictionary(entry => entry.Resource, entry => entry.Index);
        Dictionary<int, List<EqualityConstraintInput>> constraintsByResourceIndex = [];

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var equalityConstraints = ReadEqualityConstraints(resourceContext.ResourceSchema);

            if (equalityConstraints.Count == 0)
            {
                continue;
            }

            var concreteResourceIndex = ResolveConcreteResourceIndex(
                resourceContext,
                resource,
                baseResourcesByName,
                resourceIndexByKey
            );

            if (!constraintsByResourceIndex.TryGetValue(concreteResourceIndex, out var combinedConstraints))
            {
                combinedConstraints = [];
                constraintsByResourceIndex.Add(concreteResourceIndex, combinedConstraints);
            }

            combinedConstraints.AddRange(equalityConstraints);
        }

        foreach (var entry in constraintsByResourceIndex.OrderBy(item => item.Key))
        {
            var concreteResource = context.ConcreteResourcesInNameOrder[entry.Key];
            var deduplicatedConstraints = DeduplicateUndirectedConstraints(entry.Value);
            var updatedModel = ApplyKeyUnification(
                concreteResource.RelationalModel,
                concreteResource.ResourceKey.Resource,
                deduplicatedConstraints
            );

            context.ConcreteResourcesInNameOrder[entry.Key] = concreteResource with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    /// <summary>
    /// Resolves the concrete-resource index for a schema context (including extension-to-base mapping).
    /// </summary>
    private static int ResolveConcreteResourceIndex(
        ConcreteResourceSchemaContext resourceContext,
        QualifiedResourceName resource,
        IReadOnlyDictionary<string, List<BaseResourceEntry>> baseResourcesByName,
        IReadOnlyDictionary<QualifiedResourceName, int> resourceIndexByKey
    )
    {
        if (IsResourceExtension(resourceContext))
        {
            var baseResource = ResolveBaseResourceForExtension(
                resourceContext.ResourceName,
                resource,
                baseResourcesByName,
                static entry => entry.Model.ResourceKey.Resource
            );

            return baseResource.Index;
        }

        if (resourceIndexByKey.TryGetValue(resource, out var index))
        {
            return index;
        }

        throw new InvalidOperationException(
            $"Concrete resource '{FormatResource(resource)}' was not found for key unification."
        );
    }

    /// <summary>
    /// Parses equality constraints from a resource schema.
    /// </summary>
    private static IReadOnlyList<EqualityConstraintInput> ReadEqualityConstraints(JsonObject resourceSchema)
    {
        if (!resourceSchema.TryGetPropertyValue("equalityConstraints", out var equalityConstraintsNode))
        {
            return Array.Empty<EqualityConstraintInput>();
        }

        if (equalityConstraintsNode is null)
        {
            return Array.Empty<EqualityConstraintInput>();
        }

        if (equalityConstraintsNode is not JsonArray equalityConstraintsArray)
        {
            throw new InvalidOperationException(
                "Expected equalityConstraints to be an array, invalid ApiSchema."
            );
        }

        List<EqualityConstraintInput> constraints = new(equalityConstraintsArray.Count);

        foreach (var constraintNode in equalityConstraintsArray)
        {
            if (constraintNode is null)
            {
                throw new InvalidOperationException(
                    "Expected equalityConstraints entries to be non-null, invalid ApiSchema."
                );
            }

            if (constraintNode is not JsonObject constraintObject)
            {
                throw new InvalidOperationException(
                    "Expected equalityConstraints entries to be objects, invalid ApiSchema."
                );
            }

            var sourcePath = JsonPathExpressionCompiler.Compile(
                RequireString(constraintObject, "sourceJsonPath")
            );
            var targetPath = JsonPathExpressionCompiler.Compile(
                RequireString(constraintObject, "targetJsonPath")
            );

            constraints.Add(new EqualityConstraintInput(sourcePath, targetPath));
        }

        return constraints;
    }

    /// <summary>
    /// De-duplicates merged constraints by undirected canonical endpoint-path key.
    /// </summary>
    private static IReadOnlyList<EqualityConstraintInput> DeduplicateUndirectedConstraints(
        IReadOnlyList<EqualityConstraintInput> constraints
    )
    {
        if (constraints.Count < 2)
        {
            return constraints;
        }

        HashSet<UndirectedConstraintKey> seenConstraints = [];
        List<EqualityConstraintInput> deduplicatedConstraints = new(constraints.Count);

        foreach (var constraint in constraints)
        {
            var key = BuildUndirectedConstraintKey(
                constraint.SourcePath.Canonical,
                constraint.TargetPath.Canonical
            );

            if (!seenConstraints.Add(key))
            {
                continue;
            }

            deduplicatedConstraints.Add(constraint);
        }

        return deduplicatedConstraints;
    }

    /// <summary>
    /// Builds a deterministic undirected key for one equality constraint.
    /// </summary>
    private static UndirectedConstraintKey BuildUndirectedConstraintKey(
        string sourcePathCanonical,
        string targetPathCanonical
    )
    {
        return string.CompareOrdinal(sourcePathCanonical, targetPathCanonical) <= 0
            ? new UndirectedConstraintKey(sourcePathCanonical, targetPathCanonical)
            : new UndirectedConstraintKey(targetPathCanonical, sourcePathCanonical);
    }

    /// <summary>
    /// Applies key unification to one concrete resource model.
    /// </summary>
    private static RelationalResourceModel ApplyKeyUnification(
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource,
        IReadOnlyList<EqualityConstraintInput> constraints
    )
    {
        if (constraints.Count == 0)
        {
            return resourceModel;
        }

        var tables = resourceModel.TablesInDependencyOrder.ToArray();
        var bindingsByPath = BuildSourcePathBindingsByTable(tables);
        Dictionary<int, List<(TableBoundColumn Left, TableBoundColumn Right)>> appliedEdgesByTable = [];
        List<AppliedConstraintCandidate> appliedConstraints = [];
        List<KeyUnificationRedundantConstraint> redundantConstraints = [];
        List<KeyUnificationIgnoredConstraint> ignoredConstraints = [];

        foreach (var constraint in constraints)
        {
            var leftCandidates = ResolveEndpointCandidates(bindingsByPath, constraint.SourcePath, resource);
            var rightCandidates = ResolveEndpointCandidates(bindingsByPath, constraint.TargetPath, resource);

            var leftTableIndex = ResolveEndpointTableIndex(constraint.SourcePath, leftCandidates, resource);
            var rightTableIndex = ResolveEndpointTableIndex(constraint.TargetPath, rightCandidates, resource);

            var left = leftCandidates[0];
            var right = rightCandidates[0];

            ValidateSupportedEndpointKind(left, constraint.SourcePath, resource);
            ValidateSupportedEndpointKind(right, constraint.TargetPath, resource);
            var orderedEndpoints = CanonicalizeConstraintEndpoints(constraint, left, right);

            if (leftTableIndex != rightTableIndex)
            {
                ignoredConstraints.Add(
                    new KeyUnificationIgnoredConstraint(
                        orderedEndpoints.EndpointAPath,
                        orderedEndpoints.EndpointBPath,
                        KeyUnificationIgnoredReason.CrossTable,
                        orderedEndpoints.EndpointABinding,
                        orderedEndpoints.EndpointBBinding
                    )
                );
                continue;
            }

            if (leftCandidates.Length != 1)
            {
                ThrowAmbiguousEndpointBinding(constraint.SourcePath, leftCandidates, resource);
            }

            if (rightCandidates.Length != 1)
            {
                ThrowAmbiguousEndpointBinding(constraint.TargetPath, rightCandidates, resource);
            }

            if (left.Column.ColumnName.Equals(right.Column.ColumnName))
            {
                redundantConstraints.Add(
                    new KeyUnificationRedundantConstraint(
                        orderedEndpoints.EndpointAPath,
                        orderedEndpoints.EndpointBPath,
                        orderedEndpoints.EndpointABinding
                    )
                );
                continue;
            }

            if (!appliedEdgesByTable.TryGetValue(left.TableIndex, out var edges))
            {
                edges = [];
                appliedEdgesByTable[left.TableIndex] = edges;
            }

            edges.Add((left, right));
            appliedConstraints.Add(
                new AppliedConstraintCandidate(
                    orderedEndpoints.EndpointAPath,
                    orderedEndpoints.EndpointBPath,
                    left.TableIndex,
                    left.Table.Table,
                    orderedEndpoints.EndpointABinding.Column,
                    orderedEndpoints.EndpointBBinding.Column
                )
            );
        }

        var changed = false;

        foreach (var tableEntry in appliedEdgesByTable.OrderBy(item => item.Key))
        {
            var table = tables[tableEntry.Key];
            var updatedTable = ApplyKeyUnificationToTable(
                table,
                resource,
                resourceModel.DocumentReferenceBindings,
                tableEntry.Value
            );

            if (ReferenceEquals(updatedTable, table))
            {
                continue;
            }

            tables[tableEntry.Key] = updatedTable;
            changed = true;
        }

        var diagnostics = BuildEqualityConstraintDiagnostics(
            FinalizeAppliedConstraints(appliedConstraints, tables, resource),
            redundantConstraints,
            ignoredConstraints
        );
        var updatedModel = resourceModel with { KeyUnificationEqualityConstraints = diagnostics };

        if (!changed)
        {
            return updatedModel;
        }

        var updatedRoot = tables.Single(table => table.JsonScope.Equals(updatedModel.Root.JsonScope));

        return updatedModel with
        {
            Root = updatedRoot,
            TablesInDependencyOrder = tables,
        };
    }

    /// <summary>
    /// Builds a lookup from source JSON path to all bound columns.
    /// </summary>
    private static IReadOnlyDictionary<string, List<TableBoundColumn>> BuildSourcePathBindingsByTable(
        IReadOnlyList<DbTableModel> tables
    )
    {
        Dictionary<string, List<TableBoundColumn>> lookup = new(StringComparer.Ordinal);

        for (var tableIndex = 0; tableIndex < tables.Count; tableIndex++)
        {
            var table = tables[tableIndex];

            foreach (var column in table.Columns)
            {
                if (column.SourceJsonPath is not { } sourcePath)
                {
                    continue;
                }

                if (!lookup.TryGetValue(sourcePath.Canonical, out var boundColumns))
                {
                    boundColumns = [];
                    lookup[sourcePath.Canonical] = boundColumns;
                }

                boundColumns.Add(new TableBoundColumn(tableIndex, table, column));
            }
        }

        return lookup;
    }

    /// <summary>
    /// Resolves one source-path endpoint to all distinct physical table/column bindings.
    /// </summary>
    private static TableBoundColumn[] ResolveEndpointCandidates(
        IReadOnlyDictionary<string, List<TableBoundColumn>> bindingsByPath,
        JsonPathExpression endpointPath,
        QualifiedResourceName resource
    )
    {
        if (!bindingsByPath.TryGetValue(endpointPath.Canonical, out var rawCandidates))
        {
            throw new InvalidOperationException(
                $"Equality constraint endpoint '{endpointPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' was not bound to any column."
            );
        }

        return rawCandidates
            .DistinctBy(candidate =>
                (
                    candidate.TableIndex,
                    candidate.Table.Table,
                    candidate.Column.ColumnName,
                    candidate.Table.JsonScope.Canonical
                )
            )
            .OrderBy(candidate => candidate.Table.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Table.Table.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Table.JsonScope.Canonical, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Column.ColumnName.Value, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Resolves an equality-constraint endpoint to a unique table index, failing fast when bindings span tables.
    /// </summary>
    private static int ResolveEndpointTableIndex(
        JsonPathExpression endpointPath,
        IReadOnlyList<TableBoundColumn> candidates,
        QualifiedResourceName resource
    )
    {
        var distinctTableIndexes = candidates.Select(candidate => candidate.TableIndex).Distinct().ToArray();

        if (distinctTableIndexes.Length == 1)
        {
            return distinctTableIndexes[0];
        }

        return ThrowAmbiguousEndpointBinding(endpointPath, candidates, resource);
    }

    /// <summary>
    /// Throws an exception that reports all distinct bindings for an ambiguous endpoint.
    /// </summary>
    [DoesNotReturn]
    private static int ThrowAmbiguousEndpointBinding(
        JsonPathExpression endpointPath,
        IReadOnlyList<TableBoundColumn> distinctCandidates,
        QualifiedResourceName resource
    )
    {
        var details = string.Join(
            ", ",
            distinctCandidates.Select(candidate =>
                $"{candidate.Table.Table.Schema.Value}.{candidate.Table.Table.Name}"
                + $"[{candidate.Table.JsonScope.Canonical}].{candidate.Column.ColumnName.Value}"
            )
        );

        throw new InvalidOperationException(
            $"Equality constraint endpoint '{endpointPath.Canonical}' on resource "
                + $"'{FormatResource(resource)}' resolved to multiple distinct bindings: {details}."
        );
    }

    /// <summary>
    /// Ensures key unification only accepts scalar/descriptor endpoint kinds.
    /// </summary>
    private static void ValidateSupportedEndpointKind(
        TableBoundColumn endpoint,
        JsonPathExpression endpointPath,
        QualifiedResourceName resource
    )
    {
        if (endpoint.Column.Kind is ColumnKind.Scalar or ColumnKind.DescriptorFk)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Equality constraint endpoint '{endpointPath.Canonical}' on resource "
                + $"'{FormatResource(resource)}' resolved to unsupported column kind "
                + $"'{endpoint.Column.Kind}' at '{endpoint.Table.Table}.{endpoint.Column.ColumnName.Value}'."
        );
    }

    /// <summary>
    /// Canonicalizes endpoint ordering for deterministic diagnostics.
    /// </summary>
    private static OrderedConstraintEndpoints CanonicalizeConstraintEndpoints(
        EqualityConstraintInput constraint,
        TableBoundColumn sourceEndpoint,
        TableBoundColumn targetEndpoint
    )
    {
        if (string.CompareOrdinal(constraint.SourcePath.Canonical, constraint.TargetPath.Canonical) <= 0)
        {
            return new OrderedConstraintEndpoints(
                constraint.SourcePath,
                ToEndpointBinding(sourceEndpoint),
                constraint.TargetPath,
                ToEndpointBinding(targetEndpoint)
            );
        }

        return new OrderedConstraintEndpoints(
            constraint.TargetPath,
            ToEndpointBinding(targetEndpoint),
            constraint.SourcePath,
            ToEndpointBinding(sourceEndpoint)
        );
    }

    /// <summary>
    /// Converts a bound endpoint to diagnostics binding shape.
    /// </summary>
    private static KeyUnificationEndpointBinding ToEndpointBinding(TableBoundColumn endpoint)
    {
        return new KeyUnificationEndpointBinding(endpoint.Table.Table, endpoint.Column.ColumnName);
    }

    /// <summary>
    /// Finalizes applied constraints by resolving each endpoint binding to its class canonical column.
    /// </summary>
    private static IReadOnlyList<KeyUnificationAppliedConstraint> FinalizeAppliedConstraints(
        IReadOnlyList<AppliedConstraintCandidate> appliedConstraints,
        IReadOnlyList<DbTableModel> tables,
        QualifiedResourceName resource
    )
    {
        if (appliedConstraints.Count == 0)
        {
            return [];
        }

        Dictionary<int, IReadOnlyDictionary<DbColumnName, DbColumnName>> canonicalByMemberByTable = [];

        foreach (
            var tableIndex in appliedConstraints
                .Select(constraint => constraint.TableIndex)
                .Distinct()
                .OrderBy(index => index)
        )
        {
            canonicalByMemberByTable[tableIndex] = BuildCanonicalByMemberLookup(tables[tableIndex], resource);
        }

        List<KeyUnificationAppliedConstraint> finalized = new(appliedConstraints.Count);

        foreach (var appliedConstraint in appliedConstraints)
        {
            if (
                !canonicalByMemberByTable.TryGetValue(appliedConstraint.TableIndex, out var canonicalByMember)
            )
            {
                throw new InvalidOperationException(
                    $"Key-unification diagnostics on resource '{FormatResource(resource)}' could not resolve "
                        + $"table index '{appliedConstraint.TableIndex}'."
                );
            }

            if (!canonicalByMember.TryGetValue(appliedConstraint.EndpointAColumn, out var canonicalColumn))
            {
                throw new InvalidOperationException(
                    $"Key-unification diagnostics on resource '{FormatResource(resource)}' could not map "
                        + $"endpoint column '{appliedConstraint.EndpointAColumn.Value}' to a canonical column "
                        + $"on table '{appliedConstraint.Table}'."
                );
            }

            if (!canonicalByMember.TryGetValue(appliedConstraint.EndpointBColumn, out var endpointBCanonical))
            {
                throw new InvalidOperationException(
                    $"Key-unification diagnostics on resource '{FormatResource(resource)}' could not map "
                        + $"endpoint column '{appliedConstraint.EndpointBColumn.Value}' to a canonical column "
                        + $"on table '{appliedConstraint.Table}'."
                );
            }

            if (!canonicalColumn.Equals(endpointBCanonical))
            {
                throw new InvalidOperationException(
                    $"Key-unification diagnostics on resource '{FormatResource(resource)}' resolved endpoint "
                        + $"columns '{appliedConstraint.EndpointAColumn.Value}' and "
                        + $"'{appliedConstraint.EndpointBColumn.Value}' to different canonical columns "
                        + $"('{canonicalColumn.Value}', '{endpointBCanonical.Value}')."
                );
            }

            finalized.Add(
                new KeyUnificationAppliedConstraint(
                    appliedConstraint.EndpointAPath,
                    appliedConstraint.EndpointBPath,
                    appliedConstraint.Table,
                    appliedConstraint.EndpointAColumn,
                    appliedConstraint.EndpointBColumn,
                    canonicalColumn
                )
            );
        }

        return finalized;
    }

    /// <summary>
    /// Builds a lookup from unification member column name to class canonical column.
    /// </summary>
    private static IReadOnlyDictionary<DbColumnName, DbColumnName> BuildCanonicalByMemberLookup(
        DbTableModel table,
        QualifiedResourceName resource
    )
    {
        Dictionary<DbColumnName, DbColumnName> lookup = [];

        foreach (var keyUnificationClass in table.KeyUnificationClasses)
        {
            foreach (var memberPathColumn in keyUnificationClass.MemberPathColumns)
            {
                if (!lookup.TryAdd(memberPathColumn, keyUnificationClass.CanonicalColumn))
                {
                    throw new InvalidOperationException(
                        $"Key-unification diagnostics on resource '{FormatResource(resource)}' table "
                            + $"'{table.Table}' encountered duplicate member column "
                            + $"'{memberPathColumn.Value}'."
                    );
                }
            }
        }

        return lookup;
    }

    /// <summary>
    /// Produces deterministic diagnostics payloads from classified constraints.
    /// </summary>
    private static KeyUnificationEqualityConstraintDiagnostics BuildEqualityConstraintDiagnostics(
        IReadOnlyList<KeyUnificationAppliedConstraint> appliedConstraints,
        IReadOnlyList<KeyUnificationRedundantConstraint> redundantConstraints,
        IReadOnlyList<KeyUnificationIgnoredConstraint> ignoredConstraints
    )
    {
        var applied = appliedConstraints
            .OrderBy(constraint => constraint.EndpointAPath.Canonical, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointBPath.Canonical, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.Table.Name, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointAColumn.Value, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointBColumn.Value, StringComparer.Ordinal)
            .ToArray();
        var redundant = redundantConstraints
            .OrderBy(constraint => constraint.EndpointAPath.Canonical, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointBPath.Canonical, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.Binding.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.Binding.Table.Name, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.Binding.Column.Value, StringComparer.Ordinal)
            .ToArray();
        var ignored = ignoredConstraints
            .OrderBy(constraint => constraint.EndpointAPath.Canonical, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointBPath.Canonical, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.Reason.ToString(), StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointABinding.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointABinding.Table.Name, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointABinding.Column.Value, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointBBinding.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointBBinding.Table.Name, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.EndpointBBinding.Column.Value, StringComparer.Ordinal)
            .ToArray();
        var ignoredByReason = ignored
            .GroupBy(constraint => constraint.Reason)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group => new KeyUnificationIgnoredByReasonEntry(group.Key, group.Count()))
            .ToArray();

        return new KeyUnificationEqualityConstraintDiagnostics(applied, redundant, ignored, ignoredByReason);
    }

    /// <summary>
    /// Applies key-unification class derivation to a single table.
    /// </summary>
    private static DbTableModel ApplyKeyUnificationToTable(
        DbTableModel table,
        QualifiedResourceName resource,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings,
        IReadOnlyList<(TableBoundColumn Left, TableBoundColumn Right)> appliedEdges
    )
    {
        var componentColumnNames = BuildConnectedComponents(appliedEdges);

        if (componentColumnNames.Count == 0)
        {
            return table;
        }

        var localReferenceBindings = documentReferenceBindings
            .Where(binding => binding.Table.Equals(table.Table))
            .ToArray();
        var referenceBindingByIdentityPath = BuildReferenceIdentityBindings(localReferenceBindings, resource);
        List<DbColumnModel> updatedColumns = new(table.Columns);
        Dictionary<string, int> columnIndexByName = updatedColumns
            .Select((column, index) => new { column.ColumnName.Value, Index = index })
            .ToDictionary(entry => entry.Value, entry => entry.Index, StringComparer.Ordinal);
        HashSet<string> existingColumnNames = updatedColumns
            .Select(column => column.ColumnName.Value)
            .ToHashSet(StringComparer.Ordinal);
        List<KeyUnificationClass> keyUnificationClasses = [];
        HashSet<DbColumnName> syntheticPresenceColumns = [];

        foreach (
            var component in componentColumnNames.OrderBy(group => group, _connectedComponentOrderingComparer)
        )
        {
            var memberColumns = component
                .Select(columnName =>
                {
                    if (!columnIndexByName.TryGetValue(columnName, out var columnIndex))
                    {
                        throw new InvalidOperationException(
                            $"Key-unification member column '{columnName}' was not found on table "
                                + $"'{table.Table}' for resource '{FormatResource(resource)}'."
                        );
                    }

                    return updatedColumns[columnIndex];
                })
                .OrderBy(
                    column => GetRequiredSourcePath(column, resource, table).Canonical,
                    StringComparer.Ordinal
                )
                .ThenBy(column => column.ColumnName.Value, StringComparer.Ordinal)
                .ToArray();

            if (memberColumns.Length < 2)
            {
                continue;
            }

            ValidateUnificationMembers(memberColumns, resource, table);

            var baseName = BuildMemberBaseToken(
                memberColumns[0],
                table,
                referenceBindingByIdentityPath,
                resource
            );
            var canonicalColumnName = AllocateCanonicalColumnName(
                baseName,
                memberColumns,
                existingColumnNames,
                resource,
                table
            );
            var firstMember = memberColumns[0];
            var canonicalColumn = new DbColumnModel(
                canonicalColumnName,
                firstMember.Kind,
                firstMember.ScalarType,
                memberColumns.All(column => column.IsNullable),
                SourceJsonPath: null,
                firstMember.TargetResource
            );

            updatedColumns.Add(canonicalColumn);
            columnIndexByName[canonicalColumnName.Value] = updatedColumns.Count - 1;

            foreach (var memberColumn in memberColumns)
            {
                var sourcePath = GetRequiredSourcePath(memberColumn, resource, table);
                var presenceResolution = ResolvePresenceColumn(
                    memberColumn,
                    sourcePath,
                    referenceBindingByIdentityPath,
                    existingColumnNames,
                    updatedColumns,
                    columnIndexByName
                );

                if (presenceResolution.SyntheticPresenceColumn is { } syntheticPresenceColumn)
                {
                    syntheticPresenceColumns.Add(syntheticPresenceColumn);
                }

                var memberColumnIndex = columnIndexByName[memberColumn.ColumnName.Value];
                var updatedMemberColumn = updatedColumns[memberColumnIndex] with
                {
                    Storage = new ColumnStorage.UnifiedAlias(
                        canonicalColumnName,
                        presenceResolution.PresenceColumn
                    ),
                };

                updatedColumns[memberColumnIndex] = updatedMemberColumn;
            }

            keyUnificationClasses.Add(
                new KeyUnificationClass(
                    canonicalColumnName,
                    memberColumns.Select(column => column.ColumnName).ToArray()
                )
            );
        }

        if (keyUnificationClasses.Count == 0)
        {
            return table;
        }

        var constraintsWithPresenceHardening = AppendNullOrTrueConstraints(
            table.Constraints,
            table.Table,
            syntheticPresenceColumns
        );

        return table with
        {
            Columns = updatedColumns.ToArray(),
            Constraints = constraintsWithPresenceHardening,
            KeyUnificationClasses = keyUnificationClasses
                .OrderBy(@class => @class.CanonicalColumn.Value, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    /// <summary>
    /// Builds connected components for table-local equality edges.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> BuildConnectedComponents(
        IReadOnlyList<(TableBoundColumn Left, TableBoundColumn Right)> edges
    )
    {
        Dictionary<string, string> parent = new(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            var left = edge.Left.Column.ColumnName.Value;
            var right = edge.Right.Column.ColumnName.Value;

            EnsureSet(parent, left);
            EnsureSet(parent, right);
            Union(parent, left, right);
        }

        return parent
            .Keys.GroupBy(columnName => Find(parent, columnName), StringComparer.Ordinal)
            .Select(group =>
                (IReadOnlyList<string>)
                    group.OrderBy(columnName => columnName, StringComparer.Ordinal).ToArray()
            )
            .ToArray();
    }

    /// <summary>
    /// Deterministically orders components by first element, then size, then full sequence.
    /// </summary>
    private static int CompareConnectedComponents(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count == 0 || right.Count == 0)
        {
            return left.Count.CompareTo(right.Count);
        }

        var firstMemberComparison = string.CompareOrdinal(left[0], right[0]);

        if (firstMemberComparison != 0)
        {
            return firstMemberComparison;
        }

        var lengthComparison = left.Count.CompareTo(right.Count);

        if (lengthComparison != 0)
        {
            return lengthComparison;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var memberComparison = string.CompareOrdinal(left[index], right[index]);

            if (memberComparison != 0)
            {
                return memberComparison;
            }
        }

        return 0;
    }

    /// <summary>
    /// Adds a set entry when a node is first encountered.
    /// </summary>
    private static void EnsureSet(IDictionary<string, string> parent, string key)
    {
        if (!parent.ContainsKey(key))
        {
            parent[key] = key;
        }
    }

    /// <summary>
    /// Finds the representative for a disjoint-set node.
    /// </summary>
    private static string Find(IDictionary<string, string> parent, string key)
    {
        var current = parent[key];

        while (!string.Equals(current, parent[current], StringComparison.Ordinal))
        {
            current = parent[current];
        }

        var root = current;
        current = key;

        while (!string.Equals(current, parent[current], StringComparison.Ordinal))
        {
            var next = parent[current];
            parent[current] = root;
            current = next;
        }

        return root;
    }

    /// <summary>
    /// Unions two disjoint-set nodes.
    /// </summary>
    private static void Union(IDictionary<string, string> parent, string left, string right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);

        if (string.Equals(leftRoot, rightRoot, StringComparison.Ordinal))
        {
            return;
        }

        var winner = string.CompareOrdinal(leftRoot, rightRoot) <= 0 ? leftRoot : rightRoot;
        var loser = string.Equals(winner, leftRoot, StringComparison.Ordinal) ? rightRoot : leftRoot;
        parent[loser] = winner;
    }

    /// <summary>
    /// Validates that all members in a class have compatible kinds and types.
    /// </summary>
    private static void ValidateUnificationMembers(
        IReadOnlyList<DbColumnModel> members,
        QualifiedResourceName resource,
        DbTableModel table
    )
    {
        var first = members[0];

        if (first.Kind is not ColumnKind.Scalar and not ColumnKind.DescriptorFk)
        {
            throw new InvalidOperationException(
                $"Key unification only supports scalar/descriptor columns, but '{first.ColumnName.Value}' "
                    + $"on resource '{FormatResource(resource)}' table '{table.Table}' has kind "
                    + $"'{first.Kind}'."
            );
        }

        var unsupportedMember = members
            .Skip(1)
            .FirstOrDefault(member => member.Kind is not ColumnKind.Scalar and not ColumnKind.DescriptorFk);

        if (unsupportedMember is not null)
        {
            throw new InvalidOperationException(
                $"Key unification only supports scalar/descriptor columns, but '{unsupportedMember.ColumnName.Value}' "
                    + $"on resource '{FormatResource(resource)}' table '{table.Table}' has kind "
                    + $"'{unsupportedMember.Kind}'."
            );
        }

        var mixedKindMember = members.Skip(1).FirstOrDefault(member => member.Kind != first.Kind);

        if (mixedKindMember is not null)
        {
            throw new InvalidOperationException(
                $"Key unification class on resource '{FormatResource(resource)}' table '{table.Table}' "
                    + $"cannot mix scalar and descriptor members: '{mixedKindMember.ColumnName.Value}' has kind "
                    + $"'{mixedKindMember.Kind}' but '{first.ColumnName.Value}' has kind '{first.Kind}'."
            );
        }

        if (first.Kind == ColumnKind.Scalar)
        {
            var scalarTypeMismatchMember = members
                .Skip(1)
                .FirstOrDefault(member => !Equals(member.ScalarType, first.ScalarType));

            if (scalarTypeMismatchMember is not null)
            {
                throw new InvalidOperationException(
                    $"Key unification scalar type mismatch on resource '{FormatResource(resource)}' table "
                        + $"'{table.Table}': '{scalarTypeMismatchMember.ColumnName.Value}' does not match "
                        + $"'{first.ColumnName.Value}'."
                );
            }

            return;
        }

        var missingDescriptorTargetMember = members.FirstOrDefault(member => member.TargetResource is null);

        if (missingDescriptorTargetMember is not null)
        {
            throw new InvalidOperationException(
                $"Key unification descriptor target resource is required on resource "
                    + $"'{FormatResource(resource)}' table '{table.Table}' for column "
                    + $"'{missingDescriptorTargetMember.ColumnName.Value}'."
            );
        }

        var descriptorTargetMismatchMember = members
            .Skip(1)
            .FirstOrDefault(member => !Equals(member.TargetResource, first.TargetResource));

        if (descriptorTargetMismatchMember is not null)
        {
            throw new InvalidOperationException(
                $"Key unification descriptor target mismatch on resource '{FormatResource(resource)}' "
                    + $"table '{table.Table}': '{descriptorTargetMismatchMember.ColumnName.Value}' does not match "
                    + $"'{first.ColumnName.Value}'."
            );
        }
    }

    /// <summary>
    /// Derives one member base token using JsonPath semantics, independent of physical column names.
    /// </summary>
    private static string BuildMemberBaseToken(
        DbColumnModel member,
        DbTableModel table,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingByIdentityPath,
        QualifiedResourceName resource
    )
    {
        var sourcePath = GetRequiredSourcePath(member, resource, table);
        var prefixSegments = referenceBindingByIdentityPath.TryGetValue(sourcePath.Canonical, out var binding)
            ? binding.ReferenceObjectPath.Segments
            : table.JsonScope.Segments;

        if (!IsPrefixOf(prefixSegments, sourcePath.Segments))
        {
            throw new InvalidOperationException(
                $"Key-unification member path '{sourcePath.Canonical}' does not match expected prefix on "
                    + $"resource '{FormatResource(resource)}' table '{table.Table}'."
            );
        }

        var relativeSegments = sourcePath.Segments.Skip(prefixSegments.Count).ToArray();

        if (relativeSegments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
        {
            throw new InvalidOperationException(
                $"Key-unification member path '{sourcePath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' contains unsupported wildcard segments."
            );
        }

        var relativeProperties = relativeSegments.OfType<JsonPathSegment.Property>().ToArray();

        if (relativeProperties.Length > 0)
        {
            return string.Concat(
                relativeProperties.Select(property => RelationalNameConventions.ToPascalCase(property.Name))
            );
        }

        if (relativeSegments.Length != 0)
        {
            throw new InvalidOperationException(
                $"Key-unification member path '{sourcePath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' table '{table.Table}' contains unsupported "
                    + "non-property segments after prefix stripping."
            );
        }

        for (var index = prefixSegments.Count - 1; index >= 0; index--)
        {
            if (prefixSegments[index] is not JsonPathSegment.Property property)
            {
                continue;
            }

            var singular = RelationalNameConventions.SingularizeCollectionSegment(property.Name);
            return RelationalNameConventions.ToPascalCase(singular);
        }

        throw new InvalidOperationException(
            $"Unable to derive key-unification base token for path '{sourcePath.Canonical}' on resource "
                + $"'{FormatResource(resource)}'."
        );
    }

    /// <summary>
    /// Allocates a unique canonical column name for a unification class.
    /// </summary>
    private static DbColumnName AllocateCanonicalColumnName(
        string baseName,
        IReadOnlyList<DbColumnModel> members,
        ISet<string> existingColumnNames,
        QualifiedResourceName resource,
        DbTableModel table
    )
    {
        var firstMember = members[0];
        var suffix = firstMember.Kind == ColumnKind.DescriptorFk ? "_Unified_DescriptorId" : "_Unified";
        var initialName = $"{baseName}{suffix}";

        if (existingColumnNames.Add(initialName))
        {
            return new DbColumnName(initialName);
        }

        var signature = string.Join(
            "\n",
            members
                .Select(member => GetRequiredSourcePath(member, resource, table).Canonical)
                .OrderBy(path => path, StringComparer.Ordinal)
        );
        var hash = ComputeHash8($"key-unification-canonical-name:v1\n{signature}");
        var disambiguatedName = $"{baseName}_U{hash}{suffix}";

        if (existingColumnNames.Add(disambiguatedName))
        {
            return new DbColumnName(disambiguatedName);
        }

        var maxSuffixIndex = existingColumnNames.Count + 2;

        // Bound attempts: since candidates are unique per suffix and the set is finite, a free name must exist within Count + 1 tries.
        for (var suffixIndex = 2; suffixIndex <= maxSuffixIndex; suffixIndex++)
        {
            var fallbackName =
                firstMember.Kind == ColumnKind.DescriptorFk
                    ? $"{baseName}_U{hash}_{suffixIndex}_Unified_DescriptorId"
                    : $"{baseName}_U{hash}_{suffixIndex}_Unified";

            if (!existingColumnNames.Add(fallbackName))
            {
                continue;
            }

            return new DbColumnName(fallbackName);
        }

        throw new InvalidOperationException(
            $"Could not allocate unique canonical column name for base '{baseName}' after {maxSuffixIndex - 1} attempts."
        );
    }

    /// <summary>
    /// Resolves reference or synthetic presence gating for one member alias.
    /// </summary>
    private static PresenceResolution ResolvePresenceColumn(
        DbColumnModel memberColumn,
        JsonPathExpression sourcePath,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingByIdentityPath,
        ISet<string> existingColumnNames,
        ICollection<DbColumnModel> updatedColumns,
        IDictionary<string, int> columnIndexByName
    )
    {
        if (referenceBindingByIdentityPath.TryGetValue(sourcePath.Canonical, out var binding))
        {
            return new PresenceResolution(binding.FkColumn, null);
        }

        if (!memberColumn.IsNullable)
        {
            return new PresenceResolution(null, null);
        }

        var presenceColumnName = AllocatePresenceColumnName(
            memberColumn.ColumnName,
            sourcePath.Canonical,
            existingColumnNames
        );
        var presenceColumn = new DbColumnModel(
            presenceColumnName,
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Boolean),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null
        );

        updatedColumns.Add(presenceColumn);
        columnIndexByName[presenceColumnName.Value] = updatedColumns.Count - 1;
        return new PresenceResolution(presenceColumnName, presenceColumnName);
    }

    /// <summary>
    /// Allocates a deterministic synthetic presence-column name for an optional non-reference member.
    /// </summary>
    private static DbColumnName AllocatePresenceColumnName(
        DbColumnName memberColumnName,
        string sourcePathCanonical,
        ISet<string> existingColumnNames
    )
    {
        var initialName = $"{memberColumnName.Value}_Present";

        if (existingColumnNames.Add(initialName))
        {
            return new DbColumnName(initialName);
        }

        var hash = ComputeHash8($"key-unification-presence-name:v1\n{sourcePathCanonical}");
        var disambiguatedName = $"{memberColumnName.Value}_U{hash}_Present";

        if (existingColumnNames.Add(disambiguatedName))
        {
            return new DbColumnName(disambiguatedName);
        }

        var maxSuffixIndex = existingColumnNames.Count + 2;

        // Bound attempts: since candidates are unique per suffix and the set is finite, a free name must exist within Count + 1 tries.
        for (var suffixIndex = 2; suffixIndex <= maxSuffixIndex; suffixIndex++)
        {
            var fallbackName = $"{memberColumnName.Value}_U{hash}_{suffixIndex}_Present";

            if (!existingColumnNames.Add(fallbackName))
            {
                continue;
            }

            return new DbColumnName(fallbackName);
        }

        throw new InvalidOperationException(
            $"Could not allocate unique presence column name for '{memberColumnName.Value}' after {maxSuffixIndex - 1} attempts."
        );
    }

    /// <summary>
    /// Appends one <see cref="TableConstraint.NullOrTrue"/> per synthetic presence column.
    /// </summary>
    private static IReadOnlyList<TableConstraint> AppendNullOrTrueConstraints(
        IReadOnlyList<TableConstraint> constraints,
        DbTableName table,
        IReadOnlyCollection<DbColumnName> syntheticPresenceColumns
    )
    {
        if (syntheticPresenceColumns.Count == 0)
        {
            return constraints;
        }

        var existingIdentities = constraints
            .OfType<TableConstraint.NullOrTrue>()
            .Select(nullOrTrue => ConstraintIdentity.ForNullOrTrue(table, nullOrTrue.Column))
            .ToHashSet();
        List<TableConstraint> updatedConstraints = new(constraints);

        foreach (
            var syntheticPresenceColumn in syntheticPresenceColumns.OrderBy(
                column => column.Value,
                StringComparer.Ordinal
            )
        )
        {
            var identity = ConstraintIdentity.ForNullOrTrue(table, syntheticPresenceColumn);

            if (!existingIdentities.Add(identity))
            {
                continue;
            }

            updatedConstraints.Add(
                new TableConstraint.NullOrTrue(
                    ConstraintNaming.BuildNullOrTrueName(table, syntheticPresenceColumn),
                    syntheticPresenceColumn
                )
            );
        }

        return updatedConstraints.ToArray();
    }

    /// <summary>
    /// Returns a required source path for a key-unification member.
    /// </summary>
    private static JsonPathExpression GetRequiredSourcePath(
        DbColumnModel member,
        QualifiedResourceName resource,
        DbTableModel table
    )
    {
        if (member.SourceJsonPath is { } sourcePath)
        {
            return sourcePath;
        }

        throw new InvalidOperationException(
            $"Key-unification member '{member.ColumnName.Value}' on resource '{FormatResource(resource)}' "
                + $"table '{table.Table}' must have SourceJsonPath."
        );
    }

    /// <summary>
    /// Computes the first 8 hexadecimal characters of SHA-256 for deterministic naming.
    /// </summary>
    private static string ComputeHash8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    /// <summary>
    /// Base resource entry used when resolving extension schemas.
    /// </summary>
    private sealed record BaseResourceEntry(int Index, ConcreteResourceModel Model);

    /// <summary>
    /// Parsed equality-constraint endpoints.
    /// </summary>
    private sealed record EqualityConstraintInput(
        JsonPathExpression SourcePath,
        JsonPathExpression TargetPath
    );

    /// <summary>
    /// Canonical undirected key for equality-constraint de-duplication.
    /// </summary>
    private readonly record struct UndirectedConstraintKey(string EndpointAPath, string EndpointBPath);

    /// <summary>
    /// One source-path column binding with table context.
    /// </summary>
    private sealed record TableBoundColumn(int TableIndex, DbTableModel Table, DbColumnModel Column);

    /// <summary>
    /// Canonicalized endpoint ordering for one equality constraint.
    /// </summary>
    private sealed record OrderedConstraintEndpoints(
        JsonPathExpression EndpointAPath,
        KeyUnificationEndpointBinding EndpointABinding,
        JsonPathExpression EndpointBPath,
        KeyUnificationEndpointBinding EndpointBBinding
    );

    /// <summary>
    /// Provisional applied-constraint diagnostics captured before canonical-column resolution.
    /// </summary>
    private sealed record AppliedConstraintCandidate(
        JsonPathExpression EndpointAPath,
        JsonPathExpression EndpointBPath,
        int TableIndex,
        DbTableName Table,
        DbColumnName EndpointAColumn,
        DbColumnName EndpointBColumn
    );

    /// <summary>
    /// Presence resolution result for one member-path alias.
    /// </summary>
    private sealed record PresenceResolution(
        DbColumnName? PresenceColumn,
        DbColumnName? SyntheticPresenceColumn
    );
}
