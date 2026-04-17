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
    private const string DescriptorEndpointQueryStoryRef = "E08-S05 (05-descriptor-endpoints.md)";
    private const string QueryCapabilityPlanKind = "query capability";
    private const string ResourceIdJsonPath = "$.id";

    public RelationalQueryCapability Compile(ConcreteResourceModel concreteResourceModel)
    {
        ArgumentNullException.ThrowIfNull(concreteResourceModel);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            return new RelationalQueryCapability(
                new RelationalQuerySupport.Omitted(
                    new RelationalQueryCapabilityOmission(
                        RelationalQueryCapabilityOmissionKind.DescriptorResource,
                        $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor endpoint query path instead of compiled relational GET-many support. "
                            + $"Next story: {DescriptorEndpointQueryStoryRef}."
                    )
                ),
                new Dictionary<string, SupportedRelationalQueryField>(
                    StringComparer.Ordinal
                ).ToFrozenDictionary(StringComparer.Ordinal),
                new Dictionary<string, UnsupportedRelationalQueryField>(
                    StringComparer.Ordinal
                ).ToFrozenDictionary(StringComparer.Ordinal)
            );
        }

        if (concreteResourceModel.StorageKind != ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Cannot compile {QueryCapabilityPlanKind}: resource '{concreteResourceModel.RelationalModel.Resource.ProjectName}.{concreteResourceModel.RelationalModel.Resource.ResourceName}' "
                    + $"has unsupported storage kind '{concreteResourceModel.StorageKind}'."
            );
        }

        var resourceModel = concreteResourceModel.RelationalModel;
        var rootTable = RelationalResourceModelCompileValidator.ResolveRootScopeTableModelOrThrow(
            resourceModel,
            QueryCapabilityPlanKind
        );

        var rootColumnsByPath = CreateColumnsByPathLookup([rootTable]);
        var nonRootColumnsByPath = CreateColumnsByPathLookup(
            resourceModel.TablesInDependencyOrder.Where(tableModel => tableModel.Table != rootTable.Table)
        );
        var rootDescriptorTargetsByPath = CreateDescriptorTargetsByPathLookup(
            resourceModel.DescriptorEdgeSources.Where(descriptorEdgeSource =>
                descriptorEdgeSource.Table == rootTable.Table
            )
        );
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
                rootDescriptorTargetsByPath,
                supportedFieldsByQueryField,
                unsupportedFieldsByQueryField
            );
        }

        return new RelationalQueryCapability(
            CreateSupport(unsupportedFieldsByQueryField),
            supportedFieldsByQueryField.ToFrozenDictionary(StringComparer.Ordinal),
            unsupportedFieldsByQueryField.ToFrozenDictionary(StringComparer.Ordinal)
        );
    }

    private static void CompileQueryField(
        RelationalQueryFieldMapping queryFieldMapping,
        FrozenDictionary<string, DbColumnName[]> rootColumnsByPath,
        FrozenDictionary<string, DbColumnName[]> nonRootColumnsByPath,
        FrozenDictionary<string, DescriptorTarget[]> rootDescriptorTargetsByPath,
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
            supportedFieldsByQueryField[queryFieldMapping.QueryFieldName] = new SupportedRelationalQueryField(
                queryFieldMapping.QueryFieldName,
                queryPath,
                new RelationalQueryFieldTarget.DocumentUuid()
            );
            return;
        }

        if (rootDescriptorTargetsByPath.TryGetValue(queryPath.Path.Canonical, out var rootDescriptorTargets))
        {
            if (rootDescriptorTargets.Length == 1)
            {
                supportedFieldsByQueryField[queryFieldMapping.QueryFieldName] =
                    new SupportedRelationalQueryField(
                        queryFieldMapping.QueryFieldName,
                        queryPath,
                        new RelationalQueryFieldTarget.DescriptorIdColumn(
                            rootDescriptorTargets[0].Column,
                            rootDescriptorTargets[0].DescriptorResource
                        )
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

    private static RelationalQuerySupport CreateSupport(
        IReadOnlyDictionary<string, UnsupportedRelationalQueryField> unsupportedFieldsByQueryField
    )
    {
        if (unsupportedFieldsByQueryField.Count == 0)
        {
            return new RelationalQuerySupport.Supported();
        }

        var unsupportedFieldSummary = string.Join(
            "; ",
            unsupportedFieldsByQueryField
                .Values.OrderBy(static field => field.QueryFieldName, StringComparer.Ordinal)
                .Select(static field => $"{field.QueryFieldName}: {DescribeFailureKind(field.FailureKind)}")
        );

        return new RelationalQuerySupport.Omitted(
            new RelationalQueryCapabilityOmission(
                RelationalQueryCapabilityOmissionKind.UnsupportedQueryFields,
                $"queryFieldMapping contains unsupported relational GET-many fields: {unsupportedFieldSummary}."
            )
        );
    }

    private static string DescribeFailureKind(RelationalQueryFieldFailureKind failureKind)
    {
        return failureKind switch
        {
            RelationalQueryFieldFailureKind.MultiPath =>
                "maps to multiple ApiSchema paths and cannot compile to one deterministic predicate target",
            RelationalQueryFieldFailureKind.ArrayCrossing =>
                "crosses an array scope and cannot compile to a root-table predicate",
            RelationalQueryFieldFailureKind.NonRootTable => "targets a non-root relational table",
            RelationalQueryFieldFailureKind.UnmappedPath =>
                "did not resolve to a deterministic relational binding",
            RelationalQueryFieldFailureKind.AmbiguousRootTarget =>
                "resolved to more than one possible root-table predicate target",
            _ => throw new InvalidOperationException(
                $"Unsupported {QueryCapabilityPlanKind} failure kind '{failureKind}'."
            ),
        };
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

    private static FrozenDictionary<string, DescriptorTarget[]> CreateDescriptorTargetsByPathLookup(
        IEnumerable<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        return descriptorEdgeSources
            .GroupBy(
                static descriptorEdgeSource => descriptorEdgeSource.DescriptorValuePath.Canonical,
                StringComparer.Ordinal
            )
            .ToFrozenDictionary(
                static grouping => grouping.Key,
                static grouping =>
                    grouping
                        .Select(static descriptorEdgeSource => new DescriptorTarget(
                            descriptorEdgeSource.FkColumn,
                            descriptorEdgeSource.DescriptorResource
                        ))
                        .Distinct()
                        .OrderBy(static target => target.Column.Value, StringComparer.Ordinal)
                        .ThenBy(
                            static target => target.DescriptorResource.ProjectName,
                            StringComparer.Ordinal
                        )
                        .ThenBy(
                            static target => target.DescriptorResource.ResourceName,
                            StringComparer.Ordinal
                        )
                        .ToArray(),
                StringComparer.Ordinal
            );
    }

    private readonly record struct DescriptorTarget(
        DbColumnName Column,
        QualifiedResourceName DescriptorResource
    );
}
