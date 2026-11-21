// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Logs once at startup, on an instance registered without the job worker, that queued jobs do not run here. Refresh
/// requests and schedules still enqueue jobs on such an instance, so the warning says where they run instead.
/// </summary>
public sealed class JobWorkersDisabledNotice(ILogger<JobWorkersDisabledNotice> logger) : IHostedService
{
    public static readonly EventId JobWorkersDisabled = new(1441_11, "JobWorkersDisabled");

    public const string Message =
        "Job workers are disabled on this instance; queued jobs run only on instances with JobSettings:WorkerEnabled=true.";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(JobWorkersDisabled, Message);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
