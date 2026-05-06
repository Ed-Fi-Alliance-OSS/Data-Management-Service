// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal sealed class DescriptorQueryPageKeysetPlanner(SqlDialect dialect)
{
    private const string OffsetParameterName = "offset";
    private const string LimitParameterName = "limit";
    private const string ResourceKeyIdParameterName = "resourceKeyId";
    private static readonly DbColumnName _documentUuidColumn = new("DocumentUuid");
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");
    private readonly PageDocumentIdSqlCompiler _sqlCompiler = new(dialect);

    public PageKeysetSpec.Query Plan(
        MappingSet mappingSet,
        QualifiedResourceName requestResource,
        DescriptorQueryPreprocessingResult preprocessingResult,
        PaginationParameters paginationParameters
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(preprocessingResult);

        if (preprocessingResult.Outcome is not RelationalQueryPreprocessingOutcome.Continue)
        {
            throw new ArgumentException(
                "Descriptor query page planning requires preprocessing results in the continue state.",
                nameof(preprocessingResult)
            );
        }

        var queryPredicates = PlanPredicates(preprocessingResult.QueryElementsInOrder);
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, requestResource);
        var pageQuerySpec = new PageDocumentIdQuerySpec(
            RootTable: _documentTable,
            Predicates:
            [
                new QueryValuePredicate(
                    new DbColumnName("ResourceKeyId"),
                    QueryComparisonOperator.Equal,
                    ResourceKeyIdParameterName
                ),
                .. queryPredicates,
            ],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            OffsetParameterName: OffsetParameterName,
            LimitParameterName: LimitParameterName,
            IncludeTotalCountSql: paginationParameters.TotalCount
        );
        var sqlPlan = _sqlCompiler.Compile(pageQuerySpec);
        var parameterValues = BuildParameterValues(
            resourceKeyId,
            preprocessingResult.QueryElementsInOrder,
            paginationParameters
        );

        return new PageKeysetSpec.Query(sqlPlan, parameterValues);
    }

    private static IReadOnlyList<QueryValuePredicate> PlanPredicates(
        IReadOnlyList<PreprocessedDescriptorQueryElement> queryElementsInOrder
    )
    {
        var parameterNamesByIndex = DeriveParameterNames(queryElementsInOrder);
        var predicates = new QueryValuePredicate[queryElementsInOrder.Count];

        for (var index = 0; index < queryElementsInOrder.Count; index++)
        {
            var queryElement =
                queryElementsInOrder[index]
                ?? throw new ArgumentException(
                    "Query elements must not contain null entries.",
                    nameof(queryElementsInOrder)
                );

            predicates[index] = queryElement.Value switch
            {
                PreprocessedDescriptorQueryValue.DocumentUuid => new QueryValuePredicate(
                    _documentUuidColumn,
                    QueryComparisonOperator.Equal,
                    parameterNamesByIndex[index]
                ),
                PreprocessedDescriptorQueryValue.Raw => queryElement.SupportedField.Target switch
                {
                    DescriptorQueryFieldTarget.Namespace(var column) => CreateDescriptorColumnPredicate(
                        column,
                        parameterNamesByIndex[index],
                        ScalarKind.String
                    ),
                    DescriptorQueryFieldTarget.CodeValue(var column) => CreateDescriptorColumnPredicate(
                        column,
                        parameterNamesByIndex[index],
                        ScalarKind.String
                    ),
                    DescriptorQueryFieldTarget.ShortDescription(var column) =>
                        CreateDescriptorColumnPredicate(
                            column,
                            parameterNamesByIndex[index],
                            ScalarKind.String
                        ),
                    DescriptorQueryFieldTarget.Description(var column) => CreateDescriptorColumnPredicate(
                        column,
                        parameterNamesByIndex[index],
                        ScalarKind.String
                    ),
                    _ => throw new InvalidOperationException(
                        $"Descriptor query page planning expected a string descriptor target for query field "
                            + $"'{queryElement.SupportedField.QueryFieldName}', but received "
                            + $"'{queryElement.SupportedField.Target.GetType().Name}'."
                    ),
                },
                PreprocessedDescriptorQueryValue.DateOnlyValue => queryElement.SupportedField.Target switch
                {
                    DescriptorQueryFieldTarget.EffectiveBeginDate(var column) =>
                        CreateDescriptorColumnPredicate(
                            column,
                            parameterNamesByIndex[index],
                            ScalarKind.Date
                        ),
                    DescriptorQueryFieldTarget.EffectiveEndDate(var column) =>
                        CreateDescriptorColumnPredicate(
                            column,
                            parameterNamesByIndex[index],
                            ScalarKind.Date
                        ),
                    _ => throw new InvalidOperationException(
                        $"Descriptor query page planning expected a date descriptor target for query field "
                            + $"'{queryElement.SupportedField.QueryFieldName}', but received "
                            + $"'{queryElement.SupportedField.Target.GetType().Name}'."
                    ),
                },
                _ => throw new InvalidOperationException(
                    $"Descriptor query page planning does not recognize preprocessed value type "
                        + $"'{queryElement.Value.GetType().Name}' for query field "
                        + $"'{queryElement.SupportedField.QueryFieldName}'."
                ),
            };
        }

        return predicates;
    }

    private static QueryValuePredicate CreateDescriptorColumnPredicate(
        DbColumnName column,
        string parameterName,
        ScalarKind scalarKind
    )
    {
        return new QueryValuePredicate(
            new QueryPredicateTarget.DescriptorColumn(column),
            QueryComparisonOperator.Equal,
            parameterName,
            scalarKind
        );
    }

    private static IReadOnlyDictionary<string, object?> BuildParameterValues(
        short resourceKeyId,
        IReadOnlyList<PreprocessedDescriptorQueryElement> queryElementsInOrder,
        PaginationParameters paginationParameters
    )
    {
        var parameterNamesByIndex = DeriveParameterNames(queryElementsInOrder);
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal)
        {
            [ResourceKeyIdParameterName] = resourceKeyId,
            [OffsetParameterName] = (long)(paginationParameters.Offset ?? 0),
            [LimitParameterName] = (long)(paginationParameters.Limit ?? paginationParameters.MaximumPageSize),
        };

        for (var index = 0; index < queryElementsInOrder.Count; index++)
        {
            var queryElement = queryElementsInOrder[index];
            parameterValues[parameterNamesByIndex[index]] = queryElement.Value switch
            {
                PreprocessedDescriptorQueryValue.Raw(var rawValue) => rawValue,
                PreprocessedDescriptorQueryValue.DocumentUuid(var documentUuid) => documentUuid,
                PreprocessedDescriptorQueryValue.DateOnlyValue(var dateOnlyValue) => dateOnlyValue,
                _ => throw new InvalidOperationException(
                    $"Descriptor query page planning does not recognize preprocessed value type "
                        + $"'{queryElement.Value.GetType().Name}' for query field "
                        + $"'{queryElement.SupportedField.QueryFieldName}'."
                ),
            };
        }

        return parameterValues;
    }

    private static IReadOnlyList<string> DeriveParameterNames(
        IReadOnlyList<PreprocessedDescriptorQueryElement> queryElementsInOrder
    )
    {
        HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ResourceKeyIdParameterName,
            OffsetParameterName,
            LimitParameterName,
        };
        Dictionary<string, int> nextSuffixByBaseName = new(StringComparer.OrdinalIgnoreCase)
        {
            [ResourceKeyIdParameterName] = 2,
            [OffsetParameterName] = 2,
            [LimitParameterName] = 2,
        };
        string[] resolvedNames = new string[queryElementsInOrder.Count];

        var orderedSeeds = queryElementsInOrder
            .Select(
                (element, index) =>
                    new ParameterNameSeed(
                        Index: index,
                        BaseName: PlanNamingConventions.SanitizeBareParameterName(
                            PlanNamingConventions.CamelCaseFirstCharacter(
                                element.SupportedField.QueryFieldName
                            )
                        ),
                        QueryFieldName: element.SupportedField.QueryFieldName,
                        PathSet: string.Join(
                            "|",
                            element.QueryElement.DocumentPaths.Select(static path => path.Value)
                        )
                    )
            )
            .OrderBy(static seed => seed.BaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static seed => seed.BaseName, StringComparer.Ordinal)
            .ThenBy(static seed => seed.QueryFieldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static seed => seed.QueryFieldName, StringComparer.Ordinal)
            .ThenBy(static seed => seed.PathSet, StringComparer.Ordinal)
            .ThenBy(static seed => seed.Index)
            .ToArray();

        foreach (var seed in orderedSeeds)
        {
            resolvedNames[seed.Index] = AllocateParameterName(seed.BaseName, usedNames, nextSuffixByBaseName);
        }

        return resolvedNames;
    }

    private static string AllocateParameterName(
        string baseName,
        ISet<string> usedNames,
        IDictionary<string, int> nextSuffixByBaseName
    )
    {
        if (usedNames.Add(baseName))
        {
            if (!nextSuffixByBaseName.TryGetValue(baseName, out var nextSuffix) || nextSuffix < 2)
            {
                nextSuffixByBaseName[baseName] = 2;
            }

            return baseName;
        }

        var suffix = nextSuffixByBaseName.TryGetValue(baseName, out var nextSuffixForBase)
            ? nextSuffixForBase
            : 2;
        var candidate = $"{baseName}_{suffix}";

        while (!usedNames.Add(candidate))
        {
            suffix++;
            candidate = $"{baseName}_{suffix}";
        }

        nextSuffixByBaseName[baseName] = suffix + 1;
        return candidate;
    }

    private sealed record ParameterNameSeed(
        int Index,
        string BaseName,
        string QueryFieldName,
        string PathSet
    );
}
