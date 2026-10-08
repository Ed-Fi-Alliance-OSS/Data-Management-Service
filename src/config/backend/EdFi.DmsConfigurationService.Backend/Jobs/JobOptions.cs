// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The <c>JobSettings</c> section (spec §6.1, D-15). The defaults are the values approved at step 0.2 and confirmed at
/// step 2.11; <see cref="JobOptionsValidator"/> enforces the §6.3 bounds at startup.
/// </summary>
public sealed class JobOptions
{
    public const string SectionName = "JobSettings";

    /// <summary>Registers the worker hosted service, which claims and runs jobs.</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>Registers the schedule dispatcher hosted service, which materializes due schedules.</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>Registers the retention hosted service, which deletes old finished jobs.</summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>How often an idle worker or dispatcher polls.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a claim or renewal owns a job before another replica may reclaim it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often a running job renews its lease.</summary>
    public TimeSpan RenewalInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The upper bound of a fenced consumer write's deadline (D-5).</summary>
    public TimeSpan FenceTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Executions a job may start before it ends as <c>Error(AttemptsExhausted)</c>.</summary>
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan RetryBackoffBase { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryBackoffMaximum { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Jobs one replica runs at the same time.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>How long a finished job is kept before retention deletes it.</summary>
    public TimeSpan FinishedJobRetention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    public int RetentionBatchSize { get; set; } = 500;

    /// <summary>
    /// The deadline of one whole renewal or outcome write (A3): half the renewal interval. It is derived, not
    /// configured.
    /// </summary>
    public TimeSpan RenewalTimeout => RenewalInterval / 2;

    /// <summary>The configured timings the lease repositories and fence factories use.</summary>
    public JobLeaseTimings LeaseTimings => new(RenewalTimeout, FenceTimeout);
}
