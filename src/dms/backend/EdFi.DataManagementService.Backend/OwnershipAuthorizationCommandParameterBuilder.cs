// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Binds runtime parameter values for single-record ownership authorization commands. Distinct from both
/// sibling builders: ownership tokens bind as a PostgreSQL <c>short[]</c> array (or SQL Server
/// <c>smallint</c> scalars), never as the namespace builder's <c>string[]</c> prefix patterns and never as
/// the relationship builder's long-only claim-EdOrg array path.
/// </summary>
internal static class OwnershipAuthorizationCommandParameterBuilder
{
    public static void AddParameterValues(
        IDictionary<string, object?> parameterValues,
        OwnershipTokenParameterization ownershipTokenParameterization,
        long documentId
    )
    {
        ArgumentNullException.ThrowIfNull(parameterValues);
        ArgumentNullException.ThrowIfNull(ownershipTokenParameterization);

        parameterValues[OwnershipAuthorizationSqlSpecDefaults.DocumentIdParameterName] = documentId;

        OwnershipTokenParameterValueBinder.Bind(parameterValues, ownershipTokenParameterization);
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
                RequireShortList(value, parameter.ParameterName).ToArray()
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameter),
                parameter.Binding.Kind,
                "Unsupported ownership authorization parameter binding kind."
            ),
        };
    }

    /// <remarks>
    /// The list type is checked rather than assumed. A <c>long</c> or <c>int</c> list would still bind and
    /// still return correct rows, but it would widen the comparison against the <c>smallint</c> ownership
    /// column and silently cost the index on it, so the wrong element type fails loudly here instead.
    /// </remarks>
    private static IReadOnlyList<short> RequireShortList(object? value, string parameterName)
    {
        if (value is IReadOnlyList<short> ownershipTokenIds)
        {
            return ownershipTokenIds;
        }

        throw new InvalidOperationException(
            $"Ownership authorization array parameter '{parameterName}' requires an IReadOnlyList<short> runtime value."
        );
    }
}
