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

    public ReferenceIdentityQueryTargetResolver(RelationalResourceModel resourceModel, DbTableModel rootTable)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(rootTable);

        _resourceModel = resourceModel;
        _rootTable = rootTable;
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
        var column = _rootTable.Columns.FirstOrDefault(column => column.ColumnName.Equals(columnName));

        if (column is not null)
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
