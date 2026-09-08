// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Binds runtime namespace-prefix parameter values into a planner parameter-value dictionary.
/// PostgreSQL stores the escaped LIKE pattern list under the base parameter name (Npgsql infers
/// <c>text[]</c> from the runtime <see cref="IReadOnlyList{T}"/>); SQL Server stores each
/// scalar pattern under its allocated parameter name in declaration order.
/// </summary>
internal static class NamespacePrefixParameterValueBinder
{
    public static void Bind(
        IDictionary<string, object?> parameterValues,
        NamespacePrefixParameterization? namespacePrefixParameterization
    )
    {
        ArgumentNullException.ThrowIfNull(parameterValues);

        if (namespacePrefixParameterization is null)
        {
            return;
        }

        switch (namespacePrefixParameterization.Kind)
        {
            case NamespacePrefixParameterizationKind.PgsqlArray:
                parameterValues[namespacePrefixParameterization.BaseParameterName] =
                    namespacePrefixParameterization.LikePatternsInOrder;
                return;

            case NamespacePrefixParameterizationKind.MssqlScalar:
                for (
                    var parameterIndex = 0;
                    parameterIndex < namespacePrefixParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    parameterValues[namespacePrefixParameterization.ParameterNamesInOrder[parameterIndex]] =
                        namespacePrefixParameterization.LikePatternsInOrder[parameterIndex];
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
}
