// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

/// <summary>
/// The SQL Server half of spec D-16a, and the only reader of <see cref="SqlException"/> in the job
/// subsystem. It keeps the error number of the first SQL Server error in the exception chain, such as
/// <c>2627</c> or <c>2601</c> for a unique violation or <c>1222</c> for a lock timeout, and never reads the
/// message, which can carry row and payload values.
/// </summary>
public static class MssqlJobDiagnostics
{
    public static JobFailureDiagnostic From(Exception exception, string operation) =>
        JobDiagnostics.From(exception, operation, ErrorNumberOf(exception));

    private static string? ErrorNumberOf(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql.Number.ToString(CultureInfo.InvariantCulture);
            }
        }
        return null;
    }
}
