// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Resolves the shortest column-path chain from a subject resource to a named person resource
/// (<c>Student</c>, <c>Contact</c>, or <c>Staff</c>) via the subject's Student / Contact /
/// Staff securable element JSON paths.
/// </summary>
/// <remarks>
/// <para>Single source of truth for the person-join BFS algorithm. Two call sites consume it:</para>
/// <list type="bullet">
///   <item>
///     <description><c>EdFi.DataManagementService.Backend.Plans.SecurableElementColumnPathResolver</c>
///     — at runtime, the resolver wires the chain into the auth-filter query plan.</description>
///   </item>
///   <item>
///     <description><c>EdFi.DataManagementService.Backend.RelationalModel.SetPasses.DeriveAuthorizationIndexInventoryPass</c>
///     — at DDL emission time, the pass walks each hop in the chain and emits the corresponding
///     covering auth index.</description>
///   </item>
/// </list>
/// <para>Algorithm:</para>
/// <list type="number">
///   <item>
///     <description>Partition <c>personPaths</c> into supported (root-level) and array-nested
///     (containing <c>[*]</c>); array-nested paths are unsupported and accumulated into
///     <c>skippedArrayNestedPaths</c>.</description>
///   </item>
///   <item>
///     <description>For each root-level path, find a
///     <see cref="DocumentReferenceBinding"/> on the subject's root table that owns an
///     <see cref="ReferenceIdentityBinding"/> whose <c>ReferenceJsonPath</c> equals the
///     securable path exactly. A reference-object prefix match alone is not enough — the
///     full identity path must be present in the binding's <c>IdentityBindings</c>.</description>
///   </item>
///   <item>
///     <description>If the binding's target is the person resource, the chain is a single
///     direct hop. Otherwise, run BFS over root-table bindings of intermediate resources
///     until the person resource is reached.</description>
///   </item>
///   <item>
///     <description>Return the shortest chain across all candidate paths. Each step's
///     source column is canonicalized through
///     <see cref="ColumnStorage.UnifiedAlias.CanonicalColumn"/>.</description>
///   </item>
/// </list>
/// <para>Returns <see langword="null"/> when no non-array-nested path resolves. Callers decide
/// whether that null combined with "subject is not the person resource itself" means
/// "unresolved" (throw) or "OK, subject is the person and its own DocumentId is the anchor"
/// (skip). Use <see cref="IsPersonResource"/> for that check.</para>
/// </remarks>
public static class PersonJoinPathResolver
{
    private const string EdFiProjectName = "Ed-Fi";
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    /// <summary>
    /// Resolves the shortest person-join chain from <paramref name="subjectResource"/> to the
    /// named person resource via the supplied securable element JSON paths.
    /// </summary>
    /// <param name="subjectResource">Subject resource declaring the person securable element(s).</param>
    /// <param name="personPaths">Securable element JSON paths for one person kind (Student / Contact / Staff).</param>
    /// <param name="personResourceName">Person resource name — <c>"Student"</c>, <c>"Contact"</c>, or <c>"Staff"</c>.</param>
    /// <param name="resourceLookup">All concrete resources keyed by qualified resource name.</param>
    /// <param name="skippedArrayNestedPaths">Mutable accumulator: array-nested paths (containing <c>[*]</c>) are appended here.</param>
    /// <param name="rootLevelPaths">Output: the subset of <paramref name="personPaths"/> that are not array-nested, in input order.</param>
    /// <returns>
    /// Shortest column-path chain, or <see langword="null"/> if no non-array-nested path resolved.
    /// </returns>
    public static IReadOnlyList<ColumnPathStep>? ResolveShortestPersonPath(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<string> personPaths,
        string personResourceName,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<string> skippedArrayNestedPaths,
        out IReadOnlyList<string> rootLevelPaths
    )
    {
        ArgumentNullException.ThrowIfNull(subjectResource);
        ArgumentNullException.ThrowIfNull(personPaths);
        ArgumentNullException.ThrowIfNull(personResourceName);
        ArgumentNullException.ThrowIfNull(resourceLookup);
        ArgumentNullException.ThrowIfNull(skippedArrayNestedPaths);

        if (personPaths.Count == 0)
        {
            rootLevelPaths = [];
            return null;
        }

        // Person path traversal currently follows only root-table bindings.
        var rootLevel = new List<string>();
        foreach (var p in personPaths)
        {
            if (IsArrayNestedPath(p))
            {
                skippedArrayNestedPaths.Add(p);
            }
            else
            {
                rootLevel.Add(p);
            }
        }

        rootLevelPaths = rootLevel;

        if (rootLevel.Count == 0)
        {
            return null;
        }

        var model = subjectResource.RelationalModel;
        var rootTable = model.Root;

        IReadOnlyList<ColumnPathStep>? shortestPath = null;

        foreach (var personPath in rootLevel)
        {
            var binding = FindBindingByPersonPath(model, rootTable.Table, personPath);
            if (binding is null)
            {
                continue;
            }

            if (IsPersonResource(binding.TargetResource, personResourceName))
            {
                if (!resourceLookup.TryGetValue(binding.TargetResource, out var directTarget))
                {
                    continue;
                }

                var fkColumn = ResolveToCanonicalColumn(rootTable, binding.FkColumn);
                var path = new List<ColumnPathStep>
                {
                    new(
                        rootTable.Table,
                        fkColumn,
                        directTarget.RelationalModel.Root.Table,
                        _documentIdColumn
                    ),
                };
                if (shortestPath is null || path.Count < shortestPath.Count)
                {
                    shortestPath = path;
                }
            }
            else
            {
                var chain = BfsToPersonResource(
                    subjectResource,
                    rootTable,
                    binding,
                    personResourceName,
                    resourceLookup
                );
                if (chain is not null && (shortestPath is null || chain.Count < shortestPath.Count))
                {
                    shortestPath = chain;
                }
            }
        }

        return shortestPath;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="resource"/> is the core Ed-Fi person
    /// resource named <paramref name="personResourceName"/> (both project and resource names
    /// must match to avoid homograph collisions).
    /// </summary>
    public static bool IsPersonResource(QualifiedResourceName resource, string personResourceName) =>
        string.Equals(resource.ProjectName, EdFiProjectName, StringComparison.Ordinal)
        && string.Equals(resource.ResourceName, personResourceName, StringComparison.Ordinal);

    /// <summary>
    /// Builds a <see cref="QualifiedResourceName"/> → <see cref="ConcreteResourceModel"/> lookup
    /// from the model set's concrete resource list. Duplicates (same qualified name appearing
    /// more than once) are silently ignored — the first wins.
    /// </summary>
    public static Dictionary<QualifiedResourceName, ConcreteResourceModel> BuildResourceLookup(
        IReadOnlyList<ConcreteResourceModel> allResources
    )
    {
        ArgumentNullException.ThrowIfNull(allResources);
        var lookup = new Dictionary<QualifiedResourceName, ConcreteResourceModel>(allResources.Count);
        foreach (var resource in allResources)
        {
            lookup.TryAdd(resource.ResourceKey.Resource, resource);
        }

        return lookup;
    }

    private static IReadOnlyList<ColumnPathStep>? BfsToPersonResource(
        ConcreteResourceModel subjectResource,
        DbTableModel subjectRootTable,
        DocumentReferenceBinding firstHopBinding,
        string personResourceName,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup
    )
    {
        var queue = new Queue<(QualifiedResourceName Resource, List<ColumnPathStep> Path)>();
        var visited = new HashSet<QualifiedResourceName> { subjectResource.RelationalModel.Resource };

        var firstFkColumn = ResolveToCanonicalColumn(subjectRootTable, firstHopBinding.FkColumn);
        if (!resourceLookup.TryGetValue(firstHopBinding.TargetResource, out var intermediateResource))
        {
            return null;
        }

        var firstStep = new ColumnPathStep(
            subjectRootTable.Table,
            firstFkColumn,
            intermediateResource.RelationalModel.Root.Table,
            _documentIdColumn
        );

        // Defensive: callers don't invoke BFS when the first hop is already the person resource
        // (they take a direct-hit branch), but mirror the original runtime resolver so behavior
        // is preserved if an unusual call path lands here.
        if (IsPersonResource(firstHopBinding.TargetResource, personResourceName))
        {
            return [firstStep];
        }

        visited.Add(firstHopBinding.TargetResource);
        queue.Enqueue((firstHopBinding.TargetResource, new List<ColumnPathStep> { firstStep }));

        while (queue.Count > 0)
        {
            var (currentResourceName, currentPath) = queue.Dequeue();

            if (!resourceLookup.TryGetValue(currentResourceName, out var currentResource))
            {
                continue;
            }

            var currentModel = currentResource.RelationalModel;
            var currentRoot = currentModel.Root;

            foreach (var binding in currentModel.DocumentReferenceBindings)
            {
                if (binding.Table != currentRoot.Table)
                {
                    continue;
                }

                if (visited.Contains(binding.TargetResource))
                {
                    continue;
                }

                if (!resourceLookup.TryGetValue(binding.TargetResource, out var targetResource))
                {
                    continue;
                }

                var fkColumn = ResolveToCanonicalColumn(currentRoot, binding.FkColumn);
                var step = new ColumnPathStep(
                    currentRoot.Table,
                    fkColumn,
                    targetResource.RelationalModel.Root.Table,
                    _documentIdColumn
                );

                var newPath = new List<ColumnPathStep>(currentPath) { step };

                if (IsPersonResource(binding.TargetResource, personResourceName))
                {
                    return newPath;
                }

                visited.Add(binding.TargetResource);
                queue.Enqueue((binding.TargetResource, newPath));
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a <see cref="DocumentReferenceBinding"/> on the specified table that owns a
    /// <see cref="ReferenceIdentityBinding"/> whose <c>ReferenceJsonPath</c> equals
    /// <paramref name="personPath"/> exactly. Implements the design contract from
    /// <c>auth.md</c>: the securable element path must match a real <c>referenceJsonPath</c>
    /// on the resource, not merely share a reference-object prefix.
    /// </summary>
    private static DocumentReferenceBinding? FindBindingByPersonPath(
        RelationalResourceModel model,
        DbTableName table,
        string personPath
    )
    {
        foreach (var binding in model.DocumentReferenceBindings)
        {
            if (binding.Table != table)
            {
                continue;
            }

            if (
                binding.IdentityBindings.Any(ib =>
                    string.Equals(ib.ReferenceJsonPath.Canonical, personPath, StringComparison.Ordinal)
                )
            )
            {
                return binding;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a column name to its canonical form through key unification. If the column on
    /// <paramref name="table"/> is a <see cref="ColumnStorage.UnifiedAlias"/>, returns the
    /// canonical column; otherwise returns the column as-is.
    /// </summary>
    public static DbColumnName ResolveToCanonicalColumn(DbTableModel table, DbColumnName column)
    {
        foreach (var col in table.Columns)
        {
            if (col.ColumnName == column && col.Storage is ColumnStorage.UnifiedAlias alias)
            {
                return alias.CanonicalColumn;
            }
        }

        return column;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the JSON path contains an array wildcard (<c>[*]</c>),
    /// indicating it traverses into a child table.
    /// </summary>
    private static bool IsArrayNestedPath(string jsonPath) =>
        jsonPath.Contains("[*]", StringComparison.Ordinal);
}
