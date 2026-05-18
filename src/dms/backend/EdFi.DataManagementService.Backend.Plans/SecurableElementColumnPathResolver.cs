// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;

namespace EdFi.DataManagementService.Backend.Plans;

internal sealed record ResolvedEdOrgSecurableElementCandidate(
    string JsonPath,
    string ReadableName,
    ColumnPathStep Step
);

internal sealed record ResolvedEdOrgSecurableElementCandidateResolution(
    IReadOnlyList<ResolvedEdOrgSecurableElementCandidate> ResolvedCandidates,
    IReadOnlyList<EdOrgSecurableElement> UnresolvedElements
);

/// <summary>
/// Resolves securable element authorization column paths from the derived relational model.
/// Given a subject resource and its securable elements, produces the chain of table joins
/// needed to reach the authorization column.
/// </summary>
internal static class SecurableElementColumnPathResolver
{
    /// <summary>
    /// Resolves all securable element column paths for a single concrete resource.
    /// Returns a list of resolved paths, each carrying the element kind and the column path chain.
    /// </summary>
    public static IReadOnlyList<ResolvedSecurableElementPath> ResolveAll(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<ConcreteResourceModel> allResources
    )
    {
        var securableElements = subjectResource.SecurableElements;
        if (!securableElements.HasAny)
        {
            return [];
        }

        var results = new List<ResolvedSecurableElementPath>();
        var unresolvedPaths = new List<string>();
        var skippedArrayNestedPaths = new List<string>();
        var resolvedEdOrgCandidates = ResolveEducationOrganizationCandidates(subjectResource);
        var resolvedEdOrgByJsonPath = resolvedEdOrgCandidates
            .ResolvedCandidates.GroupBy(static candidate => candidate.JsonPath, StringComparer.Ordinal)
            .ToDictionary(
                static grouping => grouping.Key,
                static grouping => grouping.ToArray(),
                StringComparer.Ordinal
            );

        foreach (var edOrgElement in securableElements.EducationOrganization)
        {
            if (
                !resolvedEdOrgByJsonPath.TryGetValue(edOrgElement.JsonPath, out var candidates)
                || candidates.Length == 0
            )
            {
                continue;
            }

            var preferredCandidate = SelectPreferredSingleStepCandidate(subjectResource, candidates);

            if (preferredCandidate is not null)
            {
                results.Add(
                    new ResolvedSecurableElementPath(
                        SecurableElementKind.EducationOrganization,
                        [preferredCandidate.Step]
                    )
                );
            }
        }

        unresolvedPaths.AddRange(
            resolvedEdOrgCandidates
                .UnresolvedElements.Select(static element => element.JsonPath)
                .Distinct(StringComparer.Ordinal)
        );

        foreach (string ns in securableElements.Namespace)
        {
            var path = ResolveEdOrgOrNamespacePath(subjectResource, ns);
            if (path is not null)
            {
                results.Add(new ResolvedSecurableElementPath(SecurableElementKind.Namespace, [path]));
            }
            else
            {
                unresolvedPaths.Add(ns);
            }
        }

        var resourceLookup = PersonJoinPathResolver.BuildResourceLookup(allResources);

        ResolvePersonPaths(
            subjectResource,
            securableElements.Student,
            "Student",
            SecurableElementKind.Student,
            resourceLookup,
            results,
            unresolvedPaths,
            skippedArrayNestedPaths
        );
        ResolvePersonPaths(
            subjectResource,
            securableElements.Contact,
            "Contact",
            SecurableElementKind.Contact,
            resourceLookup,
            results,
            unresolvedPaths,
            skippedArrayNestedPaths
        );
        ResolvePersonPaths(
            subjectResource,
            securableElements.Staff,
            "Staff",
            SecurableElementKind.Staff,
            resourceLookup,
            results,
            unresolvedPaths,
            skippedArrayNestedPaths
        );

        if (unresolvedPaths.Count > 0)
        {
            var resource = subjectResource.RelationalModel.Resource;
            throw new InvalidOperationException(
                $"Failed to resolve securable element column paths for resource "
                    + $"'{resource.ProjectName}.{resource.ResourceName}': "
                    + $"unresolved paths: {string.Join(", ", unresolvedPaths)}"
            );
        }

        if (results.Count == 0 && skippedArrayNestedPaths.Count > 0)
        {
            var resource = subjectResource.RelationalModel.Resource;
            throw new InvalidOperationException(
                $"Failed to resolve securable element column paths for resource "
                    + $"'{resource.ProjectName}.{resource.ResourceName}': "
                    + $"all paths require unsupported child-table traversal (array-nested): "
                    + $"{string.Join(", ", skippedArrayNestedPaths)}"
            );
        }

        return results;
    }

    internal static ResolvedEdOrgSecurableElementCandidateResolution ResolveEducationOrganizationCandidates(
        ConcreteResourceModel subjectResource
    )
    {
        ArgumentNullException.ThrowIfNull(subjectResource);

        List<ResolvedEdOrgSecurableElementCandidate> resolvedCandidates = [];
        List<EdOrgSecurableElement> unresolvedElements = [];

        foreach (var edOrgElement in subjectResource.SecurableElements.EducationOrganization)
        {
            var candidates = ResolveEducationOrganizationCandidates(subjectResource, edOrgElement);

            if (candidates.Count == 0)
            {
                unresolvedElements.Add(edOrgElement);
                continue;
            }

            resolvedCandidates.AddRange(candidates);
        }

        return new ResolvedEdOrgSecurableElementCandidateResolution(resolvedCandidates, unresolvedElements);
    }

    internal static IReadOnlyList<ResolvedEdOrgSecurableElementCandidate> ResolveEducationOrganizationCandidates(
        ConcreteResourceModel subjectResource,
        EdOrgSecurableElement edOrgElement
    )
    {
        ArgumentNullException.ThrowIfNull(subjectResource);
        ArgumentNullException.ThrowIfNull(edOrgElement);

        return
        [
            .. ResolveEdOrgOrNamespaceCandidates(subjectResource, edOrgElement.JsonPath)
                .Select(candidate => new ResolvedEdOrgSecurableElementCandidate(
                    edOrgElement.JsonPath,
                    edOrgElement.MetaEdName,
                    candidate
                )),
        ];
    }

    /// <summary>
    /// Resolves an EdOrg or Namespace securable element to a single column path step
    /// (<see cref="ColumnPathStep.TargetTable"/> and <see cref="ColumnPathStep.TargetColumnName"/>
    /// are <see langword="null"/> — the auth value lives at <c>SourceTable.SourceColumnName</c>).
    /// Walks every <see cref="DocumentReferenceBinding"/> (root or child) for an identity binding
    /// whose <c>ReferenceJsonPath</c> matches; falls back to scanning every table for a scalar
    /// column whose <c>SourceJsonPath</c> matches. Mirrors
    /// <c>DeriveAuthorizationIndexInventoryPass.ResolveSecurableElementLocation</c> so the
    /// runtime mapping and the DDL index pass agree on which (table, column) carries the value
    /// for both root-level and array-nested paths.
    /// </summary>
    private static ColumnPathStep? ResolveEdOrgOrNamespacePath(
        ConcreteResourceModel resource,
        string securableElementPath
    )
    {
        var candidates = ResolveEdOrgOrNamespaceCandidates(resource, securableElementPath);
        return candidates.Count == 0
            ? null
            : SelectPreferredSingleStepCandidate(resource, candidates, securableElementPath);
    }

    private static DbTableModel? FindTable(RelationalResourceModel model, DbTableName tableName)
    {
        foreach (var t in model.TablesInDependencyOrder)
        {
            if (t.Table == tableName)
            {
                return t;
            }
        }

        return null;
    }

    private static IReadOnlyList<ColumnPathStep> ResolveEdOrgOrNamespaceCandidates(
        ConcreteResourceModel resource,
        string securableElementPath
    )
    {
        var model = resource.RelationalModel;
        List<ColumnPathStep> candidates = [];
        HashSet<ColumnPathStep> seenCandidates = [];

        foreach (var binding in model.DocumentReferenceBindings)
        {
            var identityBinding = binding.IdentityBindings.FirstOrDefault(ib =>
                string.Equals(ib.ReferenceJsonPath.Canonical, securableElementPath, StringComparison.Ordinal)
            );

            if (identityBinding is null)
            {
                continue;
            }

            var owningTable = FindTable(model, binding.Table);
            var column = owningTable is null
                ? identityBinding.Column
                : ResolveToCanonicalColumn(owningTable, identityBinding.Column);
            var candidate = new ColumnPathStep(binding.Table, column, null, null);

            if (seenCandidates.Add(candidate))
            {
                candidates.Add(candidate);
            }
        }

        foreach (var table in model.TablesInDependencyOrder)
        {
            foreach (var column in table.Columns)
            {
                if (
                    column.SourceJsonPath is null
                    || !string.Equals(
                        column.SourceJsonPath.Value.Canonical,
                        securableElementPath,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                var resolved = ResolveToCanonicalColumn(table, column.ColumnName);
                var candidate = new ColumnPathStep(table.Table, resolved, null, null);

                if (seenCandidates.Add(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Resolves person (Student/Contact/Staff) securable element paths. Delegates the shortest-
    /// path resolution to <see cref="PersonJoinPathResolver.ResolveShortestPersonPath"/> and
    /// folds the result into <paramref name="results"/> /
    /// <paramref name="unresolvedPaths"/>. The "subject IS the person resource" case is the only
    /// path where a null shortest-path is not an error — see
    /// <see cref="PersonJoinPathResolver.IsPersonResource"/>.
    /// </summary>
    private static void ResolvePersonPaths(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<string> personPaths,
        string personResourceName,
        SecurableElementKind kind,
        Dictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<ResolvedSecurableElementPath> results,
        List<string> unresolvedPaths,
        List<string> skippedArrayNestedPaths
    )
    {
        if (personPaths.Count == 0)
        {
            return;
        }

        var rootLevelPathsForUnresolved = personPaths
            .Where(p => !p.Contains("[*]", StringComparison.Ordinal))
            .ToArray();

        var shortestPath = PersonJoinPathResolver.ResolveShortestPersonPath(
            subjectResource,
            personPaths,
            personResourceName,
            resourceLookup,
            skippedArrayNestedPaths
        );

        if (shortestPath is not null)
        {
            results.Add(new ResolvedSecurableElementPath(kind, shortestPath));
        }
        else if (
            rootLevelPathsForUnresolved.Length > 0
            && !PersonJoinPathResolver.IsPersonResource(
                subjectResource.RelationalModel.Resource,
                personResourceName
            )
        )
        {
            // Only flag as unresolved when the subject resource is NOT the person resource
            // itself. Person resources (e.g., Contact with $.contactUniqueId) don't need
            // a join chain — their own identity column is the authorization anchor.
            unresolvedPaths.AddRange(rootLevelPathsForUnresolved);
        }
    }

    /// <summary>
    /// Resolves a column name to its canonical form through key unification.
    /// If the column is a <see cref="ColumnStorage.UnifiedAlias"/>, returns the canonical column;
    /// otherwise returns the column as-is.
    /// </summary>
    private static DbColumnName ResolveToCanonicalColumn(DbTableModel table, DbColumnName column)
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

    private static ResolvedEdOrgSecurableElementCandidate? SelectPreferredSingleStepCandidate(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<ResolvedEdOrgSecurableElementCandidate> candidates
    )
    {
        return candidates
            .OrderBy(candidate => GetSingleStepCandidatePriority(subjectResource, candidate.Step))
            .ThenBy(static candidate => candidate.JsonPath.Length)
            .ThenBy(static candidate => candidate.JsonPath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Step.SourceTable.ToString(), StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Step.SourceColumnName.Value, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static ColumnPathStep? SelectPreferredSingleStepCandidate(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<ColumnPathStep> candidates,
        string securableElementPath
    )
    {
        return candidates
            .OrderBy(candidate => GetSingleStepCandidatePriority(subjectResource, candidate))
            .ThenBy(_ => securableElementPath.Length)
            .ThenBy(_ => securableElementPath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.SourceTable.ToString(), StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.SourceColumnName.Value, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int GetSingleStepCandidatePriority(
        ConcreteResourceModel subjectResource,
        ColumnPathStep candidate
    )
    {
        if (candidate.SourceTable == subjectResource.RelationalModel.Root.Table)
        {
            return 0;
        }

        var table = FindTable(subjectResource.RelationalModel, candidate.SourceTable);
        return table is null ? int.MaxValue : GetJsonScopeDepth(table.JsonScope) + 1;
    }

    private static int GetJsonScopeDepth(JsonPathExpression jsonScope)
    {
        return jsonScope.Segments.Count;
    }
}
