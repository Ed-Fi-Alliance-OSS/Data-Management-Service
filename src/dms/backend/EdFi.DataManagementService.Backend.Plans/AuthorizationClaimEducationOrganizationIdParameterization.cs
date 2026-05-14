// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Runtime parameterization shape for DMS-1055 claim EdOrg ids.
/// </summary>
public enum AuthorizationClaimEducationOrganizationIdParameterizationKind
{
    PgsqlArray,
    MssqlScalar,
    MssqlStructured,
}

/// <summary>
/// Dialect-specific authorization parameter shape plus normalized claim EdOrg ids.
/// </summary>
/// <param name="Kind">The emitted SQL/binding shape for the claim list.</param>
/// <param name="BaseParameterName">The logical base parameter name.</param>
/// <param name="ClaimEducationOrganizationIds">Deduplicated claim EdOrg ids sorted ascending.</param>
/// <param name="ParameterNamesInOrder">Concrete SQL parameter names in deterministic binding order.</param>
public sealed record AuthorizationClaimEducationOrganizationIdParameterization(
    AuthorizationClaimEducationOrganizationIdParameterizationKind Kind,
    string BaseParameterName,
    IReadOnlyList<long> ClaimEducationOrganizationIds,
    IReadOnlyList<string> ParameterNamesInOrder
);

/// <summary>
/// Builds the DMS-1055 claim EdOrg authorization parameterization for the target SQL dialect.
/// </summary>
public static class AuthorizationClaimEducationOrganizationIdParameterizationFactory
{
    internal const int MssqlStructuredParameterThreshold = 2000;
    internal const string MssqlStructuredParameterTypeName = "dms.BigIntTable";
    internal const string MssqlStructuredParameterColumnName = "Id";

    public static AuthorizationClaimEducationOrganizationIdParameterization Create(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds,
        string baseParameterName
    )
    {
        ArgumentNullException.ThrowIfNull(claimEducationOrganizationIds);
        PlanSqlWriterExtensions.ValidateBareParameterName(baseParameterName, nameof(baseParameterName));

        var normalizedClaimEducationOrganizationIds = claimEducationOrganizationIds
            .Distinct()
            .Order()
            .ToArray();

        if (normalizedClaimEducationOrganizationIds.Length == 0)
        {
            throw new ArgumentException(
                "Authorization claim EdOrg parameterization requires at least one claim EdOrg id.",
                nameof(claimEducationOrganizationIds)
            );
        }

        return dialect switch
        {
            SqlDialect.Pgsql => new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
                baseParameterName,
                normalizedClaimEducationOrganizationIds,
                [baseParameterName]
            ),
            SqlDialect.Mssql
                when normalizedClaimEducationOrganizationIds.Length < MssqlStructuredParameterThreshold =>
                new AuthorizationClaimEducationOrganizationIdParameterization(
                    AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
                    baseParameterName,
                    normalizedClaimEducationOrganizationIds,
                    [
                        .. Enumerable
                            .Range(0, normalizedClaimEducationOrganizationIds.Length)
                            .Select(index => CreateScalarParameterName(baseParameterName, index)),
                    ]
                ),
            SqlDialect.Mssql => new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured,
                baseParameterName,
                normalizedClaimEducationOrganizationIds,
                [baseParameterName]
            ),
            _ => throw new NotSupportedException(
                $"Authorization claim EdOrg parameterization does not support SQL dialect '{dialect}'."
            ),
        };
    }

    private static string CreateScalarParameterName(string baseParameterName, int index)
    {
        var parameterName = string.Create(CultureInfo.InvariantCulture, $"{baseParameterName}_{index}");

        PlanSqlWriterExtensions.ValidateBareParameterName(parameterName, nameof(baseParameterName));
        return parameterName;
    }
}
