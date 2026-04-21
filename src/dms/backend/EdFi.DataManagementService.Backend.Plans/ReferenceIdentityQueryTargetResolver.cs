// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Inventories root-table reference identity candidates for relational query-field compilation.
/// </summary>
internal sealed class ReferenceIdentityQueryTargetResolver
{
    private const string QueryCapabilityPlanKind = "query capability";

    public ReferenceIdentityQueryTargetResolver(RelationalResourceModel resourceModel, DbTableModel rootTable)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(rootTable);

        CandidateGroupsInOrder = CreateCandidateGroups(resourceModel, rootTable);
        CandidatesInOrder = [.. CandidateGroupsInOrder.SelectMany(static group => group.CandidatesInOrder)];
    }

    public IReadOnlyList<ReferenceIdentityQueryCandidateGroup> CandidateGroupsInOrder { get; }

    public IReadOnlyList<ReferenceIdentityQueryCandidate> CandidatesInOrder { get; }

    public ReferenceIdentityQueryCandidateResolution ResolveExactPath(JsonPathExpression queryPath)
    {
        var matchedGroups = CandidateGroupsInOrder
            .Select(group => new ReferenceIdentityQueryCandidateGroupMatch(
                group,
                group
                    .CandidatesInOrder.Where(candidate =>
                        string.Equals(
                            candidate.IdentityJsonPath.Canonical,
                            queryPath.Canonical,
                            StringComparison.Ordinal
                        )
                        || string.Equals(
                            candidate.ReferenceJsonPath.Canonical,
                            queryPath.Canonical,
                            StringComparison.Ordinal
                        )
                    )
                    .ToArray()
            ))
            .Where(static match => match.MatchedCandidatesInOrder.Count > 0)
            .ToArray();

        return matchedGroups.Length switch
        {
            0 => new ReferenceIdentityQueryCandidateResolution.NoMatch(),
            1 => new ReferenceIdentityQueryCandidateResolution.Match(
                matchedGroups[0].CandidateGroup,
                matchedGroups[0].MatchedCandidatesInOrder
            ),
            _ => new ReferenceIdentityQueryCandidateResolution.Ambiguous(
                matchedGroups.Select(static match => match.CandidateGroup).ToArray()
            ),
        };
    }

    private static IReadOnlyList<ReferenceIdentityQueryCandidateGroup> CreateCandidateGroups(
        RelationalResourceModel resourceModel,
        DbTableModel rootTable
    )
    {
        List<ReferenceIdentityQueryCandidateGroup> groups = [];

        foreach (
            var binding in resourceModel.DocumentReferenceBindings.Where(binding =>
                binding.Table == rootTable.Table
            )
        )
        {
            var logicalFields = ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                rootTable,
                binding,
                reason => CreateQueryCapabilityException(resourceModel, reason)
            );

            foreach (var logicalField in logicalFields)
            {
                var candidates = binding
                    .IdentityBindings.Where(identityBinding =>
                        string.Equals(
                            identityBinding.ReferenceJsonPath.Canonical,
                            logicalField.ReferenceJsonPath.Canonical,
                            StringComparison.Ordinal
                        )
                    )
                    .Select(identityBinding => new ReferenceIdentityQueryCandidate(
                        identityBinding.IdentityJsonPath,
                        identityBinding.ReferenceJsonPath,
                        identityBinding.Column,
                        binding.TargetResource,
                        binding.ReferenceObjectPath,
                        binding.FkColumn,
                        logicalField.RepresentativeBindingColumn
                    ))
                    .ToArray();

                if (candidates.Length == 0)
                {
                    throw CreateQueryCapabilityException(
                        resourceModel,
                        $"reference identity query candidate group '{logicalField.ReferenceJsonPath.Canonical}' on table '{rootTable.Table}' does not contain any raw candidates"
                    );
                }

                groups.Add(
                    new ReferenceIdentityQueryCandidateGroup(
                        binding.Table,
                        binding.TargetResource,
                        binding.ReferenceObjectPath,
                        binding.FkColumn,
                        logicalField.ReferenceJsonPath,
                        logicalField.RepresentativeBindingColumn,
                        candidates
                    )
                );
            }
        }

        return groups;
    }

    private static InvalidOperationException CreateQueryCapabilityException(
        RelationalResourceModel resourceModel,
        string reason
    )
    {
        return new InvalidOperationException(
            $"Cannot compile {QueryCapabilityPlanKind}: resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}' {reason}."
        );
    }

    private sealed record ReferenceIdentityQueryCandidateGroupMatch(
        ReferenceIdentityQueryCandidateGroup CandidateGroup,
        IReadOnlyList<ReferenceIdentityQueryCandidate> MatchedCandidatesInOrder
    );
}

internal sealed record ReferenceIdentityQueryCandidate(
    JsonPathExpression IdentityJsonPath,
    JsonPathExpression ReferenceJsonPath,
    DbColumnName Column,
    QualifiedResourceName TargetResource,
    JsonPathExpression ReferenceObjectPath,
    DbColumnName FkColumn,
    DbColumnName RepresentativeBindingColumn
);

internal sealed record ReferenceIdentityQueryCandidateGroup(
    DbTableName Table,
    QualifiedResourceName TargetResource,
    JsonPathExpression ReferenceObjectPath,
    DbColumnName FkColumn,
    JsonPathExpression ReferenceJsonPath,
    DbColumnName RepresentativeBindingColumn,
    IReadOnlyList<ReferenceIdentityQueryCandidate> CandidatesInOrder
);

internal abstract record ReferenceIdentityQueryCandidateResolution
{
    public sealed record NoMatch : ReferenceIdentityQueryCandidateResolution;

    public sealed record Match(
        ReferenceIdentityQueryCandidateGroup CandidateGroup,
        IReadOnlyList<ReferenceIdentityQueryCandidate> MatchedCandidatesInOrder
    ) : ReferenceIdentityQueryCandidateResolution;

    public sealed record Ambiguous(IReadOnlyList<ReferenceIdentityQueryCandidateGroup> CandidateGroupsInOrder)
        : ReferenceIdentityQueryCandidateResolution;
}
