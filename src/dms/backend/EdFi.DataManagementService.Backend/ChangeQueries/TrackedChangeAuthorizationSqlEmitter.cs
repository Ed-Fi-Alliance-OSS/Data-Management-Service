// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.ChangeQueries;

internal sealed record TrackedChangeAuthorizationSql(
    IReadOnlyList<string> Predicates,
    IReadOnlyList<RelationalParameter> Parameters
)
{
    public static readonly TrackedChangeAuthorizationSql None = new([], []);
}

/// <summary>
/// Emits ReadChanges authorization SQL predicate fragments and parameters for tracked-change queries
/// from a <see cref="ReadChangesAuthorizationPlan"/>. Subjects AND within a strategy, strategies OR
/// across, NamespaceBased AND-ed with the relationship OR-group.
/// </summary>
internal static class TrackedChangeAuthorizationSqlEmitter
{
    public static TrackedChangeAuthorizationSql Emit(
        ReadChangesAuthorizationPlan plan,
        SqlDialect dialect,
        string alias,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(parameterConfigurator);

        List<string> predicates = [];
        List<RelationalParameter> parameters = [];

        if (
            plan.NamespaceCheck is { } namespaceCheck
            && plan.NamespaceParameterization is { } namespaceParameterization
        )
        {
            predicates.Add(
                BuildNamespacePredicate(alias, namespaceCheck, namespaceParameterization, dialect)
            );
            parameters.AddRange(BuildNamespaceParameters(namespaceParameterization, dialect));
        }

        if (plan.RelationshipChecks.Count > 0 && plan.ClaimParameterization is { } claimParameterization)
        {
            string claimFilter = BuildClaimFilterSql(dialect, claimParameterization);
            string orGroup = string.Join(
                " OR ",
                plan.RelationshipChecks.Select(check =>
                    "("
                    + string.Join(
                        " AND ",
                        check.Subjects.Select(subject =>
                            BuildSubjectPredicate(dialect, alias, subject, claimFilter)
                        )
                    )
                    + ")"
                )
            );
            predicates.Add("(" + orGroup + ")");
            parameters.AddRange(BuildClaimParameters(claimParameterization, parameterConfigurator));
        }

        return new TrackedChangeAuthorizationSql(predicates, parameters);
    }

    private static string BuildSubjectPredicate(
        SqlDialect dialect,
        string alias,
        ReadChangesAuthorizationSubject subject,
        string claimFilter
    )
    {
        string trackedColumn = $"{alias}.{Quote(dialect, subject.TrackedOldColumn)}";
        string hierarchyPredicate =
            $"{trackedColumn} IN (SELECT {Quote(dialect, subject.AuthViewSubjectColumn)} "
            + $"FROM {Quote(dialect, subject.AuthView)} WHERE {Quote(dialect, subject.AuthViewClaimColumn)} {claimFilter})";

        return subject.AuthView == AuthNames.EdOrgIdToEdOrgId
            ? $"({trackedColumn} {claimFilter} OR {hierarchyPredicate})"
            : hierarchyPredicate;
    }

    // Mirrors AuthorizationClaimEducationOrganizationIdSqlHelper.AppendClaimFilterSql so tracked-change
    // queries emit the exact claim-filter shapes the live single-record/page paths use.
    private static string BuildClaimFilterSql(
        SqlDialect dialect,
        AuthorizationClaimEducationOrganizationIdParameterization p
    ) =>
        p.Kind switch
        {
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray =>
                $"= ANY(@{p.BaseParameterName})",
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar =>
                p.ParameterNamesInOrder.Count == 0
                    ? "IN (SELECT 1 WHERE 1 = 0)" // no claims → match nothing
                    : "IN (" + string.Join(", ", p.ParameterNamesInOrder.Select(n => "@" + n)) + ")",
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured => "IN (SELECT "
                + SqlIdentifierQuoter.QuoteIdentifier(
                    dialect,
                    AuthorizationClaimEducationOrganizationIdParameterizationFactory.MssqlStructuredParameterColumnName
                )
                + $" FROM @{p.BaseParameterName})",
            _ => throw new InvalidOperationException($"Unsupported claim parameterization kind '{p.Kind}'."),
        };

    // Reuses the live binding helpers so PG array, MSSQL scalars, and the MSSQL TVP (DataTable +
    // SqlDbType.Structured via the configurator callback) all bind identically to the single-record path.
    private static IEnumerable<RelationalParameter> BuildClaimParameters(
        AuthorizationClaimEducationOrganizationIdParameterization p,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        IReadOnlyList<QuerySqlParameter> filterParameters =
            AuthorizationClaimEducationOrganizationIdSqlHelper.BuildFilterParametersInOrder(p);

        return p.Kind switch
        {
            // PgsqlArray and MssqlStructured each carry a single parameter bound to the whole id list.
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray
            or AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured =>
            [
                RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                    filterParameters[0],
                    p.ClaimEducationOrganizationIds,
                    parameterConfigurator
                ),
            ],
            // MssqlScalar binds one parameter per id, zipped positionally with the id list.
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar => filterParameters
                .Select(
                    (querySqlParameter, index) =>
                        RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                            querySqlParameter,
                            p.ClaimEducationOrganizationIds[index],
                            parameterConfigurator
                        )
                )
                .ToArray(),
            _ => throw new InvalidOperationException($"Unsupported claim parameterization kind '{p.Kind}'."),
        };
    }

    private static string BuildNamespacePredicate(
        string alias,
        ReadChangesNamespaceCheckSpec check,
        NamespacePrefixParameterization p,
        SqlDialect dialect
    )
    {
        string column = $"{alias}.{Quote(dialect, check.TrackedOldNamespaceColumn)}";
        string likeChain = dialect switch
        {
            SqlDialect.Pgsql => $"{column} LIKE ANY(@{p.ParameterNamesInOrder[0]})",
            SqlDialect.Mssql => string.Join(
                " OR ",
                p.ParameterNamesInOrder.Select(n => $"{column} LIKE @{n} ESCAPE '\\'")
            ),
            _ => throw new InvalidOperationException($"Unsupported SQL dialect '{dialect}'."),
        };
        return $"({column} IS NOT NULL AND ({likeChain}))";
    }

    private static IEnumerable<RelationalParameter> BuildNamespaceParameters(
        NamespacePrefixParameterization p,
        SqlDialect dialect
    ) =>
        dialect switch
        {
            SqlDialect.Pgsql =>
            [
                new RelationalParameter("@" + p.ParameterNamesInOrder[0], p.LikePatternsInOrder.ToArray()),
            ],
            SqlDialect.Mssql => p.ParameterNamesInOrder.Select(
                (n, i) => new RelationalParameter("@" + n, p.LikePatternsInOrder[i])
            ),
            _ => throw new InvalidOperationException($"Unsupported SQL dialect '{dialect}'."),
        };

    private static string Quote(SqlDialect dialect, DbColumnName column) =>
        SqlIdentifierQuoter.QuoteIdentifier(dialect, column);

    private static string Quote(SqlDialect dialect, DbTableName table) =>
        SqlIdentifierQuoter.QuoteTableName(dialect, table);
}
