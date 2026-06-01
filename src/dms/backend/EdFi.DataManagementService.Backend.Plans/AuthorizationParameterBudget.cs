// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Cross-checks the two list-shaped authorization parameterizations that a single GET-many page query can
/// compose — the NamespaceBased prefix <c>LIKE</c> chain and the relationship claim education organization
/// id list — against SQL Server's per-command parameter ceiling.
/// </summary>
/// <remarks>
/// Each list is capped independently below 2,000 SQL Server scalar parameters
/// (<see cref="NamespacePrefixLimitExceededException.MssqlScalarParameterLimit"/> and the claim
/// parameterization's structured-parameter threshold). SQL Server, however, binds at most
/// <see cref="MssqlMaxCommandParameters"/> parameters per command, so a query that composes both lists can
/// exceed that ceiling even when each list is within its own cap, which would otherwise surface as an
/// execution-time SQL error rather than a controlled authorization/configuration failure. Counting
/// <see cref="NamespacePrefixParameterization.ParameterNamesInOrder"/> and
/// <see cref="AuthorizationClaimEducationOrganizationIdParameterization.ParameterNamesInOrder"/> reflects
/// the real bound parameter count per shape — a PostgreSQL array or SQL Server table-valued parameter is a
/// single parameter, a SQL Server scalar list is one parameter per value — so PostgreSQL composition never
/// approaches the limit and only the SQL Server scalar-plus-scalar case can.
/// </remarks>
public static class AuthorizationParameterBudget
{
    /// <summary>SQL Server's documented maximum number of parameters per command.</summary>
    public const int MssqlMaxCommandParameters = 2100;

    /// <summary>
    /// Parameters reserved below <see cref="MssqlMaxCommandParameters"/> for the query-filter predicate
    /// parameters and the two paging parameters the page query binds alongside the authorization lists.
    /// </summary>
    public const int ReservedNonAuthorizationParameters = 100;

    /// <summary>
    /// The maximum combined number of authorization parameters the two lists may bind together before the
    /// composed page query risks exceeding SQL Server's per-command parameter ceiling.
    /// </summary>
    public const int MssqlCombinedAuthorizationParameterLimit =
        MssqlMaxCommandParameters - ReservedNonAuthorizationParameters;

    /// <summary>
    /// Returns <see langword="true"/> when composing the namespace prefix and claim education organization
    /// id parameterizations into a single command would bind more authorization parameters than
    /// <see cref="MssqlCombinedAuthorizationParameterLimit"/> permits.
    /// </summary>
    public static bool ExceedsCombinedLimit(
        NamespacePrefixParameterization namespacePrefixParameterization,
        AuthorizationClaimEducationOrganizationIdParameterization claimEducationOrganizationIdParameterization
    )
    {
        ArgumentNullException.ThrowIfNull(namespacePrefixParameterization);
        ArgumentNullException.ThrowIfNull(claimEducationOrganizationIdParameterization);

        var combinedAuthorizationParameterCount =
            namespacePrefixParameterization.ParameterNamesInOrder.Count
            + claimEducationOrganizationIdParameterization.ParameterNamesInOrder.Count;

        return combinedAuthorizationParameterCount > MssqlCombinedAuthorizationParameterLimit;
    }
}
