// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Result = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The step 2.5 provider measurement with the production write pipeline: the education organization
/// hierarchy is created through the API, writers change it through the API (names, and the School to
/// Local Education Agency and Local Education Agency parent relationships), and the production
/// projection reader, resolved from the host's container, reads the whole set alone and during the
/// writes. Opt-in; it prints <c>MEASURE</c> lines rather than asserting budgets.
/// </summary>
/// <remarks>
/// Only provider read success is measured here. A complete read of the set additionally needs every
/// page's digest to match the first page's, which committed writes between pages prevent regardless
/// of how the provider reads; that is the handler's measurement.
/// </remarks>
internal static class EducationOrganizationProjectionWriteImpactScenario
{
    private const string Json = "application/json";
    private const int EducationServiceCenters = 10;
    private const int RootLocalEducationAgencies = 10;
    private const int WarmupReads = 2;
    private const int MeasuredReads = 10;
    private const int SeedWorkers = 16;
    private const int SampleIntervalMilliseconds = 20;

    private static readonly string[] _kinds =
    [
        "sea-name",
        "esc-name",
        "lea-name",
        "school-name",
        "school-lea",
        "lea-parent",
    ];

    /// <summary>What differs between the engines.</summary>
    /// <param name="Name">The engine name for the report.</param>
    /// <param name="Dialect">The dialect, for the mapping set key.</param>
    /// <param name="EffectiveSchemaHashAsync">Reads the provisioned database's effective schema hash.</param>
    /// <param name="DeadlocksAsync">The database's cumulative deadlock count.</param>
    /// <param name="LockWaitTotalsAsync">Cumulative lock waits and wait time, where the engine keeps them.</param>
    /// <param name="SampleLockWaitersAsync">Writer and reader sessions currently waiting on a lock.</param>
    /// <param name="StartDeadlockGraphsAsync">Starts capturing deadlock graphs, where the engine can.</param>
    /// <param name="FinishDeadlockGraphsAsync">Stops capturing and summarizes the graphs.</param>
    public sealed record Engine(
        string Name,
        SqlDialect Dialect,
        Func<Task<string>> EffectiveSchemaHashAsync,
        Func<Task<long>> DeadlocksAsync,
        Func<Task<(long Waits, long Milliseconds)>>? LockWaitTotalsAsync,
        Func<Task<(int Writers, int Reader)>> SampleLockWaitersAsync,
        Func<Task>? StartDeadlockGraphsAsync,
        Func<Task<string>>? FinishDeadlockGraphsAsync
    );

    public static async Task MeasureAsync(
        ApiIntegrationHarness harness,
        string connectionString,
        Engine engine
    )
    {
        int rows = Setting("PROJECTION_MEASURE_ROWS", 50_000);
        int writers = Setting("PROJECTION_MEASURE_WRITERS", 4);
        int operationsPerWriter = Setting("PROJECTION_MEASURE_OPERATIONS_PER_WRITER", 500);
        int localEducationAgencies = Math.Max(RootLocalEducationAgencies + 1, rows / 50);
        int schools = rows - 1 - EducationServiceCenters - localEducationAgencies;
        harness.HttpClient.Timeout = TimeSpan.FromMinutes(10);

        string ns = $"uri://ed-fi.org/projection-measure/{Guid.NewGuid():N}";
        var hierarchy = new Hierarchy(ns);
        var seeding = Stopwatch.StartNew();
        await SeedDescriptorsAsync(harness, ns);
        await hierarchy.SeedAsync(harness, localEducationAgencies, schools);
        seeding.Stop();

        MappingSet mappingSet = await ResolveMappingSetAsync(harness, engine);
        var request = new EducationOrganizationProjectionSetReadRequest(mappingSet, rows, 5, 60);
        Func<Task<Result>> read = () => ReadAsync(harness, connectionString, request);

        for (int index = 0; index < WarmupReads; index++)
        {
            (await read()).Should().BeOfType<Result.Set>().Which.Rows.Should().HaveCount(rows);
        }

        List<double> readMilliseconds = [];
        for (int index = 0; index < MeasuredReads; index++)
        {
            var timer = Stopwatch.StartNew();
            var result = await read();
            readMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
            result.Should().BeOfType<Result.Set>().Which.Rows.Should().HaveCount(rows);
        }

        var output = TestContext.Out;
        await output.WriteLineAsync(
            $"MEASURE engine={engine.Name} pipeline=api rows={rows} leas={localEducationAgencies} schools={schools} "
                + $"seed_s={seeding.Elapsed.TotalSeconds:F1}"
        );
        await output.WriteLineAsync($"MEASURE read_ms n={MeasuredReads} {Summary(readMilliseconds)}");

        foreach (bool withReads in new[] { false, true })
        {
            var phase = await RunPhaseAsync(
                harness,
                engine,
                hierarchy,
                writers,
                operationsPerWriter,
                withReads ? read : null
            );
            await output.WriteLineAsync(
                phase.Describe(withReads ? "writes_with_reads" : "writes_alone", writers)
            );
        }
    }

    private static async Task<Phase> RunPhaseAsync(
        ApiIntegrationHarness harness,
        Engine engine,
        Hierarchy hierarchy,
        int writers,
        int operationsPerWriter,
        Func<Task<Result>>? read
    )
    {
        var phase = new Phase();
        long deadlocksBefore = await engine.DeadlocksAsync();
        var lockWaitsBefore = engine.LockWaitTotalsAsync is null
            ? default
            : await engine.LockWaitTotalsAsync();
        if (engine.StartDeadlockGraphsAsync is not null)
        {
            await engine.StartDeadlockGraphsAsync();
        }

        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var (writersWaiting, readerWaiting) = await engine.SampleLockWaitersAsync();
                phase.Sample(writersWaiting, readerWaiting);
                await Task.Delay(SampleIntervalMilliseconds);
            }
        });
        var readLoop = read is null
            ? Task.CompletedTask
            : Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var timer = Stopwatch.StartNew();
                    var result = await read();
                    phase.ReadMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
                    phase.ReadOutcomes.Add(
                        result switch
                        {
                            Result.Set => "Set",
                            Result.TargetUnavailable unavailable => $"Unavailable({unavailable.Describe})",
                            _ => result.ToString() ?? "unknown",
                        }
                    );
                }
            });

        await Task.WhenAll(
            Enumerable
                .Range(0, writers)
                .Select(writer =>
                    Task.Run(() => WriteAsync(harness, hierarchy, writer, operationsPerWriter, phase))
                )
        );
        await stop.CancelAsync();
        await readLoop;
        await sampler;

        phase.Deadlocks = await engine.DeadlocksAsync() - deadlocksBefore;
        if (engine.LockWaitTotalsAsync is not null)
        {
            var after = await engine.LockWaitTotalsAsync();
            phase.LockWaits = (
                after.Waits - lockWaitsBefore.Waits,
                after.Milliseconds - lockWaitsBefore.Milliseconds
            );
        }

        if (engine.FinishDeadlockGraphsAsync is not null)
        {
            phase.DeadlockGraphs = await engine.FinishDeadlockGraphsAsync();
        }

        return phase;
    }

    private static async Task WriteAsync(
        ApiIntegrationHarness harness,
        Hierarchy hierarchy,
        int writer,
        int operations,
        Phase phase
    )
    {
        var random = new Random(1440 + writer);
        for (int index = 0; index < operations; index++)
        {
            string kind = _kinds[(index + writer) % _kinds.Length];
            string suffix = $"{writer}-{index}";
            var (path, body) = kind switch
            {
                "sea-name" => hierarchy.StateEducationAgency($"Renamed {suffix}"),
                "esc-name" => hierarchy.EducationServiceCenter(random, $"Renamed {suffix}"),
                "lea-name" => hierarchy.LocalEducationAgency(
                    random,
                    $"Renamed {suffix}",
                    changeParent: false
                ),
                "lea-parent" => hierarchy.LocalEducationAgency(
                    random,
                    $"Reparented {suffix}",
                    changeParent: true
                ),
                "school-name" => hierarchy.School(
                    random,
                    $"Renamed {suffix}",
                    changeLocalEducationAgency: false
                ),
                _ => hierarchy.School(random, $"Moved {suffix}", changeLocalEducationAgency: true),
            };

            var timer = Stopwatch.StartNew();
            var (status, responseBody) = await PutAsync(harness, path, body);
            phase.WriteMilliseconds.GetOrAdd(kind, static _ => []).Add(timer.Elapsed.TotalMilliseconds);
            if (status != HttpStatusCode.NoContent)
            {
                phase.Failures.AddOrUpdate(
                    Signature(status, responseBody),
                    1,
                    static (_, count) => count + 1
                );
            }
        }
    }

    private static async Task<Result> ReadAsync(
        ApiIntegrationHarness harness,
        string connectionString,
        EducationOrganizationProjectionSetReadRequest request
    )
    {
        await using var scope = harness.Services.CreateAsyncScope();
        // What the request pipeline's data-store resolution and effective-target selection would set:
        // the projection reads the primary.
        var selection = scope.ServiceProvider.GetRequiredService<IDataStoreSelection>();
        selection.SetSelectedDataStore(
            new DataStore(1, "measurement", "projection-measure", connectionString, new())
        );
        selection.SetEffectiveTarget(
            new EffectiveDataStoreTarget(EffectiveTargetKind.Primary, connectionString)
        );
        return await scope
            .ServiceProvider.GetRequiredService<IEducationOrganizationProjectionSetReader>()
            .ReadSetAsync(request, CancellationToken.None);
    }

    private static async Task<MappingSet> ResolveMappingSetAsync(ApiIntegrationHarness harness, Engine engine)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var key = new MappingSetKey(
            EffectiveSchemaHash: await engine.EffectiveSchemaHashAsync(),
            Dialect: engine.Dialect,
            RelationalMappingVersion: scope
                .ServiceProvider.GetRequiredService<IEffectiveSchemaSetProvider>()
                .EffectiveSchemaSet.EffectiveSchema.RelationalMappingVersion
        );
        return await scope
            .ServiceProvider.GetRequiredService<IMappingSetProvider>()
            .GetOrCreateAsync(key, CancellationToken.None);
    }

    private static async Task SeedDescriptorsAsync(ApiIntegrationHarness harness, string ns)
    {
        foreach (
            var (endpoint, resource, code) in new[]
            {
                (
                    "educationOrganizationCategoryDescriptors",
                    "EducationOrganizationCategoryDescriptor",
                    "Other"
                ),
                ("gradeLevelDescriptors", "GradeLevelDescriptor", "Tenth grade"),
                (
                    "localEducationAgencyCategoryDescriptors",
                    "LocalEducationAgencyCategoryDescriptor",
                    "Independent"
                ),
            }
        )
        {
            var (status, body, _) = await PostAsync(
                harness,
                $"/data/ed-fi/{endpoint}",
                new JsonObject
                {
                    ["namespace"] = $"{ns}/{resource}",
                    ["codeValue"] = code,
                    ["shortDescription"] = code,
                }
            );
            status.Should().Be(HttpStatusCode.Created, $"seeding {resource} returned {body}");
        }
    }

    private static async Task<(HttpStatusCode Status, string Body, string? Location)> PostAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, Json);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            PathOf(response.Headers.Location)
        );
    }

    private static string? PathOf(Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PutAsync(
        ApiIntegrationHarness harness,
        string path,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, Json);
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Signature(HttpStatusCode status, string body)
    {
        string type = "none";
        try
        {
            type = JsonNode.Parse(body)?["type"]?.GetValue<string>() ?? "none";
        }
        catch (System.Text.Json.JsonException)
        {
            // Not a problem document; the status alone identifies it.
        }

        return $"{(int)status}:{type}";
    }

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
        return $"min={sorted[0]:F1} p50={Percentile(sorted, 0.5):F1} p95={Percentile(sorted, 0.95):F1} max={sorted[^1]:F1}";
    }

    private static double Percentile(List<double> sorted, double percentile) =>
        sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(percentile * sorted.Count) - 1)];

    /// <summary>
    /// The created hierarchy and the PUT bodies that change it. Root local agencies never get a
    /// parent, and only non-root agencies are reparented, always to a root, so writes cannot create a
    /// parent cycle.
    /// </summary>
    private sealed class Hierarchy(string ns)
    {
        private string _stateEducationAgencyPath = "";
        private readonly List<(long Id, string Path)> _educationServiceCenters = [];
        private readonly List<(long Id, string Path)> _localEducationAgencies = [];
        private readonly List<(long Id, string Path)> _schools = [];
        private long[] _parents = [];
        private long[] _schoolAgencies = [];

        public async Task SeedAsync(ApiIntegrationHarness harness, int localEducationAgencies, int schools)
        {
            _stateEducationAgencyPath = await CreateAsync(
                harness,
                "stateEducationAgencies",
                StateEducationAgencyBody(1, "Measurement State Agency")
            );

            for (int index = 1; index <= EducationServiceCenters; index++)
            {
                long id = 10 + index;
                _educationServiceCenters.Add(
                    (
                        id,
                        await CreateAsync(
                            harness,
                            "educationServiceCenters",
                            EducationServiceCenterBody(id, $"Region {index}")
                        )
                    )
                );
            }

            var agencies = new ConcurrentBag<(long Id, string Path)>();
            for (int index = 1; index <= RootLocalEducationAgencies; index++)
            {
                long id = 200_000 + index;
                agencies.Add(
                    (
                        id,
                        await CreateAsync(
                            harness,
                            "localEducationAgencies",
                            LocalEducationAgencyBody(
                                id,
                                $"District {id}",
                                11 + (index % EducationServiceCenters),
                                parent: null
                            )
                        )
                    )
                );
            }

            await ParallelAsync(
                RootLocalEducationAgencies + 1,
                localEducationAgencies,
                async index =>
                {
                    long id = 200_000 + index;
                    long parent = 200_000 + 1 + (index % RootLocalEducationAgencies);
                    agencies.Add(
                        (
                            id,
                            await CreateAsync(
                                harness,
                                "localEducationAgencies",
                                LocalEducationAgencyBody(
                                    id,
                                    $"District {id}",
                                    11 + (index % EducationServiceCenters),
                                    parent
                                )
                            )
                        )
                    );
                }
            );
            _localEducationAgencies.AddRange(agencies.OrderBy(static agency => agency.Id));
            _parents =
            [
                .. _localEducationAgencies.Select(
                    static (agency, index) =>
                        index < RootLocalEducationAgencies
                            ? 0L
                            : 200_000 + 1 + ((agency.Id - 200_000) % RootLocalEducationAgencies)
                ),
            ];

            var created = new ConcurrentBag<(long Id, string Path)>();
            await ParallelAsync(
                1,
                schools,
                async index =>
                {
                    long id = 1_000_000 + index;
                    long agency = 200_000 + 1 + (index % localEducationAgencies);
                    created.Add(
                        (id, await CreateAsync(harness, "schools", SchoolBody(id, $"School {id}", agency)))
                    );
                }
            );
            _schools.AddRange(created.OrderBy(static school => school.Id));
            _schoolAgencies =
            [
                .. _schools.Select(school =>
                    200_000 + 1 + ((school.Id - 1_000_000) % localEducationAgencies)
                ),
            ];
        }

        public (string Path, JsonObject Body) StateEducationAgency(string name) =>
            (_stateEducationAgencyPath, WithId(StateEducationAgencyBody(1, name), _stateEducationAgencyPath));

        public (string Path, JsonObject Body) EducationServiceCenter(Random random, string name)
        {
            var (id, path) = _educationServiceCenters[random.Next(_educationServiceCenters.Count)];
            return (path, WithId(EducationServiceCenterBody(id, name), path));
        }

        public (string Path, JsonObject Body) LocalEducationAgency(
            Random random,
            string name,
            bool changeParent
        )
        {
            int index = random.Next(RootLocalEducationAgencies, _localEducationAgencies.Count);
            var (id, path) = _localEducationAgencies[index];
            // Writers race on the same rows; the tracked value is only what this process last sent.
            long parent = changeParent
                ? 200_000 + 1 + random.Next(RootLocalEducationAgencies)
                : Volatile.Read(ref _parents[index]);
            Volatile.Write(ref _parents[index], parent);
            long serviceCenter = 11 + ((id - 200_000) % EducationServiceCenters);
            return (path, WithId(LocalEducationAgencyBody(id, name, serviceCenter, parent), path));
        }

        public (string Path, JsonObject Body) School(
            Random random,
            string name,
            bool changeLocalEducationAgency
        )
        {
            int index = random.Next(_schools.Count);
            var (id, path) = _schools[index];
            long agency = changeLocalEducationAgency
                ? _localEducationAgencies[random.Next(_localEducationAgencies.Count)].Id
                : Volatile.Read(ref _schoolAgencies[index]);
            Volatile.Write(ref _schoolAgencies[index], agency);
            return (path, WithId(SchoolBody(id, name, agency), path));
        }

        private JsonArray Categories() =>
            new(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        $"{ns}/EducationOrganizationCategoryDescriptor#Other",
                }
            );

        private JsonObject StateEducationAgencyBody(long id, string name) =>
            new()
            {
                ["stateEducationAgencyId"] = id,
                ["nameOfInstitution"] = name,
                ["categories"] = Categories(),
            };

        private JsonObject EducationServiceCenterBody(long id, string name) =>
            new()
            {
                ["educationServiceCenterId"] = id,
                ["nameOfInstitution"] = name,
                ["categories"] = Categories(),
                ["stateEducationAgencyReference"] = new JsonObject { ["stateEducationAgencyId"] = 1 },
            };

        private JsonObject LocalEducationAgencyBody(long id, string name, long serviceCenter, long? parent)
        {
            var body = new JsonObject
            {
                ["localEducationAgencyId"] = id,
                ["nameOfInstitution"] = name,
                ["localEducationAgencyCategoryDescriptor"] =
                    $"{ns}/LocalEducationAgencyCategoryDescriptor#Independent",
                ["categories"] = Categories(),
                ["educationServiceCenterReference"] = new JsonObject
                {
                    ["educationServiceCenterId"] = serviceCenter,
                },
                ["stateEducationAgencyReference"] = new JsonObject { ["stateEducationAgencyId"] = 1 },
            };
            if (parent is { } parentId)
            {
                body["parentLocalEducationAgencyReference"] = new JsonObject
                {
                    ["localEducationAgencyId"] = parentId,
                };
            }

            return body;
        }

        private JsonObject SchoolBody(long id, string name, long agency) =>
            new()
            {
                ["schoolId"] = id,
                ["nameOfInstitution"] = name,
                ["educationOrganizationCategories"] = Categories(),
                ["gradeLevels"] = new JsonArray(
                    new JsonObject { ["gradeLevelDescriptor"] = $"{ns}/GradeLevelDescriptor#Tenth grade" }
                ),
                ["localEducationAgencyReference"] = new JsonObject { ["localEducationAgencyId"] = agency },
            };

        private static JsonObject WithId(JsonObject body, string path)
        {
            body["id"] = path[(path.LastIndexOf('/') + 1)..];
            return body;
        }

        private static async Task<string> CreateAsync(
            ApiIntegrationHarness harness,
            string resource,
            JsonObject body
        )
        {
            var (status, responseBody, location) = await PostAsync(harness, $"/data/ed-fi/{resource}", body);
            status.Should().Be(HttpStatusCode.Created, $"seeding {resource} returned {responseBody}");
            return location
                ?? throw new InvalidOperationException($"Seeding {resource} returned no Location.");
        }

        private static Task ParallelAsync(int first, int last, Func<int, Task> action)
        {
            int next = first - 1;
            return Task.WhenAll(
                Enumerable
                    .Range(0, SeedWorkers)
                    .Select(_ =>
                        Task.Run(async () =>
                        {
                            for (
                                int index = Interlocked.Increment(ref next);
                                index <= last;
                                index = Interlocked.Increment(ref next)
                            )
                            {
                                await action(index);
                            }
                        })
                    )
            );
        }
    }

    private sealed class Phase
    {
        private long _samples;
        private long _writerWaitingSamples;
        private long _writerWaitingSessions;
        private long _readerWaitingSamples;

        public ConcurrentDictionary<string, ConcurrentBag<double>> WriteMilliseconds { get; } = new();
        public ConcurrentDictionary<string, int> Failures { get; } = new();
        public ConcurrentBag<double> ReadMilliseconds { get; } = [];
        public ConcurrentBag<string> ReadOutcomes { get; } = [];
        public long Deadlocks { get; set; }
        public (long Waits, long Milliseconds)? LockWaits { get; set; }
        public string DeadlockGraphs { get; set; } = "not captured";

        public void Sample(int writersWaiting, int readerWaiting)
        {
            _samples++;
            if (writersWaiting > 0)
            {
                _writerWaitingSamples++;
                _writerWaitingSessions += writersWaiting;
            }

            if (readerWaiting > 0)
            {
                _readerWaitingSamples++;
            }
        }

        public string Describe(string name, int writers)
        {
            var all = WriteMilliseconds.Values.SelectMany(static values => values).ToList();
            string byKind = string.Join(
                " ",
                WriteMilliseconds
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key}_p95={Percentile([.. entry.Value.Order()], 0.95):F1}")
            );
            string failures = Failures.IsEmpty
                ? "none"
                : string.Join(",", Failures.Select(static failure => $"{failure.Key}x{failure.Value}"));
            string outcomes = ReadOutcomes.IsEmpty
                ? "none"
                : string.Join(
                    ",",
                    ReadOutcomes
                        .GroupBy(static outcome => outcome)
                        .Select(static group => $"{group.Key}:{group.Count()}")
                );
            string lockWaits = LockWaits is { } waits
                ? $"lock_waits={waits.Waits} lock_wait_ms={waits.Milliseconds} "
                : "";
            return $"MEASURE phase={name} writers={writers} ops={all.Count} op_ms {Summary(all)} {byKind} "
                + $"failures={failures} deadlocks={Deadlocks} {lockWaits}"
                + $"sampled: writer_waiting={_writerWaitingSamples}/{_samples} writer_waiting_sessions={_writerWaitingSessions} "
                + $"reader_waiting={_readerWaitingSamples}/{_samples} "
                + $"reads={ReadOutcomes.Count} read_ms {Summary(ReadMilliseconds)} outcomes={outcomes} "
                + $"deadlock_graphs={DeadlockGraphs}";
        }
    }
}
