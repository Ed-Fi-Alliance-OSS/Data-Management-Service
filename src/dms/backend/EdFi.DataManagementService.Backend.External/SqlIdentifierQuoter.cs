// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Quotes SQL identifiers for a specific dialect, escaping reserved quoting characters as required.
/// </summary>
public static class SqlIdentifierQuoter
{
    /// <summary>
    /// Quotes a raw identifier (schema, table, column, etc.) for the given dialect.
    /// </summary>
    /// <param name="dialect">The SQL dialect to target.</param>
    /// <param name="identifier">The raw identifier to quote.</param>
    /// <returns>The quoted identifier string.</returns>
    public static string QuoteIdentifier(SqlDialect dialect, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        return dialect switch
        {
            SqlDialect.Pgsql => $"\"{identifier.Replace("\"", "\"\"")}\"",
            SqlDialect.Mssql => $"[{identifier.Replace("]", "]]")}]",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    /// <summary>
    /// Quotes a schema identifier for the given dialect.
    /// </summary>
    public static string QuoteIdentifier(SqlDialect dialect, DbSchemaName schema)
    {
        return QuoteIdentifier(dialect, schema.Value);
    }

    /// <summary>
    /// Quotes a column identifier for the given dialect.
    /// </summary>
    public static string QuoteIdentifier(SqlDialect dialect, DbColumnName column)
    {
        return QuoteIdentifier(dialect, column.Value);
    }

    /// <summary>
    /// Quotes an index identifier for the given dialect.
    /// </summary>
    public static string QuoteIdentifier(SqlDialect dialect, DbIndexName index)
    {
        return QuoteIdentifier(dialect, index.Value);
    }

    /// <summary>
    /// Quotes a trigger identifier for the given dialect.
    /// </summary>
    public static string QuoteIdentifier(SqlDialect dialect, DbTriggerName trigger)
    {
        return QuoteIdentifier(dialect, trigger.Value);
    }

    /// <summary>
    /// Quotes and qualifies a table name as <c>schema.table</c> for the given dialect.
    /// </summary>
    public static string QuoteTableName(SqlDialect dialect, DbTableName table)
    {
        return $"{QuoteIdentifier(dialect, table.Schema)}.{QuoteIdentifier(dialect, table.Name)}";
    }
}
