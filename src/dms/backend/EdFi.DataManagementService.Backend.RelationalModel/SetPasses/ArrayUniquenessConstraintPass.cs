// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives array uniqueness constraints.
/// </summary>
public sealed class ArrayUniquenessConstraintPass : IRelationalModelSetPass
{
    /// <summary>
    /// Applies child-table unique constraints derived from <c>arrayUniquenessConstraints</c> for all concrete
    /// resources and resource extensions.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        SetPassHelpers.ExecuteContributingResourceMutationPass(
            context,
            "constraint derivation",
            static builderContext => builderContext.ArrayUniquenessConstraints.Count > 0,
            static (mutation, resourceModel, builderContext, resource) =>
                ApplyArrayUniquenessConstraintsForResource(
                    mutation,
                    resourceModel,
                    builderContext,
                    resource,
                    emitUniqueConstraints: true
                )
        );
    }

    /// <summary>
    /// Applies array uniqueness constraints for a single resource model, recording table mutations.
    /// </summary>
    internal static void ApplyArrayUniquenessConstraintsForResource(
        ResourceMutation mutation,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource,
        bool emitUniqueConstraints
    )
    {
        var tablesByScope = resourceModel
            .TablesInDependencyOrder.GroupBy(table => table.JsonScope.Canonical, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                    (IReadOnlyList<DbTableModel>)
                        group
                            .GroupBy(table => table.Table)
                            .Select(tableGroup => tableGroup.First())
                            .ToArray(),
                StringComparer.Ordinal
            );
        var tablesByName = resourceModel
            .TablesInDependencyOrder.GroupBy(table => table.Table)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DbTableModel>)group.ToArray());
        var referenceBindingsByIdentityPath = BuildReferenceIdentityBindings(
            resourceModel.DocumentReferenceBindings,
            resource
        );
        Dictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable = new();

        foreach (var constraint in builderContext.ArrayUniquenessConstraints)
        {
            ApplyArrayUniquenessConstraint(
                constraint,
                mutation,
                resource,
                tablesByScope,
                tablesByName,
                referenceBindingsByIdentityPath,
                columnsByTable,
                emitUniqueConstraints
            );
        }
    }

    /// <summary>
    /// Applies an array uniqueness constraint by resolving its paths to a child-table scope and creating a
    /// deterministic unique constraint (including nested constraints).
    /// </summary>
    private static void ApplyArrayUniquenessConstraint(
        ArrayUniquenessConstraintInput constraint,
        ResourceMutation mutation,
        QualifiedResourceName resource,
        IReadOnlyDictionary<string, IReadOnlyList<DbTableModel>> tablesByScope,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable,
        bool emitUniqueConstraints
    )
    {
        var resolvedPaths = constraint
            .Paths.Select(path => ResolveConstraintPath(constraint.BasePath, path))
            .ToArray();
        var pathsByScope = GroupPathsByArrayScope(resolvedPaths, resource);

        foreach (var scopeGroup in pathsByScope.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var constraintPaths = scopeGroup.Value;
            var scopePath = GetArrayScope(constraintPaths[0], resource);
            Exception? scopeFailure = null;

            if (
                TryResolveArrayUniquenessTableForScope(
                    scopePath.Canonical,
                    constraintPaths,
                    tablesByScope,
                    referenceBindingsByIdentityPath,
                    tablesByName,
                    columnsByTable,
                    resource,
                    out var table,
                    out var compiledIdentity,
                    out scopeFailure
                )
            )
            {
                AddArrayUniquenessConstraint(
                    mutation,
                    table,
                    compiledIdentity,
                    resource,
                    emitUniqueConstraints
                );
                continue;
            }

            Exception? alignedFailure = null;

            if (TryStripExtensionRootPrefix(scopePath, out var alignedScope))
            {
                // Extension schemas can declare array uniqueness under _ext.{project} even though the
                // owning table lives in the base scope (e.g., contacts $._ext.sample.addresses[*]
                // with paths $.periods[*].beginDate -> $.addresses[*].periods[*]).
                var alignedPaths = StripExtensionRootPrefix(constraintPaths, resource);

                if (
                    TryResolveArrayUniquenessTableForScope(
                        alignedScope.Canonical,
                        alignedPaths,
                        tablesByScope,
                        referenceBindingsByIdentityPath,
                        tablesByName,
                        columnsByTable,
                        resource,
                        out var alignedTable,
                        out var alignedIdentity,
                        out alignedFailure
                    )
                )
                {
                    AddArrayUniquenessConstraint(
                        mutation,
                        alignedTable,
                        alignedIdentity,
                        resource,
                        emitUniqueConstraints
                    );
                    continue;
                }
            }

            if (scopeFailure is not null && alignedFailure is not null)
            {
                throw CombineArrayUniquenessFailures([scopeFailure, alignedFailure])!;
            }

            if (scopeFailure is not null)
            {
                throw scopeFailure;
            }

            if (alignedFailure is not null)
            {
                throw alignedFailure;
            }

            throw new InvalidOperationException(
                $"arrayUniquenessConstraints scope '{scopeGroup.Key}' on resource "
                    + $"'{FormatResource(resource)}' did not map to a child table."
            );
        }

        foreach (var nested in constraint.NestedConstraints)
        {
            ApplyArrayUniquenessConstraint(
                nested,
                mutation,
                resource,
                tablesByScope,
                tablesByName,
                referenceBindingsByIdentityPath,
                columnsByTable,
                emitUniqueConstraints
            );
        }
    }

    /// <summary>
    /// Groups constraint paths by the canonical JSONPath of their owning array scope.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<JsonPathExpression>> GroupPathsByArrayScope(
        IReadOnlyList<JsonPathExpression> paths,
        QualifiedResourceName resource
    )
    {
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("arrayUniquenessConstraints must include at least one path.");
        }

        Dictionary<string, List<JsonPathExpression>> grouped = new(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var arrayScope = GetArrayScope(path, resource);
            var scope = arrayScope.Canonical;

            if (!grouped.TryGetValue(scope, out var scopePaths))
            {
                scopePaths = [];
                grouped.Add(scope, scopePaths);
            }

            scopePaths.Add(path);
        }

        return grouped.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<JsonPathExpression>)entry.Value,
            StringComparer.Ordinal
        );
    }

    /// <summary>
    /// Returns the owning array scope for a path by taking the prefix through its last wildcard array segment.
    /// </summary>
    private static JsonPathExpression GetArrayScope(JsonPathExpression path, QualifiedResourceName resource)
    {
        var lastArrayIndex = -1;

        for (var index = 0; index < path.Segments.Count; index++)
        {
            if (path.Segments[index] is JsonPathSegment.AnyArrayElement)
            {
                lastArrayIndex = index;
            }
        }

        if (lastArrayIndex < 0)
        {
            throw new InvalidOperationException(
                $"arrayUniquenessConstraints path '{path.Canonical}' on resource '{FormatResource(resource)}' "
                    + "must include an array wildcard segment."
            );
        }

        var scopeSegments = path.Segments.Take(lastArrayIndex + 1).ToArray();
        return JsonPathExpressionCompiler.FromSegments(scopeSegments);
    }

    /// <summary>
    /// Adds the derived unique constraint to the table if it is not already present.
    /// </summary>
    private static void AddArrayUniquenessConstraint(
        ResourceMutation mutation,
        DbTableModel table,
        CompiledArrayUniqueness compiledIdentity,
        QualifiedResourceName resource,
        bool emitUniqueConstraint
    )
    {
        ApplyArrayUniquenessSemanticIdentityBindings(
            mutation,
            table,
            compiledIdentity.SemanticIdentityBindings,
            resource,
            allowReferenceDerivedFallbackReplacement: emitUniqueConstraint
        );

        if (!emitUniqueConstraint)
        {
            return;
        }

        var tableAccumulator = mutation.GetTableAccumulator(table, mutation.Entry.Model.ResourceKey.Resource);

        if (
            !ContainsUniqueConstraint(
                tableAccumulator.Constraints,
                tableAccumulator.Definition.Table,
                compiledIdentity.UniqueColumns
            )
        )
        {
            var uniqueName = ConstraintNaming.BuildArrayUniquenessName(
                table.Table,
                compiledIdentity.UniqueColumns
            );
            tableAccumulator.AddConstraint(
                new TableConstraint.Unique(uniqueName, compiledIdentity.UniqueColumns)
            );
            mutation.MarkTableMutated(table);
        }
    }

    /// <summary>
    /// Applies AUC-derived semantic identity bindings, replacing an earlier reference-derived fallback when
    /// the resolved AUC member set is more specific for the same persisted scope.
    /// </summary>
    private static void ApplyArrayUniquenessSemanticIdentityBindings(
        ResourceMutation mutation,
        DbTableModel table,
        IReadOnlyList<CollectionSemanticIdentityBinding> semanticIdentityBindings,
        QualifiedResourceName resource,
        bool allowReferenceDerivedFallbackReplacement
    )
    {
        var tableAccumulator = mutation.GetTableAccumulator(table, mutation.Entry.Model.ResourceKey.Resource);
        var existingIdentityMetadata = tableAccumulator.IdentityMetadata;

        if (semanticIdentityBindings.Count == 0)
        {
            throw new InvalidOperationException(
                $"Semantic identity scope '{table.JsonScope.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not compile any semantic identity bindings."
            );
        }

        if (existingIdentityMetadata.SemanticIdentityBindings.Count > 0)
        {
            if (
                SemanticIdentityBindingsMatch(
                    existingIdentityMetadata.SemanticIdentityBindings,
                    semanticIdentityBindings
                )
            )
            {
                if (
                    allowReferenceDerivedFallbackReplacement
                    && existingIdentityMetadata.SemanticIdentitySource
                        == CollectionSemanticIdentitySource.ReferenceFallback
                )
                {
                    tableAccumulator.IdentityMetadata = existingIdentityMetadata with
                    {
                        SemanticIdentitySource = CollectionSemanticIdentitySource.ArrayUniquenessConstraint,
                    };
                    mutation.MarkTableMutated(table);
                }

                return;
            }

            if (
                allowReferenceDerivedFallbackReplacement
                && existingIdentityMetadata.SemanticIdentitySource
                    == CollectionSemanticIdentitySource.ReferenceFallback
            )
            {
                tableAccumulator.IdentityMetadata = existingIdentityMetadata with
                {
                    SemanticIdentityBindings = semanticIdentityBindings.ToArray(),
                    SemanticIdentitySource = CollectionSemanticIdentitySource.ArrayUniquenessConstraint,
                };
                mutation.MarkTableMutated(table);
                return;
            }

            throw CreateAmbiguousArrayUniquenessSemanticIdentityException(
                table,
                resource,
                existingIdentityMetadata.SemanticIdentityBindings,
                semanticIdentityBindings
            );
        }

        tableAccumulator.IdentityMetadata = existingIdentityMetadata with
        {
            SemanticIdentityBindings = semanticIdentityBindings.ToArray(),
            SemanticIdentitySource = CollectionSemanticIdentitySource.ArrayUniquenessConstraint,
        };
        mutation.MarkTableMutated(table);
    }

    /// <summary>
    /// Applies compiled semantic-identity bindings to a table when it does not already carry an applicable
    /// binding set.
    /// </summary>
    internal static void ApplySemanticIdentityBindings(
        ResourceMutation mutation,
        DbTableModel table,
        IReadOnlyList<CollectionSemanticIdentityBinding> semanticIdentityBindings,
        CollectionSemanticIdentitySource semanticIdentitySource,
        QualifiedResourceName resource
    )
    {
        var tableAccumulator = mutation.GetTableAccumulator(table, mutation.Entry.Model.ResourceKey.Resource);
        var existingIdentityMetadata = tableAccumulator.IdentityMetadata;

        if (semanticIdentityBindings.Count == 0)
        {
            throw new InvalidOperationException(
                $"Semantic identity scope '{table.JsonScope.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not compile any semantic identity bindings."
            );
        }

        if (existingIdentityMetadata.SemanticIdentityBindings.Count > 0)
        {
            return;
        }

        tableAccumulator.IdentityMetadata = existingIdentityMetadata with
        {
            SemanticIdentityBindings = semanticIdentityBindings.ToArray(),
            SemanticIdentitySource = semanticIdentitySource,
        };
        mutation.MarkTableMutated(table);
    }

    /// <summary>
    /// Returns true when two ordered semantic-identity binding sets are equivalent.
    /// </summary>
    private static bool SemanticIdentityBindingsMatch(
        IReadOnlyList<CollectionSemanticIdentityBinding> left,
        IReadOnlyList<CollectionSemanticIdentityBinding> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (
                !string.Equals(
                    left[index].RelativePath.Canonical,
                    right[index].RelativePath.Canonical,
                    StringComparison.Ordinal
                ) || !left[index].ColumnName.Equals(right[index].ColumnName)
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a deterministic diagnostic for competing array-uniqueness semantic identity candidates.
    /// </summary>
    private static Exception CreateAmbiguousArrayUniquenessSemanticIdentityException(
        DbTableModel table,
        QualifiedResourceName resource,
        IReadOnlyList<CollectionSemanticIdentityBinding> existingBindings,
        IReadOnlyList<CollectionSemanticIdentityBinding> conflictingBindings
    )
    {
        return new InvalidOperationException(
            $"Persisted multi-item scope '{table.JsonScope.Canonical}' on resource "
                + $"'{FormatResource(resource)}' resolved multiple applicable "
                + "arrayUniquenessConstraints semantic-identity binding sets: "
                + $"{FormatSemanticIdentityBindings(existingBindings)} and "
                + $"{FormatSemanticIdentityBindings(conflictingBindings)}. Collection semantic "
                + "identity must resolve to exactly one non-empty ordered binding set."
        );
    }

    /// <summary>
    /// Formats one semantic-identity binding set for diagnostics.
    /// </summary>
    private static string FormatSemanticIdentityBindings(
        IReadOnlyList<CollectionSemanticIdentityBinding> bindings
    )
    {
        return "["
            + string.Join(
                ", ",
                bindings.Select(binding =>
                    $"'{binding.RelativePath.Canonical}' -> '{binding.ColumnName.Value}'"
                )
            )
            + "]";
    }

    /// <summary>
    /// Attempts to resolve the single table that matches an array uniqueness scope by mapping all constraint
    /// paths to columns for each candidate table.
    /// </summary>
    private static bool TryResolveArrayUniquenessTable(
        IReadOnlyList<DbTableModel> candidates,
        IReadOnlyList<JsonPathExpression> paths,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable,
        string scope,
        QualifiedResourceName resource,
        out DbTableModel table,
        out CompiledArrayUniqueness compiledIdentity,
        out Exception? failure
    )
    {
        table = default!;
        compiledIdentity = new CompiledArrayUniqueness([], []);
        failure = null;

        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.Table.ToString(), StringComparer.Ordinal)
            .ToArray();
        List<(DbTableModel Table, CompiledArrayUniqueness Identity)> matches = [];
        List<Exception> failures = [];

        foreach (var candidate in orderedCandidates)
        {
            try
            {
                var columns = BuildArrayUniquenessColumns(
                    candidate,
                    paths,
                    referenceBindingsByIdentityPath,
                    tablesByName,
                    columnsByTable,
                    resource
                );
                matches.Add((candidate, columns));
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(ex);
            }
        }

        if (matches.Count == 1)
        {
            table = matches[0].Table;
            compiledIdentity = matches[0].Identity;
            return true;
        }

        if (matches.Count > 1)
        {
            var candidatesList = string.Join(
                ", ",
                matches
                    .Select(match => match.Table.Table.ToString())
                    .OrderBy(name => name, StringComparer.Ordinal)
            );

            throw new InvalidOperationException(
                $"arrayUniquenessConstraints scope '{scope}' on resource '{FormatResource(resource)}' "
                    + $"matched multiple tables: {candidatesList}."
            );
        }

        if (failures.Count > 0)
        {
            failure = CombineArrayUniquenessFailures(failures);
        }

        return false;
    }

    /// <summary>
    /// Combines multiple candidate-table failures into a single exception with aggregate details.
    /// </summary>
    private static Exception? CombineArrayUniquenessFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return null;
        }

        if (failures.Count == 1)
        {
            return failures[0];
        }

        var combinedMessage = string.Join(" ", failures.Select(failure => failure.Message));
        var aggregate = new AggregateException(failures);

        return new InvalidOperationException(combinedMessage, aggregate);
    }

    /// <summary>
    /// Attempts to resolve the single matching child table for the provided scope string.
    /// </summary>
    private static bool TryResolveArrayUniquenessTableForScope(
        string scope,
        IReadOnlyList<JsonPathExpression> paths,
        IReadOnlyDictionary<string, IReadOnlyList<DbTableModel>> tablesByScope,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable,
        QualifiedResourceName resource,
        out DbTableModel table,
        out CompiledArrayUniqueness compiledIdentity,
        out Exception? failure
    )
    {
        if (!tablesByScope.TryGetValue(scope, out var candidates))
        {
            table = default!;
            compiledIdentity = new CompiledArrayUniqueness([], []);
            failure = null;
            return false;
        }

        return TryResolveArrayUniquenessTable(
            candidates,
            paths,
            referenceBindingsByIdentityPath,
            tablesByName,
            columnsByTable,
            scope,
            resource,
            out table,
            out compiledIdentity,
            out failure
        );
    }

    /// <summary>
    /// Strips a leading <c>._ext.{project}</c> prefix from a scope path when present.
    /// </summary>
    private static bool TryStripExtensionRootPrefix(JsonPathExpression path, out JsonPathExpression stripped)
    {
        if (
            path.Segments.Count >= 2
            && path.Segments[0] is JsonPathSegment.Property { Name: "_ext" }
            && path.Segments[1] is JsonPathSegment.Property
        )
        {
            var remainingSegments = path.Segments.Skip(2).ToArray();
            stripped = JsonPathExpressionCompiler.FromSegments(remainingSegments);
            return true;
        }

        stripped = default;
        return false;
    }

    /// <summary>
    /// Strips a leading <c>._ext.{project}</c> prefix from each path, throwing when any path does not match the
    /// expected extension-aligned form.
    /// </summary>
    private static IReadOnlyList<JsonPathExpression> StripExtensionRootPrefix(
        IReadOnlyList<JsonPathExpression> paths,
        QualifiedResourceName resource
    )
    {
        List<JsonPathExpression> stripped = new(paths.Count);

        foreach (var path in paths)
        {
            if (!TryStripExtensionRootPrefix(path, out var strippedPath))
            {
                throw new InvalidOperationException(
                    $"arrayUniquenessConstraints path '{path.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' did not map to an extension-aligned scope."
                );
            }

            stripped.Add(strippedPath);
        }

        return stripped.ToArray();
    }

    /// <summary>
    /// Builds the deterministic unique column list for an array uniqueness constraint: parent key parts first,
    /// then resolved constraint columns. Semantic-identity bindings preserve the declared path order even when
    /// multiple identity members resolve to the same physical column.
    /// </summary>
    private static CompiledArrayUniqueness BuildArrayUniquenessColumns(
        DbTableModel table,
        IReadOnlyList<JsonPathExpression> paths,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable,
        QualifiedResourceName resource
    )
    {
        var parentKeyColumns = ResolveParentScopeColumns(table);

        HashSet<string> seenColumns = new(StringComparer.Ordinal);
        List<DbColumnName> uniqueColumns = new(parentKeyColumns.Length + paths.Count);
        List<CollectionSemanticIdentityBinding> semanticIdentityBindings = new(paths.Count);

        foreach (var keyColumn in parentKeyColumns)
        {
            AddUniqueColumn(keyColumn, uniqueColumns, seenColumns);
        }

        foreach (var path in paths)
        {
            var columnName = ResolveArrayUniquenessColumn(
                table,
                path,
                referenceBindingsByIdentityPath,
                tablesByName,
                columnsByTable,
                resource
            );

            semanticIdentityBindings.Add(
                new CollectionSemanticIdentityBinding(
                    DeriveScopeRelativeSemanticIdentityPath(table.JsonScope, path),
                    columnName
                )
            );
            AddUniqueColumn(columnName, uniqueColumns, seenColumns);
        }

        return new CompiledArrayUniqueness(uniqueColumns.ToArray(), semanticIdentityBindings.ToArray());
    }

    /// <summary>
    /// Resolves the immediate parent-scope locator columns for semantic uniqueness. Stable-key tables surface
    /// this explicitly through identity metadata; older table shapes fall back to parent-key columns.
    /// </summary>
    private static DbColumnName[] ResolveParentScopeColumns(DbTableModel table)
    {
        if (table.IdentityMetadata.ImmediateParentScopeLocatorColumns.Count > 0)
        {
            return table.IdentityMetadata.ImmediateParentScopeLocatorColumns.ToArray();
        }

        return table
            .Key.Columns.Where(column => column.Kind == ColumnKind.ParentKeyPart)
            .Select(column => column.ColumnName)
            .ToArray();
    }

    /// <summary>
    /// Resolves the physical column used for an array uniqueness path, binding to the per-identity-part raw
    /// scalar column when the path matches a reference identity component.
    /// </summary>
    private static DbColumnName ResolveArrayUniquenessColumn(
        DbTableModel table,
        JsonPathExpression path,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable,
        QualifiedResourceName resource
    )
    {
        if (referenceBindingsByIdentityPath.TryGetValue(path.Canonical, out var binding))
        {
            if (!binding.Table.Equals(table.Table))
            {
                throw new InvalidOperationException(
                    $"arrayUniquenessConstraints path '{path.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not bind to the owning table scope."
                );
            }

            var identityBinding = binding.IdentityBindings.FirstOrDefault(ib =>
                string.Equals(ib.ReferenceJsonPath.Canonical, path.Canonical, StringComparison.Ordinal)
            );

            if (identityBinding is not null)
            {
                return UnifiedAliasColumnResolver.ResolveStorageColumnName(table, identityBinding.Column);
            }

            // Defensive: if no matching identity binding is found, fall through to the
            // column-by-path lookup so the missing-column diagnostic surfaces downstream.
        }

        if (!columnsByTable.TryGetValue(table.Table, out var columnsByPath))
        {
            var sourceTables = tablesByName.TryGetValue(table.Table, out var tables) ? tables : [table];

            columnsByPath = sourceTables
                .SelectMany(sourceTable => sourceTable.Columns)
                .Where(column => column.SourceJsonPath is not null)
                .GroupBy(column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group =>
                        group
                            .OrderBy(column => column.ColumnName.Value, StringComparer.Ordinal)
                            .First()
                            .ColumnName,
                    StringComparer.Ordinal
                );
            columnsByTable.Add(table.Table, columnsByPath);
        }

        if (!columnsByPath.TryGetValue(path.Canonical, out var columnName))
        {
            throw new InvalidOperationException(
                $"arrayUniquenessConstraints path '{path.Canonical}' on resource '{FormatResource(resource)}' "
                    + $"did not map to a column on table '{table.Table.Name}'."
            );
        }

        return columnName;
    }

    /// <summary>
    /// Resolves a constraint path relative to its optional base path.
    /// </summary>
    private static JsonPathExpression ResolveConstraintPath(
        JsonPathExpression? basePath,
        JsonPathExpression path
    )
    {
        return basePath is null ? path : ResolveRelativePath(basePath.Value, path);
    }

    /// <summary>
    /// Resolves a path relative to a base array path by concatenating JSONPath segments.
    /// </summary>
    private static JsonPathExpression ResolveRelativePath(
        JsonPathExpression basePath,
        JsonPathExpression relativePath
    )
    {
        if (relativePath.Segments.Count == 0)
        {
            return basePath;
        }

        var combinedSegments = basePath.Segments.Concat(relativePath.Segments).ToArray();
        return JsonPathExpressionCompiler.FromSegments(combinedSegments);
    }

    /// <summary>
    /// Captures the unique-constraint columns and ordered semantic-identity bindings compiled for one scope.
    /// </summary>
    /// <param name="UniqueColumns">The deduplicated unique-constraint columns, including parent locator parts.</param>
    /// <param name="SemanticIdentityBindings">
    /// The ordered semantic-identity bindings in declared path order.
    /// </param>
    private sealed record CompiledArrayUniqueness(
        DbColumnName[] UniqueColumns,
        CollectionSemanticIdentityBinding[] SemanticIdentityBindings
    );
}
