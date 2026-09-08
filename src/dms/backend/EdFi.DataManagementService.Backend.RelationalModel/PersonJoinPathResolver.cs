// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Resolves column-path chains from a subject resource to a named person resource
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
/// <para>Algorithm (matches <c>design-docs/auth.md</c> § "ResolveSecurableElementColumnPath",
/// L879: "the function should follow each path and pick the shortest one to minimize the number
/// of joins"):</para>
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
///     Paths that don't bind are accumulated into <c>unresolvedRootLevelPaths</c>; callers
///     suppress only paths accepted by <see cref="IsSelfPersonIdentityPath"/>.</description>
///   </item>
///   <item>
///     <description>If the binding's target is the person resource, the chain is a single
///     direct hop. Otherwise, walk transitively: at each intermediate resource, enumerate
///     that resource's own declared <c>SecurableElements.Student/Contact/Staff</c> for the
///     same person kind, find the binding whose <c>IdentityBindings.ReferenceJsonPath</c>
///     matches each declared securable path, and recursively resolve the remainder of the
///     chain through each candidate. Pick the shortest successful continuation. A declared
///     securable that leads to a cycle or unresolvable target is skipped — alternate
///     declared securables at the same hop are still tried. Walking arbitrary root bindings
///     (BFS over all references) is wrong: it can pick a non-securable optional reference
///     as the chain hop, emitting auth indexes that the runtime resolver does not use.</description>
///   </item>
///   <item>
///     <description>Among all supplied root-level paths that produced a chain, return the
///     shortest. Ties break first-wins (input order). Callers that need executable metadata for
///     every independent declared person path call this resolver once per path; the shortest
///     choice then applies only to alternate continuation routes for that declared path.</description>
///   </item>
/// </list>
/// <para>Returns <see langword="null"/> when no non-array-nested path resolves. Callers decide
/// whether that — combined with <see cref="IsSelfPersonIdentityPath"/> — means "unresolved"
/// (throw) or "OK, this exact self path uses the person resource's own DocumentId anchor"
/// (skip).</para>
/// </remarks>
public static class PersonJoinPathResolver
{
    /// <summary>
    /// Resolves the shortest person-join chain from <paramref name="subjectResource"/> to the
    /// named person resource via the supplied securable element JSON paths.
    /// </summary>
    /// <remarks>
    /// If multiple root-level person paths are supplied, the function follows each one and
    /// returns the shortest resolved chain. Equal-length ties break first-wins in input order.
    /// Runtime People subject planning, compiled mapping metadata, and authorization index
    /// derivation pass one declared person path at a time so independent paths are preserved.
    /// Paths that fail to resolve (binding not found, target missing from the resource lookup,
    /// all declared securables at some intermediate cycle or dead-end) are accumulated into
    /// <paramref name="unresolvedRootLevelPaths"/> for callers to surface.
    /// </remarks>
    /// <param name="subjectResource">Subject resource declaring the person securable element(s).</param>
    /// <param name="personPaths">Securable element JSON paths for one person kind (Student / Contact / Staff).</param>
    /// <param name="personResourceName">Person resource name — <c>"Student"</c>, <c>"Contact"</c>, or <c>"Staff"</c>.</param>
    /// <param name="resourceLookup">All concrete resources keyed by qualified resource name.</param>
    /// <param name="skippedArrayNestedPaths">Mutable accumulator: array-nested paths (containing <c>[*]</c>) are appended here.</param>
    /// <param name="unresolvedRootLevelPaths">Output: root-level paths that did not produce a chain, in input order.</param>
    /// <returns>
    /// The shortest resolved chain, or <see langword="null"/> when no non-array-nested path
    /// resolved.
    /// </returns>
    public static IReadOnlyList<ColumnPathStep>? ResolveShortestPersonChain(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<string> personPaths,
        string personResourceName,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<string> skippedArrayNestedPaths,
        out IReadOnlyList<string> unresolvedRootLevelPaths
    )
    {
        ArgumentNullException.ThrowIfNull(subjectResource);
        ArgumentNullException.ThrowIfNull(personPaths);
        ArgumentNullException.ThrowIfNull(personResourceName);
        ArgumentNullException.ThrowIfNull(resourceLookup);
        ArgumentNullException.ThrowIfNull(skippedArrayNestedPaths);

        unresolvedRootLevelPaths = [];

        if (personPaths.Count == 0)
        {
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

        if (rootLevel.Count == 0)
        {
            return null;
        }

        var model = subjectResource.RelationalModel;
        var rootTable = model.Root;

        IReadOnlyList<ColumnPathStep>? shortest = null;
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
            else if (shortest is null || chain.Count < shortest.Count)
            {
                shortest = chain;
            }
        }

        unresolvedRootLevelPaths = unresolved;

        return shortest;
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

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="resource"/> is the core Ed-Fi person
    /// resource named <paramref name="personResourceName"/> (both project and resource names
    /// must match to avoid homograph collisions).
    /// </summary>
    public static bool IsPersonResource(QualifiedResourceName resource, string personResourceName) =>
        DataModelConstants.IsCoreProjectName(resource.ProjectName)
        && string.Equals(resource.ResourceName, personResourceName, StringComparison.Ordinal);

    /// <summary>
    /// Returns <see langword="true"/> only for the exact self identity path on a core Ed-Fi
    /// person resource. Other same-kind paths on the person resource still require a resolvable
    /// DocumentId reference path.
    /// </summary>
    public static bool IsSelfPersonIdentityPath(
        QualifiedResourceName resource,
        SecurableElementKind securableElementKind,
        string personPath
    ) =>
        IsPersonResource(resource, GetPersonResourceName(securableElementKind))
        && string.Equals(
            personPath,
            GetSelfPersonIdentityJsonPath(securableElementKind),
            StringComparison.Ordinal
        );

    private static string GetSelfPersonIdentityJsonPath(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "$.studentUniqueId",
            SecurableElementKind.Contact => "$.contactUniqueId",
            SecurableElementKind.Staff => "$.staffUniqueId",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported relationship authorization person securable element kind."
            ),
        };

    private static string GetPersonResourceName(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "Student",
            SecurableElementKind.Contact => "Contact",
            SecurableElementKind.Staff => "Staff",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported relationship authorization person securable element kind."
            ),
        };

    /// <summary>
    /// Builds a <see cref="QualifiedResourceName"/> → <see cref="ConcreteResourceModel"/> lookup
    /// from the model set's concrete resource list. Duplicates (same qualified name appearing
    /// more than once) are silently ignored — the first wins.
    /// </summary>
    public static Dictionary<QualifiedResourceName, ConcreteResourceModel> BuildResourceLookup(
        IReadOnlyList<ConcreteResourceModel> allResources
    )
    {
        return MappingSetResourceLookupExtensions.BuildConcreteResourceModelsByResource(allResources);
    }

    /// <summary>
    /// Walks transitively from the first-hop intermediate to the person resource. At each
    /// intermediate, enumerates the resource's own declared
    /// <c>SecurableElements.{Student|Contact|Staff}</c> for the kind being resolved, finds the
    /// <see cref="DocumentReferenceBinding"/> whose <c>IdentityBindings.ReferenceJsonPath</c>
    /// matches each declared securable path, recursively resolves the remainder of the chain
    /// through each candidate, and returns the shortest successful continuation. Declared
    /// securables that lead to a cycle, an unresolvable target, or a dead-end are skipped —
    /// alternate declared securables at the same hop are still tried. Returns
    /// <see langword="null"/> when every branch fails.
    /// </summary>
    private static IReadOnlyList<ColumnPathStep>? WalkToPersonResource(
        ConcreteResourceModel subjectResource,
        DbTableModel subjectRootTable,
        DocumentReferenceBinding firstHopBinding,
        string personResourceName,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup
    )
    {
        if (!resourceLookup.TryGetValue(firstHopBinding.TargetResource, out var firstTarget))
        {
            return null;
        }

        var visited = new HashSet<QualifiedResourceName>
        {
            subjectResource.RelationalModel.Resource,
            firstHopBinding.TargetResource,
        };

        var continuation = ShortestContinuationToPersonResource(
            firstTarget,
            personResourceName,
            resourceLookup,
            visited
        );
        if (continuation is null)
        {
            return null;
        }

        var firstFkColumn = ResolveToCanonicalColumn(subjectRootTable, firstHopBinding.FkColumn);
        var path = new List<ColumnPathStep>(1 + continuation.Count)
        {
            new(
                subjectRootTable.Table,
                firstFkColumn,
                firstTarget.RelationalModel.Root.Table,
                RelationalNameConventions.DocumentIdColumnName
            ),
        };
        path.AddRange(continuation);

        return path;
    }

    /// <summary>
    /// Recursively resolves the shortest tail-of-chain from <paramref name="currentResource"/>
    /// to the person resource. Returns an empty list when <paramref name="currentResource"/> is
    /// already the person resource, the shortest non-empty continuation when one exists, or
    /// <see langword="null"/> when every declared securable at this hop dead-ends.
    /// <paramref name="visited"/> is mutated for backtracking — each candidate adds its target
    /// before recursing and removes it after, so distinct branches do not poison each other's
    /// cycle detection.
    /// </summary>
    private static IReadOnlyList<ColumnPathStep>? ShortestContinuationToPersonResource(
        ConcreteResourceModel currentResource,
        string personResourceName,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        HashSet<QualifiedResourceName> visited
    )
    {
        if (IsPersonResource(currentResource.RelationalModel.Resource, personResourceName))
        {
            return [];
        }

        var declaredPaths = SelectPersonSecurablePaths(currentResource, personResourceName);
        if (declaredPaths.Count == 0)
        {
            return null;
        }

        var currentModel = currentResource.RelationalModel;
        var currentRoot = currentModel.Root;

        IReadOnlyList<ColumnPathStep>? best = null;

        foreach (var declaredPath in declaredPaths)
        {
            if (IsArrayNestedPath(declaredPath))
            {
                continue;
            }

            var nextBinding = FindBindingByPersonPath(currentModel, currentRoot.Table, declaredPath);
            if (nextBinding is null)
            {
                continue;
            }

            if (!visited.Add(nextBinding.TargetResource))
            {
                continue;
            }

            try
            {
                if (!resourceLookup.TryGetValue(nextBinding.TargetResource, out var nextResource))
                {
                    continue;
                }

                var tail = ShortestContinuationToPersonResource(
                    nextResource,
                    personResourceName,
                    resourceLookup,
                    visited
                );
                if (tail is null)
                {
                    continue;
                }

                var fkColumn = ResolveToCanonicalColumn(currentRoot, nextBinding.FkColumn);
                var candidate = new List<ColumnPathStep>(1 + tail.Count)
                {
                    new(
                        currentRoot.Table,
                        fkColumn,
                        nextResource.RelationalModel.Root.Table,
                        RelationalNameConventions.DocumentIdColumnName
                    ),
                };
                candidate.AddRange(tail);

                if (best is null || candidate.Count < best.Count)
                {
                    best = candidate;
                }
            }
            finally
            {
                visited.Remove(nextBinding.TargetResource);
            }
        }

        return best;
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
