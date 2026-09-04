// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// SQL emission and parameter helpers for the ownership-token membership predicate. Centralizes the
/// dialect split — PostgreSQL <c>col = ANY(@base)</c>, SQL Server <c>col IN (@base_0, @base_1, …)</c> — and
/// the empty-token-list rendering, so a single-record compiler and any later page-query compiler cannot
/// diverge on either.
/// </summary>
internal static class OwnershipTokenSqlHelper
{
    /// <summary>
    /// The predicate emitted when the caller holds no ownership tokens. A constant false rather than an
    /// empty <c>IN ()</c>, which is a syntax error on SQL Server, or an untyped empty array on PostgreSQL.
    /// </summary>
    private const string MatchesNothingPredicate = "1 = 0";

    /// <summary>
    /// Runtime parameter metadata for an ownership-token parameterization, in binding order.
    /// </summary>
    /// <remarks>
    /// Empty for an empty token list, on both dialects. The emitted predicate is then a constant that
    /// references no parameter, and a command must not declare a parameter its SQL never mentions —
    /// co-batching rejects exactly that as a dangling parameter. This is why the decision lives here rather
    /// than being read off <see cref="OwnershipTokenParameterization.ParameterNamesInOrder"/>, which
    /// describes the parameterization's shape rather than what a given statement binds.
    /// </remarks>
    public static IReadOnlyList<QuerySqlParameter> BuildFilterParametersInOrder(
        OwnershipTokenParameterization ownershipTokenParameterization
    )
    {
        ArgumentNullException.ThrowIfNull(ownershipTokenParameterization);

        if (ownershipTokenParameterization.MatchesNoToken)
        {
            return [];
        }

        return ownershipTokenParameterization.Kind switch
        {
            OwnershipTokenParameterizationKind.PgsqlArray =>
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    ownershipTokenParameterization.BaseParameterName,
                    QuerySqlParameterBinding.PgsqlArray
                ),
            ],
            OwnershipTokenParameterizationKind.MssqlScalar =>
            [
                .. ownershipTokenParameterization.ParameterNamesInOrder.Select(
                    static parameterName => new QuerySqlParameter(QuerySqlParameterRole.Filter, parameterName)
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(ownershipTokenParameterization),
                ownershipTokenParameterization.Kind,
                "Unsupported ownership token parameterization kind."
            ),
        };
    }

    /// <summary>
    /// Appends the membership predicate for the stored ownership token against the caller's tokens. No outer
    /// parentheses are added beyond the SQL Server list grouping — callers control bracketing.
    /// </summary>
    public static void AppendMembershipPredicate(
        SqlWriter writer,
        string tableAlias,
        DbColumnName ownershipTokenColumn,
        OwnershipTokenParameterization ownershipTokenParameterization
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(ownershipTokenParameterization);

        if (ownershipTokenParameterization.MatchesNoToken)
        {
            writer.Append(MatchesNothingPredicate);
            return;
        }

        switch (ownershipTokenParameterization.Kind)
        {
            case OwnershipTokenParameterizationKind.PgsqlArray:
                AppendQualifiedColumn(writer, tableAlias, ownershipTokenColumn);
                writer.Append(" = ANY(");
                writer.AppendParameter(ownershipTokenParameterization.BaseParameterName);
                writer.Append(")");
                return;

            case OwnershipTokenParameterizationKind.MssqlScalar:
                AppendQualifiedColumn(writer, tableAlias, ownershipTokenColumn);
                writer.Append(" IN (");
                for (
                    var parameterIndex = 0;
                    parameterIndex < ownershipTokenParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    if (parameterIndex > 0)
                    {
                        writer.Append(", ");
                    }

                    writer.AppendParameter(
                        ownershipTokenParameterization.ParameterNamesInOrder[parameterIndex]
                    );
                }
                writer.Append(")");
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(ownershipTokenParameterization),
                    ownershipTokenParameterization.Kind,
                    "Unsupported ownership token parameterization kind."
                );
        }
    }

    public static void AppendIsNotNull(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        AppendQualifiedColumn(writer, tableAlias, column);
        writer.Append(" IS NOT NULL");
    }

    public static void AppendIsNull(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        AppendQualifiedColumn(writer, tableAlias, column);
        writer.Append(" IS NULL");
    }

    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(column.Value);
    }
}
