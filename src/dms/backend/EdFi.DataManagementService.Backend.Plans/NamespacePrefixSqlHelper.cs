// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// SQL emission and parameter helpers shared by the GET-many and single-record namespace authorization
/// SQL compilers. Centralizes the null-guarded prefix-LIKE predicate so both call sites emit identical
/// shape: PG <c>col IS NOT NULL AND col LIKE ANY(ARRAY[...])</c> and SQL Server
/// <c>col IS NOT NULL AND (col LIKE @p0 ESCAPE '\' OR col LIKE @p1 ESCAPE '\' OR ...)</c>.
/// Prefix patterns are pre-escaped by <see cref="NamespacePrefixParameterizationFactory"/>; PostgreSQL
/// uses the default backslash <c>LIKE</c> escape, while SQL Server requires the explicit
/// <c>ESCAPE '\'</c> clause emitted here.
/// </summary>
internal static class NamespacePrefixSqlHelper
{
    private const string MssqlEscapeClause = " ESCAPE '\\'";

    /// <summary>
    /// Builds the runtime parameter metadata for a namespace prefix parameterization.
    /// </summary>
    public static IReadOnlyList<QuerySqlParameter> BuildFilterParametersInOrder(
        NamespacePrefixParameterization namespacePrefixParameterization
    ) =>
        namespacePrefixParameterization.Kind switch
        {
            NamespacePrefixParameterizationKind.PgsqlArray =>
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    namespacePrefixParameterization.BaseParameterName,
                    QuerySqlParameterBinding.PgsqlArray
                ),
            ],
            NamespacePrefixParameterizationKind.MssqlScalar =>
            [
                .. namespacePrefixParameterization.ParameterNamesInOrder.Select(
                    static parameterName => new QuerySqlParameter(QuerySqlParameterRole.Filter, parameterName)
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(namespacePrefixParameterization),
                namespacePrefixParameterization.Kind,
                "Unsupported namespace prefix parameterization kind."
            ),
        };

    /// <summary>
    /// Appends a null-guarded root-table namespace LIKE predicate using the table's fixed alias.
    /// </summary>
    /// <remarks>
    /// Emits <c>{alias}."Col" IS NOT NULL AND {alias}."Col" LIKE ANY(@base)</c> for PostgreSQL or
    /// <c>{alias}.[Col] IS NOT NULL AND ({alias}.[Col] LIKE @base_0 OR {alias}.[Col] LIKE @base_1 OR ...)</c>
    /// for SQL Server. The predicate is never wrapped in outer parentheses — callers control bracketing.
    /// </remarks>
    public static void AppendRootTableNamespacePredicate(
        SqlWriter writer,
        string tableAlias,
        DbColumnName namespaceColumn,
        NamespacePrefixParameterization namespacePrefixParameterization
    )
    {
        AppendIsNotNull(writer, tableAlias, namespaceColumn);
        writer.Append(" AND ");
        AppendLikeMatch(writer, tableAlias, namespaceColumn, namespacePrefixParameterization);
    }

    /// <summary>
    /// Appends just the LIKE-match portion of a namespace check (no <c>IS NOT NULL</c> guard).
    /// Used by single-record proposed-value checks where the subject value is a SQL parameter, not a
    /// stored column, and the null case is reported separately as a <c>'r'</c> failure kind.
    /// </summary>
    public static void AppendLikeMatch(
        SqlWriter writer,
        string tableAlias,
        DbColumnName namespaceColumn,
        NamespacePrefixParameterization namespacePrefixParameterization
    ) =>
        AppendLikeMatch(
            writer,
            lhsWriter => AppendQualifiedColumn(lhsWriter, tableAlias, namespaceColumn),
            namespacePrefixParameterization
        );

    /// <summary>
    /// Appends the dialect-specific prefix-LIKE match over a caller-supplied left-hand side. The LHS
    /// emitter is invoked once per emitted <c>LIKE</c> (once for PostgreSQL, once per prefix parameter
    /// for SQL Server) so the subject may be a stored column or a bound parameter. SQL Server emits a
    /// parenthesized OR-chain of <c>&lt;lhs&gt; LIKE @p ESCAPE '\'</c>; PostgreSQL emits
    /// <c>&lt;lhs&gt; LIKE ANY(@base)</c>. No outer parentheses are added beyond the SQL Server chain
    /// grouping — callers control surrounding bracketing.
    /// </summary>
    public static void AppendLikeMatch(
        SqlWriter writer,
        Action<SqlWriter> appendLeftHandSide,
        NamespacePrefixParameterization namespacePrefixParameterization
    )
    {
        switch (namespacePrefixParameterization.Kind)
        {
            case NamespacePrefixParameterizationKind.PgsqlArray:
                appendLeftHandSide(writer);
                writer.Append(" LIKE ANY(");
                writer.AppendParameter(namespacePrefixParameterization.BaseParameterName);
                writer.Append(")");
                return;

            case NamespacePrefixParameterizationKind.MssqlScalar:
                writer.Append("(");
                for (
                    var parameterIndex = 0;
                    parameterIndex < namespacePrefixParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    if (parameterIndex > 0)
                    {
                        writer.Append(" OR ");
                    }

                    appendLeftHandSide(writer);
                    writer.Append(" LIKE ");
                    writer.AppendParameter(
                        namespacePrefixParameterization.ParameterNamesInOrder[parameterIndex]
                    );
                    writer.Append(MssqlEscapeClause);
                }
                writer.Append(")");
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(namespacePrefixParameterization),
                    namespacePrefixParameterization.Kind,
                    "Unsupported namespace prefix parameterization kind."
                );
        }
    }

    /// <summary>
    /// Appends a parameter-only <c>IS NULL OR = ''</c> check used by single-record proposed-value
    /// classification. Empty strings classify identically to NULL so a proposed namespace bound from
    /// an empty value maps to the missing branch rather than mismatch.
    /// </summary>
    public static void AppendParameterIsNullOrEmpty(SqlWriter writer, string parameterName)
    {
        writer.Append("(");
        writer.AppendParameter(parameterName);
        writer.Append(" IS NULL OR ");
        writer.AppendParameter(parameterName);
        writer.Append(" = '')");
    }

    private static void AppendIsNotNull(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        AppendQualifiedColumn(writer, tableAlias, column);
        writer.Append(" IS NOT NULL");
    }

    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(column.Value);
    }
}
