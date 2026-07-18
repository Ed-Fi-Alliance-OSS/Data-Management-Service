// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text;
using EdFi.DataManagementService.Core.External.Diagnostics;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// DMS-1236 instrumentation reporter. Periodically logs (1) .NET runtime health stats
/// (CPU, GC, thread pool, allocations, lock contention) to correlate latency with
/// runtime-level effects, and (2) a compact summary of the aggregated request phase
/// timings. The full aggregate is always available from GET /instrumentation.
/// </summary>
internal sealed class RequestTimingReportingService(
    ILogger<RequestTimingReportingService> logger,
    IOptions<RequestTimingOptions> options
) : BackgroundService
{
    private const int SummaryTopPhaseCount = 45;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RequestTimingOptions timingOptions = options.Value;
        List<Task> loops = [];

        if (timingOptions.RuntimeStatsIntervalSeconds > 0)
        {
            loops.Add(RuntimeStatsLoop(timingOptions.RuntimeStatsIntervalSeconds, stoppingToken));
        }

        if (timingOptions.SummaryIntervalSeconds > 0)
        {
            loops.Add(SummaryLoop(timingOptions.SummaryIntervalSeconds, stoppingToken));
        }

        return loops.Count == 0 ? Task.CompletedTask : Task.WhenAll(loops);
    }

    private async Task RuntimeStatsLoop(int intervalSeconds, CancellationToken stoppingToken)
    {
        RuntimeStatsSample previous = RuntimeStatsSample.Capture();
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                RuntimeStatsSample current = RuntimeStatsSample.Capture();
                LogRuntimeStats(previous, current);
                previous = current;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task SummaryLoop(int intervalSeconds, CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                LogSummary();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown; log a final summary so short runs still capture totals.
            LogSummary();
        }
    }

    private void LogRuntimeStats(RuntimeStatsSample previous, RuntimeStatsSample current)
    {
        double elapsedSeconds = (current.Timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        double cpuPercent =
            (current.TotalCpu - previous.TotalCpu).TotalMilliseconds
            / (elapsedSeconds * 1000.0 * Environment.ProcessorCount)
            * 100.0;
        double allocMbPerSecond =
            (current.AllocatedBytes - previous.AllocatedBytes) / (1024.0 * 1024.0) / elapsedSeconds;

        logger.LogInformation(
            "RuntimeStats: cpu={CpuPercent}% tpThreads={ThreadCount} tpQueue={PendingWorkItems} "
                + "tpCompletedPerSec={CompletedPerSec} gc0={Gen0} gc1={Gen1} gc2={Gen2} "
                + "gcPauseMs={GcPauseMs} allocMBPerSec={AllocMbPerSec} lockContentions={LockContentions} "
                + "workingSetMB={WorkingSetMb} inFlightRequests={InFlight}",
            Math.Round(cpuPercent, 1),
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            Math.Round((current.CompletedWorkItems - previous.CompletedWorkItems) / elapsedSeconds, 0),
            current.Gen0Collections - previous.Gen0Collections,
            current.Gen1Collections - previous.Gen1Collections,
            current.Gen2Collections - previous.Gen2Collections,
            Math.Round((current.GcPause - previous.GcPause).TotalMilliseconds, 1),
            Math.Round(allocMbPerSecond, 1),
            current.LockContentions - previous.LockContentions,
            Math.Round(current.WorkingSetBytes / (1024.0 * 1024.0), 0),
            RequestTimingRegistry.Snapshot().InFlightRequests
        );
    }

    private void LogSummary()
    {
        RegistrySnapshot snapshot = RequestTimingRegistry.Snapshot();
        if (snapshot.Phases.Count == 0 || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        StringBuilder table = new(4096);
        table
            .AppendLine()
            .AppendLine("phase | count | totalMs | meanMs | p50Ms | p95Ms | p99Ms | maxMs");

        int rows = Math.Min(SummaryTopPhaseCount, snapshot.Phases.Count);
        for (int i = 0; i < rows; i++)
        {
            PhaseStats stats = snapshot.Phases[i];
            table
                .Append(stats.Phase)
                .Append(" | ")
                .Append(stats.Count)
                .Append(" | ")
                .Append(stats.TotalMs)
                .Append(" | ")
                .Append(stats.MeanMs)
                .Append(" | ")
                .Append(stats.P50Ms)
                .Append(" | ")
                .Append(stats.P95Ms)
                .Append(" | ")
                .Append(stats.P99Ms)
                .Append(" | ")
                .Append(stats.MaxMs)
                .AppendLine();
        }

        logger.LogInformation(
            "RequestTimingSummary: windowSeconds={WindowSeconds} requests={Requests} "
                + "phaseCount={PhaseCount} top{Rows} by total time:{Table}",
            snapshot.WindowSeconds,
            snapshot.Requests,
            snapshot.Phases.Count,
            rows,
            table.ToString()
        );
    }

    private readonly record struct RuntimeStatsSample(
        long Timestamp,
        TimeSpan TotalCpu,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        TimeSpan GcPause,
        long AllocatedBytes,
        long LockContentions,
        long CompletedWorkItems,
        long WorkingSetBytes
    )
    {
        public static RuntimeStatsSample Capture()
        {
            using Process process = Process.GetCurrentProcess();
            return new RuntimeStatsSample(
                Stopwatch.GetTimestamp(),
                process.TotalProcessorTime,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                GC.GetTotalPauseDuration(),
                GC.GetTotalAllocatedBytes(),
                Monitor.LockContentionCount,
                ThreadPool.CompletedWorkItemCount,
                Environment.WorkingSet
            );
        }
    }
}
