// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Shared constructor-validation helpers for executor-facing plan contracts.
/// </summary>
internal static class PlanContractArgumentValidator
{
    /// <summary>
    /// Validates a required reference argument and returns it for assignment.
    /// </summary>
    /// <typeparam name="T">Reference type.</typeparam>
    /// <param name="value">Candidate value.</param>
    /// <param name="parameterName">Argument name for exception metadata.</param>
    /// <returns>The non-null value.</returns>
    public static T RequireNotNull<T>(T? value, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        return value;
    }

    /// <summary>
    /// Materializes a required enumerable argument into an immutable array.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="values">Values to materialize.</param>
    /// <param name="parameterName">Argument name for exception metadata.</param>
    /// <returns>Values in original enumeration order.</returns>
    public static ImmutableArray<T> RequireImmutableArray<T>(IEnumerable<T> values, string parameterName) =>
        PlanContractCollectionCloner.ToImmutableArray(values, parameterName);

    /// <summary>
    /// Validates and materializes optional SQL-parameter inventories.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="sql">SQL companion value that controls whether the inventory is required.</param>
    /// <param name="sqlParameterName">SQL argument name for exception messaging.</param>
    /// <param name="values">Optional inventory values.</param>
    /// <param name="valuesParameterName">Inventory argument name for exception messaging.</param>
    /// <returns>Null when SQL is null; otherwise a materialized immutable array.</returns>
    public static ImmutableArray<T>? RequireImmutableArrayWhenSqlPresent<T>(
        string? sql,
        string sqlParameterName,
        IEnumerable<T>? values,
        string valuesParameterName
    )
    {
        if (sql is null)
        {
            if (values is not null)
            {
                throw new ArgumentException(
                    $"{valuesParameterName} must be null when {sqlParameterName} is null.",
                    valuesParameterName
                );
            }

            return null;
        }

        return PlanContractCollectionCloner.ToImmutableArray(
            values ?? throw new ArgumentNullException(valuesParameterName),
            valuesParameterName
        );
    }
}
