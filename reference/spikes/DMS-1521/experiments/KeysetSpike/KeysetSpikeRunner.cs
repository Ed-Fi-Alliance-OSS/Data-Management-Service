// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace KeysetSpike;

/// <summary>DMS1521_* and PERF_* settings for one fixture run, read once from the environment.</summary>
public sealed record KeysetSpikeSettings(
    string ResultsDirectory,
    string VariantLabel,
    string KeysetVariant,
    string SequenceId,
    PerfFixtureKind Fixture,
    PerfDescriptorFixtureKind DescriptorFixture,
    long DeepOffset,
    int WarmupIterations,
    int MeasuredIterations,
    IReadOnlyList<int> PageSizes,
    IReadOnlyList<int> ReplayConcurrency,
    int ReplayWarmupPerWorker,
    int ReplayIterationsPerWorker,
    bool Counters,
    IReadOnlyList<int> CounterConcurrency,
    bool Calibrate,
    bool Parity,
    PerfEvidenceRunSettings Evidence
)
{
    public static KeysetSpikeSettings FromEnvironment()
    {
        static string Read(string name, string? fallback = null) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value.Trim()
                : fallback ?? throw new InvalidOperationException($"{name} is required.");

        static int ReadInt(string name, int fallback) =>
            int.Parse(
                Read(name, fallback.ToString(CultureInfo.InvariantCulture)),
                CultureInfo.InvariantCulture
            );

        static IReadOnlyList<int> ReadList(string name, string fallback) =>
            [
                .. Read(name, fallback)
                    .Split(',')
                    .Select(item => int.Parse(item.Trim(), CultureInfo.InvariantCulture)),
            ];

        return new KeysetSpikeSettings(
            Read(
                "DMS1521_RESULTS_DIR",
                Environment.GetEnvironmentVariable(PerfEnvironmentVariables.ResultsDirectory)
            ),
            Read("DMS1521_VARIANT_LABEL"),
            Read("DMS1521_KEYSET_VARIANT", "baseline"),
            Read("DMS1521_SEQUENCE_ID", "adhoc"),
            PerfFixtureKind.FindById(Read(PerfEnvironmentVariables.Fixture, "primary-500k"))
                ?? throw new InvalidOperationException("Unknown PERF_FIXTURE."),
            PerfDescriptorFixtureKind.FindById(
                Read(PerfEnvironmentVariables.DescriptorFixture, "descriptors-25k")
            ) ?? throw new InvalidOperationException("Unknown PERF_DESCRIPTOR_FIXTURE."),
            long.Parse(Read(PerfEnvironmentVariables.DeepOffset, "450000"), CultureInfo.InvariantCulture),
            ReadInt(PerfEnvironmentVariables.WarmupIterations, 10),
            ReadInt(PerfEnvironmentVariables.MeasuredIterations, 60),
            ReadList("DMS1521_PAGE_SIZES", "25,500"),
            ReadList("DMS1521_REPLAY_CONCURRENCY", "1,8,32"),
            ReadInt("DMS1521_REPLAY_WARMUP", 3),
            ReadInt("DMS1521_REPLAY_ITERATIONS", 20),
            Read("DMS1521_COUNTERS", "true") == "true",
            ReadList("DMS1521_COUNTER_CONCURRENCY", "1"),
            Read("DMS1521_CALIBRATE", "true") == "true",
            Read("DMS1521_PARITY", "true") == "true",
            PerfEvidenceRunSettings.FromEnvironment()
        );
    }
}

/// <summary>The pieces of a harness fixture a run needs, passed in because they are protected.</summary>
public sealed record KeysetSpikeContext(
    ApiIntegrationHarness Harness,
    PerfProvider Provider,
    string LeasedConnectionString,
    Func<Task<DbConnection>> OpenConnectionAsync
)
{
    public ApiIntegrationQueryRecorder Recorder =>
        Harness.QueryRecorder ?? throw new InvalidOperationException("CaptureQueryPlans must be enabled.");
}

/// <summary>
/// The three spike fixture runs. Each writes one run directory under the results directory, with the
/// run manifest, the measured cells, every captured batch with its binding specifications and SHA-256,
/// the spike plans, the replays, the counters, and the parity check.
/// </summary>
public static class KeysetSpikeRunner
{
    public static async Task<string> RunResourceAsync(KeysetSpikeContext context)
    {
        KeysetSpikeSettings settings = KeysetSpikeSettings.FromEnvironment();
        RunArtifacts run = RunArtifacts.Create(settings, context.Provider, "resource");
        Guard(settings, run);
        await using ReplayTarget target = new(context.Provider, context.LeasedConnectionString);
        await using ServerCounters counters = await StartCountersAsync(settings, context);

        if (settings.Calibrate)
        {
            JsonObject calibration = await ObserverCalibration.RunAsync(counters, target);
            run.WriteJson("calibration.json", calibration);
            ObserverCalibration.RequireCalibrated(calibration);
        }

        PerfFixtureDefinition definition = new(settings.Fixture);
        Stopwatch load = Stopwatch.StartNew();
        await PerfFixtureLoader.LoadAndVerifyAsync(
            context.Harness.DbConnection,
            context.Provider,
            definition
        );
        run.Manifest["fixtureLoadSeconds"] = load.Elapsed.TotalSeconds;

        using SpikeCommandCapture capture = SpikeCommandCapture.Start(context.Provider);
        int keysetBaseline = context.Recorder.HydrationKeysets.Count;
        int relationalBaseline = context.Recorder.RelationalCommands.Count;
        IReadOnlyList<PerfMeasuredCell> cells = await PerfScenarioExecutor.RunAsync(
            context.Harness,
            context.Provider,
            settings.DeepOffset,
            settings.WarmupIterations,
            settings.MeasuredIterations
        );
        capture.AttachProductionBindings(
            0,
            [.. context.Recorder.HydrationKeysets.Skip(keysetBaseline)],
            [.. context.Recorder.RelationalCommands.Skip(relationalBaseline)]
        );

        JsonArray cellsJson = [];
        List<(ReplayBatch Batch, bool Replay)> batches = [];
        foreach (PerfMeasuredCell cell in cells)
        {
            // Every cell compiles the same batch text; only the bound offset and limit tell them apart.
            SpikeCapturedCommand command =
                capture.Commands.LastOrDefault(candidate =>
                    candidate.CommandText == cell.HydrationBatchSql
                    && candidate.Parameters is not null
                    && BoundLong(candidate.Parameters, "offset") == cell.Offset
                    && BoundLong(candidate.Parameters, "limit") == cell.PageSize
                )
                ?? throw new InvalidOperationException(
                    $"{cell.ScenarioId}/{cell.PageSize}: no capture bound to offset {cell.Offset} and limit {cell.PageSize}."
                );
            ReplayBatch batch = new(
                $"resource.{cell.ScenarioId}",
                cell.PageSize,
                command.CommandText,
                command.Parameters!
            );
            batches.Add((batch, cell.ScenarioId == PerfScenarios.TraditionalOffsetZero));
            cellsJson.Add(
                new JsonObject
                {
                    ["cell"] = $"{cell.ScenarioId}/{cell.PageSize}",
                    ["offset"] = cell.Offset,
                    ["returnedRows"] = cell.ReturnedRows,
                    ["commandsPerRequest"] = cell.CommandCountPerRequest,
                    ["latencyMs"] = Latency(cell.LatencyMs),
                    ["driverExecuteMs"] = Latency(cell.DriverExecuteMs),
                    ["batchSha256"] = Sha256(command.CommandText),
                    ["bindingSource"] = command.BindingSource,
                    ["boundOffset"] = BoundLong(command.Parameters!, "offset"),
                    ["boundLimit"] = BoundLong(command.Parameters!, "limit"),
                }
            );
        }

        run.WriteJson("cells.json", cellsJson);
        foreach (int pageSize in settings.PageSizes)
        {
            await AutoExplainRequestsAsync(
                context,
                run,
                $"resource.{pageSize}",
                $"{PerfFixtureDefinition.ResourceEndpoint}?limit={pageSize}&offset=0",
                pageSize
            );
        }

        await CaptureEvidenceAsync(
            settings,
            context,
            run,
            target,
            settings.Counters ? counters : null,
            batches
        );
        return await FinishAsync(settings, context, run);
    }

    public static async Task<string> RunDescriptorAsync(KeysetSpikeContext context)
    {
        KeysetSpikeSettings settings = KeysetSpikeSettings.FromEnvironment();
        RunArtifacts run = RunArtifacts.Create(settings, context.Provider, "descriptor");
        Guard(settings, run);
        await using ReplayTarget target = new(context.Provider, context.LeasedConnectionString);
        await using ServerCounters counters = await StartCountersAsync(settings, context);

        PerfDescriptorFixtureDefinition definition = new(settings.DescriptorFixture);
        Stopwatch load = Stopwatch.StartNew();
        await PerfDescriptorFixtureLoader.LoadAndVerifyAsync(
            context.Harness.DbConnection,
            context.Provider,
            definition
        );
        run.Manifest["fixtureLoadSeconds"] = load.Elapsed.TotalSeconds;

        using SpikeCommandCapture capture = SpikeCommandCapture.Start(context.Provider);
        JsonArray cellsJson = [];
        List<(ReplayBatch Batch, bool Replay)> batches = [];
        foreach (int pageSize in settings.PageSizes)
        {
            string url = $"{PerfDescriptorFixtureDefinition.ResourceEndpoint}?limit={pageSize}&offset=0";
            CellObservation observation = await MeasureCellAsync(
                context,
                capture,
                url,
                settings,
                $"descriptor/{pageSize}",
                beforeAsync: null,
                verify: (window, rows) =>
                {
                    Require(rows == pageSize, $"returned {rows} rows; expected {pageSize}");
                    Require(window.Keysets.Count == 0, "a descriptor read must not hydrate");
                    Require(
                        window.Relational.Count == 1
                            && window
                                .Relational[0]
                                .CommandText.Contains("-- Descriptor acquisition:", StringComparison.Ordinal),
                        "expected exactly one descriptor acquisition command"
                    );
                }
            );
            SpikeCapturedCommand command = observation.RelationalBatch();
            batches.Add(
                (new ReplayBatch("descriptor", pageSize, command.CommandText, command.Parameters!), true)
            );
            cellsJson.Add(observation.ToJson($"descriptor/{pageSize}", command));
        }

        run.WriteJson("cells.json", cellsJson);
        foreach (int pageSize in settings.PageSizes)
        {
            await AutoExplainRequestsAsync(
                context,
                run,
                $"descriptor.{pageSize}",
                $"{PerfDescriptorFixtureDefinition.ResourceEndpoint}?limit={pageSize}&offset=0",
                pageSize
            );
        }

        await CaptureEvidenceAsync(
            settings,
            context,
            run,
            target,
            settings.Counters ? counters : null,
            batches
        );
        return await FinishAsync(settings, context, run);
    }

    public static async Task<string> RunReadAccelerationAsync(KeysetSpikeContext context)
    {
        KeysetSpikeSettings settings = KeysetSpikeSettings.FromEnvironment();
        RunArtifacts run = RunArtifacts.Create(settings, context.Provider, "read-acceleration");
        Guard(settings, run);
        await using ReplayTarget target = new(context.Provider, context.LeasedConnectionString);
        await using ServerCounters counters = await StartCountersAsync(settings, context);

        PerfFixtureDefinition definition = new(settings.Fixture);
        Stopwatch load = Stopwatch.StartNew();
        await PerfFixtureLoader.LoadAndVerifyAsync(
            context.Harness.DbConnection,
            context.Provider,
            definition
        );
        run.Manifest["fixtureLoadSeconds"] = load.Elapsed.TotalSeconds;
        await ExecuteAsync(
            context.Harness.DbConnection,
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = 'Tracking', "CacheAheadRecoveryRequired" = @no
            WHERE "StateId" = 1;
            """,
            ("@no", false)
        );

        using SpikeCommandCapture capture = SpikeCommandCapture.Start(context.Provider);
        JsonArray cellsJson = [];
        List<(ReplayBatch Batch, bool Replay)> batches = [];
        foreach (int pageSize in settings.PageSizes)
        {
            string url = $"{PerfFixtureDefinition.ResourceEndpoint}?limit={pageSize}&offset=0";
            long[] pageIds = await DiscoverAndWarmPageAsync(context, url, pageSize);
            run.Manifest[$"readAcceleration.page{pageSize}.firstId"] = pageIds[0];

            CellObservation hit = await MeasureCellAsync(
                context,
                capture,
                url,
                settings,
                $"cache-hit/{pageSize}",
                beforeAsync: null,
                verify: (window, rows) =>
                {
                    Require(rows == pageSize, $"returned {rows} rows; expected {pageSize}");
                    Require(window.Keysets.Count == 0, "a cache hit must not hydrate");
                    Require(window.Relational.Count >= 1, "a cache hit must select candidates");
                }
            );
            SpikeCapturedCommand candidate = hit.RelationalBatch();
            batches.Add(
                (new ReplayBatch("candidate", pageSize, candidate.CommandText, candidate.Parameters!), true)
            );
            cellsJson.Add(hit.ToJson($"cache-hit/{pageSize}", candidate));
            await AutoExplainRequestsAsync(context, run, $"candidate.{pageSize}", url, pageSize);

            CellObservation miss = await MeasureCellAsync(
                context,
                capture,
                url,
                settings,
                $"forced-miss/{pageSize}",
                beforeAsync: () => ClearCacheAsync(context, pageIds),
                verify: (window, rows) =>
                {
                    Require(rows == pageSize, $"returned {rows} rows; expected {pageSize}");
                    Require(
                        window.Keysets.Count == 1
                            && window.Keysets[0] is PageKeysetSpec.SelectedPage selected
                            && selected.DocumentIds.Count == pageSize,
                        "a forced miss must hydrate exactly the selected page once"
                    );
                }
            );
            SpikeCapturedCommand fallback = miss.KeysetBatch(context.Provider);
            batches.Add(
                (new ReplayBatch("fallback", pageSize, fallback.CommandText, fallback.Parameters!), true)
            );
            cellsJson.Add(miss.ToJson($"forced-miss/{pageSize}", fallback));
        }

        run.WriteJson("cells.json", cellsJson);

        // A forced-miss request also direct-fills every missed document (about 7 commands each), so an
        // API-driven window would log thousands of plans per request. The fallback batch is instead run
        // through a fresh production-equivalent Npgsql pool with its captured bindings.
        foreach ((ReplayBatch batch, _) in batches.Where(item => item.Batch.Kind == "fallback"))
        {
            await AutoExplainReplayAsync(context, run, $"fallback.{batch.PageSize}", batch);
        }

        await CaptureEvidenceAsync(
            settings,
            context,
            run,
            target,
            settings.Counters ? counters : null,
            batches
        );
        return await FinishAsync(settings, context, run);
    }

    /// <summary>
    /// Takes the page's ids from one relational (cache-miss) request, plants matching projection work,
    /// and lets one more request direct-fill the cache. Fails unless every page row is then cached.
    /// </summary>
    private static async Task<long[]> DiscoverAndWarmPageAsync(
        KeysetSpikeContext context,
        string url,
        int pageSize
    )
    {
        int keysetBaseline = context.Recorder.HydrationKeysets.Count;
        await GetRowsAsync(context.Harness, url);
        PageKeysetSpec.SelectedPage selected =
            context
                .Recorder.HydrationKeysets.Skip(keysetBaseline)
                .OfType<PageKeysetSpec.SelectedPage>()
                .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Read acceleration did not fall back through a selected page; is it enabled for this resource?"
            );
        long[] ids = [.. selected.DocumentIds];
        Require(ids.Length == pageSize, $"discovered {ids.Length} ids; expected {pageSize}");

        string idList = string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

        // A query miss direct-fills documents one at a time within DirectFillTimeout, so a page fills only
        // partly per request. Warm instead with one GET by id per missing document, each of which
        // direct-fills that document, re-planting its projection work first.
        long cached = 0;
        for (int round = 0; round < 5 && cached < pageSize; round++)
        {
            await ExecuteAsync(
                context.Harness.DbConnection,
                $"""
                DELETE FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" IN ({idList});
                INSERT INTO "dms"."DocumentProjectionWork" ("DocumentId", "RequiredContentVersion", "FirstEnqueuedAt", "LastEnqueuedAt")
                SELECT d."DocumentId", d."ContentVersion", @now, @now
                FROM "dms"."Document" d
                WHERE d."DocumentId" IN ({idList})
                  AND NOT EXISTS (SELECT 1 FROM "dms"."DocumentCache" c WHERE c."DocumentId" = d."DocumentId");
                """,
                ("@now", DateTimeOffset.UtcNow)
            );
            foreach (Guid uuid in await MissingUuidsAsync(context.Harness.DbConnection, idList))
            {
                using HttpResponseMessage response = await context.Harness.HttpClient.GetAsync(
                    $"{PerfFixtureDefinition.ResourceEndpoint}/{uuid}"
                );
                Require(
                    response.StatusCode == HttpStatusCode.OK,
                    $"GET by id {uuid}: HTTP {(int)response.StatusCode}"
                );
            }

            cached = await ScalarLongAsync(
                context.Harness.DbConnection,
                $"""SELECT COUNT(*) FROM "dms"."DocumentCache" WHERE "DocumentId" IN ({idList});"""
            );
        }

        Require(cached == pageSize, $"direct fill cached {cached} of {pageSize} page rows after 5 rounds");
        await ExecuteAsync(
            context.Harness.DbConnection,
            $"""DELETE FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" IN ({idList});"""
        );
        return ids;
    }

    private static async Task<IReadOnlyList<Guid>> MissingUuidsAsync(DbConnection connection, string idList)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d."DocumentUuid" FROM "dms"."Document" d
            WHERE d."DocumentId" IN ({idList})
              AND NOT EXISTS (SELECT 1 FROM "dms"."DocumentCache" c WHERE c."DocumentId" = d."DocumentId");
            """;
        List<Guid> uuids = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            uuids.Add(reader.GetGuid(0));
        }

        return uuids;
    }

    private static async Task ClearCacheAsync(KeysetSpikeContext context, long[] ids)
    {
        string idList = string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        await ExecuteAsync(
            context.Harness.DbConnection,
            $"""
            DELETE FROM "dms"."DocumentCache" WHERE "DocumentId" IN ({idList});
            DELETE FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" IN ({idList});
            """
        );
    }

    private sealed record RequestWindow(
        IReadOnlyList<SpikeCapturedCommand> Commands,
        IReadOnlyList<PageKeysetSpec> Keysets,
        IReadOnlyList<RelationalCommand> Relational
    );

    private sealed record CellObservation(
        PerfLatencySummary Latency,
        IReadOnlyList<int> CommandsPerRequest,
        RequestWindow Last
    )
    {
        /// <summary>The last request's first relational command, matched to its capture by exact text.</summary>
        public SpikeCapturedCommand RelationalBatch() =>
            Last.Commands.First(command =>
                command.Parameters is not null && command.CommandText == Last.Relational[0].CommandText
            );

        /// <summary>
        /// The last request's hydration batch: the capture whose parameter names are exactly the ones the
        /// production binder gives the recorded keyset for that capture's own text (never an empty set).
        /// </summary>
        public SpikeCapturedCommand KeysetBatch(PerfProvider provider) =>
            Last.Commands.First(command =>
            {
                if (command.Parameters is not { Count: > 0 })
                {
                    return false;
                }

                HashSet<string> names =
                [
                    .. SpikeCommandCapture
                        .FromProductionKeysetBinding(provider, Last.Keysets[0], command.CommandText)
                        .Select(parameter => parameter.BareName),
                ];
                return names.SetEquals(command.Parameters.Select(parameter => parameter.BareName));
            });

        public JsonObject ToJson(string cell, SpikeCapturedCommand batch) =>
            new()
            {
                ["cell"] = cell,
                ["latencyMs"] = KeysetSpikeRunner.Latency(Latency),
                ["commandsPerRequest"] = new JsonObject
                {
                    ["min"] = CommandsPerRequest.Min(),
                    ["max"] = CommandsPerRequest.Max(),
                },
                ["batchSha256"] = Sha256(batch.CommandText),
                ["bindingSource"] = batch.BindingSource,
                ["lastRequestCommandMix"] = new JsonArray([
                    .. Last
                        .Commands.GroupBy(command =>
                            System.Text.RegularExpressions.Regex.Replace(
                                command.CommandText[..Math.Min(command.CommandText.Length, 160)],
                                @"\s+",
                                " "
                            )
                        )
                        .Select(group =>
                            (JsonNode)new JsonObject { ["count"] = group.Count(), ["textPrefix"] = group.Key }
                        ),
                ]),
            };
    }

    /// <summary>
    /// Measures one GET cell. Per iteration, outside the timed window: the optional state setup runs
    /// before, and after, the response, the commands, keysets, and relational commands of that request
    /// are checked against the cell's intended state.
    /// </summary>
    private static async Task<CellObservation> MeasureCellAsync(
        KeysetSpikeContext context,
        SpikeCommandCapture capture,
        string url,
        KeysetSpikeSettings settings,
        string cell,
        Func<Task>? beforeAsync,
        Action<RequestWindow, int> verify
    )
    {
        int commandBaseline = 0;
        int keysetBaseline = 0;
        int relationalBaseline = 0;
        RequestWindow? last = null;
        HttpStatusCode status = default;
        string body = string.Empty;
        List<int> commandsPerRequest = [];

        PerfLatencySummary latency = await PerfLatencyMeasurement.MeasureAsync(
            async _ =>
            {
                using HttpResponseMessage response = await context.Harness.HttpClient.GetAsync(url);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync();
            },
            settings.WarmupIterations,
            settings.MeasuredIterations,
            beforeIterationAsync: async _ =>
            {
                if (beforeAsync is not null)
                {
                    await beforeAsync();
                }

                commandBaseline = capture.Count;
                keysetBaseline = context.Recorder.HydrationKeysets.Count;
                relationalBaseline = context.Recorder.RelationalCommands.Count;
            },
            afterIterationAsync: iteration =>
            {
                Require(
                    status == HttpStatusCode.OK,
                    $"{cell} iteration {iteration}: HTTP {(int)status}: {body[..Math.Min(body.Length, 300)]}"
                );
                IReadOnlyList<PageKeysetSpec> keysets =
                [
                    .. context.Recorder.HydrationKeysets.Skip(keysetBaseline),
                ];
                IReadOnlyList<RelationalCommand> relational =
                [
                    .. context.Recorder.RelationalCommands.Skip(relationalBaseline),
                ];
                capture.AttachProductionBindings(commandBaseline, keysets, relational);
                IReadOnlyList<SpikeCapturedCommand> commands = capture.CommandsSince(commandBaseline);
                commandsPerRequest.Add(commands.Count);
                last = new RequestWindow(commands, keysets, relational);
                verify(last, PerfScenarioExecutor.CountReturnedRows(body));
                return Task.CompletedTask;
            }
        );

        return new CellObservation(latency, commandsPerRequest, last!);
    }

    /// <summary>
    /// For every batch: write its text, binding specifications, and SHA-256; capture spike plans; and,
    /// for replayed batches, replay at each concurrency inside a counter window. Then one parity check per
    /// batch kind.
    /// </summary>
    private static async Task CaptureEvidenceAsync(
        KeysetSpikeSettings settings,
        KeysetSpikeContext context,
        RunArtifacts run,
        ReplayTarget target,
        ServerCounters? counters,
        IReadOnlyList<(ReplayBatch Batch, bool Replay)> batches
    )
    {
        JsonArray replays = [];
        JsonArray parity = [];
        HashSet<string> parityKinds = [];
        foreach ((ReplayBatch batch, bool replay) in batches)
        {
            long boundPageSize = BoundPageSize(batch);
            Require(
                boundPageSize == batch.PageSize,
                $"{batch.Kind}.{batch.PageSize}: the captured binding selects {boundPageSize} rows, not {batch.PageSize}"
            );
            string prefix = $"batches/{batch.Kind}.{batch.PageSize}";
            // Exact text: the sidecar's sha256 and length, and cells.json's batchSha256, describe it.
            run.WriteExactText($"{prefix}.sql", batch.Text);
            run.WriteJson(
                $"{prefix}.params.json",
                new JsonObject
                {
                    ["boundPageSize"] = boundPageSize,
                    ["sha256"] = Sha256(batch.Text),
                    ["length"] = batch.Text.Length,
                    ["parameters"] = SpikeJson.Parameters(batch.Parameters),
                }
            );

            await using (DbConnection connection = await context.OpenConnectionAsync())
            {
                string planPrefix = $"plans/{batch.Kind}.{batch.PageSize}";
                if (context.Provider == PerfProvider.Postgresql)
                {
                    run.WriteJson(
                        $"{planPrefix}.explain.json",
                        await SpikePgsqlPlanCapture.CaptureAsync(connection, batch.Text, batch.Parameters)
                    );
                }
                else
                {
                    SpikeMssqlPlanCaptureResult plans = await SpikeMssqlPlanCapture.CaptureAsync(
                        (SqlConnection)connection,
                        batch.Text,
                        batch.Parameters
                    );
                    for (int index = 0; index < plans.ShowplanXmlDocuments.Count; index++)
                    {
                        run.WriteText(
                            $"{planPrefix}.plan{index + 1:D2}.sqlplan",
                            plans.ShowplanXmlDocuments[index]
                        );
                    }

                    run.WriteText($"{planPrefix}.stats.txt", plans.StatisticsText);
                    run.WriteJson(
                        $"{planPrefix}.plans.json",
                        SpikeMssqlPlanCapture.Summary(plans, planPrefix)
                    );
                }
            }

            if (settings.Parity && parityKinds.Add(batch.Kind.Split('.')[0]))
            {
                parity.Add(await ParityCheck.RunAsync(context, batch));
            }

            if (!replay)
            {
                continue;
            }

            foreach (int concurrency in settings.ReplayConcurrency)
            {
                JsonObject entry = new()
                {
                    ["kind"] = batch.Kind,
                    ["pageSize"] = batch.PageSize,
                    ["batchSha256"] = Sha256(batch.Text),
                };
                CounterWindow? window = null;
                Func<Func<Task>, Task>? measuredPhaseWindow =
                    counters is not null && settings.CounterConcurrency.Contains(concurrency)
                        ? async measuredPhase =>
                            window = await counters.MeasureAsync(async () =>
                            {
                                await measuredPhase();
                                return concurrency * settings.ReplayIterationsPerWorker;
                            })
                        : null;
                ReplayResult result = await KeysetReplay.RunAsync(
                    target,
                    batch,
                    concurrency,
                    settings.ReplayWarmupPerWorker,
                    settings.ReplayIterationsPerWorker,
                    measuredPhaseWindow
                );
                if (window is not null)
                {
                    entry["counters"] = window.ToJson();
                }

                entry["replay"] = result.ToJson();
                replays.Add(entry);
            }
        }

        run.WriteJson("replays.json", replays);
        if (settings.Parity)
        {
            run.WriteJson("parity.json", parity);
            Require(
                parity.All(check => check!["identical"]!.GetValue<bool>()),
                "binding parity failed; see parity.json (this blocks task 3)"
            );
        }
    }

    internal static async Task<ServerCounters> StartCountersAsync(
        KeysetSpikeSettings settings,
        KeysetSpikeContext context
    )
    {
        ServerCounters counters = new(context.Provider, context.LeasedConnectionString);
        if (settings.Counters || settings.Calibrate)
        {
            await counters.InitializeAsync();
        }

        return counters;
    }

    /// <summary>A PostgreSQL auto_explain window over API GETs of one page; each must return the page.</summary>
    private static async Task AutoExplainRequestsAsync(
        KeysetSpikeContext context,
        RunArtifacts run,
        string label,
        string url,
        int pageSize
    )
    {
        if (context.Provider != PerfProvider.Postgresql || !AutoExplainWindow.Enabled)
        {
            return;
        }

        await AutoExplainWindow.CaptureAsync(
            context.OpenConnectionAsync,
            run,
            label,
            async () =>
            {
                for (int request = 0; request < AutoExplainWindow.Requests; request++)
                {
                    int rows = await GetRowsAsync(context.Harness, url);
                    Require(
                        rows == pageSize,
                        $"auto_explain {label}: returned {rows} rows; expected {pageSize}"
                    );
                }
            }
        );
    }

    /// <summary>A PostgreSQL auto_explain window over replays of a captured batch on a fresh pool.</summary>
    private static async Task AutoExplainReplayAsync(
        KeysetSpikeContext context,
        RunArtifacts run,
        string label,
        ReplayBatch batch
    )
    {
        if (context.Provider != PerfProvider.Postgresql || !AutoExplainWindow.Enabled)
        {
            return;
        }

        await AutoExplainWindow.CaptureAsync(
            context.OpenConnectionAsync,
            run,
            label,
            async () =>
            {
                await using ReplayTarget fresh = new(context.Provider, context.LeasedConnectionString);
                ReplayResult result = await KeysetReplay.RunAsync(
                    fresh,
                    batch,
                    1,
                    0,
                    AutoExplainWindow.Requests
                );
                run.WriteJson($"autoexplain/{label}.replay.json", result.ToJson());
                Require(
                    result.Errors == 0 && result.WarmupErrors == 0,
                    $"auto_explain {label}: replay errors"
                );
            }
        );
    }

    internal static void Guard(KeysetSpikeSettings settings, RunArtifacts run)
    {
        PerfBaselineRunPipeline.GuardCiEnvironment(
            settings.Evidence.AllowCi,
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS")
        );
        IReadOnlyList<string> dirty = GitIdentity.DirtyPaths(AppContext.BaseDirectory);
        PerfBaselineRunPipeline.GuardDirtyPaths(dirty, settings.Evidence.AllowedDirtyPrefixes);
        run.Manifest["subjectCommit"] = GitIdentity.HeadCommit(AppContext.BaseDirectory);
        run.Manifest["dirtyPaths"] = new JsonArray([.. dirty.Select(path => (JsonNode)path)]);
    }

    internal static async Task<string> FinishAsync(
        KeysetSpikeSettings settings,
        KeysetSpikeContext context,
        RunArtifacts run
    )
    {
        await using DbConnection connection = await context.OpenConnectionAsync();
        PerfEnvironmentIdentity environment = await PerfEnvironmentCapture.CaptureAsync(
            connection,
            context.Provider,
            settings.Evidence.ImageTag,
            settings.Evidence.ImageDigest,
            settings.Evidence.StorageNote,
            context.LeasedConnectionString
        );
        run.WriteText("environment.json", PerfArtifactJson.Serialize(environment));
        run.Manifest["completedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        run.WriteJson("run-manifest.json", run.Manifest);
        return run.Directory;
    }

    /// <summary>The value bound to the parameter with this bare name, or null if there is none.</summary>
    public static long? BoundLong(IReadOnlyList<SpikeParameterSpec> parameters, string bareName) =>
        parameters.FirstOrDefault(parameter => parameter.BareName == bareName)?.Value is { } value
        && value is not DBNull
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// The page size a batch's binding selects: its bound limit, its number of selected-page ids, or
    /// the length of its selected-page JSON array.
    /// </summary>
    public static long BoundPageSize(ReplayBatch batch)
    {
        if (BoundLong(batch.Parameters, "limit") is long limit)
        {
            return limit;
        }

        // A PostgreSQL selected page that binds its ids as one bigint[] (the inline prototype's
        // page_document_ids, or a story's own name for it). A query keyset's NULL is not a long[].
        if (
            batch.Parameters.Select(parameter => parameter.Value).OfType<long[]>().FirstOrDefault() is
            { } documentIds
        )
        {
            return documentIds.Length;
        }

        int selectedIds = batch.Parameters.Count(parameter =>
            parameter.BareName.StartsWith("selectedDocumentId_", StringComparison.Ordinal)
        );
        if (selectedIds > 0)
        {
            return selectedIds;
        }

        if (
            batch
                .Parameters.FirstOrDefault(parameter => parameter.BareName == "selectedDocumentIdsJson")
                ?.Value
            is string json
        )
        {
            return JsonNode.Parse(json)!.AsArray().Count;
        }

        throw new InvalidOperationException($"{batch.Kind}.{batch.PageSize}: no page-size binding found.");
    }

    public static JsonObject Latency(PerfLatencySummary summary) =>
        new()
        {
            ["p50"] = summary.P50Ms,
            ["p95"] = summary.P95Ms,
            ["mean"] = summary.MeanMs,
            ["min"] = summary.MinMs,
            ["max"] = summary.MaxMs,
            ["samples"] = new JsonArray([.. summary.SamplesMs.Select(sample => (JsonNode)sample)]),
        };

    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task<int> GetRowsAsync(ApiIntegrationHarness harness, string url)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(url);
        string body = await response.Content.ReadAsStringAsync();
        Require(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {url}: HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}"
        );
        return PerfScenarioExecutor.CountReturnedRows(body);
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 600;
        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}

/// <summary>A run directory and its manifest.</summary>
public sealed class RunArtifacts
{
    private RunArtifacts(string directory, JsonObject manifest)
    {
        Directory = directory;
        Manifest = manifest;
    }

    public string Directory { get; }

    public JsonObject Manifest { get; }

    public static RunArtifacts Create(KeysetSpikeSettings settings, PerfProvider provider, string fixtureRun)
    {
        string engine = PerfProviders.ArtifactName(provider);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string runId = $"{settings.SequenceId}-{engine}-{fixtureRun}-{settings.VariantLabel}-{stamp}";
        string directory = Path.Combine(settings.ResultsDirectory, runId);
        System.IO.Directory.CreateDirectory(directory);
        return new RunArtifacts(
            directory,
            new JsonObject
            {
                ["runId"] = runId,
                ["ticket"] = "DMS-1521",
                ["engine"] = engine,
                ["fixtureRun"] = fixtureRun,
                ["variantLabel"] = settings.VariantLabel,
                ["keysetVariant"] = settings.KeysetVariant,
                ["sequenceId"] = settings.SequenceId,
                ["fixture"] =
                    fixtureRun == "descriptor" ? settings.DescriptorFixture.Id : settings.Fixture.Id,
                ["warmupIterations"] = settings.WarmupIterations,
                ["measuredIterations"] = settings.MeasuredIterations,
                ["pageSizes"] = new JsonArray([.. settings.PageSizes.Select(size => (JsonNode)size)]),
                ["replayConcurrency"] = new JsonArray([
                    .. settings.ReplayConcurrency.Select(level => (JsonNode)level),
                ]),
                ["replayWarmupPerWorker"] = settings.ReplayWarmupPerWorker,
                ["replayIterationsPerWorker"] = settings.ReplayIterationsPerWorker,
                ["counterConcurrency"] = new JsonArray([
                    .. settings.CounterConcurrency.Select(level => (JsonNode)level),
                ]),
                ["startedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            }
        );
    }

    public void WriteText(string relativePath, string content) =>
        WriteExactText(relativePath, content.Replace("\r\n", "\n"));

    public void WriteExactText(string relativePath, string content)
    {
        string path = Path.Combine(Directory, relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public void WriteJson(string relativePath, JsonNode node) =>
        WriteText(relativePath, SpikeJson.Write(node) + "\n");
}

/// <summary>
/// Proves that replay binds parameters exactly as production does, once per engine and batch kind.
/// <list type="bullet">
/// <item>SQL Server: SqlClient sends a parameterized batch as <c>sp_executesql</c>, and the plan cache
/// keys a prepared batch by its parameter declaration plus text. After one replay, the cached entries
/// for the batch text must be a single entry whose declaration is the production one, with its use
/// count advanced; a differing declaration would create a second entry.</item>
/// <item>PostgreSQL: the batch is executed on one physical connection until Npgsql auto-prepares it,
/// and <c>pg_prepared_statements.parameter_types</c> on that connection is compared, position by
/// position, with the type each production-bound parameter declares.</item>
/// </list>
/// </summary>
public static class ParityCheck
{
    public static Task<JsonObject> RunAsync(KeysetSpikeContext context, ReplayBatch batch) =>
        context.Provider == PerfProvider.Postgresql
            ? PostgresqlAsync(context, batch)
            : MssqlAsync(context, batch);

    private static async Task<JsonObject> MssqlAsync(KeysetSpikeContext context, ReplayBatch batch)
    {
        const string entriesSql = """
            SELECT CAST(st.text AS nvarchar(max)), cp.usecounts
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
            WHERE cp.objtype = N'Prepared' AND RIGHT(st.text, LEN(@body + N'x') - 1) = @body;
            """;

        async Task<List<(string Declaration, long UseCount)>> ReadEntriesAsync()
        {
            await using DbConnection connection = await context.OpenConnectionAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = entriesSql;
            command.Parameters.Add(
                new SqlParameter("@body", System.Data.SqlDbType.NVarChar, -1) { Value = batch.Text }
            );
            List<(string, long)> entries = [];
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string text = reader.GetString(0);
                entries.Add(
                    (
                        text[..^batch.Text.Length],
                        Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
                    )
                );
            }

            return entries;
        }

        List<(string Declaration, long UseCount)> before = await ReadEntriesAsync();
        await using (ReplayTarget target = new(PerfProvider.Mssql, context.LeasedConnectionString))
        {
            await KeysetReplay.RunAsync(target, batch, 1, 0, 1);
        }

        List<(string Declaration, long UseCount)> after = await ReadEntriesAsync();
        bool identical =
            before.Count == 1
            && after.Count == 1
            && after[0].Declaration == before[0].Declaration
            && after[0].UseCount > before[0].UseCount;
        return new JsonObject
        {
            ["engine"] = "mssql",
            ["kind"] = batch.Kind,
            ["pageSize"] = batch.PageSize,
            ["identical"] = identical,
            ["productionEntries"] = Entries(before),
            ["entriesAfterReplay"] = Entries(after),
        };

        static JsonArray Entries(List<(string Declaration, long UseCount)> entries) =>
            [
                .. entries.Select(entry =>
                    (JsonNode)
                        new JsonObject { ["declaration"] = entry.Declaration, ["useCount"] = entry.UseCount }
                ),
            ];
    }

    private static async Task<JsonObject> PostgresqlAsync(KeysetSpikeContext context, ReplayBatch batch)
    {
        NpgsqlDataSourceBuilder builder = new(context.LeasedConnectionString);
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        builder.ConnectionStringBuilder.AutoPrepareMinUsages = 3;
        builder.ConnectionStringBuilder.MaxAutoPrepare = 256;
        builder.ConnectionStringBuilder.MaxPoolSize = 1;
        builder.ConnectionStringBuilder.ApplicationName = "EdFi.DMS.KeysetParity";
        await using NpgsqlDataSource dataSource = builder.Build();

        for (int execution = 0; execution < 5; execution++)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = batch.Text;
            foreach (SpikeParameterSpec parameter in batch.Parameters)
            {
                parameter.Bind(command);
            }

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync()) { }
            } while (await reader.NextResultAsync());
        }

        List<(string Statement, string[] Types)> prepared = [];
        await using (NpgsqlConnection connection = await dataSource.OpenConnectionAsync())
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT statement, parameter_types::text[] FROM pg_prepared_statements WHERE NOT from_sql;";
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                prepared.Add((reader.GetString(0), reader.GetFieldValue<string[]>(1)));
            }
        }

        Dictionary<string, string> expectedByName = batch.Parameters.ToDictionary(
            parameter => parameter.BareName,
            ExpectedType,
            StringComparer.Ordinal
        );
        JsonArray statements = [];
        bool identical = prepared.Count > 0;
        foreach (SpikePgsqlStatement statement in SpikePgsqlPlanCapture.Split(batch.Text))
        {
            List<string> names =
            [
                .. Regex
                    .Matches(statement.Sql, "@([A-Za-z_][A-Za-z0-9_]*)")
                    .Select(match => match.Groups[1].Value),
            ];
            if (names.Count == 0)
            {
                continue;
            }

            (string Statement, string[] Types)? match = prepared.FirstOrDefault(candidate =>
                Skeleton(candidate.Statement, @"\$\d+") == Skeleton(statement.Sql, "@[A-Za-z_][A-Za-z0-9_]*")
            );
            if (match is not { Statement: not null } found)
            {
                identical = false;
                statements.Add(new JsonObject { ["statement"] = statement.Number, ["prepared"] = false });
                continue;
            }

            // Npgsql rewrites each @name occurrence to its $n in place, so the k-th placeholder of the
            // prepared text corresponds to the k-th @name occurrence of the original statement.
            List<int> positions =
            [
                .. Regex
                    .Matches(found.Statement, @"\$(\d+)")
                    .Select(placeholder =>
                        int.Parse(placeholder.Groups[1].Value, CultureInfo.InvariantCulture)
                    ),
            ];
            JsonArray comparisons = [];
            for (int occurrence = 0; occurrence < Math.Min(names.Count, positions.Count); occurrence++)
            {
                string name = names[occurrence];
                int position = positions[occurrence];
                string actual = position - 1 < found.Types.Length ? found.Types[position - 1] : "<missing>";
                string expected = expectedByName.GetValueOrDefault(name, "<unbound>");
                bool same = expected == "*" || expected == actual;
                identical &= same;
                comparisons.Add(
                    new JsonObject
                    {
                        ["name"] = name,
                        ["position"] = position,
                        ["declared"] = expected,
                        ["prepared"] = actual,
                        ["same"] = same,
                    }
                );
            }

            identical &= names.Count == positions.Count;
            statements.Add(
                new JsonObject
                {
                    ["statement"] = statement.Number,
                    ["prepared"] = true,
                    ["parameters"] = comparisons,
                }
            );
        }

        return new JsonObject
        {
            ["engine"] = "postgresql",
            ["kind"] = batch.Kind,
            ["pageSize"] = batch.PageSize,
            ["identical"] = identical,
            ["preparedStatementCount"] = prepared.Count,
            ["statements"] = statements,
        };
    }

    /// <summary>
    /// The PostgreSQL type the production-bound parameter declares, as <c>regtype::text</c> prints it.
    /// An untyped parameter declares nothing, so the server's inference is accepted ("*").
    /// </summary>
    private static string ExpectedType(SpikeParameterSpec parameter)
    {
        NpgsqlDbType type = (NpgsqlDbType)parameter.ProviderTypeCode;
        if (type == NpgsqlDbType.Unknown)
        {
            return "*";
        }

        bool array = type.HasFlag(NpgsqlDbType.Array);
        NpgsqlDbType element = array ? type & ~NpgsqlDbType.Array : type;
        string name = element switch
        {
            NpgsqlDbType.Bigint => "bigint",
            NpgsqlDbType.Integer => "integer",
            NpgsqlDbType.Smallint => "smallint",
            NpgsqlDbType.Text => "text",
            NpgsqlDbType.Varchar => "character varying",
            NpgsqlDbType.Boolean => "boolean",
            NpgsqlDbType.Uuid => "uuid",
            NpgsqlDbType.Jsonb => "jsonb",
            NpgsqlDbType.Json => "json",
            NpgsqlDbType.TimestampTz => "timestamp with time zone",
            NpgsqlDbType.Timestamp => "timestamp without time zone",
            NpgsqlDbType.Date => "date",
            NpgsqlDbType.Numeric => "numeric",
            NpgsqlDbType.Double => "double precision",
            NpgsqlDbType.Real => "real",
            NpgsqlDbType.Bytea => "bytea",
            _ => parameter.DataTypeName ?? element.ToString(),
        };
        return array ? name + "[]" : name;
    }

    private static string Skeleton(string sql, string placeholderPattern)
    {
        string withoutComments = Regex.Replace(sql, @"--[^\n]*", " ");
        withoutComments = Regex.Replace(withoutComments, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        string placeholders = Regex.Replace(withoutComments, placeholderPattern, "?");
        return Regex.Replace(placeholders, @"\s+", " ").Trim().TrimEnd(';').Trim();
    }
}
