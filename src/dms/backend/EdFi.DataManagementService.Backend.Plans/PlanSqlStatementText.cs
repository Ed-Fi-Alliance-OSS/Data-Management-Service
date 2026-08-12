// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Converts compiled plan SQL between its two forms: a terminated standalone statement, and an
/// embeddable relation body. Plan compilers emit terminated statements, so every consumer that nests
/// compiled SQL inside a common table expression or a derived table needs the terminator removed, and
/// every consumer that concatenates compiled SQL into a multi-statement batch needs one present.
/// </summary>
public static class PlanSqlStatementText
{
    /// <summary>
    /// Returns the supplied SQL terminated with a semicolon, so it is a complete statement when
    /// concatenated into a multi-statement batch. SQL that is already terminated is returned unchanged.
    /// </summary>
    /// <param name="sql">The compiled SQL.</param>
    public static string AsTerminatedStatement(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var trimmed = sql.AsSpan().TrimEnd();

        return trimmed.Length > 0 && trimmed[^1] == ';' ? sql : $"{trimmed};";
    }

    /// <summary>
    /// Returns the supplied SQL with its statement terminator and surrounding trailing whitespace
    /// removed, so it can be embedded as a relation body. A terminator is invalid inside
    /// <c>WITH ... AS (...)</c> and inside a <c>FROM (...)</c> derived table.
    /// </summary>
    /// <param name="sql">The compiled SQL.</param>
    public static string AsEmbeddableBody(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var trimmed = sql.AsSpan().TrimEnd();

        if (trimmed.Length > 0 && trimmed[^1] == ';')
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.ToString();
    }
}
