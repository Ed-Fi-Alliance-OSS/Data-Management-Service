// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
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

        foreach (
            var candidate in match.MatchedCandidatesInOrder.Where(candidate =>
                !candidate.Column.Equals(representativeColumn.ColumnName)
            )
        )
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

        foreach (
            var candidate in match.MatchedCandidatesInOrder.Where(candidate =>
                !candidate.Column.Equals(representativeColumn.ColumnName)
            )
        )
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

        if (distinctDescriptorResources.Length == 0)
        {
            throw CreateQueryCapabilityException(
                _resourceModel,
                $"reference identity query candidate group '{match.CandidateGroup.ReferenceJsonPath.Canonical}' on table '{_rootTable.Table}' does not resolve any descriptor resource"
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

    private const string UniqueIdSuffix = "UniqueId";
    private const string ReferenceSuffix = "Reference";

    /// <summary>
    /// Maps the schema's three person-identity unique-id leaf names to their public person reference
    /// parent names. Only person identities participate in the 3-hop alias-parent bridge: a schema-mangled
    /// alias parent names the public person reference (e.g. "studentReference"), not the intermediate
    /// resource's own identity-source reference (e.g. "studentEducationOrganizationAssociationReference").
    /// No arbitrary "fooUniqueId -> fooReference" derivation is applied outside this map.
    /// </summary>
    private static readonly FrozenDictionary<string, string> _personReferenceNamesByUniqueIdLeaf =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["studentUniqueId"] = "studentReference",
            ["staffUniqueId"] = "staffReference",
            ["contactUniqueId"] = "contactReference",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Bridges schema-mangled alias query paths to reference-identity candidates using binding metadata.
    /// Recognises two alias shapes: through-reference (alias leaf names the intermediate target resource)
    /// and direct-site superclass-mangled (alias leaf names the compiling resource's superclass).
    /// Returns <see cref="ReferenceIdentityQueryCandidateResolution.NoMatch"/> for any query path that is
    /// not a two-segment property path whose leaf ends in <c>UniqueId</c>, or when neither shape matches;
    /// <see cref="ReferenceIdentityQueryCandidateResolution.Match"/> for a single unambiguous candidate group;
    /// or <see cref="ReferenceIdentityQueryCandidateResolution.Ambiguous"/> when multiple groups qualify.
    /// </summary>
    public ReferenceIdentityQueryCandidateResolution ResolveAliasPath(
        string queryFieldName,
        JsonPathExpression queryPath,
        QualifiedResourceName? superclassResource
    )
    {
        ArgumentNullException.ThrowIfNull(queryFieldName);

        if (
            queryPath.Segments
                is not [JsonPathSegment.Property aliasParent, JsonPathSegment.Property aliasLeaf]
            || !aliasLeaf.Name.EndsWith(UniqueIdSuffix, StringComparison.Ordinal)
        )
        {
            return new ReferenceIdentityQueryCandidateResolution.NoMatch();
        }

        var matchedGroups = CandidateGroupsInOrder
            .Select(group => new ReferenceIdentityQueryCandidateGroupMatch(
                group,
                group
                    .CandidatesInOrder.Where(candidate =>
                        IsAliasCandidateMatch(
                            group,
                            candidate,
                            queryFieldName,
                            aliasParent.Name,
                            aliasLeaf.Name,
                            superclassResource
                        )
                    )
                    .ToArray()
            ))
            .Where(static match => match.MatchedCandidatesInOrder.Count > 0)
            .ToArray();

        return CreateResolution(matchedGroups);
    }

    private static bool IsAliasCandidateMatch(
        ReferenceIdentityQueryCandidateGroup group,
        ReferenceIdentityQueryCandidate candidate,
        string queryFieldName,
        string aliasParentName,
        string aliasLeafName,
        QualifiedResourceName? superclassResource
    )
    {
        if (!TryResolveRolePrefix(group, out var rolePrefix))
        {
            return false;
        }

        // Through-reference shape: the target resource's identity element is itself
        // reference-sourced (its identity path has a reference parent), and the alias
        // leaf names the intermediate target resource. The alias parent must equal either
        // the role-adjusted identity-source parent (2-hop case) or the role-adjusted public
        // person reference derived from the identity leaf (3-hop person-identity case, where
        // the intermediate resource's student/staff/contact identity is itself reference-derived).
        if (
            candidate.IdentityJsonPath.Segments
            is [JsonPathSegment.Property identityParent, JsonPathSegment.Property identityLeafThrough]
        )
        {
            if (
                !identityLeafThrough.Name.EndsWith(UniqueIdSuffix, StringComparison.Ordinal)
                || !string.Equals(
                    queryFieldName,
                    ApplyRolePrefix(rolePrefix, identityLeafThrough.Name),
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    aliasLeafName,
                    ToLowerCamelCase(group.TargetResource.ResourceName) + UniqueIdSuffix,
                    StringComparison.Ordinal
                )
                || !IsLocalPropagatedIdentityBinding(group, candidate, identityLeafThrough.Name)
            )
            {
                return false;
            }

            // Arm 1: alias parent is the role-adjusted intermediate identity-source parent.
            if (
                string.Equals(
                    aliasParentName,
                    ApplyRolePrefix(rolePrefix, identityParent.Name),
                    StringComparison.Ordinal
                )
            )
            {
                return true;
            }

            // Arm 2: alias parent is the role-adjusted public person reference — applies only when
            // the identity leaf is one of the three person unique-id fields, because the schema
            // mangles the alias parent to name the public person reference rather than the
            // intermediate resource's own identity-source reference.
            return _personReferenceNamesByUniqueIdLeaf.TryGetValue(
                    identityLeafThrough.Name,
                    out var personReferenceName
                )
                && string.Equals(
                    aliasParentName,
                    ApplyRolePrefix(rolePrefix, personReferenceName),
                    StringComparison.Ordinal
                );
        }

        // Direct-site shape: the target resource's identity element is a root scalar,
        // and the alias leaf names the compiling resource's superclass.
        if (
            candidate.IdentityJsonPath.Segments is [JsonPathSegment.Property identityLeafDirect]
            && superclassResource is { } superclass
            && group.ReferenceObjectPath.Segments is [JsonPathSegment.Property referenceObjectLeaf]
        )
        {
            return identityLeafDirect.Name.EndsWith(UniqueIdSuffix, StringComparison.Ordinal)
                && string.Equals(aliasParentName, referenceObjectLeaf.Name, StringComparison.Ordinal)
                && string.Equals(
                    queryFieldName,
                    ApplyRolePrefix(rolePrefix, identityLeafDirect.Name),
                    StringComparison.Ordinal
                )
                && string.Equals(
                    aliasLeafName,
                    ToLowerCamelCase(superclass.ResourceName) + UniqueIdSuffix,
                    StringComparison.Ordinal
                )
                && IsLocalPropagatedIdentityBinding(group, candidate, identityLeafDirect.Name);
        }

        return false;
    }

    private static bool IsLocalPropagatedIdentityBinding(
        ReferenceIdentityQueryCandidateGroup group,
        ReferenceIdentityQueryCandidate candidate,
        string identityLeafName
    )
    {
        return candidate.ReferenceJsonPath.Segments
                is [JsonPathSegment.Property referenceParent, JsonPathSegment.Property referenceLeaf]
            && group.ReferenceObjectPath.Segments is [JsonPathSegment.Property referenceObjectLeaf]
            && string.Equals(referenceParent.Name, referenceObjectLeaf.Name, StringComparison.Ordinal)
            && string.Equals(referenceLeaf.Name, identityLeafName, StringComparison.Ordinal);
    }

    private static bool TryResolveRolePrefix(
        ReferenceIdentityQueryCandidateGroup group,
        out string rolePrefix
    )
    {
        rolePrefix = "";

        if (group.ReferenceObjectPath.Segments is not [.., JsonPathSegment.Property referenceObjectLeaf])
        {
            return false;
        }

        var baseReferenceName = ToLowerCamelCase(group.TargetResource.ResourceName) + ReferenceSuffix;

        if (string.Equals(referenceObjectLeaf.Name, baseReferenceName, StringComparison.Ordinal))
        {
            return true;
        }

        var roleNamedSuffix = ToUpperCamelCase(baseReferenceName);

        if (
            referenceObjectLeaf.Name.Length > roleNamedSuffix.Length
            && referenceObjectLeaf.Name.EndsWith(roleNamedSuffix, StringComparison.Ordinal)
        )
        {
            rolePrefix = referenceObjectLeaf.Name[..^roleNamedSuffix.Length];
            return true;
        }

        return false;
    }

    private static string ApplyRolePrefix(string rolePrefix, string name) =>
        rolePrefix.Length == 0 ? name : rolePrefix + ToUpperCamelCase(name);

    private static string ToLowerCamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static string ToUpperCamelCase(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

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
