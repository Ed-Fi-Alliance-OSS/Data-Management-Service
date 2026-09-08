// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Checks the list-shaped authorization parameterizations a single GET-many page query can bind — the
/// NamespaceBased prefix <c>LIKE</c> chain, the relationship claim education organization id list, and/or the
/// OwnershipBased token list — together with the query's filter and paging parameters against SQL Server's
/// per-command parameter ceiling.
/// </summary>
/// <remarks>
/// Each authorization list is capped independently below 2,000 SQL Server scalar parameters
/// (<see cref="NamespacePrefixLimitExceededException.MssqlScalarParameterLimit"/>, the claim
/// parameterization's structured-parameter threshold, and
/// <see cref="OwnershipTokenLimitExceededException.OwnershipTokenLimit"/>). SQL Server, however, binds at most
/// <see cref="MssqlMaxCommandParameters"/> parameters per command, so the page query can still exceed that
/// ceiling — whether it composes several authorization lists, or a single near-cap list alongside enough
/// query-filter parameters — which would otherwise surface as an execution-time SQL error rather than a
/// controlled authorization/configuration failure. Counting
/// <see cref="NamespacePrefixParameterization.ParameterNamesInOrder"/>,
/// <see cref="AuthorizationClaimEducationOrganizationIdParameterization.ParameterNamesInOrder"/>, and the
/// parameters <see cref="OwnershipTokenSqlHelper"/> emits for an ownership parameterization reflects the real
/// bound parameter count per shape — a PostgreSQL array or SQL Server table-valued parameter is a single
/// parameter, a SQL Server scalar list is one parameter per value, an empty ownership list is none — so
/// PostgreSQL composition never approaches the limit and only the SQL Server scalar case can.
/// </remarks>
public static class AuthorizationParameterBudget
{
    /// <summary>
    /// The number of user parameters a single SQL Server command can bind, which is the budget this type
    /// spends across the authorization lists and the query's own parameters. The value is the engine limit
    /// owned by <see cref="MssqlCommandLimits.MaxUserParametersPerCommand" /> — see that member for why the
    /// usable ceiling is 2098 rather than the documented 2100 RPC limit.
    /// </summary>
    public const int MssqlMaxCommandParameters = MssqlCommandLimits.MaxUserParametersPerCommand;

    /// <summary>The number of paging parameters (offset and limit) every page query binds.</summary>
    public const int PaginationParameterCount = 2;

    /// <summary>
    /// The number of SQL parameters the supplied authorization parameterizations bind. Any argument may be
    /// <see langword="null"/> for a shape that does not use that strategy, in which case that list contributes
    /// nothing. The ownership argument is trailing and optional so the read paths that do not filter by
    /// ownership keep their existing calls unchanged.
    /// </summary>
    /// <remarks>
    /// The ownership count comes from the parameters the SQL helper actually emits rather than from
    /// <see cref="OwnershipTokenParameterization.ParameterNamesInOrder"/>: an empty token list declares its
    /// base name but renders a constant-false predicate that binds no parameter, so it must count as zero.
    /// </remarks>
    public static int CountAuthorizationParameters(
        NamespacePrefixParameterization? namespacePrefixParameterization,
        AuthorizationClaimEducationOrganizationIdParameterization? claimEducationOrganizationIdParameterization,
        OwnershipTokenParameterization? ownershipTokenParameterization = null
    ) =>
        (namespacePrefixParameterization?.ParameterNamesInOrder.Count ?? 0)
        + (claimEducationOrganizationIdParameterization?.ParameterNamesInOrder.Count ?? 0)
        + (
            ownershipTokenParameterization is null
                ? 0
                : OwnershipTokenSqlHelper.BuildFilterParametersInOrder(ownershipTokenParameterization).Count
        );

    /// <summary>
    /// Returns <see langword="true"/> when the authorization parameters this query binds, together with
    /// <paramref name="nonAuthorizationParameterCount"/> (the query-filter predicate parameters plus the
    /// paging parameters), exceed SQL Server's per-command parameter ceiling. Applies to every shape —
    /// namespace-only, relationship-only, ownership-only, and composed — because every authorization
    /// parameterization may be <see langword="null"/>. The ceiling is specific to SQL Server, so this always
    /// returns <see langword="false"/> for other dialects; the gate lives here so no call site can apply the
    /// limit to a dialect that does not share it.
    /// </summary>
    public static bool ExceedsCommandParameterLimit(
        SqlDialect dialect,
        NamespacePrefixParameterization? namespacePrefixParameterization,
        AuthorizationClaimEducationOrganizationIdParameterization? claimEducationOrganizationIdParameterization,
        int nonAuthorizationParameterCount,
        OwnershipTokenParameterization? ownershipTokenParameterization = null
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
                claimEducationOrganizationIdParameterization,
                ownershipTokenParameterization
            ) + nonAuthorizationParameterCount;

        return totalParameterCount > MssqlMaxCommandParameters;
    }
}
