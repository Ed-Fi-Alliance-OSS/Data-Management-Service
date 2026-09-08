// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Validates that a <see cref="NamespacePrefixParameterization"/> matches the target SQL dialect and is
/// internally consistent, so a compiler cannot, for example, accept a PostgreSQL array parameterization
/// and emit <c>LIKE ANY(...)</c> against SQL Server.
/// </summary>
internal static class NamespacePrefixParameterizationValidator
{
    public static void ValidateOrThrow(
        NamespacePrefixParameterization namespacePrefixParameterization,
        SqlDialect dialect,
        string parameterizationName,
        string unsupportedDialectMessagePrefix
    )
    {
        ArgumentNullException.ThrowIfNull(namespacePrefixParameterization);
        PlanSqlWriterExtensions.ValidateBareParameterName(
            namespacePrefixParameterization.BaseParameterName,
            $"{parameterizationName}.{nameof(NamespacePrefixParameterization.BaseParameterName)}"
        );
        ArgumentNullException.ThrowIfNull(namespacePrefixParameterization.LikePatternsInOrder);
        ArgumentNullException.ThrowIfNull(namespacePrefixParameterization.ParameterNamesInOrder);

        if (namespacePrefixParameterization.LikePatternsInOrder.Count == 0)
        {
            throw new ArgumentException(
                "Namespace prefix parameterization requires at least one prefix pattern.",
                nameof(namespacePrefixParameterization)
            );
        }

        if (namespacePrefixParameterization.ParameterNamesInOrder.Count == 0)
        {
            throw new ArgumentException(
                "Namespace prefix parameterization requires at least one parameter name.",
                nameof(namespacePrefixParameterization)
            );
        }

        foreach (var parameterName in namespacePrefixParameterization.ParameterNamesInOrder)
        {
            PlanSqlWriterExtensions.ValidateBareParameterName(
                parameterName,
                $"{parameterizationName}.{nameof(NamespacePrefixParameterization.ParameterNamesInOrder)}"
            );
        }

        ValidateMatchesDialect(
            namespacePrefixParameterization.Kind,
            dialect,
            unsupportedDialectMessagePrefix
        );
        ValidateShape(namespacePrefixParameterization);
    }

    private static void ValidateMatchesDialect(
        NamespacePrefixParameterizationKind kind,
        SqlDialect dialect,
        string unsupportedDialectMessagePrefix
    )
    {
        switch (dialect)
        {
            case SqlDialect.Pgsql:
                if (kind is not NamespacePrefixParameterizationKind.PgsqlArray)
                {
                    throw CreateDialectMismatchException(kind, dialect);
                }

                return;

            case SqlDialect.Mssql:
                if (kind is not NamespacePrefixParameterizationKind.MssqlScalar)
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

    private static void ValidateShape(NamespacePrefixParameterization namespacePrefixParameterization)
    {
        switch (namespacePrefixParameterization.Kind)
        {
            case NamespacePrefixParameterizationKind.PgsqlArray:
                if (
                    namespacePrefixParameterization.ParameterNamesInOrder.Count is not 1
                    || !string.Equals(
                        namespacePrefixParameterization.ParameterNamesInOrder[0],
                        namespacePrefixParameterization.BaseParameterName,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new ArgumentException(
                        "PostgreSQL array namespace prefix parameterizations require exactly the base parameter name.",
                        nameof(namespacePrefixParameterization)
                    );
                }

                return;

            case NamespacePrefixParameterizationKind.MssqlScalar:
                if (
                    namespacePrefixParameterization.ParameterNamesInOrder.Count
                    != namespacePrefixParameterization.LikePatternsInOrder.Count
                )
                {
                    throw new ArgumentException(
                        "SQL Server scalar namespace prefix parameterizations require one parameter name per prefix pattern.",
                        nameof(namespacePrefixParameterization)
                    );
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(namespacePrefixParameterization),
                    namespacePrefixParameterization.Kind,
                    "Unsupported namespace prefix parameterization kind."
                );
        }
    }

    private static ArgumentException CreateDialectMismatchException(
        NamespacePrefixParameterizationKind kind,
        SqlDialect dialect
    ) =>
        new(
            $"Namespace prefix parameterization kind '{kind}' is not supported by SQL dialect '{dialect}'.",
            nameof(kind)
        );
}
