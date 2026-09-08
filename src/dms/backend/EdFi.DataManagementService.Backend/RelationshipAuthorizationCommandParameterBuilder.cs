// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal static class RelationshipAuthorizationCommandParameterBuilder
{
    public static void AddAuthorizationParameterValues(
        IDictionary<string, object?> parameterValues,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        AuthorizationClaimEducationOrganizationIdParameterValues.AddTo(
            parameterValues,
            authorizationClaimParameterization
        );
    }

    public static RelationalParameter BuildParameter(
        QuerySqlParameter parameter,
        object? value,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(parameterConfigurator);

        return parameter.Binding.Kind switch
        {
            QuerySqlParameterBindingKind.Scalar => new RelationalParameter(
                $"@{parameter.ParameterName}",
                value
            ),
            QuerySqlParameterBindingKind.PgsqlArray => new RelationalParameter(
                $"@{parameter.ParameterName}",
                RequireInt64Array(value, parameter.ParameterName)
            ),
            QuerySqlParameterBindingKind.MssqlStructured => new RelationalParameter(
                $"@{parameter.ParameterName}",
                CreateStructuredInt64Table(
                    parameter.Binding.StructuredColumnName
                        ?? throw new InvalidOperationException(
                            $"Structured binding for parameter '{parameter.ParameterName}' is missing a column name."
                        ),
                    RequireInt64List(value, parameter.ParameterName)
                ),
                dbParameter => parameterConfigurator.ConfigureParameter(dbParameter, parameter)
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameter),
                parameter.Binding.Kind,
                "Unsupported single-record authorization parameter binding kind."
            ),
        };
    }

    private static long[] RequireInt64Array(object? value, string parameterName)
    {
        if (value is long[] int64Array)
        {
            return int64Array;
        }

        var int64Values = RequireInt64List(value, parameterName);
        var array = new long[int64Values.Count];

        for (var index = 0; index < int64Values.Count; index++)
        {
            array[index] = int64Values[index];
        }

        return array;
    }

    private static IReadOnlyList<long> RequireInt64List(object? value, string parameterName)
    {
        if (value is IReadOnlyList<long> int64Values)
        {
            return int64Values;
        }

        throw new InvalidOperationException(
            "Single-record authorization parameter "
                + $"'{parameterName}' requires an IReadOnlyList<long> runtime value."
        );
    }

    private static DataTable CreateStructuredInt64Table(
        string structuredColumnName,
        IReadOnlyList<long> int64Values
    )
    {
        DataTable structuredTable = new();
        structuredTable.MinimumCapacity = int64Values.Count;
        structuredTable.Columns.Add(structuredColumnName, typeof(long));

        foreach (var value in int64Values)
        {
            structuredTable.Rows.Add(value);
        }

        return structuredTable;
    }
}
