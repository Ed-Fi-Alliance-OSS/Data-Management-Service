// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Validates that an <see cref="OwnershipTokenParameterization"/> matches the target SQL dialect and is
/// internally consistent, so a compiler cannot, for example, accept a PostgreSQL array parameterization and
/// emit <c>= ANY(...)</c> against SQL Server.
/// </summary>
internal static class OwnershipTokenParameterizationValidator
{
    public static void ValidateOrThrow(
        OwnershipTokenParameterization ownershipTokenParameterization,
        SqlDialect dialect,
        string parameterizationName,
        string unsupportedDialectMessagePrefix
    )
    {
        ArgumentNullException.ThrowIfNull(ownershipTokenParameterization);
        PlanSqlWriterExtensions.ValidateBareParameterName(
            ownershipTokenParameterization.BaseParameterName,
            $"{parameterizationName}.{nameof(OwnershipTokenParameterization.BaseParameterName)}"
        );
        ArgumentNullException.ThrowIfNull(ownershipTokenParameterization.TokensInOrder);
        ArgumentNullException.ThrowIfNull(ownershipTokenParameterization.ParameterNamesInOrder);

        // Deliberately no minimum-count guard: an empty token list is valid and renders a constant-false
        // membership predicate, so that the stored-row check still runs and can tell §2.14 from §2.13.
        if (
            ownershipTokenParameterization.TokensInOrder.Count
            >= OwnershipTokenLimitExceededException.OwnershipTokenLimit
        )
        {
            throw new ArgumentException(
                $"Ownership token parameterization carries {ownershipTokenParameterization.TokensInOrder.Count} tokens, which reaches the supported limit of {OwnershipTokenLimitExceededException.OwnershipTokenLimit}.",
                nameof(ownershipTokenParameterization)
            );
        }

        foreach (var parameterName in ownershipTokenParameterization.ParameterNamesInOrder)
        {
            PlanSqlWriterExtensions.ValidateBareParameterName(
                parameterName,
                $"{parameterizationName}.{nameof(OwnershipTokenParameterization.ParameterNamesInOrder)}"
            );
        }

        ValidateMatchesDialect(ownershipTokenParameterization.Kind, dialect, unsupportedDialectMessagePrefix);
        ValidateShape(ownershipTokenParameterization);
    }

    private static void ValidateMatchesDialect(
        OwnershipTokenParameterizationKind kind,
        SqlDialect dialect,
        string unsupportedDialectMessagePrefix
    )
    {
        switch (dialect)
        {
            case SqlDialect.Pgsql:
                if (kind is not OwnershipTokenParameterizationKind.PgsqlArray)
                {
                    throw CreateDialectMismatchException(kind, dialect);
                }

                return;

            case SqlDialect.Mssql:
                if (kind is not OwnershipTokenParameterizationKind.MssqlScalar)
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

    private static void ValidateShape(OwnershipTokenParameterization ownershipTokenParameterization)
    {
        switch (ownershipTokenParameterization.Kind)
        {
            case OwnershipTokenParameterizationKind.PgsqlArray:
                // Exactly the base name, even for an empty token list. The shape is about what the
                // parameterization declares, not what a given statement binds: an empty list renders a
                // constant-false predicate and OwnershipTokenSqlHelper then binds no token parameter at all.
                if (
                    ownershipTokenParameterization.ParameterNamesInOrder.Count is not 1
                    || !string.Equals(
                        ownershipTokenParameterization.ParameterNamesInOrder[0],
                        ownershipTokenParameterization.BaseParameterName,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new ArgumentException(
                        "PostgreSQL array ownership token parameterizations require exactly the base parameter name.",
                        nameof(ownershipTokenParameterization)
                    );
                }

                return;

            case OwnershipTokenParameterizationKind.MssqlScalar:
                if (
                    ownershipTokenParameterization.ParameterNamesInOrder.Count
                    != ownershipTokenParameterization.TokensInOrder.Count
                )
                {
                    throw new ArgumentException(
                        "SQL Server scalar ownership token parameterizations require one parameter name per token.",
                        nameof(ownershipTokenParameterization)
                    );
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(ownershipTokenParameterization),
                    ownershipTokenParameterization.Kind,
                    "Unsupported ownership token parameterization kind."
                );
        }
    }

    private static ArgumentException CreateDialectMismatchException(
        OwnershipTokenParameterizationKind kind,
        SqlDialect dialect
    ) =>
        new(
            $"Ownership token parameterization kind '{kind}' is not supported by SQL dialect '{dialect}'.",
            nameof(kind)
        );
}
