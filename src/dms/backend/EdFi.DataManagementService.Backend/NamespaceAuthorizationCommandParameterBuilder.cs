// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Binds runtime parameter values for single-record namespace authorization commands. Distinct from
/// the relationship parameter builder because namespace prefixes bind as a PostgreSQL <c>string[]</c>
/// array (or SQL Server scalar string parameters), never as the long-only claim-EdOrg array path.
/// </summary>
internal static class NamespaceAuthorizationCommandParameterBuilder
{
    public static void AddParameterValues(
        IDictionary<string, object?> parameterValues,
        NamespacePrefixParameterization namespacePrefixParameterization,
        long documentId,
        string? proposedNamespace
    )
    {
        ArgumentNullException.ThrowIfNull(parameterValues);
        ArgumentNullException.ThrowIfNull(namespacePrefixParameterization);

        parameterValues[NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName] = documentId;
        parameterValues[NamespaceAuthorizationSqlSpecDefaults.ProposedNamespaceParameterName] =
            proposedNamespace;

        NamespacePrefixParameterValueBinder.Bind(parameterValues, namespacePrefixParameterization);
    }

    public static RelationalParameter BuildParameter(QuerySqlParameter parameter, object? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        return parameter.Binding.Kind switch
        {
            QuerySqlParameterBindingKind.Scalar => new RelationalParameter(
                $"@{parameter.ParameterName}",
                value
            ),
            QuerySqlParameterBindingKind.PgsqlArray => new RelationalParameter(
                $"@{parameter.ParameterName}",
                RequireStringList(value, parameter.ParameterName).ToArray()
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameter),
                parameter.Binding.Kind,
                "Unsupported namespace authorization parameter binding kind."
            ),
        };
    }

    private static IReadOnlyList<string> RequireStringList(object? value, string parameterName)
    {
        if (value is IReadOnlyList<string> stringValues)
        {
            return stringValues;
        }

        throw new InvalidOperationException(
            $"Namespace authorization array parameter '{parameterName}' requires an IReadOnlyList<string> runtime value."
        );
    }
}
