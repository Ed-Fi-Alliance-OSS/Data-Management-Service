// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;

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
    private readonly SqlDialect _dialect = dialect;

    /// <summary>
    /// Compiles page keyset SQL and total-count SQL for the supplied query specification.
    /// </summary>
    /// <param name="spec">The root-table query specification.</param>
    /// <returns>The compiled SQL plan.</returns>
    public PageDocumentIdSqlPlan Compile(PageDocumentIdQuerySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        ValidateParameterName(spec.OffsetParameterName, nameof(spec.OffsetParameterName));
        ValidateParameterName(spec.LimitParameterName, nameof(spec.LimitParameterName));

        var aliasMappingsByColumn = BuildAliasMappingLookup(spec.UnifiedAliasMappings);
        var whereClause = BuildWhereClause(spec.Predicates, aliasMappingsByColumn);
        var quotedRootTable = SqlIdentifierQuoter.QuoteTableName(_dialect, spec.RootTable);
        var quotedDocumentId = QuoteColumn(DocumentIdColumnName);

        var pageSql = new StringBuilder()
            .Append("SELECT r.")
            .Append(quotedDocumentId)
            .Append('\n')
            .Append("FROM ")
            .Append(quotedRootTable)
            .Append(" r");

        if (whereClause is not null)
        {
            pageSql.Append('\n').Append("WHERE ").Append(whereClause);
        }

        pageSql
            .Append('\n')
            .Append("ORDER BY r.")
            .Append(quotedDocumentId)
            .Append(" ASC")
            .Append('\n')
            .Append(BuildPagingClause(spec.OffsetParameterName, spec.LimitParameterName))
            .Append(';');

        var totalCountSql = new StringBuilder()
            .Append("SELECT COUNT(1)")
            .Append('\n')
            .Append("FROM ")
            .Append(quotedRootTable)
            .Append(" r");

        if (whereClause is not null)
        {
            totalCountSql.Append('\n').Append("WHERE ").Append(whereClause);
        }

        totalCountSql.Append(';');

        return new PageDocumentIdSqlPlan(pageSql.ToString(), totalCountSql.ToString());
    }

    private const string DocumentIdColumnName = "DocumentId";

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

    private string? BuildWhereClause(
        IReadOnlyList<QueryValuePredicate> predicates,
        IReadOnlyDictionary<DbColumnName, UnifiedAliasColumnMapping> aliasMappingsByColumn
    )
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(aliasMappingsByColumn);

        if (predicates.Count == 0)
        {
            return null;
        }

        var predicateSql = predicates
            .Select(predicate => $"({BuildPredicateSql(predicate, aliasMappingsByColumn)})")
            .ToArray();

        return string.Join(" AND ", predicateSql);
    }

    private string BuildPredicateSql(
        QueryValuePredicate predicate,
        IReadOnlyDictionary<DbColumnName, UnifiedAliasColumnMapping> aliasMappingsByColumn
    )
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(aliasMappingsByColumn);

        ValidateParameterName(predicate.ParameterName, nameof(predicate.ParameterName));

        if (!aliasMappingsByColumn.TryGetValue(predicate.Column, out var mapping))
        {
            return BuildComparisonSql(predicate.Column, predicate.Operator, predicate.ParameterName);
        }

        var canonicalComparison = BuildComparisonSql(
            mapping.CanonicalColumn,
            predicate.Operator,
            predicate.ParameterName
        );

        if (mapping.PresenceColumn is null)
        {
            return canonicalComparison;
        }

        return $"{BuildIsNotNullSql(mapping.PresenceColumn.Value)} AND {canonicalComparison}";
    }

    private string BuildComparisonSql(
        DbColumnName column,
        QueryComparisonOperator @operator,
        string parameterName
    )
    {
        return $"r.{QuoteColumn(column)} {ToSqlOperator(@operator)} {parameterName}";
    }

    private string BuildIsNotNullSql(DbColumnName column)
    {
        return $"r.{QuoteColumn(column)} IS NOT NULL";
    }

    private string QuoteColumn(DbColumnName column)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialect, column);
    }

    private string QuoteColumn(string column)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialect, column);
    }

    private string BuildPagingClause(string offsetParameterName, string limitParameterName)
    {
        return _dialect switch
        {
            SqlDialect.Pgsql => $"OFFSET {offsetParameterName} LIMIT {limitParameterName}",
            SqlDialect.Mssql =>
                $"OFFSET {offsetParameterName} ROWS FETCH NEXT {limitParameterName} ROWS ONLY",
            _ => throw new ArgumentOutOfRangeException(
                nameof(_dialect),
                _dialect,
                "Unsupported SQL dialect."
            ),
        };
    }

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
            QueryComparisonOperator.In => "IN",
            _ => throw new ArgumentOutOfRangeException(
                nameof(@operator),
                @operator,
                "Unsupported query operator."
            ),
        };
    }

    private static void ValidateParameterName(string parameterName, string parameterNameArgumentName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            throw new ArgumentException(
                "Parameter name cannot be null or whitespace.",
                parameterNameArgumentName
            );
        }
    }
}
