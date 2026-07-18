// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Diagnostics;

/// <summary>
/// Configuration for the DMS-1236 request timing instrumentation, bound from the
/// "RequestTimings" configuration section by the AspNetCore frontend. Defaults are
/// chosen so the instrumentation is fully active without any configuration changes.
/// </summary>
public sealed class RequestTimingOptions
{
    public const string SectionName = "RequestTimings";

    /// <summary>
    /// Master switch. When false, no per-request timing context is created and the
    /// Npgsql activity listener is not started.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Requests with a total in-app time above this threshold log their full phase
    /// breakdown at Warning level. Set to 0 to disable slow-request detail logging.
    /// </summary>
    public double SlowRequestThresholdMs { get; set; } = 100;

    /// <summary>
    /// When greater than 0, every Nth request logs its full phase breakdown at
    /// Information level, regardless of duration. 0 disables sampling.
    /// </summary>
    public int DetailSampleEveryN { get; set; }

    /// <summary>
    /// Interval for the runtime stats log line (CPU, GC, thread pool, allocations).
    /// 0 disables runtime stats logging.
    /// </summary>
    public int RuntimeStatsIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Interval for the aggregated phase summary log line. 0 disables summary logging
    /// (the /instrumentation endpoint remains available).
    /// </summary>
    public int SummaryIntervalSeconds { get; set; } = 60;
}
