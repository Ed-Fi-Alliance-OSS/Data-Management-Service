// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal static class ComparisonProfiles
{
    public static PerfLoadedBenchmarkProfile Committed() =>
        PerfBenchmarkProfileLoader.LoadFile(PerfBenchmarkProfileLoader.DefaultPath);

    public static PerfLoadedBenchmarkProfile Edited(Action<JsonObject> edit)
    {
        JsonObject profile = ProfileTestValues.Committed();
        edit(profile);
        return ProfileTestValues.Load(profile);
    }

    /// <summary>
    /// A frozen profile (version 1.0.0, factors 0.4) whose fingerprint for the builder's
    /// provider is the builder's own environment, so the run matches it.
    /// </summary>
    public static PerfLoadedBenchmarkProfile FrozenFor(
        ComparisonBuilder builder,
        Action<JsonObject>? edit = null
    ) => Frozen(builder.Provider, builder.Fingerprint(), edit);

    public static PerfLoadedBenchmarkProfile Frozen(
        string provider,
        PerfBenchmarkEnvironmentFingerprint fingerprint,
        Action<JsonObject>? edit = null
    )
    {
        JsonObject profile = ProfileTestValues.Frozen();
        profile["environment"]!["qualifiedEnvironments"]![provider] = JsonNode.Parse(
            PerfBenchmarkJson.Serialize(fingerprint)
        );
        edit?.Invoke(profile);
        return ProfileTestValues.Load(profile);
    }

    /// <summary>
    /// The frozen detection factor ProfileTestValues.Frozen() sets on every scenario.
    /// </summary>
    public const decimal FrozenFactor = 0.4m;

    public static IReadOnlyDictionary<string, decimal> FrozenInjection =>
        new Dictionary<string, decimal>
        {
            [PerfBenchmarkScenarios.StudentPutCollectionUpdate] = FrozenFactor,
            [ComparisonBuilder.ZeroSmall] = FrozenFactor,
        };
}

/// <summary>
/// Builds a fully valid comparison input that tests then bend one fact at a time. Each side
/// has a 30 s settle, a 10 s idle and a 100 s side phase, host samples every 1 s and Docker
/// samples every 2 s, and seven 3 s measured windows inside the side phase: the six cells at
/// side offsets 50, 60, …, 100 s and the write at 110 s. Every baseline p50 is 10 ms; a
/// candidate's p50 is 10 ms × its ratio.
/// </summary>
internal sealed class ComparisonBuilder
{
    public const string Reference = "df7e23425beef2de46fa98b3b5b48578cb2d3692";
    public static readonly string OtherCommit = new('c', 40);
    public static readonly string HarnessCommit = new('d', 40);
    public const string RunId = "run-0001";
    public const string ZeroSmall = "traditional-offset-zero/25";
    public const string DeepLarge = "traditional-offset-deep/500";
    public const string Write = PerfBenchmarkScenarios.StudentPutCollectionUpdate;
    public static readonly DateTimeOffset Origin = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    public PerfLoadedBenchmarkProfile Profile { get; set; } = ComparisonProfiles.Committed();

    /// <summary>
    /// The profile the report loads; the run's own profile when null.
    /// </summary>
    public PerfLoadedBenchmarkProfile? ReportProfile { get; set; }

    public string Provider { get; init; } = "postgresql";

    public string Purpose { get; init; } = PerfBenchmarkComparison.Candidate;

    public bool Smoke { get; init; }

    /// <summary>
    /// The workload the sides actually run, when it differs from the plan's.
    /// </summary>
    public bool? SidesRunSmoke { get; init; }

    public int Pairs { get; init; } = 5;

    public string BaselineCommit { get; init; } = Reference;

    public string? CandidateCommit { get; init; }

    public IReadOnlyDictionary<string, decimal> Injection { get; init; } = new Dictionary<string, decimal>();

    /// <summary>
    /// Candidate p50 ratios per scenario, one per pair; 1.0 when absent.
    /// </summary>
    public Dictionary<string, decimal[]> CandidateRatios { get; } = [];

    /// <summary>
    /// Raw candidate samples per scenario, replacing the constant ones in every pair.
    /// </summary>
    public Dictionary<string, double[]> CandidateSamples { get; } = [];

    public List<Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence>> SideEdits { get; } = [];

    public List<Func<PerfBenchmarkRunPlan, PerfBenchmarkRunPlan>> PlanEdits { get; } = [];

    public HashSet<(int Pair, string Role)> OmittedSides { get; } = [];

    public string Candidate =>
        CandidateCommit ?? (Purpose == PerfBenchmarkComparison.Candidate ? OtherCommit : Reference);

    public PerfBenchmarkComparisonResult Compare() => PerfBenchmarkComparison.Compare(Build());

    /// <summary>
    /// Applies <paramref name="edit" /> to the one side at (pair, role).
    /// </summary>
    public ComparisonBuilder EditSide(
        int pair,
        string role,
        Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit
    )
    {
        SideEdits.Add(side => side.Side.Pair == pair && side.Side.Role == role ? edit(side) : side);
        return this;
    }

    public ComparisonBuilder EditSides(Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit)
    {
        SideEdits.Add(edit);
        return this;
    }

    public PerfBenchmarkEnvironmentFingerprint Fingerprint() =>
        PerfBenchmarkComparison.FingerprintOf(BuildSide(1, PerfBenchmarkSideResult.BaselineRole));

    public PerfBenchmarkComparisonInput Build()
    {
        List<PerfBenchmarkPlannedSide> order = [];
        for (int pair = 1; pair <= Pairs; pair++)
        {
            order.Add(
                new(
                    pair,
                    PerfBenchmarkSideResult.BaselineRole,
                    PositionOf(pair, PerfBenchmarkSideResult.BaselineRole)
                )
            );
            order.Add(
                new(
                    pair,
                    PerfBenchmarkSideResult.CandidateRole,
                    PositionOf(pair, PerfBenchmarkSideResult.CandidateRole)
                )
            );
        }

        PerfBenchmarkRunPlan plan = new(
            PerfBenchmarkRunPlan.CurrentSchemaVersion,
            RunId,
            Provider,
            Purpose,
            Smoke,
            BaselineCommit,
            Candidate,
            HarnessCommit,
            Profile.Sha256,
            Injection,
            order
        );
        plan = PlanEdits.Aggregate(plan, (current, edit) => edit(current));

        List<PerfBenchmarkSideEvidence> sides =
        [
            .. order
                .Where(side => !OmittedSides.Contains((side.Pair, side.Role)))
                .Select(side =>
                    SideEdits.Aggregate(BuildSide(side.Pair, side.Role), (current, edit) => edit(current))
                ),
        ];
        return new PerfBenchmarkComparisonInput(plan, ReportProfile ?? Profile, sides, []);
    }

    public static int PositionOf(int pair, string role) =>
        (pair % 2 == 1) == (role == PerfBenchmarkSideResult.BaselineRole) ? 1 : 2;

    public static DateTimeOffset SideStart(int pair, string role) =>
        Origin.AddSeconds((((pair - 1) * 2) + PositionOf(pair, role) - 1) * 1000);

    /// <summary>
    /// The measured window of a scenario, in seconds after the side's start.
    /// </summary>
    public static (int Start, int End) WindowSeconds(string scenario) =>
        scenario == Write
            ? (110, 113)
            : (
                50 + (10 * PerfBenchmarkScenarios.AllIds.ToList().IndexOf(scenario)),
                53 + (10 * PerfBenchmarkScenarios.AllIds.ToList().IndexOf(scenario))
            );

    private PerfBenchmarkSideEvidence BuildSide(int pair, string role)
    {
        PerfBenchmarkProfile profile = Profile.Profile;
        bool smoke = SidesRunSmoke ?? Smoke;
        PerfBenchmarkSideWorkload workload = smoke
            ? new(
                Provider,
                profile.SmokeWorkload.Fixture,
                profile.SmokeWorkload.DeepOffset,
                profile.SmokeWorkload.Reads,
                profile.SmokeWorkload.Writes
            )
            : new(
                Provider,
                profile.Workload.Fixture,
                profile.Workload.DeepOffset,
                profile.Workload.Reads,
                profile.Workload.Writes
            );
        bool candidate = role == PerfBenchmarkSideResult.CandidateRole;
        DateTimeOffset start = SideStart(pair, role);
        PerfMeasuredWindow WindowOf(string scenario)
        {
            (int from, int to) = WindowSeconds(scenario);
            return new PerfMeasuredWindow(start.AddSeconds(from), start.AddSeconds(to), 100);
        }

        PerfLatencySummary LatencyOf(string scenario, int samples)
        {
            if (candidate && CandidateSamples.TryGetValue(scenario, out double[]? raw))
            {
                return PerfLatencyMeasurement.Summarize(raw);
            }

            decimal ratio =
                candidate && CandidateRatios.TryGetValue(scenario, out decimal[]? ratios)
                    ? ratios[pair - 1]
                    : 1m;
            return PerfLatencyMeasurement.Summarize([.. Enumerable.Repeat((double)(10m * ratio), samples)]);
        }

        IReadOnlyDictionary<string, decimal> injection = candidate
            ? Injection
            : new Dictionary<string, decimal>();
        PerfBenchmarkSideResult side = new(
            PerfBenchmarkSideResult.CurrentSchemaVersion,
            RunId,
            new PerfBenchmarkSideStatus(PerfBenchmarkSideStatus.Completed),
            role,
            pair,
            PositionOf(pair, role),
            profile.ProfileVersion,
            Profile.Sha256,
            workload,
            [
                .. PerfBenchmarkScenarios
                    .AllIds.SkipLast(1)
                    .Select(id => new PerfReadCellCounts(
                        id,
                        workload.Reads.WarmupIterations,
                        workload.Reads.WarmupIterations,
                        0,
                        workload.Reads.MeasuredIterations,
                        workload.Reads.MeasuredIterations,
                        0,
                        WindowOf(id)
                    )),
            ],
            new PerfWriteScenarioResult(
                Write,
                workload.Writes.WarmupIterations,
                workload.Writes.WarmupIterations,
                workload.Writes.MeasuredIterations,
                workload.Writes.MeasuredIterations,
                0,
                [new PerfCommandCountFrequency(2, workload.Writes.MeasuredIterations)],
                WindowOf(Write),
                LatencyOf(Write, workload.Writes.MeasuredIterations)
            ),
            profile.Workload.Connection[Provider],
            "effective-schema-hash",
            "Warning",
            OwnershipPublished: true,
            new PerfBenchmarkInstrumentation(true, true),
            new PerfBenchmarkInjection(
                injection,
                PerfBenchmarkScenarios.AllIds.ToDictionary(
                    id => id,
                    id => injection.ContainsKey(id) ? RequestsOf(id, workload) : 0
                )
            )
        );

        PerfBenchmarkEnvironment environment = new(
            PerfBenchmarkEnvironment.CurrentSchemaVersion,
            RunId,
            pair,
            role,
            "Windows",
            "x64",
            "10.0.26300",
            "High performance",
            AcPowerOnline: true,
            "28.0.1",
            8,
            17_179_869_184,
            0,
            8_589_934_592,
            "volume",
            CompetingHarnessProcess: false,
            OtherProviderContainerRunning: false,
            [
                new(PerfBenchmarkMetrics.SettlePhase, start, start.AddSeconds(30)),
                new(PerfBenchmarkMetrics.IdlePhase, start.AddSeconds(30), start.AddSeconds(40)),
                new(PerfBenchmarkMetrics.SidePhase, start.AddSeconds(40), start.AddSeconds(140)),
            ],
            SideExitCode: 0
        );

        IReadOnlyList<PerfExecutedCell> cells = PerfScenarioExecutor.CellsInExecutionOrder(
            workload.DeepOffset
        );
        PerfBenchmarkReadRun reads = new(
            Provider,
            HarnessCommit,
            candidate ? Candidate : BaselineCommit,
            workload.Fixture,
            workload.DeepOffset,
            workload.Reads.WarmupIterations,
            workload.Reads.MeasuredIterations,
            cells,
            [
                .. cells.Select(cell => new PerfBenchmarkReadCell(
                    cell.ScenarioId,
                    cell.PageSize,
                    cell.Offset,
                    workload.Reads.WarmupIterations,
                    workload.Reads.MeasuredIterations,
                    1,
                    LatencyOf(
                        PerfBenchmarkScenarios.ReadCellId(cell.ScenarioId, cell.PageSize),
                        workload.Reads.MeasuredIterations
                    ),
                    PerfLatencyMeasurement.Summarize([
                        .. Enumerable.Repeat(1.0, workload.Reads.MeasuredIterations),
                    ])
                )),
            ],
            new PerfHostIdentity(
                "Microsoft Windows 10.0.26300",
                "X64",
                "Example CPU",
                16,
                34_065_952_768,
                "10.0.15",
                false,
                "8a6a4d1c1fb7dda0"
            ),
            PerfServerIdentity.Create(
                "PostgreSQL 18.6",
                "postgres:18.6-alpine",
                "sha256:" + new string('7', 64),
                "local docker volume",
                "host=127.0.0.1;port=5435",
                [new PerfSetting("work_mem", "4096"), new PerfSetting("jit", "on")]
            )
        );

        return new PerfBenchmarkSideEvidence(side, environment, reads, Samples(start));
    }

    /// <summary>
    /// Host samples every second and Docker samples every two seconds across the whole side,
    /// tagged with the phase the time falls in. Workload CPU is high during the side phase.
    /// </summary>
    private static List<PerfBenchmarkSample> Samples(DateTimeOffset start)
    {
        List<PerfBenchmarkSample> samples = [];
        for (int second = 0; second < 140; second++)
        {
            string phase = PhaseAt(second);
            bool side = phase == PerfBenchmarkMetrics.SidePhase;
            DateTimeOffset time = start.AddSeconds(second);
            samples.Add(new(time, phase, PerfBenchmarkMetrics.HostCpuPercent, side ? 60 : 5));
            samples.Add(new(time, phase, PerfBenchmarkMetrics.HostAvailableMb, 8_000));
            if (second % 2 == 0)
            {
                samples.Add(new(time, phase, PerfBenchmarkMetrics.DatabaseCpuPercent, side ? 80 : 1));
                samples.Add(new(time, phase, PerfBenchmarkMetrics.DatabaseMemoryPercent, 30));
                samples.Add(new(time, phase, PerfBenchmarkMetrics.DatabaseBlockIoBytes, 1_000 * second));
            }
        }

        return samples;
    }

    private static int RequestsOf(string scenario, PerfBenchmarkSideWorkload workload) =>
        scenario == Write
            ? workload.Writes.WarmupIterations + workload.Writes.MeasuredIterations
            : workload.Reads.WarmupIterations + workload.Reads.MeasuredIterations;

    public static string PhaseAt(int second) =>
        second switch
        {
            < 30 => PerfBenchmarkMetrics.SettlePhase,
            < 40 => PerfBenchmarkMetrics.IdlePhase,
            _ => PerfBenchmarkMetrics.SidePhase,
        };

    /// <summary>
    /// A side edit that drops the samples of the given metrics at the given side-relative
    /// seconds.
    /// </summary>
    public static Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> DropSamples(
        IEnumerable<int> seconds,
        params string[] metrics
    )
    {
        HashSet<int> drop = [.. seconds];
        return side =>
        {
            DateTimeOffset start = SideStart(side.Side.Pair, side.Side.Role);
            return side with
            {
                Samples =
                [
                    .. side.Samples.Where(sample =>
                        !(
                            metrics.Contains(sample.Metric)
                            && drop.Contains((int)(sample.TimestampUtc - start).TotalSeconds)
                        )
                    ),
                ],
            };
        };
    }

    /// <summary>
    /// A side edit that sets the value of the given metric at every sample in a phase (or at
    /// one side-relative second).
    /// </summary>
    public static Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> SetSamples(
        string metric,
        double value,
        Func<int, bool> atSecond
    ) =>
        side =>
        {
            DateTimeOffset start = SideStart(side.Side.Pair, side.Side.Role);
            return side with
            {
                Samples =
                [
                    .. side.Samples.Select(sample =>
                        sample.Metric == metric && atSecond((int)(sample.TimestampUtc - start).TotalSeconds)
                            ? sample with
                            {
                                Value = value,
                            }
                            : sample
                    ),
                ],
            };
        };

    /// <summary>
    /// The samples as <c>samples.csv</c> lines.
    /// </summary>
    public static IEnumerable<string> ToCsv(IEnumerable<PerfBenchmarkSample> samples) =>
        [
            PerfBenchmarkMetrics.SamplesHeader,
            .. samples.Select(sample =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{sample.TimestampUtc:O},{sample.Phase},{sample.Metric},{sample.Value}"
                )
            ),
        ];

    /// <summary>
    /// Writes the input as the wrapper and sides would lay it out on disk. Each side's paging run
    /// gets its own run id, as the live timestamped ids differ, and is written before
    /// <c>benchmark-side.json</c>, which names it and pins its results.
    /// </summary>
    public static void WriteDirectory(string directory, PerfBenchmarkComparisonInput input)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, PerfBenchmarkRunPlan.FileName),
            PerfBenchmarkJson.Serialize(input.Plan)
        );
        foreach (PerfBenchmarkSideEvidence side in input.Sides)
        {
            string sideDirectory = PerfBenchmarkComparisonLoader.SideDirectory(
                directory,
                side.Side.Pair,
                side.Side.Role
            );
            Directory.CreateDirectory(sideDirectory);
            File.WriteAllText(
                Path.Combine(sideDirectory, PerfBenchmarkSideResult.FileName),
                PerfBenchmarkSideResults.Serialize(
                    side.Side with
                    {
                        ReadRun = WriteReadRun(sideDirectory, side),
                    }
                )
            );
            File.WriteAllText(
                Path.Combine(sideDirectory, PerfBenchmarkEnvironment.FileName),
                PerfBenchmarkJson.Serialize(side.Environment)
            );
            File.WriteAllLines(
                Path.Combine(sideDirectory, PerfBenchmarkMetrics.SamplesFileName),
                ToCsv(side.Samples),
                new UTF8Encoding(false)
            );
        }
    }

    private static PerfBenchmarkReadRunReference? WriteReadRun(
        string sideDirectory,
        PerfBenchmarkSideEvidence side
    )
    {
        if (side.Reads is not { } reads)
        {
            return null;
        }

        string runId = $"{reads.Provider}-pair{side.Side.Pair:D2}-{side.Side.Role}";
        string runDirectory = Path.Combine(sideDirectory, runId);
        Directory.CreateDirectory(runDirectory);
        PerfRunManifest manifest = PerfRunManifest.Create(
            new PerfRunIdentity(runId, "2026-11-23T12:00:00Z", reads.Provider),
            new PerfCommitIdentity(reads.RunnerCommit, reads.SubjectCommit, []),
            new PerfManifestFixture(reads.FixtureId, 500_000, reads.DeepOffset),
            new PerfIterationPlan(reads.WarmupIterations, reads.MeasuredIterations, reads.CellExecutionOrder),
            PerfEnvironmentIdentity.Create(reads.Server, reads.Host, [new PerfSetting("Npgsql", "8.0.4")])
        );
        PerfResultsDocument results = PerfResultsDocument.Create(
            reads.Cells.Select(cell => new PerfScenarioResult(
                reads.Provider,
                cell.ScenarioId,
                cell.PageSize,
                cell.Offset,
                cell.PageSize,
                cell.CommandCountPerRequest,
                cell.WarmupIterations,
                cell.MeasuredIterations,
                cell.LatencyMs,
                cell.DriverExecuteMs,
                new PerfDatabaseMetrics(1, 0, 1.0, null, null, null, null),
                "plans/plan.json",
                new string('a', 64),
                reads.RunnerCommit,
                reads.SubjectCommit
            ))
        );
        File.WriteAllText(
            Path.Combine(runDirectory, "run-manifest.json"),
            PerfArtifactJson.Serialize(manifest)
        );
        File.WriteAllText(Path.Combine(runDirectory, "results.json"), PerfArtifactJson.Serialize(results));
        return PerfBenchmarkReadRunReference.Of(runDirectory);
    }
}
