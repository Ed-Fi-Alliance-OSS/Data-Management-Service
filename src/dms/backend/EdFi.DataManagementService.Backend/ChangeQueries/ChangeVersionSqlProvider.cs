// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.ChangeQueries;

internal static class ChangeVersionSqlProvider
{
    public static RelationalCommand NewestChangeVersionCommand(SqlDialect dialect) =>
        new(NewestChangeVersionCommandText(dialect));

    private static string NewestChangeVersionCommandText(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => "SELECT \"dms\".\"GetMaxChangeVersion\"() AS \"NewestChangeVersion\"",
            SqlDialect.Mssql => "SELECT [dms].[GetMaxChangeVersion]() AS [NewestChangeVersion]",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
}
