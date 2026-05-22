// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Shared MSSQL trigger emission helpers. Centralizes the null-safe column-diff
/// predicate used by both the relational-model stamping triggers and the core
/// descriptor stamping trigger, so a fix to one path applies to both.
/// </summary>
internal static class MssqlTriggerDiffEmitter
{
    /// <summary>
    /// Emits a NULL-safe inequality comparison for a single MSSQL column. For
    /// <see cref="ScalarKind.String"/> columns both operands are wrapped in
    /// <c>CAST(... AS varbinary(max))</c> so trailing-space-only and case-only
    /// changes — which the default <c>nvarchar</c> CI collation + ANSI padding
    /// would otherwise miss — are detected. Non-text types are unaffected by
    /// collation, so plain <c>&lt;&gt;</c> suffices.
    /// </summary>
    public static void EmitNullSafeNotEqual(
        SqlWriter writer,
        string leftAlias,
        string quotedColumn,
        string rightAlias,
        string rightQuotedColumn,
        ScalarKind? scalarKind
    )
    {
        bool needsCast = scalarKind == ScalarKind.String;

        writer.Append("(");
        if (needsCast)
        {
            writer.Append("CAST(");
        }
        writer.Append(leftAlias);
        writer.Append(".");
        writer.Append(quotedColumn);
        if (needsCast)
        {
            writer.Append(" AS varbinary(max))");
        }
        writer.Append(" <> ");
        if (needsCast)
        {
            writer.Append("CAST(");
        }
        writer.Append(rightAlias);
        writer.Append(".");
        writer.Append(rightQuotedColumn);
        if (needsCast)
        {
            writer.Append(" AS varbinary(max))");
        }
        writer.Append(" OR (");
        writer.Append(leftAlias);
        writer.Append(".");
        writer.Append(quotedColumn);
        writer.Append(" IS NULL AND ");
        writer.Append(rightAlias);
        writer.Append(".");
        writer.Append(rightQuotedColumn);
        writer.Append(" IS NOT NULL) OR (");
        writer.Append(leftAlias);
        writer.Append(".");
        writer.Append(quotedColumn);
        writer.Append(" IS NOT NULL AND ");
        writer.Append(rightAlias);
        writer.Append(".");
        writer.Append(rightQuotedColumn);
        writer.Append(" IS NULL))");
    }
}
