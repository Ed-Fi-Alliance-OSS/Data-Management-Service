// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles root-table relational GET-many query capability metadata for one resource.
/// </summary>
internal sealed class RelationalQueryCapabilityCompiler
{
    private const string QueryCapabilityPlanKind = "query capability";
    private const string ResourceIdJsonPath = "$.id";

    public RelationalQueryCapability Compile(ConcreteResourceModel concreteResourceModel)
    {
        ArgumentNullException.ThrowIfNull(concreteResourceModel);

        var resourceModel = concreteResourceModel.RelationalModel;
        var rootTable = RelationalResourceModelCompileValidator.ResolveRootScopeTableModelOrThrow(
            resourceModel,
            QueryCapabilityPlanKind
        );

        var rootColumnsByPath = CreateColumnsByPathLookup([rootTable]);
        var nonRootColumnsByPath = CreateColumnsByPathLookup(
            resourceModel.TablesInDependencyOrder.Where(tableModel => tableModel.Table != rootTable.Table)
        );
        var rootDescriptorPaths = resourceModel
            .DescriptorEdgeSources.Where(descriptorEdgeSource =>
                descriptorEdgeSource.Table == rootTable.Table
            )
            .Select(descriptorEdgeSource => descriptorEdgeSource.DescriptorValuePath.Canonical)
            .ToFrozenSet(StringComparer.Ordinal);
        Dictionary<string, SupportedRelationalQueryField> supportedFieldsByQueryField = new(
            StringComparer.Ordinal
        );
        Dictionary<string, UnsupportedRelationalQueryField> unsupportedFieldsByQueryField = new(
            StringComparer.Ordinal
        );

        foreach (
            var queryFieldMapping in concreteResourceModel.QueryFieldMappingsByQueryField.Values.OrderBy(
                static queryFieldMapping => queryFieldMapping.QueryFieldName,
                StringComparer.Ordinal
            )
        )
        {
            CompileQueryField(
                queryFieldMapping,
                rootColumnsByPath,
                nonRootColumnsByPath,
                rootDescriptorPaths,
                supportedFieldsByQueryField,
                unsupportedFieldsByQueryField
            );
        }

        return new RelationalQueryCapability(
            supportedFieldsByQueryField.ToFrozenDictionary(StringComparer.Ordinal),
            unsupportedFieldsByQueryField.ToFrozenDictionary(StringComparer.Ordinal)
        );
    }

    private static void CompileQueryField(
        RelationalQueryFieldMapping queryFieldMapping,
        FrozenDictionary<string, DbColumnName[]> rootColumnsByPath,
        FrozenDictionary<string, DbColumnName[]> nonRootColumnsByPath,
        FrozenSet<string> rootDescriptorPaths,
        IDictionary<string, SupportedRelationalQueryField> supportedFieldsByQueryField,
        IDictionary<string, UnsupportedRelationalQueryField> unsupportedFieldsByQueryField
    )
    {
        if (queryFieldMapping.Paths.Count != 1)
        {
            unsupportedFieldsByQueryField[queryFieldMapping.QueryFieldName] =
                new UnsupportedRelationalQueryField(
                    queryFieldMapping.QueryFieldName,
                    queryFieldMapping.Paths,
                    RelationalQueryFieldFailureKind.MultiPath
                );
            return;
        }

        var queryPath = queryFieldMapping.Paths[0];

        if (string.Equals(queryPath.Path.Canonical, ResourceIdJsonPath, StringComparison.Ordinal))
        {
            unsupportedFieldsByQueryField[queryFieldMapping.QueryFieldName] =
                new UnsupportedRelationalQueryField(
                    queryFieldMapping.QueryFieldName,
                    queryFieldMapping.Paths,
                    RelationalQueryFieldFailureKind.SpecialCaseId
                );
            return;
        }

        if (rootDescriptorPaths.Contains(queryPath.Path.Canonical))
        {
            unsupportedFieldsByQueryField[queryFieldMapping.QueryFieldName] =
                new UnsupportedRelationalQueryField(
                    queryFieldMapping.QueryFieldName,
                    queryFieldMapping.Paths,
                    RelationalQueryFieldFailureKind.SpecialCaseDescriptor
                );
            return;
        }

        if (rootColumnsByPath.TryGetValue(queryPath.Path.Canonical, out var rootColumns))
        {
            if (rootColumns.Length == 1)
            {
                supportedFieldsByQueryField[queryFieldMapping.QueryFieldName] =
                    new SupportedRelationalQueryField(
                        queryFieldMapping.QueryFieldName,
                        queryPath,
                        new RelationalQueryFieldTarget.RootColumn(rootColumns[0])
                    );
                return;
            }

            unsupportedFieldsByQueryField[queryFieldMapping.QueryFieldName] =
                new UnsupportedRelationalQueryField(
                    queryFieldMapping.QueryFieldName,
                    queryFieldMapping.Paths,
                    RelationalQueryFieldFailureKind.AmbiguousRootTarget
                );
            return;
        }

        if (queryPath.Path.Segments.Any(static segment => segment is JsonPathSegment.AnyArrayElement))
        {
            unsupportedFieldsByQueryField[queryFieldMapping.QueryFieldName] =
                new UnsupportedRelationalQueryField(
                    queryFieldMapping.QueryFieldName,
                    queryFieldMapping.Paths,
                    RelationalQueryFieldFailureKind.ArrayCrossing
                );
            return;
        }

        if (nonRootColumnsByPath.ContainsKey(queryPath.Path.Canonical))
        {
            unsupportedFieldsByQueryField[queryFieldMapping.QueryFieldName] =
                new UnsupportedRelationalQueryField(
                    queryFieldMapping.QueryFieldName,
                    queryFieldMapping.Paths,
                    RelationalQueryFieldFailureKind.NonRootTable
                );
            return;
        }

        unsupportedFieldsByQueryField[queryFieldMapping.QueryFieldName] = new UnsupportedRelationalQueryField(
            queryFieldMapping.QueryFieldName,
            queryFieldMapping.Paths,
            RelationalQueryFieldFailureKind.UnmappedPath
        );
    }

    private static FrozenDictionary<string, DbColumnName[]> CreateColumnsByPathLookup(
        IEnumerable<DbTableModel> tableModels
    )
    {
        return tableModels
            .SelectMany(static tableModel => tableModel.Columns)
            .Where(static column => column.Kind is ColumnKind.Scalar && column.SourceJsonPath is not null)
            .GroupBy(static column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
            .ToFrozenDictionary(
                static grouping => grouping.Key,
                static grouping =>
                    grouping
                        .Select(static column => column.ColumnName)
                        .Distinct()
                        .OrderBy(static columnName => columnName.Value, StringComparer.Ordinal)
                        .ToArray(),
                StringComparer.Ordinal
            );
    }
}
