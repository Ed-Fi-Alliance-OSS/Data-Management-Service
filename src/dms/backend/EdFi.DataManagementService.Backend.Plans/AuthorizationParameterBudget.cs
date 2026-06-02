// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Checks the list-shaped authorization parameterizations a single GET-many page query can bind — the
/// NamespaceBased prefix <c>LIKE</c> chain and/or the relationship claim education organization id list —
/// together with the query's filter and paging parameters against SQL Server's per-command parameter
/// ceiling.
/// </summary>
/// <remarks>
/// Each authorization list is capped independently below 2,000 SQL Server scalar parameters
/// (<see cref="NamespacePrefixLimitExceededException.MssqlScalarParameterLimit"/> and the claim
/// parameterization's structured-parameter threshold). SQL Server, however, binds at most
/// <see cref="MssqlMaxCommandParameters"/> parameters per command, so the page query can still exceed that
/// ceiling — whether it composes both authorization lists, or a single near-cap list alongside enough
/// query-filter parameters — which would otherwise surface as an execution-time SQL error rather than a
/// controlled authorization/configuration failure. Counting
/// <see cref="NamespacePrefixParameterization.ParameterNamesInOrder"/> and
/// <see cref="AuthorizationClaimEducationOrganizationIdParameterization.ParameterNamesInOrder"/> reflects
/// the real bound parameter count per shape — a PostgreSQL array or SQL Server table-valued parameter is a
/// single parameter, a SQL Server scalar list is one parameter per value — so PostgreSQL composition never
/// approaches the limit and only the SQL Server scalar case can.
/// </remarks>
public static class AuthorizationParameterBudget
{
    /// <summary>SQL Server's documented maximum number of parameters per command.</summary>
    public const int MssqlMaxCommandParameters = 2100;

    /// <summary>The number of paging parameters (offset and limit) every page query binds.</summary>
    public const int PaginationParameterCount = 2;

    /// <summary>
    /// The number of SQL parameters the supplied authorization parameterizations bind. Either argument may
    /// be <see langword="null"/> for a shape that does not use that strategy (namespace-only or
    /// relationship-only), in which case that list contributes nothing.
    /// </summary>
    public static int CountAuthorizationParameters(
        NamespacePrefixParameterization? namespacePrefixParameterization,
        AuthorizationClaimEducationOrganizationIdParameterization? claimEducationOrganizationIdParameterization
    ) =>
        (namespacePrefixParameterization?.ParameterNamesInOrder.Count ?? 0)
        + (claimEducationOrganizationIdParameterization?.ParameterNamesInOrder.Count ?? 0);

    /// <summary>
    /// Returns <see langword="true"/> when the authorization parameters this query binds, together with
    /// <paramref name="nonAuthorizationParameterCount"/> (the query-filter predicate parameters plus the
    /// paging parameters), exceed SQL Server's per-command parameter ceiling. Applies to every shape —
    /// namespace-only, relationship-only, and composed — because either authorization parameterization may
    /// be <see langword="null"/>. The ceiling is specific to SQL Server, so this always returns
    /// <see langword="false"/> for other dialects; the gate lives here so no call site can apply the limit
    /// to a dialect that does not share it.
    /// </summary>
    public static bool ExceedsCommandParameterLimit(
        SqlDialect dialect,
        NamespacePrefixParameterization? namespacePrefixParameterization,
        AuthorizationClaimEducationOrganizationIdParameterization? claimEducationOrganizationIdParameterization,
        int nonAuthorizationParameterCount
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nonAuthorizationParameterCount);

        if (dialect is not SqlDialect.Mssql)
        {
            // PostgreSQL binds each authorization list as a single array/table-valued parameter and allows
            // far more command parameters than SQL Server, so it cannot reach this limit; only the SQL
            // Server scalar lists can.
            return false;
        }

        var totalParameterCount =
            CountAuthorizationParameters(
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization
            ) + nonAuthorizationParameterCount;

        return totalParameterCount > MssqlMaxCommandParameters;
    }
}
