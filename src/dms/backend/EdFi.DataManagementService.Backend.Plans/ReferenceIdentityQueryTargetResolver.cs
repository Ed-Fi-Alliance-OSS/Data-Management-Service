// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Inventories root-table reference identity candidates for relational query-field compilation.
/// </summary>
internal sealed class ReferenceIdentityQueryTargetResolver
{
    private const string QueryCapabilityPlanKind = "query capability";
    private readonly RelationalResourceModel _resourceModel;
    private readonly DbTableModel _rootTable;
    private readonly IReadOnlyDictionary<DbColumnName, DbColumnModel> _rootColumnsByName;

    public ReferenceIdentityQueryTargetResolver(RelationalResourceModel resourceModel, DbTableModel rootTable)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(rootTable);

        _resourceModel = resourceModel;
        _rootTable = rootTable;
        _rootColumnsByName = rootTable.Columns.ToDictionary(static column => column.ColumnName);
        CandidateGroupsInOrder = CreateCandidateGroups(resourceModel, rootTable);
    }

    public IReadOnlyList<ReferenceIdentityQueryCandidateGroup> CandidateGroupsInOrder { get; }

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

        return CreateResolution(matchedGroups);
    }

    public ReferenceIdentityQueryCandidateResolution ResolveReferenceAlias(
        string queryFieldName,
        JsonPathExpression queryPath
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(queryFieldName);

        var exactResolution = ResolveExactPath(queryPath);
        if (exactResolution is not ReferenceIdentityQueryCandidateResolution.NoMatch)
        {
            return exactResolution;
        }

        if (
            !TryGetPropertyLeaf(queryPath, out var queryPathLeaf)
            || !TryGetParentPropertyLeaf(queryPath, out var queryPathParentReferenceLeaf)
        )
        {
            return new ReferenceIdentityQueryCandidateResolution.NoMatch();
        }

        var guardedMatches = CandidateGroupsInOrder
            .Select(group => new ReferenceIdentityQueryCandidateGroupMatch(
                group,
                group
                    .CandidatesInOrder.Where(candidate =>
                        MatchesQueryAliasGuard(queryFieldName, queryPathParentReferenceLeaf, candidate)
                    )
                    .ToArray()
            ))
            .Where(static match => match.MatchedCandidatesInOrder.Count > 0)
            .ToArray();

        if (guardedMatches.Length == 0)
        {
            return new ReferenceIdentityQueryCandidateResolution.NoMatch();
        }

        var targetResourceMatches = guardedMatches
            .Select(match => new ReferenceIdentityQueryCandidateGroupMatch(
                match.CandidateGroup,
                match
                    .MatchedCandidatesInOrder.Where(candidate =>
                        MatchesTargetResourceUniqueIdAlias(queryPathLeaf, candidate.TargetResource)
                    )
                    .ToArray()
            ))
            .Where(static match => match.MatchedCandidatesInOrder.Count > 0)
            .ToArray();

        if (targetResourceMatches.Length > 0)
        {
            return CreateResolution(targetResourceMatches);
        }

        var uniqueIdFallbackMatches = guardedMatches
            .Select(match => new ReferenceIdentityQueryCandidateGroupMatch(
                match.CandidateGroup,
                match
                    .MatchedCandidatesInOrder.Where(candidate =>
                        IsGenericUniqueIdFallbackMatch(queryPathLeaf, candidate)
                    )
                    .ToArray()
            ))
            .Where(static match => match.MatchedCandidatesInOrder.Count > 0)
            .ToArray();

        return CreateResolution(uniqueIdFallbackMatches);
    }

    public RelationalQueryFieldTarget ResolveTargetOrThrow(
        ReferenceIdentityQueryCandidateResolution.Match match
    )
    {
        ArgumentNullException.ThrowIfNull(match);

        if (match.MatchedCandidatesInOrder.Count == 0)
        {
            throw CreateQueryCapabilityException(
                _resourceModel,
                $"reference identity query candidate group '{match.CandidateGroup.ReferenceJsonPath.Canonical}' on table '{_rootTable.Table}' cannot resolve a query target because no candidates matched"
            );
        }

        var representativeColumn = ResolveRootColumnOrThrow(
            match.CandidateGroup.RepresentativeBindingColumn,
            match.CandidateGroup
        );

        return representativeColumn.Kind switch
        {
            ColumnKind.Scalar => ResolveScalarTargetOrThrow(representativeColumn, match),
            ColumnKind.DescriptorFk => ResolveDescriptorTargetOrThrow(representativeColumn, match),
            _ => throw CreateInvalidColumnKindException(representativeColumn, match.CandidateGroup),
        };
    }

    public RelationalQueryFieldTarget? CollapseExactAmbiguityOrThrow(
        JsonPathExpression queryPath,
        IReadOnlyCollection<DbColumnName> exactTargetColumns
    )
    {
        ArgumentNullException.ThrowIfNull(exactTargetColumns);

        var resolution = ResolveExactPath(queryPath);

        if (resolution is not ReferenceIdentityQueryCandidateResolution.Match match)
        {
            return null;
        }

        var exactColumns = OrderDistinctColumns(exactTargetColumns);
        var matchedColumns = OrderDistinctColumns(
            match.MatchedCandidatesInOrder.Select(static candidate => candidate.Column)
        );

        if (!exactColumns.SequenceEqual(matchedColumns))
        {
            return null;
        }

        return ResolveTargetOrThrow(match);
    }

    private RelationalQueryFieldTarget.RootColumn ResolveScalarTargetOrThrow(
        DbColumnModel representativeColumn,
        ReferenceIdentityQueryCandidateResolution.Match match
    )
    {
        ValidateScalarTargetColumnOrThrow(representativeColumn, match.CandidateGroup);

        foreach (var candidate in match.MatchedCandidatesInOrder)
        {
            var candidateColumn = ResolveRootColumnOrThrow(candidate.Column, match.CandidateGroup);
            ValidateScalarTargetColumnOrThrow(candidateColumn, match.CandidateGroup);
        }

        return new RelationalQueryFieldTarget.RootColumn(representativeColumn.ColumnName);
    }

    private RelationalQueryFieldTarget.DescriptorIdColumn ResolveDescriptorTargetOrThrow(
        DbColumnModel representativeColumn,
        ReferenceIdentityQueryCandidateResolution.Match match
    )
    {
        List<QualifiedResourceName> descriptorResources = [];
        descriptorResources.Add(
            ResolveDescriptorTargetResourceOrThrow(representativeColumn, match.CandidateGroup)
        );

        foreach (var candidate in match.MatchedCandidatesInOrder)
        {
            var candidateColumn = ResolveRootColumnOrThrow(candidate.Column, match.CandidateGroup);
            descriptorResources.Add(
                ResolveDescriptorTargetResourceOrThrow(candidateColumn, match.CandidateGroup)
            );
        }

        var distinctDescriptorResources = descriptorResources
            .Distinct()
            .OrderBy(static descriptorResource => descriptorResource.ProjectName, StringComparer.Ordinal)
            .ThenBy(static descriptorResource => descriptorResource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        if (distinctDescriptorResources.Length > 1)
        {
            throw CreateQueryCapabilityException(
                _resourceModel,
                $"reference identity query candidate group '{match.CandidateGroup.ReferenceJsonPath.Canonical}' on table '{_rootTable.Table}' resolves descriptor candidates to multiple descriptor resources: {string.Join(", ", distinctDescriptorResources.Select(FormatResource))}"
            );
        }

        return new RelationalQueryFieldTarget.DescriptorIdColumn(
            representativeColumn.ColumnName,
            distinctDescriptorResources[0]
        );
    }

    private DbColumnModel ResolveRootColumnOrThrow(
        DbColumnName columnName,
        ReferenceIdentityQueryCandidateGroup candidateGroup
    )
    {
        if (_rootColumnsByName.TryGetValue(columnName, out var column))
        {
            return column;
        }

        throw CreateQueryCapabilityException(
            _resourceModel,
            $"reference identity query candidate group '{candidateGroup.ReferenceJsonPath.Canonical}' on table '{_rootTable.Table}' references missing root column '{columnName.Value}'"
        );
    }

    private void ValidateScalarTargetColumnOrThrow(
        DbColumnModel column,
        ReferenceIdentityQueryCandidateGroup candidateGroup
    )
    {
        if (column.Kind is not ColumnKind.Scalar)
        {
            throw CreateInvalidColumnKindException(column, candidateGroup);
        }

        if (column.ScalarType is not null)
        {
            return;
        }

        throw CreateQueryCapabilityException(
            _resourceModel,
            $"reference identity query candidate group '{candidateGroup.ReferenceJsonPath.Canonical}' on table '{_rootTable.Table}' scalar target column '{column.ColumnName.Value}' is missing scalar type metadata"
        );
    }

    private QualifiedResourceName ResolveDescriptorTargetResourceOrThrow(
        DbColumnModel column,
        ReferenceIdentityQueryCandidateGroup candidateGroup
    )
    {
        if (column.Kind is not ColumnKind.DescriptorFk)
        {
            throw CreateInvalidColumnKindException(column, candidateGroup);
        }

        var descriptorEdgeSources = _resourceModel
            .DescriptorEdgeSources.Where(descriptorEdgeSource =>
                descriptorEdgeSource.Table.Equals(_rootTable.Table)
                && descriptorEdgeSource.FkColumn.Equals(column.ColumnName)
            )
            .ToArray();

        return descriptorEdgeSources.Length switch
        {
            1 => descriptorEdgeSources[0].DescriptorResource,
            0 => throw CreateQueryCapabilityException(
                _resourceModel,
                $"reference identity query candidate group '{candidateGroup.ReferenceJsonPath.Canonical}' on table '{_rootTable.Table}' descriptor target column '{column.ColumnName.Value}' has no matching DescriptorEdgeSource metadata"
            ),
            _ => throw CreateQueryCapabilityException(
                _resourceModel,
                $"reference identity query candidate group '{candidateGroup.ReferenceJsonPath.Canonical}' on table '{_rootTable.Table}' descriptor target column '{column.ColumnName.Value}' has multiple matching DescriptorEdgeSource metadata entries"
            ),
        };
    }

    private Exception CreateInvalidColumnKindException(
        DbColumnModel column,
        ReferenceIdentityQueryCandidateGroup candidateGroup
    )
    {
        return CreateQueryCapabilityException(
            _resourceModel,
            $"reference identity query candidate group '{candidateGroup.ReferenceJsonPath.Canonical}' on table '{_rootTable.Table}' target column '{column.ColumnName.Value}' has unsupported kind '{column.Kind}'. Expected '{ColumnKind.Scalar}' or '{ColumnKind.DescriptorFk}'"
        );
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

    private static string FormatResource(QualifiedResourceName resource) =>
        $"'{resource.ProjectName}.{resource.ResourceName}'";

    private static DbColumnName[] OrderDistinctColumns(IEnumerable<DbColumnName> columns)
    {
        return columns.Distinct().OrderBy(static column => column.Value, StringComparer.Ordinal).ToArray();
    }

    private static ReferenceIdentityQueryCandidateResolution CreateResolution(
        IReadOnlyList<ReferenceIdentityQueryCandidateGroupMatch> matchedGroups
    )
    {
        return matchedGroups.Count switch
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

    private static bool MatchesQueryAliasGuard(
        string queryFieldName,
        string queryPathParentReferenceLeaf,
        ReferenceIdentityQueryCandidate candidate
    )
    {
        if (!TryGetParentPropertyLeaf(candidate.IdentityJsonPath, out var candidateIdentityParentLeaf))
        {
            return false;
        }

        return (
                TryGetAliasPrefix(queryFieldName, candidate.IdentityJsonPath, out var queryFieldPrefix)
                || TryGetAliasPrefix(queryFieldName, candidate.ReferenceJsonPath, out queryFieldPrefix)
            )
            && TryGetAliasPrefix(
                queryPathParentReferenceLeaf,
                candidateIdentityParentLeaf,
                out var parentReferencePrefix
            )
            && string.Equals(queryFieldPrefix, parentReferencePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetAliasPrefix(string value, JsonPathExpression candidatePath, out string prefix)
    {
        prefix = string.Empty;

        return TryGetPropertyLeaf(candidatePath, out var candidateLeaf)
            && TryGetAliasPrefix(value, candidateLeaf, out prefix);
    }

    private static bool TryGetAliasPrefix(string value, string suffix, out string prefix)
    {
        prefix = string.Empty;

        if (string.Equals(value, suffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        prefix = value[..^suffix.Length];
        return prefix.Length > 0;
    }

    private static bool IsGenericUniqueIdFallbackMatch(
        string queryPathLeaf,
        ReferenceIdentityQueryCandidate candidate
    )
    {
        return queryPathLeaf.EndsWith("UniqueId", StringComparison.Ordinal)
            && IsUniqueIdCandidate(candidate)
            && !PathLeafMatches(queryPathLeaf, candidate.IdentityJsonPath)
            && !PathLeafMatches(queryPathLeaf, candidate.ReferenceJsonPath);
    }

    private static bool PathLeafMatches(string queryPathLeaf, JsonPathExpression candidatePath)
    {
        return TryGetPropertyLeaf(candidatePath, out var candidateLeaf)
            && queryPathLeaf.EndsWith(candidateLeaf, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUniqueIdCandidate(ReferenceIdentityQueryCandidate candidate)
    {
        return PathLeafEndsWithUniqueId(candidate.IdentityJsonPath)
            || PathLeafEndsWithUniqueId(candidate.ReferenceJsonPath);
    }

    private static bool PathLeafEndsWithUniqueId(JsonPathExpression candidatePath)
    {
        return TryGetPropertyLeaf(candidatePath, out var candidateLeaf)
            && candidateLeaf.EndsWith("UniqueId", StringComparison.Ordinal);
    }

    private static bool MatchesTargetResourceUniqueIdAlias(
        string queryPathLeaf,
        QualifiedResourceName targetResource
    )
    {
        var expectedAliasLeaf =
            $"{PlanNamingConventions.CamelCaseFirstCharacter(targetResource.ResourceName)}UniqueId";

        return string.Equals(queryPathLeaf, expectedAliasLeaf, StringComparison.Ordinal);
    }

    private static bool TryGetPropertyLeaf(JsonPathExpression path, out string leaf)
    {
        leaf = string.Empty;

        if (path.Segments.Count == 0 || path.Segments[^1] is not JsonPathSegment.Property property)
        {
            return false;
        }

        leaf = property.Name;
        return true;
    }

    private static bool TryGetParentPropertyLeaf(JsonPathExpression path, out string parentLeaf)
    {
        parentLeaf = string.Empty;

        if (path.Segments.Count < 2 || path.Segments[^2] is not JsonPathSegment.Property property)
        {
            return false;
        }

        parentLeaf = property.Name;
        return true;
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
