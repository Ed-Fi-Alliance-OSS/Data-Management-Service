// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal sealed class RelationalQueryPageKeysetPlanner(SqlDialect dialect)
{
    private const string OffsetParameterName = "offset";
    private const string LimitParameterName = "limit";

    private readonly PageDocumentIdSqlCompiler _sqlCompiler = new(dialect);

    public PageKeysetSpec.Query Plan(
        DbTableModel rootTable,
        RelationalQueryPreprocessingResult preprocessingResult,
        PaginationParameters paginationParameters,
        Func<PreprocessedRelationalQueryElement, QueryComparisonOperator>? comparisonOperatorResolver = null
    )
    {
        var plannedQuery = PlanOrEmptyPage(
            rootTable,
            preprocessingResult,
            paginationParameters,
            out var emptyPageReason,
            comparisonOperatorResolver
        );

        return plannedQuery
            ?? throw new InvalidOperationException(
                emptyPageReason ?? "Relational query planning could not produce a page keyset for this query."
            );
    }

    public bool TryPlan(
        DbTableModel rootTable,
        RelationalQueryPreprocessingResult preprocessingResult,
        PaginationParameters paginationParameters,
        out PageKeysetSpec.Query? plannedQuery,
        out string? emptyPageReason,
        Func<PreprocessedRelationalQueryElement, QueryComparisonOperator>? comparisonOperatorResolver = null
    )
    {
        plannedQuery = PlanOrEmptyPage(
            rootTable,
            preprocessingResult,
            paginationParameters,
            out emptyPageReason,
            comparisonOperatorResolver
        );

        return plannedQuery is not null;
    }

    private PageKeysetSpec.Query? PlanOrEmptyPage(
        DbTableModel rootTable,
        RelationalQueryPreprocessingResult preprocessingResult,
        PaginationParameters paginationParameters,
        out string? emptyPageReason,
        Func<PreprocessedRelationalQueryElement, QueryComparisonOperator>? comparisonOperatorResolver = null
    )
    {
        ArgumentNullException.ThrowIfNull(rootTable);
        ArgumentNullException.ThrowIfNull(preprocessingResult);
        emptyPageReason = null;

        if (preprocessingResult.Outcome is not RelationalQueryPreprocessingOutcome.Continue)
        {
            throw new ArgumentException(
                "Relational query planning requires preprocessing results in the continue state.",
                nameof(preprocessingResult)
            );
        }

        comparisonOperatorResolver ??= static _ => QueryComparisonOperator.Equal;

        var rootColumnsByName = rootTable.Columns.ToDictionary(
            static column => column.ColumnName,
            static column => column
        );
        var parameterNamesByIndex = DeriveParameterNames(preprocessingResult.QueryElementsInOrder);
        var predicates = new QueryValuePredicate[preprocessingResult.QueryElementsInOrder.Count];
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal)
        {
            [OffsetParameterName] = (long)(paginationParameters.Offset ?? 0),
            [LimitParameterName] = (long)(paginationParameters.Limit ?? paginationParameters.MaximumPageSize),
        };

        for (var index = 0; index < preprocessingResult.QueryElementsInOrder.Count; index++)
        {
            var queryElement =
                preprocessingResult.QueryElementsInOrder[index]
                ?? throw new ArgumentException(
                    "Query elements must not contain null entries.",
                    nameof(preprocessingResult)
                );
            var comparisonOperator = comparisonOperatorResolver(queryElement);
            var parameterName = parameterNamesByIndex[index];
            var plannedPredicate = PlanPredicate(
                rootTable,
                rootColumnsByName,
                queryElement,
                parameterName,
                comparisonOperator,
                out var predicateEmptyPageReason
            );

            if (plannedPredicate is null)
            {
                emptyPageReason =
                    predicateEmptyPageReason
                    ?? "Relational query planning determined this query has no matches.";
                return null;
            }

            predicates[index] = plannedPredicate.Predicate;
            parameterValues[parameterName] = plannedPredicate.ParameterValue;
        }

        var querySpec = new PageDocumentIdQuerySpec(
            RootTable: rootTable.Table,
            Predicates: predicates,
            UnifiedAliasMappingsByColumn: BuildUnifiedAliasMappingsByColumn(rootTable),
            OffsetParameterName: OffsetParameterName,
            LimitParameterName: LimitParameterName,
            IncludeTotalCountSql: paginationParameters.TotalCount
        );
        var sqlPlan = _sqlCompiler.Compile(querySpec);

        return new PageKeysetSpec.Query(sqlPlan, parameterValues);
    }

    private static PlannedPredicate? PlanPredicate(
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        PreprocessedRelationalQueryElement queryElement,
        string parameterName,
        QueryComparisonOperator comparisonOperator,
        out string? emptyPageReason
    )
    {
        emptyPageReason = null;

        if (comparisonOperator is not QueryComparisonOperator.Equal)
        {
            throw new NotSupportedException(
                $"Relational query planning only supports exact-match equality predicates. Query field "
                    + $"'{queryElement.SupportedField.QueryFieldName}' was routed with operator '{comparisonOperator}'."
            );
        }

        ValidateSinglePathOrThrow(queryElement);

        return queryElement.SupportedField.Target switch
        {
            RelationalQueryFieldTarget.RootColumn(var column) => PlanRootColumnPredicate(
                rootTable,
                rootColumnsByName,
                queryElement,
                column,
                parameterName,
                comparisonOperator,
                out emptyPageReason
            ),
            RelationalQueryFieldTarget.DocumentUuid => PlanDocumentUuidPredicate(
                queryElement,
                parameterName,
                comparisonOperator
            ),
            RelationalQueryFieldTarget.DescriptorIdColumn(var column, _) => PlanDescriptorIdPredicate(
                rootTable,
                rootColumnsByName,
                queryElement,
                column,
                parameterName,
                comparisonOperator
            ),
            _ => throw new InvalidOperationException(
                $"Relational query planning does not recognize supported target type "
                    + $"'{queryElement.SupportedField.Target.GetType().Name}' for query field "
                    + $"'{queryElement.SupportedField.QueryFieldName}'."
            ),
        };
    }

    private static PlannedPredicate? PlanRootColumnPredicate(
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        PreprocessedRelationalQueryElement queryElement,
        DbColumnName column,
        string parameterName,
        QueryComparisonOperator comparisonOperator,
        out string? emptyPageReason
    )
    {
        emptyPageReason = null;

        if (queryElement.Value is not PreprocessedRelationalQueryValue.Raw(var rawValue))
        {
            throw new InvalidOperationException(
                $"Relational query planning expected a raw scalar value for query field "
                    + $"'{queryElement.SupportedField.QueryFieldName}', but received '{queryElement.Value.GetType().Name}'."
            );
        }

        var rootColumn = GetRootColumnOrThrow(rootTable, rootColumnsByName, column);
        var scalarType =
            rootColumn.ScalarType
            ?? throw new InvalidOperationException(
                $"Relational query planning requires root column '{column.Value}' on table '{rootTable.Table}' "
                    + $"to expose scalar type metadata for query field '{queryElement.SupportedField.QueryFieldName}'."
            );

        ValidateCompatibleQueryTypeOrThrow(
            queryElement.SupportedField.QueryFieldName,
            queryElement.SupportedField.Path.Type,
            scalarType
        );

        if (
            !TryConvertRawValue(
                queryElement.SupportedField.QueryFieldName,
                rawValue,
                scalarType,
                out var convertedValue,
                out emptyPageReason
            )
        )
        {
            return null;
        }

        return new PlannedPredicate(
            new QueryValuePredicate(column, comparisonOperator, parameterName, scalarType.Kind),
            convertedValue!
        );
    }

    private static PlannedPredicate PlanDocumentUuidPredicate(
        PreprocessedRelationalQueryElement queryElement,
        string parameterName,
        QueryComparisonOperator comparisonOperator
    )
    {
        if (queryElement.Value is not PreprocessedRelationalQueryValue.DocumentUuid(var documentUuid))
        {
            throw new InvalidOperationException(
                $"Relational query planning expected a parsed document UUID for query field "
                    + $"'{queryElement.SupportedField.QueryFieldName}', but received '{queryElement.Value.GetType().Name}'."
            );
        }

        return new PlannedPredicate(
            new QueryValuePredicate(
                new QueryPredicateTarget.DocumentUuid(),
                comparisonOperator,
                parameterName
            ),
            documentUuid
        );
    }

    private static PlannedPredicate PlanDescriptorIdPredicate(
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        PreprocessedRelationalQueryElement queryElement,
        DbColumnName column,
        string parameterName,
        QueryComparisonOperator comparisonOperator
    )
    {
        if (queryElement.Value is not PreprocessedRelationalQueryValue.DescriptorDocumentId(var descriptorId))
        {
            throw new InvalidOperationException(
                $"Relational query planning expected a resolved descriptor document id for query field "
                    + $"'{queryElement.SupportedField.QueryFieldName}', but received '{queryElement.Value.GetType().Name}'."
            );
        }

        var rootColumn = GetRootColumnOrThrow(rootTable, rootColumnsByName, column);
        var scalarKind = rootColumn.ScalarType?.Kind ?? ScalarKind.Int64;

        return new PlannedPredicate(
            new QueryValuePredicate(column, comparisonOperator, parameterName, scalarKind),
            descriptorId
        );
    }

    private static IReadOnlyList<string> DeriveParameterNames(
        IReadOnlyList<PreprocessedRelationalQueryElement> queryElementsInOrder
    )
    {
        HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            OffsetParameterName,
            LimitParameterName,
        };
        Dictionary<string, int> nextSuffixByBaseName = new(StringComparer.OrdinalIgnoreCase)
        {
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
                        Path: element.SupportedField.Path.Path.Canonical
                    )
            )
            .OrderBy(static seed => seed.BaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static seed => seed.BaseName, StringComparer.Ordinal)
            .ThenBy(static seed => seed.QueryFieldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static seed => seed.QueryFieldName, StringComparer.Ordinal)
            .ThenBy(static seed => seed.Path, StringComparer.Ordinal)
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

    private static IReadOnlyDictionary<
        DbColumnName,
        ColumnStorage.UnifiedAlias
    > BuildUnifiedAliasMappingsByColumn(DbTableModel rootTable)
    {
        Dictionary<DbColumnName, ColumnStorage.UnifiedAlias> unifiedAliasMappingsByColumn = [];

        foreach (var column in rootTable.Columns)
        {
            if (column.Storage is ColumnStorage.UnifiedAlias unifiedAlias)
            {
                unifiedAliasMappingsByColumn[column.ColumnName] = unifiedAlias;
            }
        }

        return unifiedAliasMappingsByColumn;
    }

    private static DbColumnModel GetRootColumnOrThrow(
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        DbColumnName column
    )
    {
        if (rootColumnsByName.TryGetValue(column, out var rootColumn))
        {
            return rootColumn;
        }

        throw new InvalidOperationException(
            $"Relational query planning could not find root column '{column.Value}' on table '{rootTable.Table}'."
        );
    }

    private static void ValidateSinglePathOrThrow(PreprocessedRelationalQueryElement queryElement)
    {
        if (queryElement.QueryElement.DocumentPaths.Length != 1)
        {
            throw new NotSupportedException(
                $"Relational query planning only supports one compiled document path per query field. Query field "
                    + $"'{queryElement.SupportedField.QueryFieldName}' was routed with "
                    + $"{queryElement.QueryElement.DocumentPaths.Length} paths."
            );
        }

        if (
            !string.Equals(
                queryElement.QueryElement.DocumentPaths[0].Value,
                queryElement.SupportedField.Path.Path.Canonical,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidOperationException(
                $"Relational query planning expected canonical path '{queryElement.SupportedField.Path.Path.Canonical}' "
                    + $"for query field '{queryElement.SupportedField.QueryFieldName}', but received "
                    + $"'{queryElement.QueryElement.DocumentPaths[0].Value}'."
            );
        }
    }

    private static void ValidateCompatibleQueryTypeOrThrow(
        string queryFieldName,
        string queryType,
        RelationalScalarType scalarType
    )
    {
        var isCompatible = queryType switch
        {
            "boolean" => scalarType.Kind == ScalarKind.Boolean,
            "date" => scalarType.Kind == ScalarKind.Date,
            "date-time" => scalarType.Kind == ScalarKind.DateTime,
            "number" => scalarType.Kind is ScalarKind.Int32 or ScalarKind.Int64 or ScalarKind.Decimal,
            "string" => scalarType.Kind == ScalarKind.String,
            "time" => scalarType.Kind == ScalarKind.Time,
            _ => false,
        };

        if (isCompatible)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Relational query planning found incompatible scalar metadata for query field '{queryFieldName}'. "
                + $"ApiSchema type '{queryType}' cannot bind to relational scalar kind '{scalarType.Kind}'."
        );
    }

    private static bool TryConvertRawValue(
        string queryFieldName,
        string rawValue,
        RelationalScalarType scalarType,
        out object? convertedValue,
        out string? emptyPageReason
    )
    {
        if (RelationalScalarLiteralParser.TryParse(rawValue, scalarType, out convertedValue))
        {
            emptyPageReason = null;
            return true;
        }

        emptyPageReason =
            $"Relational query planning determined query field '{queryFieldName}' value "
            + $"'{rawValue}' cannot be represented as relational scalar kind '{scalarType.Kind}', "
            + "so the query has no matches.";
        return false;
    }

    private sealed record PlannedPredicate(QueryValuePredicate Predicate, object ParameterValue);

    private sealed record ParameterNameSeed(int Index, string BaseName, string QueryFieldName, string Path);
}
