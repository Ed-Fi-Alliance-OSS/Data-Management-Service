// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

internal sealed record MssqlGeneratedDdlTimeoutDiagnostic(
    MssqlProvisioningTimingContext Context,
    string DatabaseName,
    int BatchOrdinal,
    int BatchCount,
    int CommandTimeoutSeconds,
    TimeSpan BatchElapsed,
    TimeSpan AdmissionWaitElapsed,
    int MaximumConcurrency,
    int AdmittedAtBatchStart,
    int AdmittedAtTimeout
);

internal static class MssqlGeneratedDdlProvisioningDiagnostics
{
    public static bool IsProvisioningCommandTimeout(Exception exception, bool isAdmitted) =>
        isAdmitted && exception is SqlException { Number: -2 };

    public static string Format(MssqlGeneratedDdlTimeoutDiagnostic diagnostic)
    {
        var fixtureSignature = Normalize(diagnostic.Context.FixtureSignature);
        var fixture = string.IsNullOrWhiteSpace(fixtureSignature)
            ? Normalize(diagnostic.Context.GeneratedDdlHash)
            : fixtureSignature;
        var database = Normalize(diagnostic.DatabaseName);
        var strategy = Normalize(diagnostic.Context.LeaseStrategy);
        var limit =
            diagnostic.MaximumConcurrency == 0
                ? "unlimited"
                : diagnostic.MaximumConcurrency.ToString(CultureInfo.InvariantCulture);
        var remedy = diagnostic.MaximumConcurrency switch
        {
            1 => "Already limited to 1 in this process; investigate other processes/server waits.",
            0 =>
                "To reduce in-process overlap, set MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY=1 before starting the test process, then restart the test process.",
            _ =>
                "To reduce in-process overlap, set MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY=1 before starting the test process, then restart the test process.",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"MSSQL generated-DDL provisioning timeout: suspected provisioning contention. fixture={fixture} database={database} strategy={strategy} batch={diagnostic.BatchOrdinal}/{diagnostic.BatchCount} elapsedSeconds={diagnostic.BatchElapsed.TotalSeconds:0.000} commandTimeoutSeconds={diagnostic.CommandTimeoutSeconds} admissionWaitSeconds={diagnostic.AdmissionWaitElapsed.TotalSeconds:0.000} limit={limit} admittedAtBatchStart={diagnostic.AdmittedAtBatchStart} admittedAtTimeout={diagnostic.AdmittedAtTimeout}. Admitted counts are process-wide operations (including this operation and excluding queued entrants), not executing SQL or server-wide counts; samples do not prove an engine wait. {remedy}"
        );
    }

    public static void TryWrite(TextWriter writer, Func<string> format)
    {
        try
        {
            writer.WriteLine(format());
        }
        catch
        {
            // Diagnostics must never replace the provider failure or interrupt cleanup.
        }
    }

    private static string Normalize(string value) =>
        string.Concat(value.Select(character => char.IsControl(character) ? ' ' : character)).Trim();
}
