// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;

namespace EdFi.DataManagementService.Backend.External;

public static class SqlIdentifierQuoter
{
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

    public static string QuoteIdentifier(SqlDialect dialect, DbSchemaName schema)
    {
        return QuoteIdentifier(dialect, schema.Value);
    }

    public static string QuoteIdentifier(SqlDialect dialect, DbColumnName column)
    {
        return QuoteIdentifier(dialect, column.Value);
    }

    public static string QuoteIdentifier(SqlDialect dialect, DbIndexName index)
    {
        return QuoteIdentifier(dialect, index.Value);
    }

    public static string QuoteIdentifier(SqlDialect dialect, DbTriggerName trigger)
    {
        return QuoteIdentifier(dialect, trigger.Value);
    }

    public static string QuoteTableName(SqlDialect dialect, DbTableName table)
    {
        return $"{QuoteIdentifier(dialect, table.Schema)}.{QuoteIdentifier(dialect, table.Name)}";
    }
}
