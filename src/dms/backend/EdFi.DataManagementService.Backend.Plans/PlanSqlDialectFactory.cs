// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Factory for constructing dialect-specific helpers used by plan/query SQL compilers.
/// </summary>
internal static class PlanSqlDialectFactory
{
    /// <summary>
    /// Creates a plan/query SQL dialect helper for the supplied backend dialect.
    /// </summary>
    /// <param name="dialect">The backend SQL dialect.</param>
    /// <returns>The dialect-specific plan/query SQL helper.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the dialect is not supported.</exception>
    public static IPlanSqlDialect Create(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new PgsqlPlanDialect(),
            SqlDialect.Mssql => new MssqlPlanDialect(),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }
}
