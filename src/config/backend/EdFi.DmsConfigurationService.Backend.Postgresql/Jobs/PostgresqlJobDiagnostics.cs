// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;

/// <summary>
/// The PostgreSQL half of spec D-16a, and the only reader of <see cref="PostgresException"/> in the job
/// subsystem. It keeps the <c>SqlState</c> of the first PostgreSQL error in the exception chain, such as
/// <c>23505</c> for a unique violation or <c>55P03</c> for a lock timeout, and never reads the message,
/// detail, hint, or any other field that can carry row or payload values.
/// </summary>
public static class PostgresqlJobDiagnostics
{
    public static JobFailureDiagnostic From(Exception exception, string operation) =>
        JobDiagnostics.From(exception, operation, SqlStateOf(exception));

    private static string? SqlStateOf(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return IsSqlState(postgres.SqlState) ? postgres.SqlState : null;
            }
        }
        return null;
    }

    // A SqlState is five ASCII digits or upper-case letters; any other text is not kept.
    private static bool IsSqlState(string? sqlState) =>
        sqlState is { Length: 5 }
        && sqlState.All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character));
}
