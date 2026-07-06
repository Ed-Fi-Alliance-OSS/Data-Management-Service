// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class AuthorizationClaimEducationOrganizationIdParameterizationValidator
{
    public static void ValidateOrThrow(
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        SqlDialect dialect,
        string parameterizationName,
        string unsupportedDialectMessagePrefix
    )
    {
        PlanSqlWriterExtensions.ValidateBareParameterName(
            authorizationClaimParameterization.BaseParameterName,
            $"{parameterizationName}.{nameof(AuthorizationClaimEducationOrganizationIdParameterization.BaseParameterName)}"
        );
        ArgumentNullException.ThrowIfNull(authorizationClaimParameterization.ClaimEducationOrganizationIds);
        ArgumentNullException.ThrowIfNull(authorizationClaimParameterization.ParameterNamesInOrder);

        if (authorizationClaimParameterization.ClaimEducationOrganizationIds.Count == 0)
        {
            throw new ArgumentException(
                "Authorization claim EdOrg parameterization requires at least one claim EdOrg id.",
                nameof(authorizationClaimParameterization)
            );
        }

        if (authorizationClaimParameterization.ParameterNamesInOrder.Count == 0)
        {
            throw new ArgumentException(
                "Authorization claim EdOrg parameterization requires at least one parameter name.",
                nameof(authorizationClaimParameterization)
            );
        }

        foreach (var parameterName in authorizationClaimParameterization.ParameterNamesInOrder)
        {
            PlanSqlWriterExtensions.ValidateBareParameterName(
                parameterName,
                $"{parameterizationName}.{nameof(AuthorizationClaimEducationOrganizationIdParameterization.ParameterNamesInOrder)}"
            );
        }

        ValidateMatchesDialect(
            authorizationClaimParameterization.Kind,
            dialect,
            unsupportedDialectMessagePrefix
        );
        ValidateShape(authorizationClaimParameterization);
    }

    private static void ValidateMatchesDialect(
        AuthorizationClaimEducationOrganizationIdParameterizationKind kind,
        SqlDialect dialect,
        string unsupportedDialectMessagePrefix
    )
    {
        switch (dialect)
        {
            case SqlDialect.Pgsql:
                if (kind is not AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray)
                {
                    throw CreateDialectMismatchException(kind, dialect);
                }

                return;

            case SqlDialect.Mssql:
                if (
                    kind
                    is not AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar
                        and not AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured
                )
                {
                    throw CreateDialectMismatchException(kind, dialect);
                }

                return;

            default:
                throw new NotSupportedException(
                    $"{unsupportedDialectMessagePrefix} does not support SQL dialect '{dialect}'."
                );
        }
    }

    private static void ValidateShape(
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        switch (authorizationClaimParameterization.Kind)
        {
            case AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray:
            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured:
                if (
                    authorizationClaimParameterization.ParameterNamesInOrder.Count is not 1
                    || !string.Equals(
                        authorizationClaimParameterization.ParameterNamesInOrder[0],
                        authorizationClaimParameterization.BaseParameterName,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new ArgumentException(
                        "Array and structured authorization claim EdOrg parameterizations require exactly the base parameter name.",
                        nameof(authorizationClaimParameterization)
                    );
                }

                return;

            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar:
                if (
                    authorizationClaimParameterization.ParameterNamesInOrder.Count
                    != authorizationClaimParameterization.ClaimEducationOrganizationIds.Count
                )
                {
                    throw new ArgumentException(
                        "SQL Server scalar authorization claim EdOrg parameterizations require one parameter name per claim EdOrg id.",
                        nameof(authorizationClaimParameterization)
                    );
                }

                for (
                    var parameterIndex = 0;
                    parameterIndex < authorizationClaimParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    var expectedParameterName =
                        $"{authorizationClaimParameterization.BaseParameterName}_{parameterIndex}";

                    if (
                        !string.Equals(
                            authorizationClaimParameterization.ParameterNamesInOrder[parameterIndex],
                            expectedParameterName,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        throw new ArgumentException(
                            "SQL Server scalar authorization claim EdOrg parameter names must be derived from the base parameter name and ordinal.",
                            nameof(authorizationClaimParameterization)
                        );
                    }
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(authorizationClaimParameterization),
                    authorizationClaimParameterization.Kind,
                    "Unsupported authorization claim EdOrg parameterization kind."
                );
        }
    }

    private static ArgumentException CreateDialectMismatchException(
        AuthorizationClaimEducationOrganizationIdParameterizationKind kind,
        SqlDialect dialect
    ) =>
        new(
            $"Authorization claim EdOrg parameterization kind '{kind}' is not supported by SQL dialect '{dialect}'.",
            nameof(kind)
        );
}
