// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

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
    private static readonly DbTableName DescriptorTable = new(new DbSchemaName("dms"), "Descriptor");

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

    /// <summary>
    /// Resolves the preferred join path from a subject resource name to a basis resource name.
    /// This overload matches the view-based authorization design contract.
    /// </summary>
    public static IReadOnlyList<ColumnPathStep> ResolveSecurableElementColumnPath(
        QualifiedResourceName subjectResource,
        QualifiedResourceName basisResource,
        DerivedRelationalModelSet modelSet
    ) => ResolveBasisResourcePath(subjectResource, basisResource, modelSet);

    /// <summary>
    /// Resolves the preferred join path from a subject resource name to a basis resource name.
    /// </summary>
    public static IReadOnlyList<ColumnPathStep> ResolveBasisResourcePath(
        QualifiedResourceName subjectResource,
        QualifiedResourceName basisResource,
        DerivedRelationalModelSet modelSet
    )
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        var resourceLookup = modelSet.GetConcreteResourceModelsByResource();
        return
            !resourceLookup.TryGetValue(subjectResource, out var subjectConcreteResource)
            || !IsKnownBasisResource(basisResource, resourceLookup, modelSet.AbstractUnionViewsInNameOrder)
            ? []
            : ResolveBasisResourcePath(
                subjectConcreteResource,
                basisResource,
                resourceLookup,
                modelSet.AbstractUnionViewsInNameOrder
            );
    }

    private static bool IsKnownBasisResource(
        QualifiedResourceName basisResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        IReadOnlyList<AbstractUnionViewInfo> abstractUnionViews
    ) =>
        resourceLookup.ContainsKey(basisResource)
        || abstractUnionViews.Any(view =>
            view.AbstractResourceKey.Resource == basisResource
            || view.UnionArmsInOrder.Any(arm => arm.ConcreteMemberResourceKey.Resource == basisResource)
        );

    /// <summary>
    /// Resolves the preferred join path from a subject resource model to a basis resource.
    /// Returns an ordered list of column-path steps that end at the basis resource's DocumentId.
    /// </summary>
    public static IReadOnlyList<ColumnPathStep> ResolveBasisResourcePath(
        ConcreteResourceModel subjectResource,
        QualifiedResourceName basisResource,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        IReadOnlyList<AbstractUnionViewInfo> abstractUnionViews
    )
    {
        ArgumentNullException.ThrowIfNull(subjectResource);
        ArgumentNullException.ThrowIfNull(resourceLookup);
        ArgumentNullException.ThrowIfNull(abstractUnionViews);

        var subjectModel = subjectResource.RelationalModel;
        var subjectRoot = subjectModel.Root;
        var abstractBasisMembersByResource = abstractUnionViews
            .GroupBy(static view => view.AbstractResourceKey.Resource)
            .ToDictionary(
                static grouping => grouping.Key,
                grouping =>
                    grouping
                        .SelectMany(static view => view.UnionArmsInOrder)
                        .Select(static arm => arm.ConcreteMemberResourceKey.Resource)
                        .ToHashSet()
            );
        var basisIsAbstract = abstractBasisMembersByResource.ContainsKey(basisResource);

        if (IsBasisMatch(subjectModel.Resource, basisResource, abstractBasisMembersByResource))
        {
            return
            [
                new ColumnPathStep(
                    subjectRoot.Table,
                    PersonJoinPathResolver.ResolveToCanonicalColumn(
                        subjectRoot,
                        RelationalNameConventions.DocumentIdColumnName
                    ),
                    null,
                    null
                ),
            ];
        }

        var candidates = Explore(subjectModel, [subjectModel.Resource], [], []);

        if (candidates.Count == 0)
        {
            return [];
        }

        var bestCandidate = candidates[0];
        for (var index = 1; index < candidates.Count; index++)
        {
            if (CompareCandidates(candidates[index], bestCandidate) < 0)
            {
                bestCandidate = candidates[index];
            }
        }

        return bestCandidate.Steps;

        List<(
            IReadOnlyList<(bool IsIdentity, bool IsRequired, bool IsRoleNamed)> Hops,
            bool PreferExactAbstractBasis,
            IReadOnlyList<ColumnPathStep> Steps
        )> Explore(
            RelationalResourceModel currentModel,
            HashSet<QualifiedResourceName> visitedResources,
            List<(bool IsIdentity, bool IsRequired, bool IsRoleNamed)> hopsSoFar,
            List<ColumnPathStep> stepsSoFar
        )
        {
            var foundCandidates =
                new List<(
                    IReadOnlyList<(bool IsIdentity, bool IsRequired, bool IsRoleNamed)> Hops,
                    bool PreferExactAbstractBasis,
                    IReadOnlyList<ColumnPathStep> Steps
                )>();

            foreach (var binding in currentModel.DocumentReferenceBindings)
            {
                var owningTable = FindOwningTable(currentModel, binding.Table);
                if (owningTable is null)
                {
                    continue;
                }

                if (visitedResources.Contains(binding.TargetResource))
                {
                    continue;
                }

                if (hopsSoFar.Count > 0 && !binding.IsIdentityComponent)
                {
                    continue;
                }

                var sourceColumnName = PersonJoinPathResolver.ResolveToCanonicalColumn(
                    owningTable,
                    binding.FkColumn
                );
                var terminalMatch = IsBasisMatch(
                    binding.TargetResource,
                    basisResource,
                    abstractBasisMembersByResource
                );
                if (terminalMatch && !IsKnownBasisResource(basisResource, resourceLookup, abstractUnionViews))
                {
                    continue;
                }

                if (!terminalMatch)
                {
                    if (!resourceLookup.TryGetValue(binding.TargetResource, out var nextResource))
                    {
                        continue;
                    }

                    var nextSteps = CreateStepsToOwningTable(currentModel, owningTable, stepsSoFar);
                    if (nextSteps is null)
                    {
                        continue;
                    }

                    nextSteps.Add(
                        new(
                            owningTable.Table,
                            sourceColumnName,
                            nextResource.RelationalModel.Root.Table,
                            PersonJoinPathResolver.ResolveToCanonicalColumn(
                                nextResource.RelationalModel.Root,
                                RelationalNameConventions.DocumentIdColumnName
                            )
                        )
                    );
                    var nextHops = new List<(bool IsIdentity, bool IsRequired, bool IsRoleNamed)>(hopsSoFar)
                    {
                        GetBindingPriority((binding, currentModel)),
                    };
                    var nextVisitedResources = new HashSet<QualifiedResourceName>(visitedResources)
                    {
                        binding.TargetResource,
                    };

                    foundCandidates.AddRange(
                        Explore(nextResource.RelationalModel, nextVisitedResources, nextHops, nextSteps)
                    );
                    continue;
                }

                var terminalSteps = CreateStepsToOwningTable(currentModel, owningTable, stepsSoFar);
                if (terminalSteps is null)
                {
                    continue;
                }

                terminalSteps.Add(new(owningTable.Table, sourceColumnName, null, null));
                var terminalHops = new List<(bool IsIdentity, bool IsRequired, bool IsRoleNamed)>(hopsSoFar)
                {
                    GetBindingPriority((binding, currentModel)),
                };

                foundCandidates.Add(
                    (
                        terminalHops,
                        basisIsAbstract && binding.TargetResource == basisResource && hopsSoFar.Count > 0,
                        terminalSteps
                    )
                );
            }

            foreach (var descriptorEdge in currentModel.DescriptorEdgeSources)
            {
                if (!Equals(descriptorEdge.DescriptorResource, basisResource))
                {
                    continue;
                }

                var owningTable = FindOwningTable(currentModel, descriptorEdge.Table);
                if (owningTable is null)
                {
                    continue;
                }

                var descriptorSteps = CreateStepsToOwningTable(currentModel, owningTable, stepsSoFar);
                if (descriptorSteps is null)
                {
                    continue;
                }

                descriptorSteps.Add(
                    new(
                        owningTable.Table,
                        PersonJoinPathResolver.ResolveToCanonicalColumn(owningTable, descriptorEdge.FkColumn),
                        DescriptorTable,
                        RelationalNameConventions.DocumentIdColumnName
                    )
                );
                var descriptorHops = new List<(bool IsIdentity, bool IsRequired, bool IsRoleNamed)>(hopsSoFar)
                {
                    GetDescriptorEdgePriority(descriptorEdge, owningTable),
                };

                foundCandidates.Add((descriptorHops, false, descriptorSteps));
            }

            return foundCandidates;
        }

        static DbTableModel? FindOwningTable(RelationalResourceModel model, DbTableName table) =>
            model.TablesInDependencyOrder.FirstOrDefault(candidate => candidate.Table.Equals(table));

        static List<ColumnPathStep>? CreateStepsToOwningTable(
            RelationalResourceModel model,
            DbTableModel owningTable,
            IReadOnlyList<ColumnPathStep> stepsSoFar
        )
        {
            var steps = new List<ColumnPathStep>(stepsSoFar);
            if (owningTable.Table.Equals(model.Root.Table))
            {
                return steps;
            }

            if (owningTable.IdentityMetadata.RootScopeLocatorColumns.Count != 1)
            {
                return null;
            }

            steps.Add(
                new(
                    model.Root.Table,
                    PersonJoinPathResolver.ResolveToCanonicalColumn(
                        model.Root,
                        RelationalNameConventions.DocumentIdColumnName
                    ),
                    owningTable.Table,
                    PersonJoinPathResolver.ResolveToCanonicalColumn(
                        owningTable,
                        owningTable.IdentityMetadata.RootScopeLocatorColumns[0]
                    )
                )
            );

            return steps;
        }

        static bool IsBasisMatch(
            QualifiedResourceName reachedResource,
            QualifiedResourceName basis,
            IReadOnlyDictionary<QualifiedResourceName, HashSet<QualifiedResourceName>> abstractMembersByBasis
        ) =>
            reachedResource == basis
            || (
                abstractMembersByBasis.TryGetValue(basis, out var abstractMembers)
                && abstractMembers.Contains(reachedResource)
            )
            || (
                abstractMembersByBasis.TryGetValue(reachedResource, out var reachedAbstractMembers)
                && reachedAbstractMembers.Contains(basis)
            );

        static int CompareCandidates(
            (
                IReadOnlyList<(bool IsIdentity, bool IsRequired, bool IsRoleNamed)> Hops,
                bool PreferExactAbstractBasis,
                IReadOnlyList<ColumnPathStep> Steps
            ) left,
            (
                IReadOnlyList<(bool IsIdentity, bool IsRequired, bool IsRoleNamed)> Hops,
                bool PreferExactAbstractBasis,
                IReadOnlyList<ColumnPathStep> Steps
            ) right
        )
        {
            if (left.PreferExactAbstractBasis != right.PreferExactAbstractBasis)
            {
                return left.PreferExactAbstractBasis ? -1 : 1;
            }

            var hopCount = Math.Min(left.Hops.Count, right.Hops.Count);
            for (var hopIndex = 0; hopIndex < hopCount; hopIndex++)
            {
                var leftPriority = left.Hops[hopIndex];
                var rightPriority = right.Hops[hopIndex];

                if (leftPriority.IsIdentity != rightPriority.IsIdentity)
                {
                    return leftPriority.IsIdentity ? -1 : 1;
                }

                if (leftPriority.IsRequired != rightPriority.IsRequired)
                {
                    return leftPriority.IsRequired ? -1 : 1;
                }

                if (leftPriority.IsRoleNamed != rightPriority.IsRoleNamed)
                {
                    return leftPriority.IsRoleNamed ? 1 : -1;
                }
            }

            if (left.Steps.Count != right.Steps.Count)
            {
                return left.Steps.Count < right.Steps.Count ? -1 : 1;
            }

            return 0;
        }

        static (bool IsIdentity, bool IsRequired, bool IsRoleNamed) GetBindingPriority(
            (DocumentReferenceBinding Binding, RelationalResourceModel OwningModel) bindingInfo
        )
        {
            var binding = bindingInfo.Binding;
            var isRequired = binding.IsRequired;

            if (!isRequired)
            {
                var owningTable = bindingInfo.OwningModel.TablesInDependencyOrder.FirstOrDefault(table =>
                    table.Table == binding.Table
                );

                if (owningTable is not null)
                {
                    var fkColumn = owningTable.Columns.FirstOrDefault(column =>
                        column.ColumnName == binding.FkColumn
                    );

                    if (fkColumn is not null)
                    {
                        isRequired = !fkColumn.IsNullable;
                    }
                }
            }

            return (binding.IsIdentityComponent, isRequired, binding.IsRoleNamed);
        }

        static (bool IsIdentity, bool IsRequired, bool IsRoleNamed) GetDescriptorEdgePriority(
            DescriptorEdgeSource descriptorEdge,
            DbTableModel owningTable
        )
        {
            var fkColumn = owningTable.Columns.FirstOrDefault(column =>
                column.ColumnName == descriptorEdge.FkColumn
            );
            var isRequired = descriptorEdge.IsRequired || (fkColumn is not null && !fkColumn.IsNullable);

            return (descriptorEdge.IsIdentityComponent, isRequired, descriptorEdge.IsRoleNamed);
        }
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
