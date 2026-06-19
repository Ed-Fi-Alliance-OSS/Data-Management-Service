// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.ChangeQueries;

internal sealed class ChangeQueryResponseFieldMapper
{
    [SuppressMessage(
        "Major Code Smell",
        "S2325:Methods and properties that don't access instance data should be static",
        Justification = "Preserves the instance mapper contract used by later Change Query planners."
    )]
    public IReadOnlyList<ChangeQueryResponseField> Map(
        MappingSet mappingSet,
        ConcreteResourceModel resourceModel,
        TrackedChangeTableInfo trackedChangeTable
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(trackedChangeTable);

        TrackedChangeColumnInfo[] identityColumns = trackedChangeTable
            .ValueColumnsInTableOrder.Where(column =>
                column.Origin.HasFlag(TrackedChangeColumnOrigin.Identity)
                && column.Role is not TrackedChangeColumnRole.PersonDocumentId
            )
            .ToArray();

        List<ChangeQueryResponseField> fields = [];
        IReadOnlyDictionary<DescriptorGroupKey, DescriptorColumnGroup> descriptorGroups =
            BuildDescriptorGroups(resourceModel, identityColumns);

        foreach (TrackedChangeColumnInfo column in identityColumns)
        {
            if (
                column.Role
                is TrackedChangeColumnRole.DescriptorNamespace
                    or TrackedChangeColumnRole.DescriptorCodeValue
            )
            {
                DescriptorColumnGroup descriptorGroup = descriptorGroups[
                    new DescriptorGroupKey(column.DescriptorJoinName, column.SourceJsonPath)
                ];
                if (!ReferenceEquals(column, descriptorGroup.FirstColumn))
                {
                    continue;
                }

                fields.Add(
                    new ChangeQueryResponseField(
                        ResolveQueryFieldName(mappingSet, resourceModel, descriptorGroup.NamespaceColumn),
                        ChangeQueryResponseFieldKind.Descriptor,
                        descriptorGroup.NamespaceColumn,
                        descriptorGroup.NamespaceColumn,
                        descriptorGroup.CodeValueColumn,
                        descriptorGroup.CodeValueColumn
                    )
                );
                continue;
            }

            fields.Add(
                new ChangeQueryResponseField(
                    ResolveQueryFieldName(mappingSet, resourceModel, column),
                    ChangeQueryResponseFieldKind.Scalar,
                    column,
                    column,
                    null,
                    null
                )
            );
        }

        return fields;
    }

    private static string ResolveQueryFieldName(
        MappingSet mappingSet,
        ConcreteResourceModel resourceModel,
        TrackedChangeColumnInfo column
    )
    {
        string[] exactQueryFieldNames = GetExactQueryFieldMappingMatches(
            resourceModel.QueryFieldMappingsByQueryField.Values,
            column.SourceJsonPath
        );
        if (exactQueryFieldNames.Length == 1)
        {
            return exactQueryFieldNames[0];
        }

        if (exactQueryFieldNames.Length > 1)
        {
            throw CreateUnsupportedMappingException(resourceModel, column);
        }

        string[] keyUnificationQueryFieldNames = GetKeyUnificationMappingMatches(
            resourceModel.QueryFieldMappingsByQueryField.Values,
            resourceModel.RelationalModel.KeyUnificationEqualityConstraints,
            column
        );
        if (keyUnificationQueryFieldNames.Length == 1)
        {
            return keyUnificationQueryFieldNames[0];
        }

        if (keyUnificationQueryFieldNames.Length > 1)
        {
            throw CreateUnsupportedMappingException(resourceModel, column);
        }

        string? queryFieldName;
        if (TryResolveQueryCapabilityAlias(mappingSet, resourceModel, column, out queryFieldName))
        {
            return queryFieldName;
        }

        throw CreateUnsupportedMappingException(resourceModel, column);
    }

    private static NotSupportedException CreateUnsupportedMappingException(
        ConcreteResourceModel resourceModel,
        TrackedChangeColumnInfo column
    )
    {
        QualifiedResourceName resource = resourceModel.RelationalModel.Resource;
        return new NotSupportedException(
            $"Unable to map tracked-change identity path '{column.SourceJsonPath}' on resource "
                + $"'{resource.ProjectName}:{resource.ResourceName}' to a Change Query response field."
        );
    }

    private static IReadOnlyDictionary<DescriptorGroupKey, DescriptorColumnGroup> BuildDescriptorGroups(
        ConcreteResourceModel resourceModel,
        IReadOnlyList<TrackedChangeColumnInfo> identityColumns
    )
    {
        Dictionary<DescriptorGroupKey, DescriptorColumnGroup> descriptorGroups = [];

        foreach (
            IGrouping<
                DescriptorGroupKey,
                (TrackedChangeColumnInfo Column, int Index)
            > group in identityColumns
                .Select((column, index) => (Column: column, Index: index))
                .Where(entry =>
                    entry.Column.Role
                        is TrackedChangeColumnRole.DescriptorNamespace
                            or TrackedChangeColumnRole.DescriptorCodeValue
                )
                .GroupBy(entry => new DescriptorGroupKey(
                    entry.Column.DescriptorJoinName,
                    entry.Column.SourceJsonPath
                ))
        )
        {
            (TrackedChangeColumnInfo Column, int Index) firstMember = group.MinBy(static entry =>
                entry.Index
            );
            TrackedChangeColumnInfo[] namespaceColumns = group
                .Where(static entry => entry.Column.Role is TrackedChangeColumnRole.DescriptorNamespace)
                .Select(static entry => entry.Column)
                .ToArray();
            TrackedChangeColumnInfo[] codeValueColumns = group
                .Where(static entry => entry.Column.Role is TrackedChangeColumnRole.DescriptorCodeValue)
                .Select(static entry => entry.Column)
                .ToArray();

            if (namespaceColumns.Length != 1 || codeValueColumns.Length != 1)
            {
                throw CreateUnsupportedMappingException(resourceModel, firstMember.Column);
            }

            descriptorGroups.Add(
                group.Key,
                new DescriptorColumnGroup(firstMember.Column, namespaceColumns[0], codeValueColumns[0])
            );
        }

        return descriptorGroups;
    }

    private static string[] GetExactQueryFieldMappingMatches(
        IEnumerable<RelationalQueryFieldMapping> mappings,
        string sourceJsonPath
    )
    {
        return mappings
            .Where(mapping => mapping.Paths.Any(path => path.Path.Canonical == sourceJsonPath))
            .Select(static mapping => mapping.QueryFieldName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetKeyUnificationMappingMatches(
        IEnumerable<RelationalQueryFieldMapping> mappings,
        KeyUnificationEqualityConstraintDiagnostics diagnostics,
        TrackedChangeColumnInfo column
    )
    {
        HashSet<string> equivalentPaths = GetKeyUnificationEquivalentPaths(
            diagnostics,
            column,
            includeTrackedPath: false
        );
        if (equivalentPaths.Count == 0)
        {
            return [];
        }

        return mappings
            .Where(mapping => mapping.Paths.Any(path => equivalentPaths.Contains(path.Path.Canonical)))
            .Select(static mapping => mapping.QueryFieldName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<string> GetKeyUnificationEquivalentPaths(
        KeyUnificationEqualityConstraintDiagnostics diagnostics,
        TrackedChangeColumnInfo column,
        bool includeTrackedPath
    )
    {
        HashSet<string> equivalentPaths = new(StringComparer.Ordinal);
        if (includeTrackedPath)
        {
            equivalentPaths.Add(column.SourceJsonPath);
        }

        foreach (KeyUnificationAppliedConstraint constraint in diagnostics.Applied)
        {
            if (!MatchesTrackedColumn(constraint, column))
            {
                continue;
            }

            equivalentPaths.Add(constraint.EndpointAPath.Canonical);
            equivalentPaths.Add(constraint.EndpointBPath.Canonical);
        }

        foreach (KeyUnificationRedundantConstraint constraint in diagnostics.Redundant)
        {
            if (!MatchesTrackedColumn(constraint, column))
            {
                continue;
            }

            equivalentPaths.Add(constraint.EndpointAPath.Canonical);
            equivalentPaths.Add(constraint.EndpointBPath.Canonical);
        }

        return equivalentPaths;
    }

    private static bool MatchesTrackedColumn(
        KeyUnificationAppliedConstraint constraint,
        TrackedChangeColumnInfo column
    )
    {
        if (
            constraint.EndpointAPath.Canonical == column.SourceJsonPath
            || constraint.EndpointBPath.Canonical == column.SourceJsonPath
        )
        {
            return true;
        }

        return column.CanonicalStorageColumn is { } canonicalStorageColumn
            && (
                constraint.CanonicalColumn == canonicalStorageColumn
                || constraint.EndpointAColumn == canonicalStorageColumn
                || constraint.EndpointBColumn == canonicalStorageColumn
            );
    }

    private static bool MatchesTrackedColumn(
        KeyUnificationRedundantConstraint constraint,
        TrackedChangeColumnInfo column
    )
    {
        if (
            constraint.EndpointAPath.Canonical == column.SourceJsonPath
            || constraint.EndpointBPath.Canonical == column.SourceJsonPath
        )
        {
            return true;
        }

        return column.CanonicalStorageColumn is { } canonicalStorageColumn
            && constraint.Binding.Column == canonicalStorageColumn;
    }

    private static bool TryResolveQueryCapabilityAlias(
        MappingSet mappingSet,
        ConcreteResourceModel resourceModel,
        TrackedChangeColumnInfo column,
        [NotNullWhen(true)] out string? queryFieldName
    )
    {
        if (
            !mappingSet.QueryCapabilitiesByResource.TryGetValue(
                resourceModel.RelationalModel.Resource,
                out RelationalQueryCapability? capability
            )
        )
        {
            queryFieldName = null;
            return false;
        }

        HashSet<DbColumnName> candidateColumns = GetCapabilityCandidateColumns(resourceModel, column);
        HashSet<string> equivalentPaths = GetKeyUnificationEquivalentPaths(
            resourceModel.RelationalModel.KeyUnificationEqualityConstraints,
            column,
            includeTrackedPath: true
        );
        SupportedRelationalQueryField[] targetMatches = capability
            .SupportedFieldsByQueryField.Values.Where(supportedField =>
                SupportedFieldTargetMatches(supportedField, candidateColumns)
            )
            .ToArray();

        if (targetMatches.Length == 0)
        {
            queryFieldName = null;
            return false;
        }

        SupportedRelationalQueryField[] pathMatches = targetMatches
            .Where(supportedField => equivalentPaths.Contains(supportedField.Path.Path.Canonical))
            .ToArray();

        if (pathMatches.Length == 1)
        {
            queryFieldName = pathMatches[0].QueryFieldName;
            return true;
        }

        if (pathMatches.Length > 1)
        {
            throw CreateUnsupportedMappingException(resourceModel, column);
        }

        if (targetMatches.Length == 1)
        {
            queryFieldName = targetMatches[0].QueryFieldName;
            return true;
        }

        throw CreateUnsupportedMappingException(resourceModel, column);
    }

    private static bool SupportedFieldTargetMatches(
        SupportedRelationalQueryField supportedField,
        HashSet<DbColumnName> candidateColumns
    )
    {
        return TryGetTargetColumn(supportedField.Target, out DbColumnName targetColumn)
            && candidateColumns.Contains(targetColumn);
    }

    private static HashSet<DbColumnName> GetCapabilityCandidateColumns(
        ConcreteResourceModel resourceModel,
        TrackedChangeColumnInfo column
    )
    {
        HashSet<DbColumnName> candidateColumns = [];

        if (column.CanonicalStorageColumn is { } canonicalStorageColumn)
        {
            candidateColumns.Add(canonicalStorageColumn);
        }

        AddRootTableSourceColumns(resourceModel, column, candidateColumns);
        AddKeyUnificationTargetColumns(
            resourceModel.RelationalModel.KeyUnificationEqualityConstraints,
            column,
            candidateColumns
        );
        AddReferenceBindingTargetColumns(resourceModel, column, candidateColumns);

        return candidateColumns;
    }

    private static void AddRootTableSourceColumns(
        ConcreteResourceModel resourceModel,
        TrackedChangeColumnInfo column,
        HashSet<DbColumnName> candidateColumns
    )
    {
        foreach (DbColumnModel dbColumn in resourceModel.RelationalModel.Root.Columns)
        {
            if (dbColumn.SourceJsonPath?.Canonical != column.SourceJsonPath)
            {
                continue;
            }

            candidateColumns.Add(dbColumn.ColumnName);
            if (dbColumn.Storage is ColumnStorage.UnifiedAlias unifiedAlias)
            {
                candidateColumns.Add(unifiedAlias.CanonicalColumn);
            }
        }
    }

    private static void AddKeyUnificationTargetColumns(
        KeyUnificationEqualityConstraintDiagnostics diagnostics,
        TrackedChangeColumnInfo column,
        HashSet<DbColumnName> candidateColumns
    )
    {
        foreach (KeyUnificationAppliedConstraint constraint in diagnostics.Applied)
        {
            if (!MatchesTrackedColumn(constraint, column))
            {
                continue;
            }

            candidateColumns.Add(constraint.EndpointAColumn);
            candidateColumns.Add(constraint.EndpointBColumn);
            candidateColumns.Add(constraint.CanonicalColumn);
        }

        foreach (KeyUnificationRedundantConstraint constraint in diagnostics.Redundant)
        {
            if (
                constraint.EndpointAPath.Canonical != column.SourceJsonPath
                && constraint.EndpointBPath.Canonical != column.SourceJsonPath
            )
            {
                continue;
            }

            candidateColumns.Add(constraint.Binding.Column);
        }
    }

    private static void AddReferenceBindingTargetColumns(
        ConcreteResourceModel resourceModel,
        TrackedChangeColumnInfo column,
        HashSet<DbColumnName> candidateColumns
    )
    {
        foreach (
            DocumentReferenceBinding documentReference in resourceModel
                .RelationalModel
                .DocumentReferenceBindings
        )
        {
            foreach (ReferenceIdentityBinding identityBinding in documentReference.IdentityBindings)
            {
                if (identityBinding.ReferenceJsonPath.Canonical == column.SourceJsonPath)
                {
                    candidateColumns.Add(identityBinding.Column);
                }
            }
        }

        foreach (
            DescriptorEdgeSource descriptorEdgeSource in resourceModel.RelationalModel.DescriptorEdgeSources
        )
        {
            if (descriptorEdgeSource.DescriptorValuePath.Canonical == column.SourceJsonPath)
            {
                candidateColumns.Add(descriptorEdgeSource.FkColumn);
            }
        }
    }

    private static bool TryGetTargetColumn(RelationalQueryFieldTarget target, out DbColumnName targetColumn)
    {
        switch (target)
        {
            case RelationalQueryFieldTarget.RootColumn rootColumn:
                targetColumn = rootColumn.Column;
                return true;

            case RelationalQueryFieldTarget.DescriptorIdColumn descriptorIdColumn:
                targetColumn = descriptorIdColumn.Column;
                return true;

            default:
                targetColumn = default;
                return false;
        }
    }

    private sealed record DescriptorGroupKey(string? DescriptorJoinName, string SourceJsonPath);

    private sealed record DescriptorColumnGroup(
        TrackedChangeColumnInfo FirstColumn,
        TrackedChangeColumnInfo NamespaceColumn,
        TrackedChangeColumnInfo CodeValueColumn
    );
}
