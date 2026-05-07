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
    private static readonly StringComparer QueryFieldNameComparer = StringComparer.OrdinalIgnoreCase;

    public RelationalQueryCapability Compile(ConcreteResourceModel concreteResourceModel)
    {
        ArgumentNullException.ThrowIfNull(concreteResourceModel);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            return new RelationalQueryCapability(
                new RelationalQuerySupport.Omitted(
                    new RelationalQueryCapabilityOmission(
                        RelationalQueryCapabilityOmissionKind.DescriptorResource,
                        $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses descriptor-specific query capability metadata instead of compiled relational GET-many support."
                    )
                ),
                FrozenDictionary<string, SupportedRelationalQueryField>.Empty,
                FrozenDictionary<string, UnsupportedRelationalQueryField>.Empty
            );
        }

        if (concreteResourceModel.StorageKind != ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Cannot compile {QueryCapabilityPlanKind}: resource '{concreteResourceModel.RelationalModel.Resource.ProjectName}.{concreteResourceModel.RelationalModel.Resource.ResourceName}' "
                    + $"has unsupported storage kind '{concreteResourceModel.StorageKind}'."
            );
        }

        ValidateCaseInsensitiveQueryFieldNameCollisions(concreteResourceModel);

        var resourceModel = concreteResourceModel.RelationalModel;
        var rootTable = RelationalResourceModelCompileValidator.ResolveRootScopeTableModelOrThrow(
            resourceModel,
            QueryCapabilityPlanKind
        );

        var rootColumnsByPath = CreateColumnsByPathLookup([rootTable]);
        var rootColumnsByName = rootTable.Columns.ToFrozenDictionary(static column => column.ColumnName);
        var nonRootColumnsByPath = CreateColumnsByPathLookup(
            resourceModel.TablesInDependencyOrder.Where(tableModel => tableModel.Table != rootTable.Table)
        );
        var rootDescriptorTargetsByPath = CreateDescriptorTargetsByPathLookup(
            resourceModel.DescriptorEdgeSources.Where(descriptorEdgeSource =>
                descriptorEdgeSource.Table == rootTable.Table
            )
        );
        ReferenceIdentityQueryTargetResolver? referenceIdentityQueryTargetResolver = null;

        ReferenceIdentityQueryTargetResolver GetReferenceIdentityQueryTargetResolver() =>
            referenceIdentityQueryTargetResolver ??= new ReferenceIdentityQueryTargetResolver(
                resourceModel,
                rootTable
            );

        Dictionary<string, SupportedRelationalQueryField> supportedFieldsByQueryField = new(
            QueryFieldNameComparer
        );
        Dictionary<string, UnsupportedRelationalQueryField> unsupportedFieldsByQueryField = new(
            QueryFieldNameComparer
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
                rootColumnsByName,
                nonRootColumnsByPath,
                rootDescriptorTargetsByPath,
                GetReferenceIdentityQueryTargetResolver,
                supportedFieldsByQueryField,
                unsupportedFieldsByQueryField
            );
        }

        return new RelationalQueryCapability(
            CreateSupport(unsupportedFieldsByQueryField),
            supportedFieldsByQueryField.ToFrozenDictionary(QueryFieldNameComparer),
            unsupportedFieldsByQueryField.ToFrozenDictionary(QueryFieldNameComparer)
        );
    }

    private static void ValidateCaseInsensitiveQueryFieldNameCollisions(
        ConcreteResourceModel concreteResourceModel
    )
    {
        string[][] collidingQueryFieldGroups = concreteResourceModel
            .QueryFieldMappingsByQueryField.Values.Select(static queryFieldMapping =>
                queryFieldMapping.QueryFieldName
            )
            .OrderBy(static queryFieldName => queryFieldName, StringComparer.Ordinal)
            .GroupBy(static queryFieldName => queryFieldName, QueryFieldNameComparer)
            .Select(static queryFieldGroup => queryFieldGroup.ToArray())
            .Where(static queryFieldGroup => queryFieldGroup.Length > 1)
            .ToArray();

        if (collidingQueryFieldGroups.Length == 0)
        {
            return;
        }

        var resource = concreteResourceModel.RelationalModel.Resource;
        var collisionSummary = string.Join(
            "; ",
            collidingQueryFieldGroups.Select(static queryFieldGroup =>
                string.Join(", ", queryFieldGroup.Select(static queryFieldName => $"'{queryFieldName}'"))
            )
        );

        throw new InvalidOperationException(
            $"Cannot compile {QueryCapabilityPlanKind}: resource '{resource.ProjectName}.{resource.ResourceName}' contains queryFieldMapping entries that collide case-insensitively: {collisionSummary}. "
                + "Rename each colliding entry so every query field name is unique under OrdinalIgnoreCase comparison."
        );
    }

    private static void CompileQueryField(
        RelationalQueryFieldMapping queryFieldMapping,
        FrozenDictionary<string, DbColumnName[]> rootColumnsByPath,
        FrozenDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        FrozenDictionary<string, DbColumnName[]> nonRootColumnsByPath,
        FrozenDictionary<string, DescriptorTarget[]> rootDescriptorTargetsByPath,
        Func<ReferenceIdentityQueryTargetResolver> getReferenceIdentityQueryTargetResolver,
        IDictionary<string, SupportedRelationalQueryField> supportedFieldsByQueryField,
        IDictionary<string, UnsupportedRelationalQueryField> unsupportedFieldsByQueryField
    )
    {
        if (queryFieldMapping.Paths.Count != 1)
        {
            MarkUnsupported(
                queryFieldMapping,
                RelationalQueryFieldFailureKind.MultiPath,
                unsupportedFieldsByQueryField
            );
            return;
        }

        var queryPath = queryFieldMapping.Paths[0];

        if (string.Equals(queryPath.Path.Canonical, ResourceIdJsonPath, StringComparison.Ordinal))
        {
            AddSupportedQueryFieldOrUnsupportedType(
                queryFieldMapping,
                queryPath,
                new RelationalQueryFieldTarget.DocumentUuid(),
                rootColumnsByName,
                supportedFieldsByQueryField,
                unsupportedFieldsByQueryField
            );
            return;
        }

        if (rootDescriptorTargetsByPath.TryGetValue(queryPath.Path.Canonical, out var rootDescriptorTargets))
        {
            if (rootDescriptorTargets.Length == 1)
            {
                AddSupportedQueryFieldOrUnsupportedType(
                    queryFieldMapping,
                    queryPath,
                    new RelationalQueryFieldTarget.DescriptorIdColumn(
                        rootDescriptorTargets[0].Column,
                        rootDescriptorTargets[0].DescriptorResource
                    ),
                    rootColumnsByName,
                    supportedFieldsByQueryField,
                    unsupportedFieldsByQueryField
                );
                return;
            }

            var collapsedDescriptorTarget = TryCollapseExactDescriptorAmbiguity(
                queryPath.Path,
                rootDescriptorTargets,
                getReferenceIdentityQueryTargetResolver
            );

            if (collapsedDescriptorTarget is not null)
            {
                AddSupportedQueryFieldOrUnsupportedType(
                    queryFieldMapping,
                    queryPath,
                    collapsedDescriptorTarget,
                    rootColumnsByName,
                    supportedFieldsByQueryField,
                    unsupportedFieldsByQueryField
                );
                return;
            }

            MarkUnsupported(
                queryFieldMapping,
                RelationalQueryFieldFailureKind.AmbiguousRootTarget,
                unsupportedFieldsByQueryField
            );
            return;
        }

        if (rootColumnsByPath.TryGetValue(queryPath.Path.Canonical, out var rootColumns))
        {
            if (rootColumns.Length == 1)
            {
                AddSupportedQueryFieldOrUnsupportedType(
                    queryFieldMapping,
                    queryPath,
                    new RelationalQueryFieldTarget.RootColumn(rootColumns[0]),
                    rootColumnsByName,
                    supportedFieldsByQueryField,
                    unsupportedFieldsByQueryField
                );
                return;
            }

            var collapsedRootColumnTarget = getReferenceIdentityQueryTargetResolver()
                .CollapseExactAmbiguityOrThrow(queryPath.Path, rootColumns);

            if (collapsedRootColumnTarget is not null)
            {
                AddSupportedQueryFieldOrUnsupportedType(
                    queryFieldMapping,
                    queryPath,
                    collapsedRootColumnTarget,
                    rootColumnsByName,
                    supportedFieldsByQueryField,
                    unsupportedFieldsByQueryField
                );
                return;
            }

            MarkUnsupported(
                queryFieldMapping,
                RelationalQueryFieldFailureKind.AmbiguousRootTarget,
                unsupportedFieldsByQueryField
            );
            return;
        }

        if (queryPath.Path.Segments.Any(static segment => segment is JsonPathSegment.AnyArrayElement))
        {
            MarkUnsupported(
                queryFieldMapping,
                RelationalQueryFieldFailureKind.ArrayCrossing,
                unsupportedFieldsByQueryField
            );
            return;
        }

        if (nonRootColumnsByPath.ContainsKey(queryPath.Path.Canonical))
        {
            MarkUnsupported(
                queryFieldMapping,
                RelationalQueryFieldFailureKind.NonRootTable,
                unsupportedFieldsByQueryField
            );
            return;
        }

        MarkUnsupported(
            queryFieldMapping,
            RelationalQueryFieldFailureKind.UnmappedPath,
            unsupportedFieldsByQueryField
        );
    }

    private static void AddSupportedQueryFieldOrUnsupportedType(
        RelationalQueryFieldMapping queryFieldMapping,
        RelationalQueryFieldPath queryPath,
        RelationalQueryFieldTarget target,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        IDictionary<string, SupportedRelationalQueryField> supportedFieldsByQueryField,
        IDictionary<string, UnsupportedRelationalQueryField> unsupportedFieldsByQueryField
    )
    {
        if (!IsCompatibleTargetQueryType(queryPath, target, rootColumnsByName))
        {
            MarkUnsupported(
                queryFieldMapping,
                RelationalQueryFieldFailureKind.UnmappedPath,
                unsupportedFieldsByQueryField
            );
            return;
        }

        supportedFieldsByQueryField[queryFieldMapping.QueryFieldName] = new SupportedRelationalQueryField(
            queryFieldMapping.QueryFieldName,
            queryPath,
            target
        );
    }

    private static void MarkUnsupported(
        RelationalQueryFieldMapping queryFieldMapping,
        RelationalQueryFieldFailureKind failureKind,
        IDictionary<string, UnsupportedRelationalQueryField> unsupportedFieldsByQueryField
    )
    {
        unsupportedFieldsByQueryField[queryFieldMapping.QueryFieldName] = new UnsupportedRelationalQueryField(
            queryFieldMapping.QueryFieldName,
            queryFieldMapping.Paths,
            failureKind
        );
    }

    private static bool IsCompatibleTargetQueryType(
        RelationalQueryFieldPath queryPath,
        RelationalQueryFieldTarget target,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName
    )
    {
        return target switch
        {
            RelationalQueryFieldTarget.DocumentUuid => IsStringQueryType(queryPath),
            RelationalQueryFieldTarget.DescriptorIdColumn => IsStringQueryType(queryPath),
            RelationalQueryFieldTarget.RootColumn(var column) => rootColumnsByName.TryGetValue(
                column,
                out var rootColumn
            )
                && rootColumn.ScalarType is not null
                && IsCompatibleScalarQueryType(queryPath, rootColumn.ScalarType),
            _ => false,
        };
    }

    private static bool IsStringQueryType(RelationalQueryFieldPath queryPath) =>
        string.Equals(queryPath.Type, "string", StringComparison.Ordinal);

    private static bool IsCompatibleScalarQueryType(
        RelationalQueryFieldPath queryPath,
        RelationalScalarType scalarType
    )
    {
        return queryPath.Type switch
        {
            "boolean" => scalarType.Kind == ScalarKind.Boolean,
            "date" => scalarType.Kind == ScalarKind.Date,
            "date-time" => scalarType.Kind == ScalarKind.DateTime,
            "number" => scalarType.Kind is ScalarKind.Int32 or ScalarKind.Int64 or ScalarKind.Decimal,
            "string" => scalarType.Kind == ScalarKind.String,
            "time" => scalarType.Kind == ScalarKind.Time,
            _ => false,
        };
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

    private static RelationalQueryFieldTarget? TryCollapseExactDescriptorAmbiguity(
        JsonPathExpression queryPath,
        IReadOnlyCollection<DescriptorTarget> rootDescriptorTargets,
        Func<ReferenceIdentityQueryTargetResolver> getReferenceIdentityQueryTargetResolver
    )
    {
        var descriptorResources = rootDescriptorTargets
            .Select(static target => target.DescriptorResource)
            .Distinct()
            .ToArray();

        if (descriptorResources.Length != 1)
        {
            return null;
        }

        return getReferenceIdentityQueryTargetResolver()
            .CollapseExactAmbiguityOrThrow(
                queryPath,
                rootDescriptorTargets.Select(static target => target.Column).ToArray()
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
