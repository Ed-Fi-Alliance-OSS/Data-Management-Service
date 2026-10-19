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
        var callerFile = Normalize(Path.GetFileName(diagnostic.Context.CallerFilePath.Replace('\\', '/')));
        var caller = Normalize(diagnostic.Context.CallerMemberName);
        var contention =
            diagnostic.AdmittedAtBatchStart > 1 || diagnostic.AdmittedAtTimeout > 1
                ? "suspected provisioning contention."
                : "possible provisioning contention; no overlap observed in sampled admitted operations.";
        var limit =
            diagnostic.MaximumConcurrency == 0
                ? "unlimited"
                : diagnostic.MaximumConcurrency.ToString(CultureInfo.InvariantCulture);
        var remedy = diagnostic.MaximumConcurrency switch
        {
            1 =>
                "Already limited to 1 in this process; investigate standalone DDL reapplication, other processes, and SQL Server waits.",
            _ =>
                $"To reduce in-process overlap, set {MssqlGeneratedDdlProvisioningGate.SettingName}=1 and restart the test process.",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"MSSQL generated-DDL provisioning timeout: {contention} fixture={fixture} contextOrigin={callerFile}:{caller}:{diagnostic.Context.CallerLineNumber} database={database} strategy={strategy} batch={diagnostic.BatchOrdinal}/{diagnostic.BatchCount} elapsedSeconds={diagnostic.BatchElapsed.TotalSeconds:0.000} commandTimeoutSeconds={diagnostic.CommandTimeoutSeconds} admissionWaitSeconds={diagnostic.AdmissionWaitElapsed.TotalSeconds:0.000} limit={limit} admittedAtBatchStart={diagnostic.AdmittedAtBatchStart} admittedAtTimeout={diagnostic.AdmittedAtTimeout}. Context origin is the recorded provisioning caller; shared baselines retain their first registration. Admitted counts are process-wide operations (including this operation and excluding queued entrants), not executing SQL or server-wide counts; samples do not prove an engine wait. Standalone DDL reapplication is outside this gate. {remedy}"
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
