// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives root-level uniqueness constraints and descriptor-system columns from identity metadata.
/// </summary>
public sealed class ConstraintDerivationRelationalModelSetPass : IRelationalModelSetPass
{
    private const string UriColumnLabel = "Uri";
    private const string DiscriminatorColumnLabel = "Discriminator";
    private const int UriMaxLength = 306;
    private const int DiscriminatorMaxLength = 128;

    /// <summary>
    /// Derives root-table unique constraints for each concrete resource.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ApplyRootConstraints(context);
        ApplyReferenceConstraints(context);
        ApplyArrayUniquenessConstraints(context);
    }

    private static void ApplyRootConstraints(RelationalModelSetBuilderContext context)
    {
        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);

        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);

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
                    $"Concrete resource '{FormatResource(resource)}' was not found for constraint derivation."
                );
            }

            var builderContext = BuildResourceContext(resourceContext, apiSchemaRootsByProjectEndpoint);
            var updatedModel = ApplyRootConstraints(builderContext, entry.Model.RelationalModel, resource);

            if (ReferenceEquals(updatedModel, entry.Model.RelationalModel))
            {
                continue;
            }

            context.ConcreteResourcesInNameOrder[entry.Index] = entry.Model with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    private static void ApplyReferenceConstraints(RelationalModelSetBuilderContext context)
    {
        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);
        var baseResourcesByName = BuildBaseResourceLookup(context.ConcreteResourcesInNameOrder);
        var abstractIdentityTablesByResource = context.AbstractIdentityTablesInNameOrder.ToDictionary(table =>
            table.AbstractResourceKey.Resource
        );
        var resourceContextsByResource = BuildResourceContextLookup(context);
        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);
        Dictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource = new();
        Dictionary<QualifiedResourceName, TargetIdentityInfo> targetIdentityCache = new();
        Dictionary<QualifiedResourceName, ResourceMutation> mutations = new();

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = GetOrCreateBuilderContext(
                resourceContext,
                apiSchemaRootsByProjectEndpoint,
                builderContextsByResource
            );

            if (builderContext.DocumentReferenceMappings.Count == 0)
            {
                continue;
            }

            if (IsResourceExtension(resourceContext))
            {
                if (!baseResourcesByName.TryGetValue(resourceContext.ResourceName, out var baseEntries))
                {
                    throw new InvalidOperationException(
                        $"Resource extension '{FormatResource(resource)}' did not match a concrete base resource."
                    );
                }

                if (baseEntries.Count != 1)
                {
                    var candidates = string.Join(
                        ", ",
                        baseEntries
                            .Select(entry => FormatResource(entry.Model.ResourceKey.Resource))
                            .OrderBy(name => name, StringComparer.Ordinal)
                    );

                    throw new InvalidOperationException(
                        $"Resource extension '{FormatResource(resource)}' matched multiple concrete resources: "
                            + $"{candidates}."
                    );
                }

                var baseEntry = baseEntries[0];
                var baseResource = baseEntry.Model.ResourceKey.Resource;
                var mutation = GetOrCreateMutation(baseResource, baseEntry, mutations);

                ApplyReferenceConstraintsForResource(
                    mutation,
                    baseEntry.Model.RelationalModel,
                    builderContext,
                    baseResource,
                    resourcesByKey,
                    abstractIdentityTablesByResource,
                    resourceContextsByResource,
                    apiSchemaRootsByProjectEndpoint,
                    builderContextsByResource,
                    targetIdentityCache,
                    mutations
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

            ApplyReferenceConstraintsForResource(
                resourceMutation,
                entry.Model.RelationalModel,
                builderContext,
                resource,
                resourcesByKey,
                abstractIdentityTablesByResource,
                resourceContextsByResource,
                apiSchemaRootsByProjectEndpoint,
                builderContextsByResource,
                targetIdentityCache,
                mutations
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

    private static void ApplyArrayUniquenessConstraints(RelationalModelSetBuilderContext context)
    {
        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);
        var baseResourcesByName = BuildBaseResourceLookup(context.ConcreteResourcesInNameOrder);
        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);
        Dictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource = new();
        Dictionary<QualifiedResourceName, ResourceMutation> mutations = new();

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = GetOrCreateBuilderContext(
                resourceContext,
                apiSchemaRootsByProjectEndpoint,
                builderContextsByResource
            );

            if (builderContext.ArrayUniquenessConstraints.Count == 0)
            {
                continue;
            }

            if (IsResourceExtension(resourceContext))
            {
                if (!baseResourcesByName.TryGetValue(resourceContext.ResourceName, out var baseEntries))
                {
                    throw new InvalidOperationException(
                        $"Resource extension '{FormatResource(resource)}' did not match a concrete base resource."
                    );
                }

                if (baseEntries.Count != 1)
                {
                    var candidates = string.Join(
                        ", ",
                        baseEntries
                            .Select(entry => FormatResource(entry.Model.ResourceKey.Resource))
                            .OrderBy(name => name, StringComparer.Ordinal)
                    );

                    throw new InvalidOperationException(
                        $"Resource extension '{FormatResource(resource)}' matched multiple concrete resources: "
                            + $"{candidates}."
                    );
                }

                var baseEntry = baseEntries[0];
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
            Exception? failure = null;

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
                    out failure
                )
            )
            {
                AddArrayUniquenessConstraint(mutation, table, uniqueColumns);
                continue;
            }

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
                        out failure
                    )
                )
                {
                    AddArrayUniquenessConstraint(mutation, alignedTable, alignedColumns);
                    continue;
                }
            }

            if (failure is not null)
            {
                throw failure;
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
        var tableBuilder = mutation.GetTableBuilder(table, mutation.Entry.Model.ResourceKey.Resource);

        if (!ContainsUniqueConstraint(tableBuilder.Constraints, uniqueName))
        {
            tableBuilder.AddConstraint(new TableConstraint.Unique(uniqueName, uniqueColumns));
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
                failure = ex;
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

        return false;
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

    private static RelationalResourceModel ApplyRootConstraints(
        RelationalModelBuilderContext builderContext,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        var rootTable = resourceModel.Root;
        var tableBuilder = new TableBuilder(rootTable);
        var mutated = false;

        if (resourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            mutated |= EnsureDescriptorColumn(tableBuilder, rootTable, BuildUriColumn(), UriColumnLabel);
            mutated |= EnsureDescriptorColumn(
                tableBuilder,
                rootTable,
                BuildDiscriminatorColumn(),
                DiscriminatorColumnLabel
            );

            var descriptorUniqueColumns = new DbColumnName[]
            {
                new(UriColumnLabel),
                new(DiscriminatorColumnLabel),
            };
            var descriptorUniqueName = BuildUniqueConstraintName(
                rootTable.Table.Name,
                descriptorUniqueColumns
            );

            if (!ContainsUniqueConstraint(rootTable.Constraints, descriptorUniqueName))
            {
                tableBuilder.AddConstraint(
                    new TableConstraint.Unique(descriptorUniqueName, descriptorUniqueColumns)
                );
                mutated = true;
            }

            if (!mutated)
            {
                return resourceModel;
            }

            var updatedRoot = CanonicalizeTable(tableBuilder.Build());

            return UpdateResourceModel(resourceModel, updatedRoot);
        }

        var identityColumns = BuildRootIdentityColumns(resourceModel, builderContext, resource);

        if (identityColumns.Count == 0)
        {
            return resourceModel;
        }

        var rootUniqueName = BuildUniqueConstraintName(rootTable.Table.Name, identityColumns);

        if (!ContainsUniqueConstraint(rootTable.Constraints, rootUniqueName))
        {
            tableBuilder.AddConstraint(new TableConstraint.Unique(rootUniqueName, identityColumns));
            mutated = true;
        }

        if (!mutated)
        {
            return resourceModel;
        }

        var updatedRootTable = CanonicalizeTable(tableBuilder.Build());

        return UpdateResourceModel(resourceModel, updatedRootTable);
    }

    private static RelationalResourceModel UpdateResourceModel(
        RelationalResourceModel resourceModel,
        DbTableModel updatedRoot
    )
    {
        var updatedTables = resourceModel
            .TablesInReadDependencyOrder.Select(table =>
                table.JsonScope.Canonical == updatedRoot.JsonScope.Canonical ? updatedRoot : table
            )
            .ToArray();

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInReadDependencyOrder = updatedTables,
            TablesInWriteDependencyOrder = updatedTables,
        };
    }

    private static RelationalResourceModel UpdateResourceModel(
        RelationalResourceModel resourceModel,
        ResourceMutation mutation
    )
    {
        var updatedTables = resourceModel
            .TablesInReadDependencyOrder.Select(table => mutation.BuildTable(table))
            .ToArray();
        var updatedRoot = updatedTables.Single(table => table.Table.Equals(resourceModel.Root.Table));

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInReadDependencyOrder = updatedTables,
            TablesInWriteDependencyOrder = updatedTables,
        };
    }

    private static IReadOnlyList<DbColumnName> BuildRootIdentityColumns(
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        if (builderContext.IdentityJsonPaths.Count == 0)
        {
            return Array.Empty<DbColumnName>();
        }

        var rootTable = resourceModel.Root;
        var rootColumnsByPath = rootTable
            .Columns.Where(column => column.SourceJsonPath is not null)
            .GroupBy(column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ColumnName, StringComparer.Ordinal);
        var referenceBindingsByIdentityPath = BuildReferenceIdentityBindings(
            resourceModel.DocumentReferenceBindings,
            resource
        );

        HashSet<string> seenColumns = new(StringComparer.Ordinal);
        List<DbColumnName> uniqueColumns = new(builderContext.IdentityJsonPaths.Count);

        foreach (var identityPath in builderContext.IdentityJsonPaths)
        {
            if (identityPath.Segments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "must not include array segments when deriving root unique constraints."
                );
            }

            if (referenceBindingsByIdentityPath.TryGetValue(identityPath.Canonical, out var binding))
            {
                if (binding.Table != rootTable.Table)
                {
                    throw new InvalidOperationException(
                        $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                            + "must bind to the root table when deriving unique constraints."
                    );
                }

                AddUniqueColumn(binding.FkColumn, uniqueColumns, seenColumns);
                continue;
            }

            if (!rootColumnsByPath.TryGetValue(identityPath.Canonical, out var columnName))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column."
                );
            }

            AddUniqueColumn(columnName, uniqueColumns, seenColumns);
        }

        return uniqueColumns.ToArray();
    }

    private static IReadOnlyList<DbColumnName> BuildIdentityValueColumns(
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        if (builderContext.IdentityJsonPaths.Count == 0)
        {
            return Array.Empty<DbColumnName>();
        }

        var rootTable = resourceModel.Root;
        var rootColumnsByPath = rootTable
            .Columns.Where(column => column.SourceJsonPath is not null)
            .GroupBy(column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ColumnName, StringComparer.Ordinal);

        List<DbColumnName> identityColumns = new(builderContext.IdentityJsonPaths.Count);

        foreach (var identityPath in builderContext.IdentityJsonPaths)
        {
            if (identityPath.Segments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "must not include array segments when deriving reference constraints."
                );
            }

            if (!rootColumnsByPath.TryGetValue(identityPath.Canonical, out var columnName))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column."
                );
            }

            identityColumns.Add(columnName);
        }

        return identityColumns.ToArray();
    }

    private static IReadOnlyDictionary<string, DocumentReferenceBinding> BuildReferenceIdentityBindings(
        IReadOnlyList<DocumentReferenceBinding> bindings,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, DocumentReferenceBinding> lookup = new(StringComparer.Ordinal);

        foreach (var binding in bindings)
        {
            foreach (var identityBinding in binding.IdentityBindings)
            {
                if (!lookup.TryAdd(identityBinding.ReferenceJsonPath.Canonical, binding))
                {
                    var existing = lookup[identityBinding.ReferenceJsonPath.Canonical];

                    if (existing.ReferenceObjectPath.Canonical == binding.ReferenceObjectPath.Canonical)
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Identity path '{identityBinding.ReferenceJsonPath.Canonical}' on resource "
                            + $"'{FormatResource(resource)}' was bound to multiple references."
                    );
                }
            }
        }

        return lookup;
    }

    private static DbTableModel ResolveReferenceBindingTable(
        DocumentReferenceBinding binding,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        var candidates = resourceModel
            .TablesInReadDependencyOrder.Where(table => table.Table.Equals(binding.Table))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not map to table '{binding.Table}'."
            );
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        var orderedCandidates = candidates
            .OrderBy(table => table.JsonScope.Canonical, StringComparer.Ordinal)
            .ToArray();
        DbTableModel? bestMatch = null;

        foreach (var candidate in orderedCandidates)
        {
            if (!IsPrefixOf(candidate.JsonScope.Segments, binding.ReferenceObjectPath.Segments))
            {
                continue;
            }

            if (bestMatch is null || candidate.JsonScope.Segments.Count > bestMatch.JsonScope.Segments.Count)
            {
                bestMatch = candidate;
            }
        }

        if (bestMatch is null)
        {
            var scopeList = string.Join(", ", orderedCandidates.Select(table => table.JsonScope.Canonical));

            throw new InvalidOperationException(
                $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not match any table scope for '{binding.Table}'. "
                    + $"Candidates: {scopeList}."
            );
        }

        if (
            binding.ReferenceObjectPath.Segments.Any(segment =>
                segment is JsonPathSegment.Property { Name: "_ext" }
            )
            && !bestMatch.JsonScope.Segments.Any(segment =>
                segment is JsonPathSegment.Property { Name: "_ext" }
            )
        )
        {
            throw new InvalidOperationException(
                $"Reference object path '{binding.ReferenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' requires an extension table scope, but none was found."
            );
        }

        return bestMatch;
    }

    private static void ApplyReferenceConstraintsForResource(
        ResourceMutation mutation,
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IReadOnlyDictionary<
            QualifiedResourceName,
            AbstractIdentityTableInfo
        > abstractIdentityTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        IDictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource,
        IDictionary<QualifiedResourceName, TargetIdentityInfo> targetIdentityCache,
        IDictionary<QualifiedResourceName, ResourceMutation> mutations
    )
    {
        var bindingByReferencePath = resourceModel.DocumentReferenceBindings.ToDictionary(
            binding => binding.ReferenceObjectPath.Canonical,
            StringComparer.Ordinal
        );

        foreach (var mapping in builderContext.DocumentReferenceMappings)
        {
            if (!bindingByReferencePath.TryGetValue(mapping.ReferenceObjectPath.Canonical, out var binding))
            {
                throw new InvalidOperationException(
                    $"Reference object path '{mapping.ReferenceObjectPath.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' was not bound to a table."
                );
            }

            var targetInfo = GetTargetIdentityInfo(
                mapping.TargetResource,
                resourcesByKey,
                abstractIdentityTablesByResource,
                resourceContextsByResource,
                apiSchemaRootsByProjectEndpoint,
                builderContextsByResource,
                targetIdentityCache
            );
            var identityColumns = BuildReferenceIdentityColumns(mapping, binding, targetInfo, resource);

            var hasCompleteIdentity = identityColumns.TargetColumns.Count == targetInfo.IdentityColumns.Count;

            if (targetInfo.IsAbstract && !hasCompleteIdentity)
            {
                continue;
            }

            EnsureTargetUnique(targetInfo, identityColumns.TargetColumns, resourcesByKey, mutations);

            var bindingTable = ResolveReferenceBindingTable(binding, resourceModel, resource);
            var tableBuilder = mutation.GetTableBuilder(bindingTable, resource);

            if (identityColumns.LocalColumns.Count > 0)
            {
                var allOrNoneName = BuildAllOrNoneConstraintName(
                    tableBuilder.Definition.Table.Name,
                    binding.FkColumn
                );

                if (!ContainsAllOrNoneConstraint(tableBuilder.Constraints, allOrNoneName))
                {
                    tableBuilder.AddConstraint(
                        new TableConstraint.AllOrNoneNullability(
                            allOrNoneName,
                            binding.FkColumn,
                            identityColumns.LocalColumns
                        )
                    );
                    mutation.MarkTableMutated(bindingTable);
                }
            }

            var localColumns = new List<DbColumnName>(1 + identityColumns.LocalColumns.Count)
            {
                binding.FkColumn,
            };
            localColumns.AddRange(identityColumns.LocalColumns);

            var targetColumns = new List<DbColumnName>(1 + identityColumns.TargetColumns.Count)
            {
                RelationalNameConventions.DocumentIdColumnName,
            };
            targetColumns.AddRange(identityColumns.TargetColumns);

            var fkName = RelationalNameConventions.ForeignKeyName(
                tableBuilder.Definition.Table.Name,
                localColumns
            );

            if (!ContainsForeignKeyConstraint(tableBuilder.Constraints, fkName))
            {
                var onUpdate =
                    targetInfo.IsAbstract ? ReferentialAction.Cascade
                    : targetInfo.AllowIdentityUpdates ? ReferentialAction.Cascade
                    : ReferentialAction.NoAction;

                tableBuilder.AddConstraint(
                    new TableConstraint.ForeignKey(
                        fkName,
                        localColumns.ToArray(),
                        targetInfo.Table,
                        targetColumns.ToArray(),
                        OnDelete: ReferentialAction.NoAction,
                        OnUpdate: onUpdate
                    )
                );
                mutation.MarkTableMutated(bindingTable);
            }
        }
    }

    private static ReferenceIdentityColumnSet BuildReferenceIdentityColumns(
        DocumentReferenceMapping mapping,
        DocumentReferenceBinding binding,
        TargetIdentityInfo targetInfo,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, JsonPathExpression> referencePathsByIdentityPath = new(StringComparer.Ordinal);
        Dictionary<string, DbColumnName> localColumnsByIdentityPath = new(StringComparer.Ordinal);

        if (mapping.ReferenceJsonPaths.Count != binding.IdentityBindings.Count)
        {
            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + "did not align referenceJsonPaths with identity bindings."
            );
        }

        for (var index = 0; index < mapping.ReferenceJsonPaths.Count; index++)
        {
            var path = mapping.ReferenceJsonPaths[index];
            var identityBinding = binding.IdentityBindings[index];

            if (
                !string.Equals(
                    path.ReferenceJsonPath.Canonical,
                    identityBinding.ReferenceJsonPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"did not align reference path '{path.ReferenceJsonPath.Canonical}' with "
                        + $"binding '{identityBinding.ReferenceJsonPath.Canonical}'."
                );
            }

            if (!referencePathsByIdentityPath.TryAdd(path.IdentityJsonPath.Canonical, path.ReferenceJsonPath))
            {
                var existing = referencePathsByIdentityPath[path.IdentityJsonPath.Canonical];

                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"contains duplicate identity path '{path.IdentityJsonPath.Canonical}' bound to "
                        + $"'{existing.Canonical}' and '{path.ReferenceJsonPath.Canonical}'."
                );
            }

            if (!localColumnsByIdentityPath.TryAdd(path.IdentityJsonPath.Canonical, identityBinding.Column))
            {
                var existing = localColumnsByIdentityPath[path.IdentityJsonPath.Canonical];

                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"contains duplicate identity path '{path.IdentityJsonPath.Canonical}' bound to "
                        + $"columns '{existing.Value}' and '{identityBinding.Column.Value}'."
                );
            }
        }

        Dictionary<string, DbColumnName> targetColumnsByIdentityPath = new(StringComparer.Ordinal);

        for (var index = 0; index < targetInfo.IdentityJsonPaths.Count; index++)
        {
            targetColumnsByIdentityPath[targetInfo.IdentityJsonPaths[index].Canonical] =
                targetInfo.IdentityColumns[index];
        }

        List<DbColumnName> localColumns = new(targetInfo.IdentityJsonPaths.Count);
        List<DbColumnName> targetColumns = new(targetInfo.IdentityJsonPaths.Count);
        List<JsonPathExpression> missingIdentityPaths = new();

        foreach (var identityPath in targetInfo.IdentityJsonPaths)
        {
            if (!localColumnsByIdentityPath.TryGetValue(identityPath.Canonical, out var localColumn))
            {
                missingIdentityPaths.Add(identityPath);
                continue;
            }

            if (!targetColumnsByIdentityPath.TryGetValue(identityPath.Canonical, out var targetColumn))
            {
                throw new InvalidOperationException(
                    $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                        + $"did not resolve identity path '{identityPath.Canonical}' on target "
                        + $"'{FormatResource(mapping.TargetResource)}'."
                );
            }

            localColumns.Add(localColumn);
            targetColumns.Add(targetColumn);
        }

        if (missingIdentityPaths.Count > 0)
        {
            var missingPaths = string.Join(", ", missingIdentityPaths.Select(path => $"'{path.Canonical}'"));

            throw new InvalidOperationException(
                $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}' "
                    + $"did not include identity path(s) {missingPaths} required by target "
                    + $"'{FormatResource(mapping.TargetResource)}'."
            );
        }

        return new ReferenceIdentityColumnSet(localColumns.ToArray(), targetColumns.ToArray());
    }

    private static void EnsureTargetUnique(
        TargetIdentityInfo targetInfo,
        IReadOnlyList<DbColumnName> targetIdentityColumns,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IDictionary<QualifiedResourceName, ResourceMutation> mutations
    )
    {
        if (targetInfo.IsAbstract || targetIdentityColumns.Count == 0)
        {
            return;
        }

        if (!resourcesByKey.TryGetValue(targetInfo.Resource, out var entry))
        {
            throw new InvalidOperationException(
                $"Concrete target resource '{FormatResource(targetInfo.Resource)}' was not found "
                    + "when deriving reference constraints."
            );
        }

        var mutation = GetOrCreateMutation(targetInfo.Resource, entry, mutations);
        var tableBuilder = mutation.GetTableBuilder(entry.Model.RelationalModel.Root, targetInfo.Resource);
        var uniqueColumns = new List<DbColumnName>(1 + targetIdentityColumns.Count)
        {
            RelationalNameConventions.DocumentIdColumnName,
        };
        uniqueColumns.AddRange(targetIdentityColumns);

        var uniqueName = BuildUniqueConstraintName(tableBuilder.Definition.Table.Name, uniqueColumns);

        if (!ContainsUniqueConstraint(tableBuilder.Constraints, uniqueName))
        {
            tableBuilder.AddConstraint(new TableConstraint.Unique(uniqueName, uniqueColumns.ToArray()));
            mutation.MarkTableMutated(entry.Model.RelationalModel.Root);
        }
    }

    private static TargetIdentityInfo GetTargetIdentityInfo(
        QualifiedResourceName targetResource,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IReadOnlyDictionary<
            QualifiedResourceName,
            AbstractIdentityTableInfo
        > abstractIdentityTablesByResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        IDictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource,
        IDictionary<QualifiedResourceName, TargetIdentityInfo> targetIdentityCache
    )
    {
        if (targetIdentityCache.TryGetValue(targetResource, out var cached))
        {
            return cached;
        }

        if (abstractIdentityTablesByResource.TryGetValue(targetResource, out var abstractTable))
        {
            var abstractIdentityColumns = abstractTable
                .ColumnsInIdentityOrder.Where(column => column.SourceJsonPath is not null)
                .ToArray();
            var identityPaths = abstractIdentityColumns
                .Select(column => column.SourceJsonPath!.Value)
                .ToArray();
            var columnNames = abstractIdentityColumns.Select(column => column.ColumnName).ToArray();

            var abstractInfo = new TargetIdentityInfo(
                targetResource,
                abstractTable.Table,
                identityPaths,
                columnNames,
                AllowIdentityUpdates: false,
                IsAbstract: true
            );

            targetIdentityCache[targetResource] = abstractInfo;
            return abstractInfo;
        }

        if (!resourcesByKey.TryGetValue(targetResource, out var entry))
        {
            throw new InvalidOperationException(
                $"Reference target resource '{FormatResource(targetResource)}' was not found "
                    + "for constraint derivation."
            );
        }

        if (!resourceContextsByResource.TryGetValue(targetResource, out var resourceContext))
        {
            throw new InvalidOperationException(
                $"Reference target resource '{FormatResource(targetResource)}' was not found "
                    + "in schema inputs."
            );
        }

        var builderContext = GetOrCreateBuilderContext(
            resourceContext,
            apiSchemaRootsByProjectEndpoint,
            builderContextsByResource
        );
        var identityColumns = BuildIdentityValueColumns(
            entry.Model.RelationalModel,
            builderContext,
            targetResource
        );
        var info = new TargetIdentityInfo(
            targetResource,
            entry.Model.RelationalModel.Root.Table,
            builderContext.IdentityJsonPaths,
            identityColumns,
            builderContext.AllowIdentityUpdates,
            IsAbstract: false
        );

        targetIdentityCache[targetResource] = info;
        return info;
    }

    private static ResourceMutation GetOrCreateMutation(
        QualifiedResourceName resource,
        ResourceEntry entry,
        IDictionary<QualifiedResourceName, ResourceMutation> mutations
    )
    {
        if (mutations.TryGetValue(resource, out var existing))
        {
            return existing;
        }

        var mutation = new ResourceMutation(entry);
        mutations[resource] = mutation;
        return mutation;
    }

    private static void AddUniqueColumn(
        DbColumnName columnName,
        ICollection<DbColumnName> columns,
        ISet<string> seenColumns
    )
    {
        if (!seenColumns.Add(columnName.Value))
        {
            return;
        }

        columns.Add(columnName);
    }

    private static bool EnsureDescriptorColumn(
        TableBuilder tableBuilder,
        DbTableModel rootTable,
        DbColumnModel column,
        string columnName
    )
    {
        if (
            rootTable.Columns.Any(existing =>
                string.Equals(existing.ColumnName.Value, columnName, StringComparison.Ordinal)
            )
        )
        {
            return false;
        }

        tableBuilder.AddColumn(column);

        return true;
    }

    private static DbColumnModel BuildUriColumn()
    {
        return new DbColumnModel(
            new DbColumnName(UriColumnLabel),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, UriMaxLength),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static DbColumnModel BuildDiscriminatorColumn()
    {
        return new DbColumnModel(
            new DbColumnName(DiscriminatorColumnLabel),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, DiscriminatorMaxLength),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static string BuildUniqueConstraintName(string tableName, IReadOnlyList<DbColumnName> columns)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Unique constraint must include at least one column.");
        }

        return $"UX_{tableName}_{string.Join("_", columns.Select(column => column.Value))}";
    }

    private static bool ContainsUniqueConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.Unique>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    private static bool ContainsForeignKeyConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.ForeignKey>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    private static bool ContainsAllOrNoneConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.AllOrNoneNullability>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    private static string BuildAllOrNoneConstraintName(string tableName, DbColumnName fkColumn)
    {
        return $"CK_{tableName}_{fkColumn.Value}_AllOrNone";
    }

    private static DbTableModel CanonicalizeTable(DbTableModel table)
    {
        var keyColumnOrder = BuildKeyColumnOrder(table.Key.Columns);

        var orderedColumns = table
            .Columns.OrderBy(column => GetColumnGroup(column, keyColumnOrder))
            .ThenBy(column => GetColumnKeyIndex(column, keyColumnOrder))
            .ThenBy(column => column.ColumnName.Value, StringComparer.Ordinal)
            .ToArray();

        var orderedConstraints = table
            .Constraints.OrderBy(GetConstraintGroup)
            .ThenBy(GetConstraintName, StringComparer.Ordinal)
            .ToArray();

        return table with
        {
            Columns = orderedColumns,
            Constraints = orderedConstraints,
        };
    }

    private static Dictionary<string, int> BuildKeyColumnOrder(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        Dictionary<string, int> keyOrder = new(StringComparer.Ordinal);

        for (var index = 0; index < keyColumns.Count; index++)
        {
            keyOrder[keyColumns[index].ColumnName.Value] = index;
        }

        return keyOrder;
    }

    private static int GetColumnGroup(DbColumnModel column, IReadOnlyDictionary<string, int> keyColumnOrder)
    {
        if (keyColumnOrder.ContainsKey(column.ColumnName.Value))
        {
            return 0;
        }

        return column.Kind switch
        {
            ColumnKind.DescriptorFk => 1,
            ColumnKind.Scalar => 2,
            _ => 3,
        };
    }

    private static int GetColumnKeyIndex(
        DbColumnModel column,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        return keyColumnOrder.TryGetValue(column.ColumnName.Value, out var index) ? index : int.MaxValue;
    }

    private static int GetConstraintGroup(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique => 1,
            TableConstraint.ForeignKey => 2,
            TableConstraint.AllOrNoneNullability => 3,
            _ => 99,
        };
    }

    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            _ => string.Empty,
        };
    }

    private static RelationalModelBuilderContext BuildResourceContext(
        ConcreteResourceSchemaContext resourceContext,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint
    )
    {
        var projectSchema = resourceContext.Project.ProjectSchema;
        var apiSchemaRoot = GetApiSchemaRoot(
            apiSchemaRootsByProjectEndpoint,
            projectSchema.ProjectEndpointName,
            resourceContext.Project.EffectiveProject.ProjectSchema
        );

        var builderContext = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = resourceContext.ResourceEndpointName,
        };

        new ExtractInputsStep().Execute(builderContext);

        return builderContext;
    }

    private static RelationalModelBuilderContext GetOrCreateBuilderContext(
        ConcreteResourceSchemaContext resourceContext,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        IDictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource
    )
    {
        var resource = new QualifiedResourceName(
            resourceContext.Project.ProjectSchema.ProjectName,
            resourceContext.ResourceName
        );

        if (builderContextsByResource.TryGetValue(resource, out var cached))
        {
            return cached;
        }

        var builderContext = BuildResourceContext(resourceContext, apiSchemaRootsByProjectEndpoint);
        builderContextsByResource[resource] = builderContext;
        return builderContext;
    }

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

    private static Dictionary<string, List<ResourceEntry>> BuildBaseResourceLookup(
        IReadOnlyList<ConcreteResourceModel> resources
    )
    {
        Dictionary<string, List<ResourceEntry>> lookup = new(StringComparer.Ordinal);

        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            var resourceName = resource.ResourceKey.Resource.ResourceName;

            if (!lookup.TryGetValue(resourceName, out var entries))
            {
                entries = [];
                lookup.Add(resourceName, entries);
            }

            entries.Add(new ResourceEntry(index, resource));
        }

        return lookup;
    }

    private static bool IsResourceExtension(ConcreteResourceSchemaContext resourceContext)
    {
        if (
            !resourceContext.ResourceSchema.TryGetPropertyValue(
                "isResourceExtension",
                out var resourceExtensionNode
            ) || resourceExtensionNode is null
        )
        {
            throw new InvalidOperationException(
                $"Expected isResourceExtension to be on ResourceSchema for resource "
                    + $"'{resourceContext.Project.ProjectSchema.ProjectName}:{resourceContext.ResourceName}', "
                    + "invalid ApiSchema."
            );
        }

        return resourceExtensionNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            _ => throw new InvalidOperationException(
                $"Expected isResourceExtension to be a boolean for resource "
                    + $"'{resourceContext.Project.ProjectSchema.ProjectName}:{resourceContext.ResourceName}', "
                    + "invalid ApiSchema."
            ),
        };
    }

    private static JsonObject GetApiSchemaRoot(
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        string projectEndpointName,
        JsonObject projectSchema
    )
    {
        if (apiSchemaRootsByProjectEndpoint.TryGetValue(projectEndpointName, out var apiSchemaRoot))
        {
            return apiSchemaRoot;
        }

        var detachedSchema = projectSchema.DeepClone();

        if (detachedSchema is not JsonObject detachedObject)
        {
            throw new InvalidOperationException("Project schema must be an object.");
        }

        apiSchemaRoot = new JsonObject { ["projectSchema"] = detachedObject };

        apiSchemaRootsByProjectEndpoint[projectEndpointName] = apiSchemaRoot;

        return apiSchemaRoot;
    }

    private sealed record ResourceEntry(int Index, ConcreteResourceModel Model);

    private sealed record TargetIdentityInfo(
        QualifiedResourceName Resource,
        DbTableName Table,
        IReadOnlyList<JsonPathExpression> IdentityJsonPaths,
        IReadOnlyList<DbColumnName> IdentityColumns,
        bool AllowIdentityUpdates,
        bool IsAbstract
    );

    private sealed record ReferenceIdentityColumnSet(
        IReadOnlyList<DbColumnName> LocalColumns,
        IReadOnlyList<DbColumnName> TargetColumns
    );

    private sealed class ResourceMutation
    {
        private readonly HashSet<TableKey> _mutatedTables = new();

        public ResourceMutation(ResourceEntry entry)
        {
            Entry = entry;
            TableBuilders = new Dictionary<TableKey, TableBuilder>();

            foreach (var table in entry.Model.RelationalModel.TablesInReadDependencyOrder)
            {
                var key = new TableKey(table.Table, table.JsonScope.Canonical);
                TableBuilders.TryAdd(key, new TableBuilder(table));
            }
        }

        public ResourceEntry Entry { get; }

        private Dictionary<TableKey, TableBuilder> TableBuilders { get; }

        public bool HasChanges => _mutatedTables.Count > 0;

        public TableBuilder GetTableBuilder(DbTableModel table, QualifiedResourceName resource)
        {
            var key = new TableKey(table.Table, table.JsonScope.Canonical);

            if (TableBuilders.TryGetValue(key, out var builder))
            {
                return builder;
            }

            throw new InvalidOperationException(
                $"Table '{table.Table}' scope '{table.JsonScope.Canonical}' for resource "
                    + $"'{FormatResource(resource)}' was not found for constraint derivation."
            );
        }

        public void MarkTableMutated(DbTableModel table)
        {
            _mutatedTables.Add(new TableKey(table.Table, table.JsonScope.Canonical));
        }

        public DbTableModel BuildTable(DbTableModel original)
        {
            var key = new TableKey(original.Table, original.JsonScope.Canonical);

            if (!TableBuilders.TryGetValue(key, out var builder))
            {
                throw new InvalidOperationException(
                    $"Table '{original.Table}' scope '{original.JsonScope.Canonical}' was not found "
                        + "for constraint derivation."
                );
            }

            var built = builder.Build();
            return _mutatedTables.Contains(key) ? CanonicalizeTable(built) : built;
        }

        private sealed record TableKey(DbTableName Table, string Scope);
    }

    private sealed class TableBuilder
    {
        private readonly Dictionary<string, JsonPathExpression?> _columnSources = new(StringComparer.Ordinal);

        public TableBuilder(DbTableModel table)
        {
            Definition = table;
            Columns = new List<DbColumnModel>(table.Columns);
            Constraints = new List<TableConstraint>(table.Constraints);

            foreach (var column in table.Columns)
            {
                _columnSources[column.ColumnName.Value] = column.SourceJsonPath;
            }

            foreach (var keyColumn in table.Key.Columns)
            {
                _columnSources.TryAdd(keyColumn.ColumnName.Value, null);
            }
        }

        public DbTableModel Definition { get; }

        public List<DbColumnModel> Columns { get; }

        public List<TableConstraint> Constraints { get; }

        public void AddColumn(DbColumnModel column)
        {
            if (_columnSources.TryGetValue(column.ColumnName.Value, out var existingSource))
            {
                var tableName = Definition.Table.Name;
                var existingPath = ResolveSourcePath(existingSource);
                var incomingPath = ResolveSourcePath(column.SourceJsonPath);

                throw new InvalidOperationException(
                    $"Column name '{column.ColumnName.Value}' is already defined on table '{tableName}'. "
                        + $"Colliding source paths '{existingPath}' and '{incomingPath}'. "
                        + "Use relational.nameOverrides to resolve the collision."
                );
            }

            _columnSources.Add(column.ColumnName.Value, column.SourceJsonPath);
            Columns.Add(column);
        }

        public void AddConstraint(TableConstraint constraint)
        {
            Constraints.Add(constraint);
        }

        public DbTableModel Build()
        {
            return Definition with { Columns = Columns.ToArray(), Constraints = Constraints.ToArray() };
        }

        private string ResolveSourcePath(JsonPathExpression? sourcePath)
        {
            return (sourcePath ?? Definition.JsonScope).Canonical;
        }
    }
}
