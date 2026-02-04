// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Factory for creating SQL dialect instances.
/// </summary>
public static class SqlDialectFactory
{
    /// <summary>
    /// Creates the appropriate <see cref="ISqlDialect"/> for the specified dialect enum.
    /// </summary>
    /// <param name="dialect">The dialect to create.</param>
    /// <returns>A configured dialect instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the dialect is not supported.</exception>
    public static ISqlDialect Create(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDialect(new PgsqlDialectRules()),
            SqlDialect.Mssql => new MssqlDialect(new MssqlDialectRules()),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    /// <summary>
    /// Creates the appropriate <see cref="ISqlDialect"/> using an existing
    /// <see cref="ISqlDialectRules"/> instance.
    /// </summary>
    /// <param name="rules">The dialect rules to compose over.</param>
    /// <returns>A configured dialect instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when rules is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the dialect is not supported.</exception>
    public static ISqlDialect Create(ISqlDialectRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return rules.Dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDialect(rules),
            SqlDialect.Mssql => new MssqlDialect(rules),
            _ => throw new ArgumentOutOfRangeException(
                nameof(rules),
                rules.Dialect,
                "Unsupported SQL dialect."
            ),
        };
    }
}
