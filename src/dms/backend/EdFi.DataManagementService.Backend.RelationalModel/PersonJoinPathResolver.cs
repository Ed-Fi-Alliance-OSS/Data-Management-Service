// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Resolves the column-path chain from a subject resource to a named person resource
/// (<c>Student</c>, <c>Contact</c>, or <c>Staff</c>) via the subject's Student / Contact /
/// Staff securable element JSON paths.
/// </summary>
/// <remarks>
/// <para>Single source of truth for the person-join chain algorithm. Two call sites consume it:</para>
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
/// <para>Algorithm (matches <c>design-docs/auth.md</c> § "ResolveSecurableElementColumnPath"):</para>
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
///     full identity path must be present in the binding's <c>IdentityBindings</c>.
///     Paths that don't bind are accumulated into <c>unresolvedRootLevelPaths</c>; the caller
///     decides whether to throw (subject is not the person resource) or skip (subject IS the
///     person resource, the path is a self-reference).</description>
///   </item>
///   <item>
///     <description>If the binding's target is the person resource, the chain is a single
///     direct hop. Otherwise, walk transitively: at each intermediate resource, read that
///     resource's own declared <c>SecurableElements.Student/Contact/Staff</c> for the same
///     person kind, find the binding whose <c>IdentityBindings.ReferenceJsonPath</c> matches a
///     declared securable path, and follow it to the next resource. Repeat until the person
///     resource is reached. Walking arbitrary root bindings (BFS over all references) is
///     wrong: it can pick a non-securable optional reference as the chain hop, emitting auth
///     indexes that the runtime resolver does not actually use.</description>
///   </item>
///   <item>
///     <description>Dedupe by structural chain equality. Multiple paths resolving to the same
///     chain (key-unification alternatives) collapse to one. Paths producing different chains
///     are independent person references and yield one chain each — callers emit auth indexes
///     and runtime filter steps per chain.</description>
///   </item>
/// </list>
/// <para>Returns an empty list when no non-array-nested path resolves. Callers decide whether
/// that — combined with "subject is not the person resource itself" — means "unresolved"
/// (throw) or "OK, subject is the person and its own DocumentId is the anchor" (skip). Use
/// <see cref="IsPersonResource"/> for that check.</para>
/// </remarks>
public static class PersonJoinPathResolver
{
    /// <summary>
    /// Canonical project name of the Ed-Fi core data standard. Shared by callers that need to
    /// match a <see cref="QualifiedResourceName.ProjectName"/> against the core person resources
    /// (<c>Student</c>, <c>Contact</c>, <c>Staff</c>).
    /// </summary>
    public const string EdFiProjectName = "Ed-Fi";

    /// <summary>
    /// Resolves every distinct person-join chain from <paramref name="subjectResource"/> to the
    /// named person resource via the supplied securable element JSON paths.
    /// </summary>
    /// <remarks>
    /// Multiple JSON paths that fold onto the same <c>(table, FK, target, target-column)</c>
    /// sequence (key-unification alternatives) collapse into a single chain. Paths producing
    /// structurally different chains are independent person references and yield one chain
    /// each — callers emit indexes / runtime auth-filter steps per chain.
    /// </remarks>
    /// <param name="subjectResource">Subject resource declaring the person securable element(s).</param>
    /// <param name="personPaths">Securable element JSON paths for one person kind (Student / Contact / Staff).</param>
    /// <param name="personResourceName">Person resource name — <c>"Student"</c>, <c>"Contact"</c>, or <c>"Staff"</c>.</param>
    /// <param name="resourceLookup">All concrete resources keyed by qualified resource name.</param>
    /// <param name="skippedArrayNestedPaths">Mutable accumulator: array-nested paths (containing <c>[*]</c>) are appended here.</param>
    /// <param name="rootLevelPaths">Output: the subset of <paramref name="personPaths"/> that are not array-nested, in input order.</param>
    /// <param name="unresolvedRootLevelPaths">Output: root-level paths that did not bind to a <see cref="DocumentReferenceBinding"/>, in input order.</param>
    /// <returns>
    /// All distinct resolved chains (deduped by structural equality), in first-resolution order.
    /// Empty when no non-array-nested path resolved.
    /// </returns>
    public static IReadOnlyList<IReadOnlyList<ColumnPathStep>> ResolveDistinctPersonChains(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<string> personPaths,
        string personResourceName,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<string> skippedArrayNestedPaths,
        out IReadOnlyList<string> rootLevelPaths,
        out IReadOnlyList<string> unresolvedRootLevelPaths
    )
    {
        ArgumentNullException.ThrowIfNull(subjectResource);
        ArgumentNullException.ThrowIfNull(personPaths);
        ArgumentNullException.ThrowIfNull(personResourceName);
        ArgumentNullException.ThrowIfNull(resourceLookup);
        ArgumentNullException.ThrowIfNull(skippedArrayNestedPaths);

        rootLevelPaths = [];
        unresolvedRootLevelPaths = [];

        if (personPaths.Count == 0)
        {
            return [];
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
            return [];
        }

        var model = subjectResource.RelationalModel;
        var rootTable = model.Root;

        var resolvedChains = new List<IReadOnlyList<ColumnPathStep>>();
        var unresolved = new List<string>();

        foreach (var personPath in rootLevel)
        {
            var chain = TryResolveOneChain(
                subjectResource,
                rootTable,
                model,
                personPath,
                personResourceName,
                resourceLookup
            );
            if (chain is null)
            {
                unresolved.Add(personPath);
            }
            else
            {
                resolvedChains.Add(chain);
            }
        }

        unresolvedRootLevelPaths = unresolved;

        return resolvedChains.Distinct(ChainEqualityComparer.Instance).ToList();
    }

    private static IReadOnlyList<ColumnPathStep>? TryResolveOneChain(
        ConcreteResourceModel subjectResource,
        DbTableModel rootTable,
        RelationalResourceModel model,
        string personPath,
        string personResourceName,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup
    )
    {
        var binding = FindBindingByPersonPath(model, rootTable.Table, personPath);
        if (binding is null)
        {
            return null;
        }

        if (IsPersonResource(binding.TargetResource, personResourceName))
        {
            if (!resourceLookup.TryGetValue(binding.TargetResource, out var directTarget))
            {
                return null;
            }

            var fkColumn = ResolveToCanonicalColumn(rootTable, binding.FkColumn);
            return
            [
                new ColumnPathStep(
                    rootTable.Table,
                    fkColumn,
                    directTarget.RelationalModel.Root.Table,
                    RelationalNameConventions.DocumentIdColumnName
                ),
            ];
        }

        return WalkToPersonResource(subjectResource, rootTable, binding, personResourceName, resourceLookup);
    }

    private sealed class ChainEqualityComparer : IEqualityComparer<IReadOnlyList<ColumnPathStep>>
    {
        public static readonly ChainEqualityComparer Instance = new();

        public bool Equals(IReadOnlyList<ColumnPathStep>? x, IReadOnlyList<ColumnPathStep>? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null)
            {
                return false;
            }
            return x.SequenceEqual(y);
        }

        public int GetHashCode(IReadOnlyList<ColumnPathStep> obj)
        {
            var hc = new HashCode();
            foreach (var step in obj)
            {
                hc.Add(step);
            }
            return hc.ToHashCode();
        }
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

    /// <summary>
    /// Walks transitively from the first-hop intermediate to the person resource, following
    /// each intermediate's own declared <c>SecurableElements.{Student|Contact|Staff}</c> for the
    /// kind being resolved. The walk is deterministic — at every step it picks the binding whose
    /// <c>IdentityBindings.ReferenceJsonPath</c> matches a declared securable path on the
    /// current intermediate. Returns <see langword="null"/> if any step has no usable securable,
    /// the binding fails to resolve, or a cycle is hit.
    /// </summary>
    private static IReadOnlyList<ColumnPathStep>? WalkToPersonResource(
        ConcreteResourceModel subjectResource,
        DbTableModel subjectRootTable,
        DocumentReferenceBinding firstHopBinding,
        string personResourceName,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup
    )
    {
        var visited = new HashSet<QualifiedResourceName> { subjectResource.RelationalModel.Resource };

        var firstFkColumn = ResolveToCanonicalColumn(subjectRootTable, firstHopBinding.FkColumn);
        if (!resourceLookup.TryGetValue(firstHopBinding.TargetResource, out var currentResource))
        {
            return null;
        }

        var path = new List<ColumnPathStep>
        {
            new(
                subjectRootTable.Table,
                firstFkColumn,
                currentResource.RelationalModel.Root.Table,
                RelationalNameConventions.DocumentIdColumnName
            ),
        };

        visited.Add(firstHopBinding.TargetResource);

        while (!IsPersonResource(currentResource.RelationalModel.Resource, personResourceName))
        {
            var declaredPaths = SelectPersonSecurablePaths(currentResource, personResourceName);
            if (declaredPaths.Count == 0)
            {
                return null;
            }

            var currentModel = currentResource.RelationalModel;
            var currentRoot = currentModel.Root;

            DocumentReferenceBinding? nextBinding = null;
            foreach (var declaredPath in declaredPaths)
            {
                if (IsArrayNestedPath(declaredPath))
                {
                    continue;
                }

                nextBinding = FindBindingByPersonPath(currentModel, currentRoot.Table, declaredPath);
                if (nextBinding is not null)
                {
                    break;
                }
            }

            if (nextBinding is null || !visited.Add(nextBinding.TargetResource))
            {
                return null;
            }

            if (!resourceLookup.TryGetValue(nextBinding.TargetResource, out var nextResource))
            {
                return null;
            }

            var fkColumn = ResolveToCanonicalColumn(currentRoot, nextBinding.FkColumn);
            path.Add(
                new ColumnPathStep(
                    currentRoot.Table,
                    fkColumn,
                    nextResource.RelationalModel.Root.Table,
                    RelationalNameConventions.DocumentIdColumnName
                )
            );

            currentResource = nextResource;
        }

        return path;
    }

    private static IReadOnlyList<string> SelectPersonSecurablePaths(
        ConcreteResourceModel resource,
        string personResourceName
    ) =>
        personResourceName switch
        {
            "Student" => resource.SecurableElements.Student,
            "Contact" => resource.SecurableElements.Contact,
            "Staff" => resource.SecurableElements.Staff,
            _ => [],
        };

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
