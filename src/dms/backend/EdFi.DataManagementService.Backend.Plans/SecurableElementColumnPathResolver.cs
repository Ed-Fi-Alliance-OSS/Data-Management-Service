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
    ) => ResolveAll(subjectResource, PersonJoinPathResolver.BuildResourceLookup(allResources));

    public static IReadOnlyList<ResolvedSecurableElementPath> ResolveAll(
        ConcreteResourceModel subjectResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup
    )
    {
        ArgumentNullException.ThrowIfNull(subjectResource);
        ArgumentNullException.ThrowIfNull(resourceLookup);

        var securableElements = subjectResource.SecurableElements;
        if (!securableElements.HasAny)
        {
            return [];
        }

        var results = new List<ResolvedSecurableElementPath>();
        var unresolvedPaths = new List<string>();
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
            resolvedEdOrgCandidates.UnresolvedElements.Select(static element => element.JsonPath)
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

        ResolvePersonPaths(
            subjectResource,
            securableElements.Student,
            "Student",
            SecurableElementKind.Student,
            resourceLookup,
            results,
            unresolvedPaths
        );
        ResolvePersonPaths(
            subjectResource,
            securableElements.Contact,
            "Contact",
            SecurableElementKind.Contact,
            resourceLookup,
            results,
            unresolvedPaths
        );
        ResolvePersonPaths(
            subjectResource,
            securableElements.Staff,
            "Staff",
            SecurableElementKind.Staff,
            resourceLookup,
            results,
            unresolvedPaths
        );

        if (unresolvedPaths.Count > 0)
        {
            var resource = subjectResource.RelationalModel.Resource;
            throw new InvalidOperationException(
                $"Failed to resolve securable element column paths for resource "
                    + $"'{resource.ProjectName}.{resource.ResourceName}': "
                    + $"unresolved paths: {string.Join(", ", unresolvedPaths.Distinct(StringComparer.Ordinal))}"
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
            .. SecurableElementLocationResolver
                .ResolveAllCandidates(subjectResource, edOrgElement.JsonPath)
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
    /// Delegates to <see cref="SecurableElementLocationResolver"/> so the runtime mapping and the
    /// DDL index pass agree on which (table, column) carries the value for both root-level and
    /// array-nested paths.
    /// </summary>
    private static ColumnPathStep? ResolveEdOrgOrNamespacePath(
        ConcreteResourceModel resource,
        string securableElementPath
    ) => SecurableElementLocationResolver.ResolvePreferred(resource, securableElementPath);

    /// <summary>
    /// Resolves person (Student/Contact/Staff) securable element paths. One
    /// <see cref="ResolvedSecurableElementPath"/> is appended to <paramref name="results"/>
    /// per declared root-level person path that resolves to a DocumentId chain. Array-nested
    /// person paths are outside executable subject scope and are skipped here; the People
    /// authorization subject selector records them as skipped diagnostics. The shortest-path
    /// rule is scoped to alternate routes for that one declared path, not to collapsing
    /// independent declared paths for the same person kind. The exact self person identity path
    /// is the only path where a null resolved chain plus a root-level path is not an error — see
    /// <see cref="PersonJoinPathResolver.IsSelfPersonIdentityPath"/>.
    /// </summary>
    private static void ResolvePersonPaths(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<string> personPaths,
        string personResourceName,
        SecurableElementKind kind,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<ResolvedSecurableElementPath> results,
        List<string> unresolvedPaths
    )
    {
        if (personPaths.Count == 0)
        {
            return;
        }

        foreach (var personPath in personPaths)
        {
            List<string> skippedPathsForElement = [];
            var chain = PersonJoinPathResolver.ResolveShortestPersonChain(
                subjectResource,
                [personPath],
                personResourceName,
                resourceLookup,
                skippedPathsForElement,
                out var unresolvedRootLevelPaths
            );

            if (chain is not null)
            {
                results.Add(new ResolvedSecurableElementPath(kind, chain));
            }

            // Surface any root-level path that did not bind. Only the exact self identity
            // path on Student/Contact/Staff is zero-hop and intentionally skipped.
            foreach (var unresolvedPath in unresolvedRootLevelPaths)
            {
                if (
                    PersonJoinPathResolver.IsSelfPersonIdentityPath(
                        subjectResource.RelationalModel.Resource,
                        kind,
                        unresolvedPath
                    )
                )
                {
                    continue;
                }

                unresolvedPaths.Add(unresolvedPath);
            }
        }
    }

    private static ResolvedEdOrgSecurableElementCandidate? SelectPreferredSingleStepCandidate(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<ResolvedEdOrgSecurableElementCandidate> candidates
    )
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var preferredStep = SecurableElementLocationResolver.SelectPreferred(
            subjectResource,
            candidates.Select(c => c.Step).ToList()
        );

        return preferredStep is null ? null : candidates.First(c => c.Step == preferredStep);
    }
}
