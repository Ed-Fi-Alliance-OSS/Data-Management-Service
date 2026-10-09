// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The step 2.6 complete-walk measurement: whole reads of the set, page by page, through the
/// production parse step and handler over the production provider reader, at the cap and the
/// default page size. Provider reads are timed separately from the walk, so handler cost and
/// restarts are visible apart from read cost.
/// </summary>
/// <remarks>
/// A logical read follows the Configuration Service's restart rule: on <c>projection-changed</c> it
/// restarts without a cursor, at most <see cref="MaxWalkRestarts"/> times. The phases are a stable
/// set, exactly one committed change between two pages, the step 2.5 concurrent writers, and a single
/// writer paced at fixed intervals.
/// </remarks>
internal static class EducationOrganizationProjectionWalkMeasurement
{
    /// <summary>The Configuration Service default.</summary>
    private const int MaxWalkRestarts = 3;

    private const int DataStoreId = 1;
    private const int StableWalks = 3;
    private const int ChangeAfterPage = 5;

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    /// <summary>The database a walk reads and how.</summary>
    public sealed record Target(
        ApiIntegrationHarness Harness,
        string ConnectionString,
        MappingSet MappingSet,
        int Rows
    );

    /// <summary>The writes the phases apply, supplied by the scenario that seeded the hierarchy.</summary>
    /// <param name="ChangeOnceAsync">
    /// Commits one change to projected content through the API, and returns the failure's status and
    /// problem type, or <see langword="null"/> when the write succeeded.
    /// </param>
    /// <param name="RunWritersAsync">
    /// Runs the step 2.5 concurrent writer workload to completion and reports what it did.
    /// </param>
    public sealed record Writes(
        Func<int, Task<string?>> ChangeOnceAsync,
        Func<Task<WriterOutcome>> RunWritersAsync
    );

    /// <summary>What a write workload did: its operations, and its failures by status and problem type.</summary>
    public sealed record WriterOutcome(int Operations, IReadOnlyDictionary<string, int> Failures);

    public static async Task MeasureAsync(Target target, Writes writes)
    {
        var output = TestContext.Out;
        var settings = new EducationOrganizationProjectionSettings
        {
            MaxProjectionRows = Math.Max(
                EducationOrganizationProjectionSettings.MaxProjectionRowsDefault,
                target.Rows
            ),
        };
        int limit = settings.MaximumPageSize;
        var walker = new Walker(target, settings);

        await output.WriteLineAsync(
            $"MEASURE walk runtime dotnet={Environment.Version} configuration={Configuration} "
                + $"server_gc={GCSettings.IsServerGC} concurrent_gc={AppContext.GetData("System.GC.Concurrent") ?? "default(true)"} "
                + $"latency_mode={GCSettings.LatencyMode} processors={Environment.ProcessorCount} "
                + $"gc_available_mb={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_048_576}"
        );

        // Warm-up: the first walk pays JIT and pool start-up.
        (await walker.WalkAsync(limit, betweenPages: null))
            .Outcome.Should()
            .Be("Completed");

        List<WalkResult> stable = [];
        int[] collectionsBefore = [.. Enumerable.Range(0, 3).Select(GC.CollectionCount)];
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        for (int index = 0; index < StableWalks; index++)
        {
            WalkResult walk = await walker.WalkAsync(limit, betweenPages: null);
            walk.Outcome.Should().Be("Completed");
            stable.Add(walk);
        }

        await output.WriteLineAsync(
            $"MEASURE walk phase=stable rows={target.Rows} limit={limit} walks={stable.Count} "
                + $"pages={stable[0].Pages} walk_ms {Summary([.. stable.Select(walk => walk.Milliseconds)])} "
                + $"read_ms_per_walk {Summary([.. stable.Select(walk => walk.ReadMilliseconds)])} "
                + $"handler_ms_per_walk {Summary([.. stable.Select(walk => walk.Milliseconds - walk.ReadMilliseconds)])} "
                + $"page_read_ms {Summary([.. stable.SelectMany(walk => walk.PageReadMilliseconds)])} "
                + $"page_handler_ms {Summary([.. stable.SelectMany(walk => walk.PageHandlerMilliseconds)])} "
                + $"page_scope_ms {Summary([.. stable.SelectMany(walk => walk.PageScopeMilliseconds)])} "
                + $"gc_collections_gen0/1/2={string.Join("/", Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - collectionsBefore[generation]))} "
                + $"gc_pause_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F0} "
                + $"server_gc={System.Runtime.GCSettings.IsServerGC}"
        );

        // Exactly one committed change between two pages: the next page is refused and a restarted walk
        // completes on the changed set.
        int change = 0;
        WalkResult changed = await walker.WalkAsync(
            limit,
            betweenPages: async page =>
            {
                if (page == ChangeAfterPage)
                {
                    (await writes.ChangeOnceAsync(change++)).Should().BeNull();
                }
            }
        );
        changed.Outcome.Should().Be("Changed");
        changed.Pages.Should().Be(ChangeAfterPage);
        WalkResult restarted = await walker.WalkAsync(limit, betweenPages: null);
        restarted.Outcome.Should().Be("Completed");

        await output.WriteLineAsync(
            $"MEASURE walk phase=one_change change_after_page={ChangeAfterPage} "
                + $"refused_page={changed.Pages + 1} wasted_ms={changed.Milliseconds:F0} "
                + $"restart_pages={restarted.Pages} restart_ms={restarted.Milliseconds:F0} "
                + $"logical_read_ms={changed.Milliseconds + restarted.Milliseconds:F0}"
        );

        await output.WriteLineAsync(
            (await RunWithWritesAsync(walker, limit, writes.RunWritersAsync)).Describe("concurrent_writers")
        );

        foreach (int intervalMilliseconds in PacedIntervals())
        {
            int seconds = Setting("PROJECTION_MEASURE_PACED_SECONDS", 60);
            await output.WriteLineAsync(
                (
                    await RunWithWritesAsync(
                        walker,
                        limit,
                        () => PacedWritesAsync(writes, intervalMilliseconds, TimeSpan.FromSeconds(seconds))
                    )
                ).Describe($"paced_writes interval_ms={intervalMilliseconds} seconds={seconds}")
            );
        }
    }

    /// <summary>
    /// Runs logical reads back-to-back while the write workload runs. No logical read starts after
    /// the writers finish, but one in progress then runs to its end, restarts included; it is reported
    /// apart from those that finished while the writers were active.
    /// </summary>
    private static async Task<WritePhase> RunWithWritesAsync(
        Walker walker,
        int limit,
        Func<Task<WriterOutcome>> writesAsync
    )
    {
        var phase = new WritePhase();
        var clock = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();

        Task readLoop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var logical = Stopwatch.StartNew();
                int attempts = 0;

                while (true)
                {
                    attempts++;
                    WalkResult attempt = await walker.WalkAsync(limit, betweenPages: null);
                    phase.Attempts.Add((attempt, clock.Elapsed.TotalMilliseconds));

                    if (attempt.Outcome != "Changed" || attempts > MaxWalkRestarts)
                    {
                        phase.LogicalReads.Add(
                            new LogicalRead(
                                attempt.Outcome == "Changed" ? "RestartsExhausted" : attempt.Outcome,
                                attempts,
                                logical.Elapsed.TotalMilliseconds,
                                clock.Elapsed.TotalMilliseconds
                            )
                        );
                        break;
                    }
                }
            }
        });

        phase.Writers = await writesAsync();
        phase.WritersEndedMilliseconds = clock.Elapsed.TotalMilliseconds;
        await stop.CancelAsync();
        await readLoop;

        return phase;
    }

    private static async Task<WriterOutcome> PacedWritesAsync(
        Writes writes,
        int intervalMilliseconds,
        TimeSpan duration
    )
    {
        var elapsed = Stopwatch.StartNew();
        int index = 0;
        Dictionary<string, int> failures = [];

        while (elapsed.Elapsed < duration)
        {
            if (await writes.ChangeOnceAsync(10_000 + index++) is string failure)
            {
                failures[failure] = failures.GetValueOrDefault(failure) + 1;
            }

            await Task.Delay(intervalMilliseconds);
        }

        return new WriterOutcome(index, failures);
    }

    private static int[] PacedIntervals() =>
        [
            .. (Environment.GetEnvironmentVariable("PROJECTION_MEASURE_PACED_INTERVALS_MS") ?? "10000,2000")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)),
        ];

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static string Summary(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return "n=0";
        }

        var sorted = values.Order().ToList();
        return $"n={sorted.Count} min={sorted[0]:F1} p50={Percentile(sorted, 0.5):F1} p95={Percentile(sorted, 0.95):F1} max={sorted[^1]:F1}";
    }

    private static double Percentile(List<double> sorted, double percentile) =>
        sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(percentile * sorted.Count) - 1)];

    /// <summary>
    /// One attempt to read the whole set. <see cref="Pages"/> counts the pages answered with 200.
    /// </summary>
    private sealed record WalkResult(
        string Outcome,
        int Pages,
        double Milliseconds,
        double ReadMilliseconds,
        IReadOnlyList<double> PageReadMilliseconds,
        IReadOnlyList<double> PageHandlerMilliseconds,
        IReadOnlyList<double> PageScopeMilliseconds
    );

    /// <summary>One logical read: its outcome, attempts, duration, and when it finished in the phase.</summary>
    private sealed record LogicalRead(
        string Outcome,
        int Attempts,
        double Milliseconds,
        double EndedAtMilliseconds
    );

    private sealed class WritePhase
    {
        public ConcurrentBag<(WalkResult Attempt, double EndedAtMilliseconds)> Attempts { get; } = [];

        public ConcurrentBag<LogicalRead> LogicalReads { get; } = [];

        public WriterOutcome Writers { get; set; } = new(0, new Dictionary<string, int>());

        public double WritersEndedMilliseconds { get; set; }

        public string Describe(string name)
        {
            var during = LogicalReads
                .Where(read => read.EndedAtMilliseconds <= WritersEndedMilliseconds)
                .ToList();
            var after = LogicalReads
                .Where(read => read.EndedAtMilliseconds > WritersEndedMilliseconds)
                .ToList();
            var attemptsDuring = Attempts
                .Where(attempt => attempt.EndedAtMilliseconds <= WritersEndedMilliseconds)
                .Select(attempt => attempt.Attempt)
                .ToList();
            string failures =
                Writers.Failures.Count == 0
                    ? "none"
                    : string.Join(",", Writers.Failures.Select(failure => $"{failure.Key}x{failure.Value}"));

            return $"MEASURE walk phase={name} writers_active_ms={WritersEndedMilliseconds:F0} "
                + $"writer_ops={Writers.Operations} writer_failures={failures} "
                + $"reads_finished_during_writes={Outcomes(during)} "
                + $"reads_finished_after_writes={Outcomes(after)} "
                + $"attempts_during_writes={Outcomes(attemptsDuring.Select(attempt => attempt.Outcome))} "
                + $"completed_during_writes_attempts {Summary([.. during.Where(Completed).Select(read => (double)read.Attempts)])} "
                + $"completed_during_writes_ms {Summary([.. during.Where(Completed).Select(read => read.Milliseconds)])} "
                + $"pages_before_change_during_writes {Summary([.. attemptsDuring.Where(attempt => attempt.Outcome == "Changed").Select(attempt => (double)attempt.Pages)])} "
                + $"attempt_ms {Summary([.. Attempts.Select(attempt => attempt.Attempt.Milliseconds)])}";
        }

        private static bool Completed(LogicalRead read) => read.Outcome == "Completed";

        private static string Outcomes(IEnumerable<LogicalRead> reads) =>
            Outcomes(reads.Select(read => read.Outcome));

        private static string Outcomes(IEnumerable<string> outcomes)
        {
            string counted = string.Join(
                ",",
                outcomes
                    .GroupBy(outcome => outcome)
                    .OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}:{group.Count()}")
            );
            return counted.Length == 0 ? "none" : counted;
        }
    }

    /// <summary>
    /// Answers pages through the production parse step and handler. Each page has its own scope, as a
    /// request does, with the primary recorded as its effective target.
    /// </summary>
    private sealed class Walker(Target target, EducationOrganizationProjectionSettings settings)
    {
        private readonly PipelineProvider _pipeline = new([
            new ParseEducationOrganizationProjectionRequestMiddleware(
                settings,
                TimeProvider.System,
                NullLogger<ParseEducationOrganizationProjectionRequestMiddleware>.Instance
            ),
            new EducationOrganizationProjectionHandler(
                settings,
                TimeProvider.System,
                NoOpProjectionProcessingObserver.Instance,
                NullLogger<EducationOrganizationProjectionHandler>.Instance
            ),
        ]);

        public async Task<WalkResult> WalkAsync(int limit, Func<int, Task>? betweenPages)
        {
            var walk = Stopwatch.StartNew();
            List<double> pageRead = [];
            List<double> pageHandler = [];
            List<double> pageScope = [];
            string? cursor = null;
            int pages = 0;

            while (true)
            {
                var page = Stopwatch.StartNew();
                (IFrontendResponse response, double readMilliseconds, double pipelineMilliseconds) =
                    await PageAsync(limit, cursor);
                page.Stop();

                if (response.StatusCode != 200)
                {
                    string type = response.Body?["type"]?.GetValue<string>() ?? "none";
                    string outcome = type.EndsWith(":projection-changed", StringComparison.Ordinal)
                        ? "Changed"
                        : $"{response.StatusCode}:{type}";
                    return new WalkResult(
                        outcome,
                        pages,
                        walk.Elapsed.TotalMilliseconds,
                        pageRead.Sum(),
                        pageRead,
                        pageHandler,
                        pageScope
                    );
                }

                pages++;
                pageRead.Add(readMilliseconds);
                pageHandler.Add(pipelineMilliseconds - readMilliseconds);
                pageScope.Add(page.Elapsed.TotalMilliseconds - pipelineMilliseconds);
                cursor = response.Body!["nextCursor"]?.GetValue<string>();

                if (cursor is null)
                {
                    return new WalkResult(
                        "Completed",
                        pages,
                        walk.Elapsed.TotalMilliseconds,
                        pageRead.Sum(),
                        pageRead,
                        pageHandler,
                        pageScope
                    );
                }

                if (betweenPages is not null)
                {
                    // Excluded from the walk's page timings but not from its elapsed time.
                    await betweenPages(pages);
                }
            }
        }

        /// <summary>
        /// One page in its own scope. The pipeline time covers the parse step and the handler, the
        /// provider read included; scope creation and disposal are outside it.
        /// </summary>
        private async Task<(
            IFrontendResponse Response,
            double ReadMilliseconds,
            double PipelineMilliseconds
        )> PageAsync(int limit, string? cursor)
        {
            await using var scope = target.Harness.Services.CreateAsyncScope();
            var selection = scope.ServiceProvider.GetRequiredService<IDataStoreSelection>();
            selection.SetSelectedDataStore(
                new DataStore(DataStoreId, "measurement", "projection-walk", target.ConnectionString, new())
            );
            selection.SetEffectiveTarget(EffectiveDataStoreTarget.Primary(target.ConnectionString));

            var reader = new TimedReader(
                scope.ServiceProvider.GetRequiredService<IEducationOrganizationProjectionSetReader>()
            );

            Dictionary<string, string> query = new()
            {
                ["dataStoreId"] = DataStoreId.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            };
            if (cursor is not null)
            {
                query["cursor"] = cursor;
            }

            var requestInfo = new RequestInfo(
                new FrontendRequest(
                    Path: "/management/education-organizations",
                    Body: null,
                    Form: null,
                    Headers: [],
                    QueryParameters: query,
                    TraceId: new TraceId("projection-walk"),
                    RouteQualifiers: []
                ),
                RequestMethod.GET,
                new ReaderOverride(scope.ServiceProvider, reader),
                CancellationToken.None
            )
            {
                MappingSet = target.MappingSet,
            };

            var pipeline = Stopwatch.StartNew();
            await _pipeline.Run(requestInfo);
            return (requestInfo.FrontendResponse, reader.Milliseconds, pipeline.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Times the provider read the handler makes.</summary>
    private sealed class TimedReader(IEducationOrganizationProjectionSetReader inner)
        : IEducationOrganizationProjectionSetReader
    {
        public double Milliseconds { get; private set; }

        public async Task<EducationOrganizationProjectionSetResult> ReadSetAsync(
            EducationOrganizationProjectionSetReadRequest request,
            CancellationToken cancellationToken
        )
        {
            var timer = Stopwatch.StartNew();
            EducationOrganizationProjectionSetResult result = await inner.ReadSetAsync(
                request,
                cancellationToken
            );
            Milliseconds = timer.Elapsed.TotalMilliseconds;
            return result;
        }
    }

    /// <summary>The request scope, with the set reader replaced by its timed wrapper.</summary>
    private sealed class ReaderOverride(IServiceProvider scope, TimedReader reader) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEducationOrganizationProjectionSetReader)
                ? reader
                : scope.GetService(serviceType);
    }
}
