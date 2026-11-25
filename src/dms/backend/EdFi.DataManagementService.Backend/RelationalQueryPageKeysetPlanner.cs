// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// The change-version filter column and SQL parameter names shared by the relational and
/// descriptor page keyset planners. Both planners bind the same validated window, so the
/// parameter names must stay byte-identical between them; a one-sided rename would silently
/// break the change-version filter on the other path.
/// </summary>
internal static class ChangeVersionFilterConstants
{
    public const string ContentVersionColumnName = "ContentVersion";
    public const string MinChangeVersionParameterName = "minChangeVersion";
    public const string MaxChangeVersionParameterName = "maxChangeVersion";
}

internal sealed class RelationalQueryPageKeysetPlanner(SqlDialect dialect)
{
    private const string ContentVersionColumnName = ChangeVersionFilterConstants.ContentVersionColumnName;
    private const string MinChangeVersionParameterName =
        ChangeVersionFilterConstants.MinChangeVersionParameterName;
    private const string MaxChangeVersionParameterName =
        ChangeVersionFilterConstants.MaxChangeVersionParameterName;

    private readonly PageDocumentIdSqlCompiler _sqlCompiler = new(dialect);

    public PageKeysetSpec.Query Plan(
        DbTableModel rootTable,
        RelationalQueryPreprocessingResult preprocessingResult,
        CollectionPaging paging,
        Func<PreprocessedRelationalQueryElement, QueryComparisonOperator>? comparisonOperatorResolver = null,
        PageDocumentIdAuthorizationSpec? authorization = null,
        ChangeVersionRange? changeVersionRange = null,
        PageOrderingMode orderingMode = PageOrderingMode.DocumentId,
        IReadOnlyList<DbTableModel>? resourceTables = null
    )
    {
        ArgumentNullException.ThrowIfNull(paging);

        var plannedCandidates = PlanOrEmptyPage(
            rootTable,
            preprocessingResult,
            PageCandidateModePlanning.ForPaging(paging, orderingMode),
            authorization,
            out var emptyPageReason,
            comparisonOperatorResolver,
            changeVersionRange,
            resourceTables
        );

        return plannedCandidates is null
            ? throw new InvalidOperationException(
                emptyPageReason ?? "Relational query planning could not produce a page keyset for this query."
            )
            : ToPageKeysetSpec(plannedCandidates);
    }

    public bool TryPlan(
        DbTableModel rootTable,
        RelationalQueryPreprocessingResult preprocessingResult,
        CollectionPaging paging,
        out PageKeysetSpec.Query? plannedQuery,
        out string? emptyPageReason,
        Func<PreprocessedRelationalQueryElement, QueryComparisonOperator>? comparisonOperatorResolver = null,
        PageDocumentIdAuthorizationSpec? authorization = null,
        ChangeVersionRange? changeVersionRange = null,
        PageOrderingMode orderingMode = PageOrderingMode.DocumentId,
        IReadOnlyList<DbTableModel>? resourceTables = null
    )
    {
        ArgumentNullException.ThrowIfNull(paging);

        var plannedCandidates = PlanOrEmptyPage(
            rootTable,
            preprocessingResult,
            PageCandidateModePlanning.ForPaging(paging, orderingMode),
            authorization,
            out emptyPageReason,
            comparisonOperatorResolver,
            changeVersionRange,
            resourceTables
        );

        plannedQuery = plannedCandidates is null ? null : ToPageKeysetSpec(plannedCandidates);

        return plannedQuery is not null;
    }

    /// <summary>
    /// Plans the unpaged, unordered candidate relation over the same filters, change-version window,
    /// and authorization the paged modes use. Partition boundary planning consumes this relation and
    /// applies its own row numbering, so the result is not a hydration keyset.
    /// </summary>
    /// <param name="orderingMode">
    /// The anchor Core resolved for this request, forwarded so partition boundaries are cut on the same
    /// key a page of the same request seeks. Boundaries cut on a different key than the page a client
    /// replays them as would overlap and leave rows in no partition.
    /// </param>
    /// <param name="resourceTables">
    /// The resource's tables, required when a query element targets a collection table: its column
    /// metadata types, converts, and bounds the collection filter's values.
    /// </param>
    public bool TryPlanCandidates(
        DbTableModel rootTable,
        RelationalQueryPreprocessingResult preprocessingResult,
        out CandidateQueryPlan? plannedCandidates,
        out string? emptyPageReason,
        Func<PreprocessedRelationalQueryElement, QueryComparisonOperator>? comparisonOperatorResolver = null,
        PageDocumentIdAuthorizationSpec? authorization = null,
        ChangeVersionRange? changeVersionRange = null,
        PageOrderingMode orderingMode = PageOrderingMode.DocumentId,
        IReadOnlyList<DbTableModel>? resourceTables = null
    )
    {
        plannedCandidates = PlanOrEmptyPage(
            rootTable,
            preprocessingResult,
            PageCandidateModePlanning.ForUnpagedCandidates(orderingMode),
            authorization,
            out emptyPageReason,
            comparisonOperatorResolver,
            changeVersionRange,
            resourceTables
        );

        return plannedCandidates is not null;
    }

    private static PageKeysetSpec.Query ToPageKeysetSpec(CandidateQueryPlan plannedCandidates)
    {
        return new PageKeysetSpec.Query(
            plannedCandidates.Plan,
            plannedCandidates.ParameterValues,
            plannedCandidates.OrderingMode
        );
    }

    private CandidateQueryPlan? PlanOrEmptyPage(
        DbTableModel rootTable,
        RelationalQueryPreprocessingResult preprocessingResult,
        PlannedCandidateMode plannedMode,
        PageDocumentIdAuthorizationSpec? authorization,
        out string? emptyPageReason,
        Func<PreprocessedRelationalQueryElement, QueryComparisonOperator>? comparisonOperatorResolver = null,
        ChangeVersionRange? changeVersionRange = null,
        IReadOnlyList<DbTableModel>? resourceTables = null
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

        var (foldedMode, windowPredicateRange) =
            PageCandidateModePlanning.FoldChangeVersionWindowIntoCursorBounds(
                plannedMode,
                changeVersionRange
            );

        var rootColumnsByName = rootTable.Columns.ToDictionary(
            static column => column.ColumnName,
            static column => column
        );
        var authorizationClaimParameterization = authorization?.ClaimEducationOrganizationIdParameterization;
        var parameterNamesByIndex = DeriveParameterNames(
            preprocessingResult.QueryElementsInOrder,
            authorization,
            foldedMode.OwnedParameterNames
        );
        var predicates = new List<QueryValuePredicate>(preprocessingResult.QueryElementsInOrder.Count + 2);
        // Every predicate on one collection table goes into that table's single EXISTS, so all of them
        // must hold for the same collection element.
        Dictionary<DbTableName, CollectionFilterBuilder> collectionFiltersByTable = [];
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal);

        foreach (var (parameterName, parameterValue) in foldedMode.ParameterValues)
        {
            parameterValues[parameterName] = parameterValue;
        }

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

            if (
                queryElement.SupportedField.Target
                is RelationalQueryFieldTarget.CollectionColumn collectionColumn
            )
            {
                var plannedCollectionPredicate = PlanCollectionPredicate(
                    resourceTables,
                    queryElement,
                    collectionColumn,
                    parameterName,
                    comparisonOperator,
                    out var collectionEmptyPageReason
                );

                if (plannedCollectionPredicate is null)
                {
                    emptyPageReason =
                        collectionEmptyPageReason
                        ?? "Relational query planning determined this query has no matches.";
                    return null;
                }

                AddCollectionPredicate(
                    collectionFiltersByTable,
                    collectionColumn,
                    plannedCollectionPredicate.Predicate
                );
                parameterValues[parameterName] = plannedCollectionPredicate.ParameterValue;
                continue;
            }

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

            predicates.Add(plannedPredicate.Predicate);
            parameterValues[parameterName] = plannedPredicate.ParameterValue;
        }

        AppendChangeVersionPredicates(
            rootTable,
            rootColumnsByName,
            windowPredicateRange,
            foldedMode.OrderingMode,
            predicates,
            parameterValues
        );

        if (authorizationClaimParameterization is not null)
        {
            AuthorizationClaimEducationOrganizationIdParameterValues.AddTo(
                parameterValues,
                authorizationClaimParameterization
            );
        }
        NamespacePrefixParameterValueBinder.Bind(
            parameterValues,
            authorization?.NamespacePrefixParameterization
        );
        if (authorization?.OwnershipTokenParameterization is { } ownershipTokenParameterization)
        {
            // Binds nothing for an empty token list, matching the compiler, which then emits a
            // constant-false predicate that references no parameter.
            OwnershipTokenParameterValueBinder.Bind(parameterValues, ownershipTokenParameterization);
        }

        var querySpec = new PageDocumentIdQuerySpec(
            RootTable: rootTable.Table,
            Predicates: predicates,
            UnifiedAliasMappingsByColumn: BuildUnifiedAliasMappingsByColumn(rootTable),
            Mode: foldedMode.Mode,
            Authorization: authorization,
            CollectionExistsPredicates:
            [
                .. collectionFiltersByTable.Values.Select(static collectionFilter =>
                    collectionFilter.ToPredicate()
                ),
            ]
        );
        var sqlPlan = _sqlCompiler.Compile(querySpec);

        return new CandidateQueryPlan(sqlPlan, parameterValues, foldedMode.OrderingMode);
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
            new QueryValuePredicate(
                column,
                comparisonOperator,
                parameterName,
                scalarType.Kind is ScalarKind.String ? new QuerySqlStringContract(scalarType.MaxLength) : null
            ),
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
        if (queryElement.Value is not PreprocessedRelationalQueryValue.DescriptorId(var descriptorId))
        {
            throw new InvalidOperationException(
                $"Relational query planning expected a resolved compact descriptor id for query field "
                    + $"'{queryElement.SupportedField.QueryFieldName}', but received '{queryElement.Value.GetType().Name}'."
            );
        }

        var rootColumn = GetRootColumnOrThrow(rootTable, rootColumnsByName, column);
        if (
            rootColumn.Kind is not ColumnKind.DescriptorFk
            || rootColumn.ScalarType?.Kind is not ScalarKind.Int32
        )
        {
            throw new InvalidOperationException(
                $"Relational query planning requires descriptor column '{column.Value}' on table '{rootTable.Table}' "
                    + $"to expose Int32 descriptor FK metadata for query field '{queryElement.SupportedField.QueryFieldName}'."
            );
        }

        return new PlannedPredicate(
            new QueryValuePredicate(column, comparisonOperator, parameterName),
            descriptorId
        );
    }

    /// <summary>
    /// Plans one value predicate over an identification-code collection column, typed and converted from
    /// that collection column's metadata as a root predicate is from its root column. Returns null when a
    /// scalar value cannot be represented in the column, so the query has no matches.
    /// </summary>
    private static PlannedCollectionPredicate? PlanCollectionPredicate(
        IReadOnlyList<DbTableModel>? resourceTables,
        PreprocessedRelationalQueryElement queryElement,
        RelationalQueryFieldTarget.CollectionColumn target,
        string parameterName,
        QueryComparisonOperator comparisonOperator,
        out string? emptyPageReason
    )
    {
        emptyPageReason = null;
        var queryFieldName = queryElement.SupportedField.QueryFieldName;

        if (comparisonOperator is not QueryComparisonOperator.Equal)
        {
            throw new NotSupportedException(
                $"Relational query planning only supports exact-match equality predicates. Query field "
                    + $"'{queryFieldName}' was routed with operator '{comparisonOperator}'."
            );
        }

        ValidateSinglePathOrThrow(queryElement);

        var collectionTable =
            resourceTables?.FirstOrDefault(table => table.Table == target.CollectionTable)
            ?? throw new InvalidOperationException(
                $"Relational query planning requires the table model of collection table '{target.CollectionTable}' "
                    + $"for query field '{queryFieldName}', but it was not supplied."
            );
        var column =
            collectionTable.Columns.FirstOrDefault(candidate => candidate.ColumnName == target.Column)
            ?? throw new InvalidOperationException(
                $"Relational query planning could not find column '{target.Column.Value}' on collection table "
                    + $"'{target.CollectionTable}' for query field '{queryFieldName}'."
            );

        if (target.DescriptorResource is not null)
        {
            if (queryElement.Value is not PreprocessedRelationalQueryValue.DescriptorId(var descriptorId))
            {
                throw new InvalidOperationException(
                    $"Relational query planning expected a resolved compact descriptor id for query field "
                        + $"'{queryFieldName}', but received '{queryElement.Value.GetType().Name}'."
                );
            }

            if (column.Kind is not ColumnKind.DescriptorFk || column.ScalarType?.Kind is not ScalarKind.Int32)
            {
                throw new InvalidOperationException(
                    $"Relational query planning requires descriptor column '{target.Column.Value}' on collection table "
                        + $"'{target.CollectionTable}' to expose Int32 descriptor FK metadata for query field '{queryFieldName}'."
                );
            }

            return new PlannedCollectionPredicate(
                new QueryCollectionValuePredicate(target.Column, comparisonOperator, parameterName),
                descriptorId
            );
        }

        if (queryElement.Value is not PreprocessedRelationalQueryValue.Raw(var rawValue))
        {
            throw new InvalidOperationException(
                $"Relational query planning expected a raw scalar value for query field "
                    + $"'{queryFieldName}', but received '{queryElement.Value.GetType().Name}'."
            );
        }

        var scalarType =
            column.ScalarType
            ?? throw new InvalidOperationException(
                $"Relational query planning requires column '{target.Column.Value}' on collection table "
                    + $"'{target.CollectionTable}' to expose scalar type metadata for query field '{queryFieldName}'."
            );

        ValidateCompatibleQueryTypeOrThrow(queryFieldName, queryElement.SupportedField.Path.Type, scalarType);

        if (
            !TryConvertRawValue(
                queryFieldName,
                rawValue,
                scalarType,
                out var convertedValue,
                out emptyPageReason
            )
        )
        {
            return null;
        }

        return new PlannedCollectionPredicate(
            new QueryCollectionValuePredicate(
                target.Column,
                comparisonOperator,
                parameterName,
                scalarType.Kind is ScalarKind.String ? new QuerySqlStringContract(scalarType.MaxLength) : null
            ),
            convertedValue!
        );
    }

    private static void AddCollectionPredicate(
        Dictionary<DbTableName, CollectionFilterBuilder> collectionFiltersByTable,
        RelationalQueryFieldTarget.CollectionColumn target,
        QueryCollectionValuePredicate predicate
    )
    {
        if (!collectionFiltersByTable.TryGetValue(target.CollectionTable, out var collectionFilter))
        {
            collectionFilter = new CollectionFilterBuilder(target.CollectionTable, target.RootLocatorColumn);
            collectionFiltersByTable[target.CollectionTable] = collectionFilter;
        }

        if (collectionFilter.RootLocatorColumn != target.RootLocatorColumn)
        {
            throw new InvalidOperationException(
                $"Relational query planning found collection table '{target.CollectionTable}' located from its root by "
                    + $"both '{collectionFilter.RootLocatorColumn.Value}' and '{target.RootLocatorColumn.Value}'."
            );
        }

        collectionFilter.Predicates.Add(predicate);
    }

    /// <summary>
    /// Verifies the root-table <c>ContentVersion</c> mirror this plan depends on, then appends mirrored
    /// range predicates for the validated change-version window. Filtering targets only the mirror
    /// column maintained by the stamping triggers; there is no non-mirror fallback, so a missing or
    /// mis-typed mirror fails planning instead of emitting bad SQL.
    /// </summary>
    /// <param name="orderingMode">
    /// The anchor the plan is compiled against. Two independent things need the mirror column, and
    /// either alone is enough to require it: a change-version bound filters on it, and a
    /// <c>ContentVersion</c> anchor orders, bounds, and projects it. Checking only when a bound is
    /// present would leave an anchored plan naming a column no one had verified, because the anchor
    /// reaches the planner as its own request value rather than being derived from the window here.
    /// </param>
    private static void AppendChangeVersionPredicates(
        DbTableModel rootTable,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> rootColumnsByName,
        ChangeVersionRange? changeVersionRange,
        PageOrderingMode orderingMode,
        List<QueryValuePredicate> predicates,
        Dictionary<string, object?> parameterValues
    )
    {
        var isChangeVersionBounded =
            changeVersionRange is { MinChangeVersion: not null } or { MaxChangeVersion: not null };

        if (!isChangeVersionBounded && orderingMode is not PageOrderingMode.ContentVersion)
        {
            return;
        }

        var contentVersionColumn = new DbColumnName(ContentVersionColumnName);

        if (
            !rootColumnsByName.TryGetValue(contentVersionColumn, out var columnModel)
            || columnModel.Kind != ColumnKind.MirroredContentVersion
            || columnModel.ScalarType?.Kind != ScalarKind.Int64
        )
        {
            throw new InvalidOperationException(
                $"Relational query planning requires a mirrored Int64 '{ContentVersionColumnName}' column "
                    + $"(ColumnKind.{nameof(ColumnKind.MirroredContentVersion)}) on root table '{rootTable.Table}' "
                    + "to apply change-version filters or to anchor page selection on the change version. "
                    + "Neither has a non-mirror fallback."
            );
        }

        if (changeVersionRange?.MinChangeVersion is { } minChangeVersion)
        {
            predicates.Add(
                new QueryValuePredicate(
                    contentVersionColumn,
                    QueryComparisonOperator.GreaterThanOrEqual,
                    MinChangeVersionParameterName
                )
            );
            parameterValues[MinChangeVersionParameterName] = minChangeVersion;
        }

        if (changeVersionRange?.MaxChangeVersion is { } maxChangeVersion)
        {
            predicates.Add(
                new QueryValuePredicate(
                    contentVersionColumn,
                    QueryComparisonOperator.LessThanOrEqual,
                    MaxChangeVersionParameterName
                )
            );
            parameterValues[MaxChangeVersionParameterName] = maxChangeVersion;
        }
    }

    /// <summary>
    /// Allocates filter parameter names, reserving only the names the active candidate mode actually
    /// emits. Reserving another mode's names would suffix a filter parameter over a collision this
    /// query does not have, which would move the SQL of a mode that has no stake in the name.
    /// </summary>
    private static IReadOnlyList<string> DeriveParameterNames(
        IReadOnlyList<PreprocessedRelationalQueryElement> queryElementsInOrder,
        PageDocumentIdAuthorizationSpec? authorization,
        IReadOnlyList<string> modeOwnedParameterNames
    )
    {
        var seeds = queryElementsInOrder
            .Select(
                (element, index) =>
                    new QueryParameterNameSeed(
                        Index: index,
                        BaseName: QueryParameterNameAllocator.CreateBaseName(
                            element.SupportedField.QueryFieldName
                        ),
                        QueryFieldName: element.SupportedField.QueryFieldName,
                        Disambiguator: element.SupportedField.Path.Path.Canonical
                    )
            )
            .ToArray();

        return QueryParameterNameAllocator.Allocate(
            seeds,
            [
                .. modeOwnedParameterNames,
                MinChangeVersionParameterName,
                MaxChangeVersionParameterName,
                .. QueryParameterNameAllocator.CollectAuthorizationParameterNames(authorization),
            ]
        );
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

    private sealed record PlannedCollectionPredicate(
        QueryCollectionValuePredicate Predicate,
        object ParameterValue
    );

    private sealed record CollectionFilterBuilder(DbTableName CollectionTable, DbColumnName RootLocatorColumn)
    {
        public List<QueryCollectionValuePredicate> Predicates { get; } = [];

        public QueryCollectionExistsPredicate ToPredicate() =>
            new(CollectionTable, RootLocatorColumn, [.. Predicates]);
    }
}
