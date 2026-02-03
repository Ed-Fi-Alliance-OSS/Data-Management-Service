// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives array uniqueness constraints.
/// </summary>
public sealed class ArrayUniquenessConstraintRelationalModelSetPass : IRelationalModelSetPass
{
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);
        var baseResourcesByName = BuildBaseResourceLookup(
            context.ConcreteResourcesInNameOrder,
            static (index, model) => new ResourceEntry(index, model)
        );
        Dictionary<QualifiedResourceName, ResourceMutation> mutations = new();

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);

            if (builderContext.ArrayUniquenessConstraints.Count == 0)
            {
                continue;
            }

            if (IsResourceExtension(resourceContext))
            {
                var baseEntry = ResolveBaseResourceForExtension(
                    resourceContext.ResourceName,
                    resource,
                    baseResourcesByName,
                    static entry => entry.Model.ResourceKey.Resource
                );
                var baseResource = baseEntry.Model.ResourceKey.Resource;
                var mutation = GetOrCreateMutation(baseResource, baseEntry, mutations);

                ApplyArrayUniquenessConstraintsForResource(
                    mutation,
                    baseEntry.Model.RelationalModel,
                    builderContext,
                    baseResource
                );

                continue;
            }

            if (!resourcesByKey.TryGetValue(resource, out var entry))
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found for constraint derivation."
                );
            }

            var resourceMutation = GetOrCreateMutation(resource, entry, mutations);

            ApplyArrayUniquenessConstraintsForResource(
                resourceMutation,
                entry.Model.RelationalModel,
                builderContext,
                resource
            );
        }

        foreach (var mutation in mutations.Values)
        {
            if (!mutation.HasChanges)
            {
                continue;
            }

            var updatedModel = UpdateResourceModel(mutation.Entry.Model.RelationalModel, mutation);
            context.ConcreteResourcesInNameOrder[mutation.Entry.Index] = mutation.Entry.Model with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    private static void ApplyArrayUniquenessConstraintsForResource(
        ResourceMutation mutation,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        var tablesByScope = resourceModel
            .TablesInReadDependencyOrder.GroupBy(table => table.JsonScope.Canonical, StringComparer.Ordinal)
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
            .TablesInReadDependencyOrder.GroupBy(table => table.Table)
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
                columnsByTable
            );
        }
    }

    private static void ApplyArrayUniquenessConstraint(
        ArrayUniquenessConstraintInput constraint,
        ResourceMutation mutation,
        QualifiedResourceName resource,
        IReadOnlyDictionary<string, IReadOnlyList<DbTableModel>> tablesByScope,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable
    )
    {
        var resolvedPaths = constraint
            .Paths.Select(path => ResolveConstraintPath(constraint.BasePath, path))
            .ToArray();
        var pathsByScope = GroupPathsByArrayScope(resolvedPaths, resource);

        foreach (var scopeGroup in pathsByScope)
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
                    out var uniqueColumns,
                    out scopeFailure
                )
            )
            {
                AddArrayUniquenessConstraint(mutation, table, uniqueColumns);
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
                        out var alignedColumns,
                        out alignedFailure
                    )
                )
                {
                    AddArrayUniquenessConstraint(mutation, alignedTable, alignedColumns);
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
                columnsByTable
            );
        }
    }

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

    private static void AddArrayUniquenessConstraint(
        ResourceMutation mutation,
        DbTableModel table,
        IReadOnlyList<DbColumnName> uniqueColumns
    )
    {
        var uniqueName = BuildUniqueConstraintName(table.Table.Name, uniqueColumns);
        var tableAccumulator = mutation.GetTableAccumulator(table, mutation.Entry.Model.ResourceKey.Resource);

        if (!ContainsUniqueConstraint(tableAccumulator.Constraints, uniqueName))
        {
            tableAccumulator.AddConstraint(new TableConstraint.Unique(uniqueName, uniqueColumns));
            mutation.MarkTableMutated(table);
        }
    }

    private static bool TryResolveArrayUniquenessTable(
        IReadOnlyList<DbTableModel> candidates,
        IReadOnlyList<JsonPathExpression> paths,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable,
        string scope,
        QualifiedResourceName resource,
        out DbTableModel table,
        out DbColumnName[] uniqueColumns,
        out Exception? failure
    )
    {
        table = default!;
        uniqueColumns = Array.Empty<DbColumnName>();
        failure = null;

        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.Table.ToString(), StringComparer.Ordinal)
            .ToArray();
        List<(DbTableModel Table, DbColumnName[] Columns)> matches = [];
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
            uniqueColumns = matches[0].Columns;
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

    private static bool TryResolveArrayUniquenessTableForScope(
        string scope,
        IReadOnlyList<JsonPathExpression> paths,
        IReadOnlyDictionary<string, IReadOnlyList<DbTableModel>> tablesByScope,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable,
        QualifiedResourceName resource,
        out DbTableModel table,
        out DbColumnName[] uniqueColumns,
        out Exception? failure
    )
    {
        if (!tablesByScope.TryGetValue(scope, out var candidates))
        {
            table = default!;
            uniqueColumns = Array.Empty<DbColumnName>();
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
            out uniqueColumns,
            out failure
        );
    }

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

    private static DbColumnName[] BuildArrayUniquenessColumns(
        DbTableModel table,
        IReadOnlyList<JsonPathExpression> paths,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        IReadOnlyDictionary<DbTableName, IReadOnlyList<DbTableModel>> tablesByName,
        IDictionary<DbTableName, IReadOnlyDictionary<string, DbColumnName>> columnsByTable,
        QualifiedResourceName resource
    )
    {
        var parentKeyColumns = table
            .Key.Columns.Where(column => column.Kind == ColumnKind.ParentKeyPart)
            .Select(column => column.ColumnName)
            .ToArray();

        HashSet<string> seenColumns = new(StringComparer.Ordinal);
        List<DbColumnName> uniqueColumns = new(parentKeyColumns.Length + paths.Count);

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
            AddUniqueColumn(columnName, uniqueColumns, seenColumns);
        }

        return uniqueColumns.ToArray();
    }

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

            return binding.FkColumn;
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

    private static JsonPathExpression ResolveConstraintPath(
        JsonPathExpression? basePath,
        JsonPathExpression path
    )
    {
        return basePath is null ? path : ResolveRelativePath(basePath.Value, path);
    }

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
}
