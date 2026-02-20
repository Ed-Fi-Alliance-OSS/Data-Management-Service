// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles root-table page queries that return <c>DocumentId</c> keysets for GET-by-query reads.
/// </summary>
/// <remarks>
/// Predicates over unified alias columns are rewritten to canonical storage columns and,
/// when required, include an explicit non-null presence gate.
/// </remarks>
public sealed partial class PageDocumentIdSqlCompiler(SqlDialect dialect)
{
    private const string DocumentIdColumnName = "DocumentId";
    private const string MissingPresenceColumnSortValue = "";

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    /// <summary>
    /// Compiles page keyset SQL and total-count SQL for the supplied query specification.
    /// </summary>
    /// <param name="spec">The root-table query specification.</param>
    /// <returns>The compiled SQL plan.</returns>
    public PageDocumentIdSqlPlan Compile(PageDocumentIdQuerySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.OffsetParameterName,
            nameof(spec.OffsetParameterName)
        );
        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.LimitParameterName,
            nameof(spec.LimitParameterName)
        );

        var aliasMappingsByColumn = BuildAliasMappingLookup(spec.UnifiedAliasMappings);
        var rewrittenPredicates = RewriteAndSortPredicates(spec.Predicates, aliasMappingsByColumn);
        var pageSql = BuildPageDocumentIdSql(spec, rewrittenPredicates);
        var totalCountSql = spec.IncludeTotalCountSql
            ? BuildTotalCountSql(spec.RootTable, rewrittenPredicates)
            : null;

        return new PageDocumentIdSqlPlan(pageSql, totalCountSql);
    }

    /// <summary>
    /// Builds a lookup of unified-alias mappings keyed by the alias/binding column.
    /// </summary>
    private static IReadOnlyDictionary<DbColumnName, UnifiedAliasColumnMapping> BuildAliasMappingLookup(
        IReadOnlyList<UnifiedAliasColumnMapping> mappings
    )
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var lookup = new Dictionary<DbColumnName, UnifiedAliasColumnMapping>();

        foreach (var mapping in mappings)
        {
            if (!lookup.TryAdd(mapping.AliasColumn, mapping))
            {
                throw new InvalidOperationException(
                    $"Duplicate unified alias mapping for column '{mapping.AliasColumn.Value}'."
                );
            }
        }

        return lookup;
    }

    /// <summary>
    /// Rewrites predicates into canonical storage-column form and sorts by deterministic key.
    /// </summary>
    private static IReadOnlyList<RewrittenPredicate> RewriteAndSortPredicates(
        IReadOnlyList<QueryValuePredicate> predicates,
        IReadOnlyDictionary<DbColumnName, UnifiedAliasColumnMapping> aliasMappingsByColumn
    )
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(aliasMappingsByColumn);

        var rewrittenPredicates = predicates
            .Select(predicate => RewritePredicate(predicate, aliasMappingsByColumn))
            .OrderBy(
                predicate => predicate.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                StringComparer.Ordinal
            )
            .ThenBy(predicate => predicate.CanonicalColumn.Value, StringComparer.Ordinal)
            .ThenBy(predicate => predicate.Operator.ToString(), StringComparer.Ordinal)
            .ThenBy(predicate => predicate.ParameterName, StringComparer.Ordinal)
            .ToArray();

        for (var index = 1; index < rewrittenPredicates.Length; index++)
        {
            if (!HasDuplicateSortKey(rewrittenPredicates[index - 1], rewrittenPredicates[index]))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Duplicate predicate after unified alias rewrite for sort key ({FormatSortKey(rewrittenPredicates[index])})."
            );
        }

        return rewrittenPredicates;
    }

    /// <summary>
    /// Rewrites a single predicate to its canonical storage-column representation.
    /// </summary>
    private static RewrittenPredicate RewritePredicate(
        QueryValuePredicate predicate,
        IReadOnlyDictionary<DbColumnName, UnifiedAliasColumnMapping> aliasMappingsByColumn
    )
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(aliasMappingsByColumn);

        PlanSqlWriterExtensions.ValidateBareParameterName(
            predicate.ParameterName,
            nameof(predicate.ParameterName)
        );

        if (!aliasMappingsByColumn.TryGetValue(predicate.Column, out var mapping))
        {
            return new RewrittenPredicate(
                predicate.Column,
                null,
                predicate.Operator,
                predicate.ParameterName
            );
        }

        return new RewrittenPredicate(
            mapping.CanonicalColumn,
            mapping.PresenceColumn,
            predicate.Operator,
            predicate.ParameterName
        );
    }

    /// <summary>
    /// Emits canonical SQL for page-<c>DocumentId</c> selection.
    /// </summary>
    private string BuildPageDocumentIdSql(
        PageDocumentIdQuerySpec spec,
        IReadOnlyList<RewrittenPredicate> predicates
    )
    {
        var writer = new SqlWriter(_sqlDialect);

        writer
            .Append("SELECT r.")
            .AppendQuoted(DocumentIdColumnName)
            .AppendLine()
            .Append("FROM ")
            .AppendTable(spec.RootTable)
            .AppendLine(" r");

        AppendWhereClause(writer, predicates);

        writer.Append("ORDER BY r.").AppendQuoted(DocumentIdColumnName).AppendLine(" ASC");

        AppendPagingClause(writer, spec.OffsetParameterName, spec.LimitParameterName);
        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Emits canonical SQL for total-row count selection.
    /// </summary>
    private string BuildTotalCountSql(DbTableName rootTable, IReadOnlyList<RewrittenPredicate> predicates)
    {
        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine("SELECT COUNT(1)").Append("FROM ").AppendTable(rootTable).AppendLine(" r");

        AppendWhereClause(writer, predicates);
        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Emits a deterministic multi-line <c>WHERE</c> clause.
    /// </summary>
    private void AppendWhereClause(SqlWriter writer, IReadOnlyList<RewrittenPredicate> predicates)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(predicates);

        if (predicates.Count == 0)
        {
            return;
        }

        var predicateSql = predicates.Select(BuildPredicateSql).ToArray();
        writer.AppendWhereClause(predicateSql);
    }

    /// <summary>
    /// Builds SQL for a single rewritten predicate.
    /// </summary>
    private string BuildPredicateSql(RewrittenPredicate predicate)
    {
        var canonicalComparison = BuildComparisonSql(
            predicate.CanonicalColumn,
            predicate.Operator,
            predicate.ParameterName
        );

        if (predicate.PresenceColumn is null)
        {
            return canonicalComparison;
        }

        return $"{BuildIsNotNullSql(predicate.PresenceColumn.Value)} AND {canonicalComparison}";
    }

    /// <summary>
    /// Builds a simple binary comparison predicate against a table column.
    /// </summary>
    private string BuildComparisonSql(
        DbColumnName column,
        QueryComparisonOperator @operator,
        string parameterName
    )
    {
        if (@operator is QueryComparisonOperator.In)
        {
            throw new NotSupportedException(
                $"Operator '{nameof(QueryComparisonOperator.In)}' is not supported by {nameof(PageDocumentIdSqlCompiler)}."
            );
        }

        var writer = new SqlWriter(_sqlDialect, initialCapacity: 128);

        writer
            .Append("r.")
            .AppendQuoted(column.Value)
            .Append(" ")
            .Append(ToSqlOperator(@operator))
            .Append(" ")
            .AppendParameter(parameterName);

        return writer.ToString();
    }

    /// <summary>
    /// Builds an <c>IS NOT NULL</c> predicate against a table column.
    /// </summary>
    private string BuildIsNotNullSql(DbColumnName column)
    {
        var writer = new SqlWriter(_sqlDialect, initialCapacity: 64);
        writer.Append("r.").AppendQuoted(column.Value).Append(" IS NOT NULL");

        return writer.ToString();
    }

    /// <summary>
    /// Builds the paging clause for the configured SQL dialect.
    /// </summary>
    private void AppendPagingClause(SqlWriter writer, string offsetParameterName, string limitParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (_dialect)
        {
            case SqlDialect.Pgsql:
                writer
                    .Append("LIMIT ")
                    .AppendParameter(limitParameterName)
                    .Append(" OFFSET ")
                    .AppendParameter(offsetParameterName)
                    .AppendLine();
                return;

            case SqlDialect.Mssql:
                writer
                    .Append("OFFSET ")
                    .AppendParameter(offsetParameterName)
                    .Append(" ROWS FETCH NEXT ")
                    .AppendParameter(limitParameterName)
                    .AppendLine(" ROWS ONLY");
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(_dialect), _dialect, "Unsupported SQL dialect.");
        }
    }

    /// <summary>
    /// Converts a query comparison operator to its SQL token.
    /// </summary>
    private static string ToSqlOperator(QueryComparisonOperator @operator)
    {
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
            QueryComparisonOperator.In => "IN",
            _ => throw new ArgumentOutOfRangeException(
                nameof(@operator),
                @operator,
                "Unsupported query operator."
            ),
        };
    }

    /// <summary>
    /// Formats the duplicate-detection sort key.
    /// </summary>
    private static string FormatSortKey(RewrittenPredicate predicate)
    {
        var presenceColumn = predicate.PresenceColumn?.Value ?? "<none>";

        return string.Join(
            ", ",
            [
                $"presenceColumn='{presenceColumn}'",
                $"canonicalColumn='{predicate.CanonicalColumn.Value}'",
                $"operator='{predicate.Operator}'",
                $"parameterName='{predicate.ParameterName}'",
            ]
        );
    }

    private static bool HasDuplicateSortKey(RewrittenPredicate left, RewrittenPredicate right)
    {
        return string.Equals(
                left.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                right.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                StringComparison.Ordinal
            )
            && string.Equals(
                left.CanonicalColumn.Value,
                right.CanonicalColumn.Value,
                StringComparison.Ordinal
            )
            && left.Operator == right.Operator
            && string.Equals(left.ParameterName, right.ParameterName, StringComparison.Ordinal);
    }

    private readonly record struct RewrittenPredicate(
        DbColumnName CanonicalColumn,
        DbColumnName? PresenceColumn,
        QueryComparisonOperator Operator,
        string ParameterName
    );
}
