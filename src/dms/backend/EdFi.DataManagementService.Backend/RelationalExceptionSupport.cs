// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared SQL-error classification helpers for relational backend paths.
/// </summary>
internal static class RelationalExceptionSupport
{
    /// <summary>
    /// Detects foreign key constraint violations across Postgres (SqlState 23503)
    /// and SQL Server (error number 547).
    /// </summary>
    public static bool IsForeignKeyViolation(DbException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex.SqlState == "23503"
            || (
                ex is { HResult: var hr }
                && hr is unchecked((int)0x80131904)
                && ex.Message.Contains("547", StringComparison.Ordinal)
            );
    }
}
