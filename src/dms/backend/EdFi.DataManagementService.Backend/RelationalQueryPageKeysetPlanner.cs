// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal abstract record RelationalQueryPlanningResult
{
    private RelationalQueryPlanningResult() { }

    internal sealed record Planned(PageKeysetSpec.Query Keyset) : RelationalQueryPlanningResult;

    internal sealed record EmptyPage(string Reason) : RelationalQueryPlanningResult;
}

internal sealed class RelationalQueryPageKeysetPlanner(SqlDialect dialect)
{
    private const string OffsetParameterName = "offset";
    private const string LimitParameterName = "limit";
    private const string DateOnlyFormat = "yyyy-MM-dd";
    private const string UtcDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private const string OffsetDateTimeFormat = "yyyy-MM-dd'T'HH:mm:sszzz";
    private const string TimeOnlyFormat = "HH:mm:ss";

    private readonly PageDocumentIdSqlCompiler _sqlCompiler = new(dialect);

    public RelationalQueryPlanningResult Plan(
        DbTableModel rootTable,
        RelationalQueryPreprocessingResult preprocessingResult,
        PaginationParameters paginationParameters,
        Func<PreprocessedRelationalQueryElement, QueryComparisonOperator>? comparisonOperatorResolver = null
    )
    {
        ArgumentNullException.ThrowIfNull(rootTable);
        ArgumentNullException.ThrowIfNull(preprocessingResult);

        if (preprocessingResult.Outcome is RelationalQueryPreprocessingOutcome.EmptyPage(var reason))
        {
            return new RelationalQueryPlanningResult.EmptyPage(reason);
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
            var (predicate, parameterValue) = PlanPredicate(
                rootTable,
                rootColumnsByName,
                queryElement,
                parameterName,
                comparisonOperator
            );

            predicates[index] = predicate;
            parameterValues[parameterName] = parameterValue;
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

        return new RelationalQueryPlanningResult.Planned(new PageKeysetSpec.Query(sqlPlan, parameterValues));
    }

    private static (QueryValuePredicate Predicate, object ParameterValue) PlanPredicate(
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        PreprocessedRelationalQueryElement queryElement,
        string parameterName,
        QueryComparisonOperator comparisonOperator
    )
    {
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
                comparisonOperator
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

    private static (QueryValuePredicate Predicate, object ParameterValue) PlanRootColumnPredicate(
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        PreprocessedRelationalQueryElement queryElement,
        DbColumnName column,
        string parameterName,
        QueryComparisonOperator comparisonOperator
    )
    {
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

        return (
            new QueryValuePredicate(column, comparisonOperator, parameterName, scalarType.Kind),
            ConvertRawValue(queryElement.SupportedField.QueryFieldName, rawValue, scalarType)
        );
    }

    private static (QueryValuePredicate Predicate, object ParameterValue) PlanDocumentUuidPredicate(
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

        return (
            new QueryValuePredicate(
                new QueryPredicateTarget.DocumentUuid(),
                comparisonOperator,
                parameterName
            ),
            documentUuid
        );
    }

    private static (QueryValuePredicate Predicate, object ParameterValue) PlanDescriptorIdPredicate(
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

        return (new QueryValuePredicate(column, comparisonOperator, parameterName, scalarKind), descriptorId);
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

    private static object ConvertRawValue(
        string queryFieldName,
        string rawValue,
        RelationalScalarType scalarType
    )
    {
        return scalarType.Kind switch
        {
            ScalarKind.String => rawValue,
            ScalarKind.Int32
                when int.TryParse(
                    rawValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var int32Value
                ) => int32Value,
            ScalarKind.Int64
                when long.TryParse(
                    rawValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var int64Value
                ) => int64Value,
            ScalarKind.Decimal
                when decimal.TryParse(
                    rawValue,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var decimalValue
                ) => decimalValue,
            ScalarKind.Boolean when bool.TryParse(rawValue, out var boolValue) => boolValue,
            ScalarKind.Date
                when DateOnly.TryParseExact(
                    rawValue,
                    DateOnlyFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dateOnlyValue
                ) => dateOnlyValue,
            ScalarKind.DateTime when TryReadDateTimeValue(rawValue, out var dateTimeValue) => dateTimeValue,
            ScalarKind.Time
                when TimeOnly.TryParseExact(
                    rawValue,
                    TimeOnlyFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timeOnlyValue
                ) => timeOnlyValue,
            _ => throw new InvalidOperationException(
                $"Relational query planning could not convert validated query field '{queryFieldName}' value "
                    + $"'{rawValue}' to relational scalar kind '{scalarType.Kind}'."
            ),
        };
    }

    private static bool TryReadDateTimeValue(string rawValue, out DateTime dateTimeValue)
    {
        if (
            rawValue.EndsWith('Z')
            && DateTime.TryParseExact(
                rawValue[..^1],
                UtcDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var utcDateTimeValue
            )
        )
        {
            dateTimeValue = DateTime.SpecifyKind(utcDateTimeValue, DateTimeKind.Utc);
            return true;
        }

        if (
            DateTimeOffset.TryParseExact(
                rawValue,
                OffsetDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var offsetDateTimeValue
            )
        )
        {
            dateTimeValue = offsetDateTimeValue.UtcDateTime;
            return true;
        }

        dateTimeValue = default;
        return false;
    }

    private sealed record ParameterNameSeed(int Index, string BaseName, string QueryFieldName, string Path);
}
