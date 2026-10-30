// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits the SQL Server transaction-stable stamp clock used by every owner stamp. PostgreSQL's
/// <c>now()</c> is the transaction start time, so every stamp in one transaction shares a value; SQL
/// Server's <c>sysutcdatetime()</c> is read per statement. A creating write inserts the root and then
/// its child and <c>_ext</c> rows as later statements, and each of those restamps the root, so a
/// per-statement clock would leave <c>ContentLastModifiedAt</c> later than <c>ContentCreatedAt</c>.
/// The first stamp in a transaction stores its clock reading in the session context, keyed by
/// <c>CURRENT_TRANSACTION_ID()</c>, and later stamps in the same transaction reuse it. A new
/// transaction never matches the stored key, so it reads the clock again.
/// </summary>
internal static class MssqlStampClock
{
    /// <summary>
    /// The variable the emitted block declares and owner stamps assign from.
    /// </summary>
    public const string Variable = "@stampedAt";

    public static void Emit(SqlWriter writer)
    {
        writer.AppendLine("DECLARE @stampTransactionId bigint = CURRENT_TRANSACTION_ID();");
        writer.AppendLine($"DECLARE {Variable} datetime2(7) = CASE");
        using (writer.Indent())
        {
            writer.AppendLine(
                "WHEN CAST(SESSION_CONTEXT(N'dms.StampTransactionId') AS bigint) = @stampTransactionId"
            );
            writer.AppendLine("THEN CAST(SESSION_CONTEXT(N'dms.StampedAt') AS datetime2(7))");
        }
        writer.AppendLine("END;");
        writer.AppendLine($"IF {Variable} IS NULL");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine($"SET {Variable} = sysutcdatetime();");
            // Procedure arguments cannot be function calls, hence the variables.
            writer.AppendLine(
                $"EXEC sys.sp_set_session_context @key = N'dms.StampedAt', @value = {Variable};"
            );
            writer.AppendLine(
                "EXEC sys.sp_set_session_context @key = N'dms.StampTransactionId', @value = @stampTransactionId;"
            );
        }
        writer.AppendLine("END");
    }
}
