// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed record CdcSqlServerJobState(
    Guid JobId,
    int LastHistoryId,
    int CompletedExecutions,
    bool Running,
    int CaptureHistoryCount = 0
);

internal sealed record CdcSqlServerJobExecution(Guid JobId, int InstanceId, int RunStatus);

internal sealed record CdcSqlServerCompletedJobEvidence(
    int HistoryBoundary,
    bool Started,
    CdcSqlServerJobExecution Execution
);

/// <summary>A completed execution of the fixture's exact cleanup job, beyond its pre-start history.</summary>
internal static class CdcSqlServerCompletedJob
{
    internal static async Task<CdcSqlServerCompletedJobEvidence> RunAsync(
        CdcSqlServerJobState initial,
        Func<CancellationToken, Task<CdcSqlServerJobState>> readState,
        Func<Guid, CancellationToken, Task<bool>> start,
        Func<Guid, int, CancellationToken, Task<CdcSqlServerJobExecution>> readCompletion,
        CancellationToken token,
        TimeProvider clock = null!
    )
    {
        clock ??= TimeProvider.System;
        using var timer = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
        try
        {
            var before = initial;
            while (before.Running)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, deadline.Token);
                before = await readState(deadline.Token);
                if (before.JobId != initial.JobId)
                {
                    throw new InvalidOperationException("CDC fixture cleanup job identity changed.");
                }
            }
            deadline.Token.ThrowIfCancellationRequested();
            bool started = await start(before.JobId, deadline.Token);
            while (true)
            {
                var completion = await readCompletion(before.JobId, before.LastHistoryId, deadline.Token);
                if (completion.JobId != before.JobId)
                {
                    throw new InvalidOperationException(
                        "CDC fixture cleanup history belongs to another job."
                    );
                }
                if (completion.InstanceId != 0)
                {
                    if (completion.InstanceId <= before.LastHistoryId || completion.RunStatus != 1)
                    {
                        throw new InvalidOperationException(
                            "CDC fixture cleanup execution did not complete successfully beyond its history boundary."
                        );
                    }
                    return new(before.LastHistoryId, started, completion);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, deadline.Token);
            }
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested && timer.IsCancellationRequested)
        {
            throw new TimeoutException("CDC fixture cleanup job completion deadline expired.");
        }
    }
}
