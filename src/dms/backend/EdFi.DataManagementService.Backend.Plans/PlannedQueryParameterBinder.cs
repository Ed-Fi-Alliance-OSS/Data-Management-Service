// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External.Plans;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Plans;

internal sealed record PlannedQueryParameterBinding(
    string Name,
    object? Value,
    Action<DbParameter>? ConfigureParameter = null
);

internal static class PlannedQueryParameterBinder
{
    public static IReadOnlyList<PlannedQueryParameterBinding> BindParameters(
        PageDocumentIdSqlPlan plan,
        IReadOnlyDictionary<string, object?> parameterValues,
        string commandDescription,
        string parameterDescription,
        string unsupportedBindingKindMessage
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(parameterValues);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(unsupportedBindingKindMessage);

        var requiredParameters = GetRequiredParameters(plan, commandDescription);
        ValidateRequiredParameterValues(parameterValues, requiredParameters, commandDescription);

        return
        [
            .. requiredParameters.Select(parameter =>
                BindParameter(
                    parameter,
                    parameterValues[parameter.ParameterName],
                    parameterDescription,
                    unsupportedBindingKindMessage
                )
            ),
        ];
    }

    public static void AddDbParameters(
        DbCommand command,
        PageDocumentIdSqlPlan plan,
        IReadOnlyDictionary<string, object?> parameterValues,
        string commandDescription,
        string parameterDescription,
        string unsupportedBindingKindMessage
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (
            var binding in BindParameters(
                plan,
                parameterValues,
                commandDescription,
                parameterDescription,
                unsupportedBindingKindMessage
            )
        )
        {
            AddDbParameter(command, binding);
        }
    }

    public static PlannedQueryParameterBinding BindParameter(
        QuerySqlParameter parameter,
        object? value,
        string parameterDescription,
        string unsupportedBindingKindMessage
    )
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(unsupportedBindingKindMessage);

        return parameter.Binding.Kind switch
        {
            QuerySqlParameterBindingKind.Scalar => new PlannedQueryParameterBinding(
                $"@{parameter.ParameterName}",
                value
            ),
            QuerySqlParameterBindingKind.PgsqlArray => new PlannedQueryParameterBinding(
                $"@{parameter.ParameterName}",
                CreatePgsqlArrayValue(value, parameter.ParameterName, parameterDescription)
            ),
            QuerySqlParameterBindingKind.MssqlStructured => new PlannedQueryParameterBinding(
                $"@{parameter.ParameterName}",
                CreateStructuredInt64Table(
                    parameter.Binding.StructuredColumnName
                        ?? throw new InvalidOperationException(
                            $"Structured binding for parameter '{parameter.ParameterName}' is missing a column name."
                        ),
                    RequireInt64List(value, parameter.ParameterName, parameterDescription)
                ),
                dbParameter => ConfigureMssqlStructuredParameter(dbParameter, parameter)
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameter),
                parameter.Binding.Kind,
                unsupportedBindingKindMessage
            ),
        };
    }

    private static QuerySqlParameter[] GetRequiredParameters(
        PageDocumentIdSqlPlan plan,
        string commandDescription
    )
    {
        List<QuerySqlParameter> requiredParameters = [];

        AddRequiredParameters(requiredParameters, plan.PageParametersInOrder, commandDescription);

        if (plan.TotalCountParametersInOrder is { } totalCountParameters)
        {
            AddRequiredParameters(requiredParameters, totalCountParameters, commandDescription);
        }

        return [.. requiredParameters];
    }

    private static void AddRequiredParameters(
        List<QuerySqlParameter> requiredParameters,
        IReadOnlyList<QuerySqlParameter> parameters,
        string commandDescription
    )
    {
        foreach (var parameter in parameters)
        {
            var existingParameter = requiredParameters.Find(candidateParameter =>
                string.Equals(
                    candidateParameter.ParameterName,
                    parameter.ParameterName,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            if (existingParameter is not null)
            {
                if (existingParameter != parameter)
                {
                    throw new InvalidOperationException(
                        $"{commandDescription} cannot bind parameter "
                            + $"'{parameter.ParameterName}' with conflicting binding metadata."
                    );
                }

                continue;
            }

            requiredParameters.Add(parameter);
        }
    }

    private static void ValidateRequiredParameterValues(
        IReadOnlyDictionary<string, object?> parameterValues,
        IReadOnlyList<QuerySqlParameter> requiredParameters,
        string commandDescription
    )
    {
        List<string> missingParameterNames =
        [
            .. requiredParameters
                .Select(static parameter => parameter.ParameterName)
                .Where(parameterName => !parameterValues.ContainsKey(parameterName)),
        ];

        if (missingParameterNames.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{commandDescription} is missing required parameter values for "
                + $"[{string.Join(", ", missingParameterNames.ConvertAll(parameterName => $"'{parameterName}'"))}]."
        );
    }

    private static void AddDbParameter(DbCommand command, PlannedQueryParameterBinding binding)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = binding.Name;
        parameter.Value = binding.Value ?? DBNull.Value;
        binding.ConfigureParameter?.Invoke(parameter);
        command.Parameters.Add(parameter);
    }

    private static object CreatePgsqlArrayValue(
        object? value,
        string parameterName,
        string parameterDescription
    ) =>
        value switch
        {
            IReadOnlyList<long> int64Values => int64Values.ToArray(),
            IReadOnlyList<string> stringValues => stringValues.ToArray(),
            // Ownership tokens are smallint. A short[] lets Npgsql infer smallint[], so the ownership
            // membership predicate compares smallint to smallint and keeps the index on
            // dms.Document.CreatedByOwnershipTokenId; widening the list would cost that index.
            IReadOnlyList<short> int16Values => int16Values.ToArray(),
            _ => throw new InvalidOperationException(
                $"{parameterDescription} '{parameterName}' requires an IReadOnlyList<long>, IReadOnlyList<short>, or IReadOnlyList<string> runtime value."
            ),
        };

    private static IReadOnlyList<long> RequireInt64List(
        object? value,
        string parameterName,
        string parameterDescription
    )
    {
        if (value is IReadOnlyList<long> int64Values)
        {
            return int64Values;
        }

        throw new InvalidOperationException(
            $"{parameterDescription} '{parameterName}' requires an IReadOnlyList<long> runtime value."
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

    private static void ConfigureMssqlStructuredParameter(
        DbParameter dbParameter,
        QuerySqlParameter querySqlParameter
    )
    {
        if (dbParameter is not SqlParameter sqlParameter)
        {
            throw new InvalidOperationException(
                "SQL Server structured query-parameter binding requires a SqlParameter instance."
            );
        }

        sqlParameter.SqlDbType = SqlDbType.Structured;
        sqlParameter.TypeName =
            querySqlParameter.Binding.StructuredTypeName
            ?? throw new InvalidOperationException(
                $"Structured binding for parameter '{querySqlParameter.ParameterName}' is missing a type name."
            );
    }
}
