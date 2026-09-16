// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed record CdcSqlServerContainerState(string Status, int ExitCode, bool OomKilled)
{
    public CdcSqlServerStartupLogEvidence Logs { get; init; } =
        CdcSqlServerStartupLogEvidence.Empty("NotObserved");

    internal bool IsLsaInitializationTimeout =>
        Status == "exited"
        && ExitCode == 1
        && !OomKilled
        && Logs.State == "Observed"
        && !Logs.Truncated
        && Logs.FatalMessage
        && !Logs.MemoryMessage
        && !Logs.MappingMessage
        && Logs.LsaInitializationTimeout
        && Logs.FatalReasonCodes.SequenceEqual([6U])
        && Logs.LastErrnos.SequenceEqual([2])
        && Logs.SqlErrorNumbers.Count == 0
        && Logs.Signals.Count == 0;
}

internal sealed record CdcSqlServerStartupBudgets(
    TimeSpan Attempt,
    TimeSpan Inspection,
    TimeSpan Removal,
    TimeSpan Overall
)
{
    public static CdcSqlServerStartupBudgets Default { get; } =
        new(
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(200)
        );
}

// Applies only to a fresh fixture provider, before database provisioning or any scenario work.
internal static class CdcSqlServerFixtureStartup
{
    internal static async Task RunAsync(
        Func<CancellationToken, Task> startAndWait,
        Func<CancellationToken, Task<CdcSqlServerContainerState>> inspectFailure,
        Func<int, CdcSqlServerContainerState, bool, CancellationToken, Task> retainFailure,
        Func<CancellationToken, Task> removeContainer,
        bool keepContainer,
        CancellationToken cancellationToken,
        CdcSqlServerStartupBudgets budgets
    )
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(budgets.Overall);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            overall.Token.ThrowIfCancellationRequested();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            startup.CancelAfter(budgets.Attempt);
            try
            {
                await startAndWait(startup.Token);
                startup.Token.ThrowIfCancellationRequested();
                return;
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or OperationCanceledException)
            {
                // Caller cancellation and the total deadline never authorize another attempt.
                cancellationToken.ThrowIfCancellationRequested();
                overall.Token.ThrowIfCancellationRequested();
                bool recorded = false;
                bool recreate = false;
                using var inspection = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                inspection.CancelAfter(budgets.Inspection);
                try
                {
                    CdcSqlServerContainerState state = await inspectFailure(inspection.Token);
                    inspection.Token.ThrowIfCancellationRequested();
                    recreate = attempt == 1 && !keepContainer && state.IsLsaInitializationTimeout;
                    await retainFailure(attempt, state, recreate, inspection.Token);
                    inspection.Token.ThrowIfCancellationRequested();
                    recorded = true;
                }
                catch (Exception)
                {
                    // Missing diagnostics cannot turn an unknown startup failure into a retry.
                }
                cancellationToken.ThrowIfCancellationRequested();
                overall.Token.ThrowIfCancellationRequested();
                if (!recorded || !recreate)
                {
                    throw;
                }

                using var removal = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                removal.CancelAfter(budgets.Removal);
                await removeContainer(removal.Token);
                cancellationToken.ThrowIfCancellationRequested();
                removal.Token.ThrowIfCancellationRequested();
            }
        }
    }
}
