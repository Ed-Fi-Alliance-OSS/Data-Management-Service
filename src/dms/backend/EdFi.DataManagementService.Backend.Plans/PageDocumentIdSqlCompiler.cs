// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles root-table page queries that return <c>DocumentId</c> keysets for GET-by-query reads.
/// </summary>
/// <remarks>
/// Predicates over unified alias columns are rewritten to canonical storage columns and,
/// when required, include an explicit non-null presence gate.
/// </remarks>
public sealed class PageDocumentIdSqlCompiler(SqlDialect dialect)
{
    private const string DocumentIdColumnName = "DocumentId";
    private const string DocumentUuidColumnName = "DocumentUuid";
    private const string MissingPresenceColumnSortValue = "";
    private static readonly string _rootAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Root);
    private static readonly string _documentAlias = PlanNamingConventions.GetFixedAlias(
        PlanSqlAliasRole.Document
    );
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");

    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);
    private readonly IPlanSqlDialect _planSqlDialect = PlanSqlDialectFactory.Create(dialect);

    /// <summary>
    /// Compiles page keyset SQL and total-count SQL for the supplied query specification.
    /// </summary>
    /// <param name="spec">The root-table query specification.</param>
    /// <returns>The compiled SQL plan.</returns>
    public PageDocumentIdSqlPlan Compile(PageDocumentIdQuerySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Predicates);
        ArgumentNullException.ThrowIfNull(spec.UnifiedAliasMappingsByColumn);

        if (spec.Predicates.Any(predicate => predicate is null))
        {
            throw new ArgumentException("Predicates must not contain null entries.", nameof(spec.Predicates));
        }

        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.OffsetParameterName,
            nameof(spec.OffsetParameterName)
        );
        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.LimitParameterName,
            nameof(spec.LimitParameterName)
        );
        ValidatePagingParameterNamesAreDistinct(spec.OffsetParameterName, spec.LimitParameterName);

        var rewrittenPredicates = RewriteAndSortPredicates(
            spec.Predicates,
            spec.UnifiedAliasMappingsByColumn
        );
        var requiresDocumentUuidJoin = rewrittenPredicates.Any(static predicate =>
            predicate.Target is QueryPredicateTarget.DocumentUuid
        );
        ValidateFilterParameterNamesDoNotCollideWithPaging(
            rewrittenPredicates,
            spec.OffsetParameterName,
            spec.LimitParameterName
        );
        ValidateFilterParameterNamesAreUnique(rewrittenPredicates);

        var pageSql = BuildPageDocumentIdSql(spec, rewrittenPredicates, requiresDocumentUuidJoin);
        var totalCountSql = spec.IncludeTotalCountSql
            ? BuildTotalCountSql(spec.RootTable, rewrittenPredicates, requiresDocumentUuidJoin)
            : null;
        var filterParametersInOrder = BuildFilterParametersInOrder(rewrittenPredicates);
        var pageParametersInOrder = BuildPageParametersInOrder(
            filterParametersInOrder,
            spec.OffsetParameterName,
            spec.LimitParameterName
        );
        var totalCountParametersInOrder = spec.IncludeTotalCountSql
            ? BuildTotalCountParametersInOrder(filterParametersInOrder)
            : null;

        return new PageDocumentIdSqlPlan(
            pageSql,
            totalCountSql,
            pageParametersInOrder,
            totalCountParametersInOrder
        );
    }

    /// <summary>
    /// Rewrites predicates into canonical storage-column form and sorts by deterministic key.
    /// </summary>
    private static IReadOnlyList<RewrittenPredicate> RewriteAndSortPredicates(
        IReadOnlyList<QueryValuePredicate> predicates,
        IReadOnlyDictionary<DbColumnName, ColumnStorage.UnifiedAlias> aliasMappingsByColumn
    )
    {
        var rewrittenPredicates = predicates
            .Select(predicate => RewritePredicate(predicate, aliasMappingsByColumn))
            .OrderBy(predicate => GetTargetSortKey(predicate.Target), StringComparer.Ordinal)
            .ThenBy(
                predicate => predicate.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                StringComparer.Ordinal
            )
            .ThenBy(predicate => predicate.CanonicalColumn.Value, StringComparer.Ordinal)
            .ThenBy(predicate => GetOperatorSortKey(predicate.Operator), StringComparer.Ordinal)
            .ThenBy(predicate => predicate.ParameterName, StringComparer.Ordinal)
            .ToArray();

        for (var startIndex = 0; startIndex < rewrittenPredicates.Length; startIndex++)
        {
            var endExclusiveIndex = startIndex + 1;

            while (
                endExclusiveIndex < rewrittenPredicates.Length
                && HasDuplicateSemanticKey(
                    rewrittenPredicates[startIndex],
                    rewrittenPredicates[endExclusiveIndex]
                )
            )
            {
                endExclusiveIndex++;
            }

            if (endExclusiveIndex - startIndex > 1)
            {
                throw CreateDuplicateSemanticPredicateException(
                    rewrittenPredicates,
                    startIndex,
                    endExclusiveIndex
                );
            }

            startIndex = endExclusiveIndex - 1;
        }

        return rewrittenPredicates;
    }

    /// <summary>
    /// Rewrites a single predicate to its canonical storage-column representation.
    /// </summary>
    private static RewrittenPredicate RewritePredicate(
        QueryValuePredicate predicate,
        IReadOnlyDictionary<DbColumnName, ColumnStorage.UnifiedAlias> aliasMappingsByColumn
    )
    {
        PlanSqlWriterExtensions.ValidateBareParameterName(
            predicate.ParameterName,
            nameof(predicate.ParameterName)
        );

        if (predicate.Target is QueryPredicateTarget.DocumentUuid)
        {
            return new RewrittenPredicate(
                predicate.Target,
                new DbColumnName(DocumentUuidColumnName),
                new DbColumnName(DocumentUuidColumnName),
                null,
                predicate.Operator,
                predicate.ParameterName,
                predicate.ScalarKind
            );
        }

        if (predicate.Target is not QueryPredicateTarget.RootColumn(var originalColumn))
        {
            throw new InvalidOperationException(
                $"Unsupported query predicate target '{predicate.Target.GetType().Name}'."
            );
        }

        if (!aliasMappingsByColumn.TryGetValue(originalColumn, out var mapping))
        {
            return new RewrittenPredicate(
                predicate.Target,
                originalColumn,
                originalColumn,
                null,
                predicate.Operator,
                predicate.ParameterName,
                predicate.ScalarKind
            );
        }

        return new RewrittenPredicate(
            predicate.Target,
            originalColumn,
            mapping.CanonicalColumn,
            mapping.PresenceColumn,
            predicate.Operator,
            predicate.ParameterName,
            predicate.ScalarKind
        );
    }

    /// <summary>
    /// Ensures filter-parameter names do not collide with paging parameter names.
    /// </summary>
    private static void ValidateFilterParameterNamesDoNotCollideWithPaging(
        IReadOnlyList<RewrittenPredicate> predicates,
        string offsetParameterName,
        string limitParameterName
    )
    {
        foreach (var predicate in predicates)
        {
            if (
                string.Equals(
                    predicate.ParameterName,
                    offsetParameterName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw CreateFilterPagingCollisionException(
                    predicate.ParameterName,
                    offsetParameterName,
                    nameof(PageDocumentIdQuerySpec.OffsetParameterName)
                );
            }

            if (
                string.Equals(predicate.ParameterName, limitParameterName, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw CreateFilterPagingCollisionException(
                    predicate.ParameterName,
                    limitParameterName,
                    nameof(PageDocumentIdQuerySpec.LimitParameterName)
                );
            }
        }
    }

    /// <summary>
    /// Ensures paging parameter names are distinct (case-insensitive).
    /// </summary>
    private static void ValidatePagingParameterNamesAreDistinct(
        string offsetParameterName,
        string limitParameterName
    )
    {
        if (string.Equals(offsetParameterName, limitParameterName, StringComparison.OrdinalIgnoreCase))
        {
            throw CreatePagingParameterCollisionException(offsetParameterName, limitParameterName);
        }
    }

    /// <summary>
    /// Ensures filter-parameter names are unique (case-insensitive).
    /// </summary>
    private static void ValidateFilterParameterNamesAreUnique(IReadOnlyList<RewrittenPredicate> predicates)
    {
        var duplicateGroups = predicates
            .Select(predicate => predicate.ParameterName)
            .GroupBy(static parameterName => parameterName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group =>
                group.OrderBy(static parameterName => parameterName, StringComparer.Ordinal).ToArray()
            )
            .OrderBy(static group => group[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group[0], StringComparer.Ordinal)
            .ToArray();

        if (duplicateGroups.Length == 0)
        {
            return;
        }

        throw CreateDuplicateFilterParameterNamesException(duplicateGroups);
    }

    /// <summary>
    /// Builds deterministic filter-parameter metadata in canonical plan order.
    /// Executors bind parameters by name, so this ordering does not need to match placeholder appearance per dialect.
    /// </summary>
    private static IReadOnlyList<QuerySqlParameter> BuildFilterParametersInOrder(
        IReadOnlyList<RewrittenPredicate> predicates
    )
    {
        var filterParametersInOrder = new QuerySqlParameter[predicates.Count];

        for (var index = 0; index < predicates.Count; index++)
        {
            filterParametersInOrder[index] = new QuerySqlParameter(
                QuerySqlParameterRole.Filter,
                predicates[index].ParameterName
            );
        }

        return filterParametersInOrder;
    }

    /// <summary>
    /// Builds deterministic page-query parameter metadata in canonical plan order.
    /// </summary>
    private static IReadOnlyList<QuerySqlParameter> BuildPageParametersInOrder(
        IReadOnlyList<QuerySqlParameter> filterParametersInOrder,
        string offsetParameterName,
        string limitParameterName
    )
    {
        var pageParametersInOrder = new List<QuerySqlParameter>(filterParametersInOrder.Count + 2);
        pageParametersInOrder.AddRange(filterParametersInOrder);
        pageParametersInOrder.Add(new QuerySqlParameter(QuerySqlParameterRole.Offset, offsetParameterName));
        pageParametersInOrder.Add(new QuerySqlParameter(QuerySqlParameterRole.Limit, limitParameterName));

        return pageParametersInOrder;
    }

    /// <summary>
    /// Builds deterministic total-count query parameter metadata in canonical plan order (filters only).
    /// </summary>
    private static IReadOnlyList<QuerySqlParameter> BuildTotalCountParametersInOrder(
        IReadOnlyList<QuerySqlParameter> filterParametersInOrder
    )
    {
        return [.. filterParametersInOrder];
    }

    /// <summary>
    /// Emits canonical SQL for page-<c>DocumentId</c> selection.
    /// </summary>
    private string BuildPageDocumentIdSql(
        PageDocumentIdQuerySpec spec,
        IReadOnlyList<RewrittenPredicate> predicates,
        bool requiresDocumentUuidJoin
    )
    {
        var writer = new SqlWriter(_sqlDialect);

        writer
            .Append($"SELECT {_rootAlias}.")
            .AppendQuoted(DocumentIdColumnName)
            .AppendLine()
            .Append("FROM ")
            .AppendRelation(new SqlRelationRef.PhysicalTable(spec.RootTable))
            .AppendLine($" {_rootAlias}");

        AppendDocumentJoin(writer, requiresDocumentUuidJoin);
        AppendWhereClause(writer, predicates);

        writer.Append($"ORDER BY {_rootAlias}.").AppendQuoted(DocumentIdColumnName).AppendLine(" ASC");

        _planSqlDialect.AppendPagingClause(writer, spec.OffsetParameterName, spec.LimitParameterName);
        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Emits canonical SQL for total-row count selection.
    /// </summary>
    private string BuildTotalCountSql(
        DbTableName rootTable,
        IReadOnlyList<RewrittenPredicate> predicates,
        bool requiresDocumentUuidJoin
    )
    {
        var writer = new SqlWriter(_sqlDialect);

        writer
            .AppendLine("SELECT COUNT(1)")
            .Append("FROM ")
            .AppendRelation(new SqlRelationRef.PhysicalTable(rootTable))
            .AppendLine($" {_rootAlias}");

        AppendDocumentJoin(writer, requiresDocumentUuidJoin);
        AppendWhereClause(writer, predicates);
        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Emits the optional <c>dms.Document</c> join required for <c>?id=</c> filtering.
    /// </summary>
    private void AppendDocumentJoin(SqlWriter writer, bool requiresDocumentUuidJoin)
    {
        if (!requiresDocumentUuidJoin)
        {
            return;
        }

        writer
            .Append("INNER JOIN ")
            .AppendRelation(new SqlRelationRef.PhysicalTable(_documentTable))
            .Append($" {_documentAlias} ON {_documentAlias}.")
            .AppendQuoted(DocumentIdColumnName)
            .Append($" = {_rootAlias}.")
            .AppendQuoted(DocumentIdColumnName)
            .AppendLine();
    }

    /// <summary>
    /// Emits a deterministic multi-line <c>WHERE</c> clause.
    /// </summary>
    private void AppendWhereClause(SqlWriter writer, IReadOnlyList<RewrittenPredicate> predicates)
    {
        writer.AppendWhereClause(
            predicates.Count,
            (predicateWriter, index) => AppendPredicateSql(predicateWriter, predicates[index])
        );
    }

    /// <summary>
    /// Emits SQL for a single rewritten predicate.
    /// </summary>
    private void AppendPredicateSql(SqlWriter writer, RewrittenPredicate predicate)
    {
        if (predicate.PresenceColumn is not null)
        {
            AppendIsNotNullSql(writer, _rootAlias, predicate.PresenceColumn.Value);
            writer.Append(" AND ");
        }

        AppendComparisonSql(
            writer,
            GetTargetAlias(predicate.Target),
            predicate.CanonicalColumn,
            predicate.Operator,
            predicate.ParameterName,
            predicate.ScalarKind
        );
    }

    /// <summary>
    /// Emits a simple binary comparison predicate against a table column.
    /// </summary>
    private void AppendComparisonSql(
        SqlWriter writer,
        string tableAlias,
        DbColumnName column,
        QueryComparisonOperator @operator,
        string parameterName,
        ScalarKind? scalarKind
    )
    {
        _planSqlDialect.AppendComparisonSql(
            writer,
            tableAlias,
            column,
            ToSqlOperator(@operator),
            parameterName,
            scalarKind
        );
    }

    /// <summary>
    /// Emits an <c>IS NOT NULL</c> predicate against a table column.
    /// </summary>
    private static void AppendIsNotNullSql(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(column.Value).Append(" IS NOT NULL");
    }

    /// <summary>
    /// Converts a query comparison operator to its SQL token.
    /// </summary>
    private static string ToSqlOperator(QueryComparisonOperator @operator)
    {
        // Compiler-level support only. DMS-993 runtime query planning routes equality
        // predicates only; non-equality operators are retained here for future query
        // syntax stories and must not be treated as currently supported API behavior.
        return @operator switch
        {
            QueryComparisonOperator.Equal => "=",
            QueryComparisonOperator.NotEqual => "<>",
            QueryComparisonOperator.LessThan => "<",
            QueryComparisonOperator.LessThanOrEqual => "<=",
            QueryComparisonOperator.GreaterThan => ">",
            QueryComparisonOperator.GreaterThanOrEqual => ">=",
            QueryComparisonOperator.Like => "LIKE",

            // Defer implementation until the real compilation stories
            QueryComparisonOperator.In => throw new NotSupportedException(
                $"Operator '{nameof(QueryComparisonOperator.In)}' is not yet supported by {nameof(ToSqlOperator)}."
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(@operator),
                @operator,
                "Unsupported query operator for now."
            ),
        };
    }

    /// <summary>
    /// Returns a deterministic textual sort key for query operators without relying on <c>Enum.ToString()</c>.
    /// </summary>
    private static string GetOperatorSortKey(QueryComparisonOperator @operator)
    {
        return @operator switch
        {
            QueryComparisonOperator.Equal => nameof(QueryComparisonOperator.Equal),
            QueryComparisonOperator.NotEqual => nameof(QueryComparisonOperator.NotEqual),
            QueryComparisonOperator.LessThan => nameof(QueryComparisonOperator.LessThan),
            QueryComparisonOperator.LessThanOrEqual => nameof(QueryComparisonOperator.LessThanOrEqual),
            QueryComparisonOperator.GreaterThan => nameof(QueryComparisonOperator.GreaterThan),
            QueryComparisonOperator.GreaterThanOrEqual => nameof(QueryComparisonOperator.GreaterThanOrEqual),
            QueryComparisonOperator.Like => nameof(QueryComparisonOperator.Like),
            QueryComparisonOperator.In => nameof(QueryComparisonOperator.In),
            _ => throw new ArgumentOutOfRangeException(
                nameof(@operator),
                @operator,
                "Unsupported query operator sort key."
            ),
        };
    }

    /// <summary>
    /// Formats the duplicate-detection semantic key.
    /// </summary>
    private static string FormatSemanticKey(RewrittenPredicate predicate)
    {
        var presenceColumn = predicate.PresenceColumn?.Value ?? "<none>";
        var operatorToken = GetOperatorSortKey(predicate.Operator);

        return string.Join(
            ", ",
            [
                $"presenceColumn='{presenceColumn}'",
                $"canonicalColumn='{predicate.CanonicalColumn.Value}'",
                $"operator='{operatorToken}'",
            ]
        );
    }

    /// <summary>
    /// Returns <see langword="true"/> when both rewritten predicates share the same semantic key after unified-alias
    /// rewrite, ignoring parameter-name differences.
    /// </summary>
    /// <param name="left">The first rewritten predicate.</param>
    /// <param name="right">The second rewritten predicate.</param>
    /// <returns><see langword="true"/> when the semantic key collides; otherwise <see langword="false"/>.</returns>
    private static bool HasDuplicateSemanticKey(RewrittenPredicate left, RewrittenPredicate right)
    {
        return string.Equals(
                GetTargetSortKey(left.Target),
                GetTargetSortKey(right.Target),
                StringComparison.Ordinal
            )
            && string.Equals(
                left.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                right.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                StringComparison.Ordinal
            )
            && string.Equals(
                left.CanonicalColumn.Value,
                right.CanonicalColumn.Value,
                StringComparison.Ordinal
            )
            && left.Operator == right.Operator;
    }

    /// <summary>
    /// Creates a deterministic exception for a duplicate semantic predicate set, listing all colliding original
    /// columns and parameter names in stable ordinal order.
    /// </summary>
    /// <param name="rewrittenPredicates">The full rewritten and sorted predicate list.</param>
    /// <param name="startIndex">Start index of the collision group.</param>
    /// <param name="endExclusiveIndex">End (exclusive) index of the collision group.</param>
    /// <returns>An exception describing the duplicate semantic predicate collision.</returns>
    private static InvalidOperationException CreateDuplicateSemanticPredicateException(
        IReadOnlyList<RewrittenPredicate> rewrittenPredicates,
        int startIndex,
        int endExclusiveIndex
    )
    {
        var collidingOriginalColumns = new List<string>(endExclusiveIndex - startIndex);
        var collidingParameterNames = new List<string>(endExclusiveIndex - startIndex);

        for (var index = startIndex; index < endExclusiveIndex; index++)
        {
            collidingOriginalColumns.Add(rewrittenPredicates[index].OriginalColumn.Value);
            collidingParameterNames.Add(rewrittenPredicates[index].ParameterName);
        }

        collidingOriginalColumns.Sort(StringComparer.Ordinal);
        collidingParameterNames.Sort(StringComparer.Ordinal);

        return new InvalidOperationException(
            $"Duplicate predicate after unified alias rewrite for semantic key ({FormatSemanticKey(rewrittenPredicates[startIndex])}). "
                + $"Colliding original columns: [{FormatCollisionValues(collidingOriginalColumns)}]. "
                + $"Colliding parameter names: [{FormatCollisionValues(collidingParameterNames)}]."
        );
    }

    /// <summary>
    /// Formats values as a deterministic, comma-delimited list of single-quoted tokens.
    /// </summary>
    /// <param name="values">Values to format.</param>
    /// <returns>A comma-delimited list of quoted values.</returns>
    private static string FormatCollisionValues(IReadOnlyList<string> values)
    {
        return string.Join(", ", values.Select(static value => $"'{value}'"));
    }

    /// <summary>
    /// Creates a deterministic exception describing a filter/paging parameter-name collision.
    /// </summary>
    private static ArgumentException CreateFilterPagingCollisionException(
        string filterParameterName,
        string pagingParameterName,
        string pagingParameterPropertyName
    )
    {
        return new ArgumentException(
            $"Filter parameter name '{filterParameterName}' collides with paging parameter name '{pagingParameterName}' (case-insensitive). "
                + $"Rename the filter parameter or change {pagingParameterPropertyName}.",
            nameof(PageDocumentIdQuerySpec.Predicates)
        );
    }

    /// <summary>
    /// Creates a deterministic exception describing an offset/limit paging parameter-name collision.
    /// </summary>
    private static ArgumentException CreatePagingParameterCollisionException(
        string offsetParameterName,
        string limitParameterName
    )
    {
        return new ArgumentException(
            $"Paging parameter names must be distinct (case-insensitive). "
                + $"{nameof(PageDocumentIdQuerySpec.OffsetParameterName)}='{offsetParameterName}', "
                + $"{nameof(PageDocumentIdQuerySpec.LimitParameterName)}='{limitParameterName}'. "
                + $"Rename either {nameof(PageDocumentIdQuerySpec.OffsetParameterName)} or {nameof(PageDocumentIdQuerySpec.LimitParameterName)}.",
            nameof(PageDocumentIdQuerySpec.OffsetParameterName)
        );
    }

    /// <summary>
    /// Creates a deterministic exception describing duplicate filter parameter names.
    /// </summary>
    private static ArgumentException CreateDuplicateFilterParameterNamesException(
        IReadOnlyList<string[]> duplicateGroups
    )
    {
        var formattedGroups = duplicateGroups
            .Select(static group => $"[{FormatCollisionValues(group)}]")
            .ToArray();

        return new ArgumentException(
            "Duplicate filter parameter names are not allowed (case-insensitive). "
                + $"Colliding names: [{string.Join(", ", formattedGroups)}]. "
                + "Rename filter parameters so each name is unique.",
            nameof(PageDocumentIdQuerySpec.Predicates)
        );
    }

    /// <summary>
    /// Returns a stable sort key for the SQL-side predicate target.
    /// </summary>
    private static string GetTargetSortKey(QueryPredicateTarget target)
    {
        return target switch
        {
            QueryPredicateTarget.RootColumn => nameof(QueryPredicateTarget.RootColumn),
            QueryPredicateTarget.DocumentUuid => nameof(QueryPredicateTarget.DocumentUuid),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Unsupported query predicate target sort key."
            ),
        };
    }

    /// <summary>
    /// Returns the fixed SQL alias for a predicate target.
    /// </summary>
    private static string GetTargetAlias(QueryPredicateTarget target)
    {
        return target switch
        {
            QueryPredicateTarget.RootColumn => _rootAlias,
            QueryPredicateTarget.DocumentUuid => _documentAlias,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Unsupported query predicate target alias."
            ),
        };
    }

    /// <summary>
    /// Represents a predicate rewritten into canonical storage-column form, with an optional presence gate
    /// for unified-alias mappings.
    /// </summary>
    /// <param name="Target">The SQL-side predicate target.</param>
    /// <param name="OriginalColumn">The original API-bound predicate column.</param>
    /// <param name="CanonicalColumn">The canonical storage column used for SQL emission.</param>
    /// <param name="PresenceColumn">An optional presence gate column that must be <c>IS NOT NULL</c>.</param>
    /// <param name="Operator">The comparison operator.</param>
    /// <param name="ParameterName">The bare SQL parameter name that supplies the value.</param>
    /// <param name="ScalarKind">Optional scalar-kind metadata for provider-specific comparison behavior.</param>
    private readonly record struct RewrittenPredicate(
        QueryPredicateTarget Target,
        DbColumnName OriginalColumn,
        DbColumnName CanonicalColumn,
        DbColumnName? PresenceColumn,
        QueryComparisonOperator Operator,
        string ParameterName,
        ScalarKind? ScalarKind
    );
}
