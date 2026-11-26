// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;

namespace EdFi.DataManagementService.Performance.Harness.Results;

public sealed record PerfBenchmarkPlannedSide(int Pair, string Role, int Position);

/// <summary>
/// <c>run-plan.json</c>, written by the wrapper before the first side. <see cref="Injection" />
/// is the effective spec every candidate side runs with (empty when none).
/// </summary>
public sealed record PerfBenchmarkRunPlan(
    string SchemaVersion,
    string RunId,
    string Provider,
    string Purpose,
    bool Smoke,
    string BaselineCommit,
    string CandidateCommit,
    string HarnessCommit,
    string ProfileSha256,
    IReadOnlyDictionary<string, decimal> Injection,
    IReadOnlyList<PerfBenchmarkPlannedSide> Order
)
{
    public const string CurrentSchemaVersion = "benchmark-run-plan 1.0.0";
    public const string FileName = "run-plan.json";

    public int PairCount => Order.Select(side => side.Pair).Distinct().Count();
}

/// <summary>
/// A collector phase (<c>settle</c>, <c>idle</c>, <c>side</c>) as the wrapper marked it.
/// </summary>
public sealed record PerfBenchmarkPhaseWindow(string Name, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>
/// <c>environment.json</c>: the host, Docker and database-container facts of one side, and
/// its phase windows, written by the wrapper. <see cref="SideExitCode" /> is the side process's
/// exit code, recorded only by the rewrite after the process exits; null when it never was.
/// </summary>
public sealed record PerfBenchmarkEnvironment(
    string SchemaVersion,
    string RunId,
    int Pair,
    string Role,
    string OsFamily,
    string Architecture,
    string OsBuild,
    string PowerPlan,
    bool AcPowerOnline,
    string DockerVersion,
    int DockerVmCpus,
    long DockerVmMemoryBytes,
    decimal DatabaseCpuLimit,
    long DatabaseMemoryLimitBytes,
    string StorageMountType,
    bool CompetingHarnessProcess,
    bool OtherProviderContainerRunning,
    IReadOnlyList<PerfBenchmarkPhaseWindow> Phases,
    bool TrailingSampleTimeout = false,
    IReadOnlyList<string>? MissingTrailingMetrics = null,
    int? SideExitCode = null
)
{
    public const string CurrentSchemaVersion = "benchmark-environment 1.0.0";
    public const string FileName = "environment.json";
}

/// <summary>
/// One collector sample: a row of <c>samples.csv</c> (<c>timestampUtc,phase,metric,value</c>).
/// </summary>
public sealed record PerfBenchmarkSample(
    DateTimeOffset TimestampUtc,
    string Phase,
    string Metric,
    double Value
);

/// <summary>
/// The collector's metrics and the phases it marks.
/// </summary>
public static class PerfBenchmarkMetrics
{
    public const string HostCpuPercent = "host-cpu-percent";
    public const string HostAvailableMb = "host-available-mb";
    public const string DatabaseCpuPercent = "db-cpu-percent";
    public const string DatabaseMemoryPercent = "db-memory-percent";
    public const string DatabaseBlockIoBytes = "db-block-io-bytes";

    public const string SettlePhase = "settle";
    public const string IdlePhase = "idle";
    public const string SidePhase = "side";

    public const string SamplesFileName = "samples.csv";
    public const string SamplesHeader = "timestampUtc,phase,metric,value";

    public static readonly IReadOnlyList<string> All =
    [
        HostCpuPercent,
        HostAvailableMb,
        DatabaseCpuPercent,
        DatabaseMemoryPercent,
        DatabaseBlockIoBytes,
    ];

    public static readonly IReadOnlyList<string> Phases = [SettlePhase, IdlePhase, SidePhase];

    public static bool IsHostMetric(string metric) => metric is HostCpuPercent or HostAvailableMb;

    public static IReadOnlyList<PerfBenchmarkSample> ParseCsv(IEnumerable<string> lines)
    {
        List<PerfBenchmarkSample> samples = [];
        using IEnumerator<string> line = lines.GetEnumerator();
        if (!line.MoveNext() || line.Current.Trim() != SamplesHeader)
        {
            throw new PerfArtifactValidationException([
                $"{SamplesFileName}: the header must be '{SamplesHeader}'.",
            ]);
        }

        int number = 1;
        while (line.MoveNext())
        {
            number++;
            if (string.IsNullOrWhiteSpace(line.Current))
            {
                continue;
            }

            string[] fields = line.Current.Split(',');
            if (
                fields.Length != 4
                || !DateTimeOffset.TryParse(
                    fields[0],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset timestamp
                )
                || !double.TryParse(
                    fields[3],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value
                )
            )
            {
                throw new PerfArtifactValidationException([$"{SamplesFileName} line {number} is malformed."]);
            }

            samples.Add(new PerfBenchmarkSample(timestamp.ToUniversalTime(), fields[1], fields[2], value));
        }

        return samples;
    }
}

public sealed record PerfBenchmarkReadCell(
    string ScenarioId,
    int PageSize,
    long Offset,
    int WarmupIterations,
    int MeasuredIterations,
    int CommandCountPerRequest,
    PerfLatencySummary LatencyMs,
    PerfLatencySummary DriverExecuteMs
);

/// <summary>
/// What the comparison needs from one side's 1.3.0 paging run (manifest and results).
/// </summary>
public sealed record PerfBenchmarkReadRun(
    string Provider,
    string RunnerCommit,
    string SubjectCommit,
    string FixtureId,
    long DeepOffset,
    int WarmupIterations,
    int MeasuredIterations,
    IReadOnlyList<PerfExecutedCell> CellExecutionOrder,
    IReadOnlyList<PerfBenchmarkReadCell> Cells,
    PerfHostIdentity Host,
    PerfServerIdentity Server
)
{
    public static PerfBenchmarkReadRun From(PerfRunManifest manifest, PerfResultsDocument results) =>
        new(
            manifest.Run.Provider,
            manifest.Commits.RunnerCommit,
            manifest.Commits.SubjectCommit,
            manifest.Fixture.FixtureId,
            manifest.Fixture.DeepOffset,
            manifest.Iterations.WarmupIterations,
            manifest.Iterations.MeasuredIterations,
            manifest.Iterations.CellExecutionOrder,
            [
                .. results.Results.Select(row => new PerfBenchmarkReadCell(
                    row.ScenarioId,
                    row.PageSize,
                    row.Offset,
                    row.WarmupIterations,
                    row.MeasuredIterations,
                    row.CommandCountPerRequest,
                    row.LatencyMs,
                    row.DriverExecuteMs
                )),
            ],
            manifest.Environment.Host,
            manifest.Environment.Server
        );
}

/// <summary>
/// One side's evidence. <see cref="Reads" /> is null when the side failed before its paging
/// run wrote artifacts.
/// </summary>
public sealed record PerfBenchmarkSideEvidence(
    PerfBenchmarkSideResult Side,
    PerfBenchmarkEnvironment Environment,
    PerfBenchmarkReadRun? Reads,
    IReadOnlyList<PerfBenchmarkSample> Samples
);

/// <summary>
/// Everything one comparison judges. <see cref="Profile" /> is the profile the report itself
/// loaded; <see cref="LoadErrors" /> are side artifacts that could not be read.
/// </summary>
public sealed record PerfBenchmarkComparisonInput(
    PerfBenchmarkRunPlan Plan,
    PerfLoadedBenchmarkProfile Profile,
    IReadOnlyList<PerfBenchmarkSideEvidence> Sides,
    IReadOnlyList<string> LoadErrors
);

public sealed record PerfBenchmarkPairStatistics(
    int Pair,
    int SampleCount,
    double BaselineP50Ms,
    double CandidateP50Ms,
    decimal P50Ratio,
    double BaselineP95Ms,
    double CandidateP95Ms,
    decimal P95Ratio,
    double BaselineP99Ms,
    double CandidateP99Ms,
    double BaselineMeanMs,
    double CandidateMeanMs,
    string BaselineCommandCounts,
    string CandidateCommandCounts,
    double? BaselineDriverExecuteP50Ms = null,
    double? CandidateDriverExecuteP50Ms = null
);

/// <summary>
/// One scenario's verdict: the median of the per-pair p50 ratios against its threshold.
/// <see cref="Spread" /> is the largest |pair ratio − 1|.
/// </summary>
public sealed record PerfBenchmarkScenarioComparison(
    string ScenarioId,
    decimal Threshold,
    decimal MedianP50Ratio,
    decimal MedianP95Ratio,
    decimal Spread,
    bool Flagged,
    bool P95Flagged,
    string Result,
    string Reason,
    IReadOnlyList<PerfBenchmarkPairStatistics> Pairs
);

/// <summary>
/// Same-build calibration: each scenario's derived threshold, max(5%, 2 × spread) rounded up
/// to a whole percent, and the observed environment fingerprint.
/// </summary>
public sealed record PerfBenchmarkCalibration(
    IReadOnlyDictionary<string, decimal> DerivedThresholds,
    IReadOnlyDictionary<string, decimal> Spreads,
    PerfBenchmarkEnvironmentFingerprint Fingerprint
);

public sealed record PerfBenchmarkDetectionScenario(
    string ScenarioId,
    decimal ObservedIncrease,
    decimal BandLow,
    decimal BandHigh,
    bool Flagged,
    bool InBand,
    bool Passed
);

/// <summary>
/// The frozen detection rule on an injected run: every demonstration scenario flagged with its
/// observed increase in the band, and no other scenario flagged.
/// </summary>
public sealed record PerfBenchmarkDetection(
    bool Passed,
    IReadOnlyList<PerfBenchmarkDetectionScenario> Scenarios,
    IReadOnlyList<string> OtherFlaggedScenarios
);

/// <summary>
/// One demonstration scenario's sizing outcome. <see cref="NextFactor" /> is always defined
/// for an injected scenario: unchanged when in band, the suggestion when adjusting, and the
/// same input factor retried when inconclusive.
/// </summary>
public sealed record PerfBenchmarkSizing(
    string ScenarioId,
    decimal? InputFactor,
    decimal? ObservedIncrease,
    string Status,
    decimal? SuggestedFactor,
    decimal? NextFactor,
    string? Reason
)
{
    public const string InBand = "in-band";
    public const string Adjust = "adjust";
    public const string Inconclusive = "inconclusive";
}

public sealed record PerfBenchmarkComparisonResult(
    string Result,
    int ExitCode,
    string RunId,
    string Provider,
    string Purpose,
    bool Evidence,
    bool QualificationEligible,
    IReadOnlyList<string> Labels,
    string RunPlanProfileSha256,
    IReadOnlyList<string> InvalidReasons,
    IReadOnlyList<string> NotEligibleReasons,
    IReadOnlyList<string> PermittedDifferences,
    IReadOnlyList<PerfBenchmarkScenarioComparison> Scenarios,
    PerfBenchmarkCalibration? Calibration = null,
    PerfBenchmarkDetection? Detection = null,
    IReadOnlyList<PerfBenchmarkSizing>? Sizing = null
)
{
    public const string Acceptable = "acceptable";
    public const string SuspectedRegression = "suspected-regression";
    public const string Invalid = "invalid";

    public const string DemonstrationLabel = "demonstration";
    public const string UncalibratedLabel = "uncalibrated";
    public const string SmokeLabel = "smoke";
}

/// <summary>
/// The pure comparison engine. Its checks run in order: run-plan conformance, workload
/// conformance, validity, required values, consistency, injection, rules, qualified
/// environment, then statistics, the verdict, purpose eligibility and the run-kind blocks.
/// Any failed check makes the comparison invalid (exit 2); timing regressions are advisory
/// (exit 0).
/// </summary>
public static class PerfBenchmarkComparison
{
    /// <summary>
    /// The Jira acceptance criterion's minimum, independent of the profile's pair count.
    /// </summary>
    public const int MinimumQualificationPairs = 5;

    public const string Candidate = "Candidate";
    public const string Calibration = "Calibration";
    public const string Sizing = "Sizing";
    public const string Repeatability = "Repeatability";
    public const string Detection = "Detection";
    public const string BoundaryProbe = "BoundaryProbe";

    public static readonly IReadOnlyList<string> Purposes =
    [
        Candidate,
        Calibration,
        Sizing,
        Repeatability,
        Detection,
        BoundaryProbe,
    ];

    /// <summary>
    /// A sizing suggestion needs at least this observed increase to be meaningful.
    /// </summary>
    public const decimal MinimumSizingIncrease = 0.02m;

    public const decimal MinimumDerivedThreshold = 0.05m;

    public static PerfBenchmarkComparisonResult Compare(PerfBenchmarkComparisonInput input)
    {
        PerfBenchmarkRunPlan plan = input.Plan;
        PerfBenchmarkProfile profile = input.Profile.Profile;
        List<string> invalid = [.. input.LoadErrors];
        List<string> permitted = [];
        List<string> labels = [];

        if (plan.Smoke)
        {
            labels.Add(PerfBenchmarkComparisonResult.SmokeLabel);
        }

        if (plan.Injection.Count > 0)
        {
            labels.Add(PerfBenchmarkComparisonResult.DemonstrationLabel);
        }

        if (plan.Provider is not ("postgresql" or "mssql") || !Purposes.Contains(plan.Purpose))
        {
            invalid.Add($"run plan: unknown provider '{plan.Provider}' or purpose '{plan.Purpose}'.");
            return Finish(input, invalid, permitted, labels, [], false, [], null, null, null);
        }

        PerfBenchmarkSideWorkload expected = ExpectedWorkload(profile, plan);
        Dictionary<(int Pair, string Role), PerfBenchmarkSideEvidence> sides = CheckRunPlan(input, invalid);
        foreach (((int pair, string role), PerfBenchmarkSideEvidence side) in sides)
        {
            string at = $"pair {pair} {role}";
            CheckWorkload(at, side, expected, invalid);
            CheckValidity(at, side, expected, invalid);
            CheckRequiredValues(at, side, profile, plan.Provider, invalid);
            CheckInjection(at, side, plan, expected, invalid);
            CheckRules(at, side, profile.Environment, invalid);
        }

        CheckConsistency(sides.Values, invalid, permitted);
        CheckQualifiedEnvironment(sides.Values, profile, plan, invalid, labels);

        IReadOnlyList<PerfBenchmarkScenarioComparison> scenarios = Statistics(plan, profile, sides, invalid);
        bool valid = invalid.Count == 0;
        List<string> notEligible = Eligibility(plan, profile, valid);

        bool sameBuild = plan.BaselineCommit == plan.CandidateCommit;
        PerfBenchmarkCalibration? calibration =
            valid && sameBuild && plan.Injection.Count == 0
                ? CalibrationOf(scenarios, FingerprintOf(sides.Values.First()))
                : null;
        PerfBenchmarkDetection? detection =
            valid && plan.Injection.Count > 0 ? DetectionOf(scenarios, profile) : null;
        IReadOnlyList<PerfBenchmarkSizing>? sizing =
            valid && plan.Purpose == Sizing ? SizingOf(scenarios, profile, plan) : null;

        return Finish(
            input,
            invalid,
            permitted,
            labels,
            scenarios,
            notEligible.Count == 0,
            notEligible,
            calibration,
            detection,
            sizing
        );
    }

    /// <summary>
    /// The qualified-environment fingerprint of one side: host, platform, allocation and
    /// database facts plus the effective connection settings, never the profile hash or the
    /// harness commit.
    /// </summary>
    public static PerfBenchmarkEnvironmentFingerprint FingerprintOf(PerfBenchmarkSideEvidence side)
    {
        PerfBenchmarkEnvironment environment = side.Environment;
        PerfBenchmarkReadRun? reads = side.Reads;
        return new PerfBenchmarkEnvironmentFingerprint(
            environment.OsFamily,
            environment.Architecture,
            reads?.Host.MachineFingerprint ?? string.Empty,
            reads?.Host.CpuModel ?? string.Empty,
            reads?.Host.LogicalCores ?? 0,
            reads?.Host.TotalMemoryBytes ?? 0,
            environment.PowerPlan,
            environment.AcPowerOnline,
            environment.DockerVmCpus,
            environment.DockerVmMemoryBytes,
            environment.DatabaseCpuLimit,
            environment.DatabaseMemoryLimitBytes,
            reads?.Server.ImageDigest ?? string.Empty,
            reads?.Server.ServerVersion ?? string.Empty,
            [.. (reads?.Server.Settings ?? []).OrderBy(setting => setting.Name, StringComparer.Ordinal)],
            environment.StorageMountType,
            side.Side.EffectiveConnection
        );
    }

    /// <summary>
    /// Each fingerprint field as canonical JSON text, so two fingerprints compare field by field
    /// with settings in name order.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FingerprintFields(
        PerfBenchmarkEnvironmentFingerprint fingerprint
    )
    {
        PerfBenchmarkEnvironmentFingerprint canonical = fingerprint with
        {
            DatabaseSettings =
            [
                .. fingerprint.DatabaseSettings.OrderBy(setting => setting.Name, StringComparer.Ordinal),
            ],
        };
        JsonObject json = JsonNode.Parse(PerfBenchmarkJson.Serialize(canonical))!.AsObject();
        return json.ToDictionary(member => member.Key, member => member.Value?.ToJsonString() ?? "null");
    }

    private static PerfBenchmarkSideWorkload ExpectedWorkload(
        PerfBenchmarkProfile profile,
        PerfBenchmarkRunPlan plan
    ) =>
        plan.Smoke
            ? new(
                plan.Provider,
                profile.SmokeWorkload.Fixture,
                profile.SmokeWorkload.DeepOffset,
                profile.SmokeWorkload.Reads,
                profile.SmokeWorkload.Writes
            )
            : new(
                plan.Provider,
                profile.Workload.Fixture,
                profile.Workload.DeepOffset,
                profile.Workload.Reads,
                profile.Workload.Writes
            );

    /// <summary>
    /// Check 1: run-plan conformance and profile identity. Returns the planned sides found.
    /// </summary>
    private static Dictionary<(int Pair, string Role), PerfBenchmarkSideEvidence> CheckRunPlan(
        PerfBenchmarkComparisonInput input,
        List<string> invalid
    )
    {
        PerfBenchmarkRunPlan plan = input.Plan;
        PerfBenchmarkProfile profile = input.Profile.Profile;

        if (plan.SchemaVersion != PerfBenchmarkRunPlan.CurrentSchemaVersion)
        {
            invalid.Add($"run plan: schemaVersion must be '{PerfBenchmarkRunPlan.CurrentSchemaVersion}'.");
        }

        if (plan.ProfileSha256 != input.Profile.Sha256)
        {
            invalid.Add(
                $"profile identity: the run plan recorded profile {plan.ProfileSha256} but the report loaded "
                    + $"{input.Profile.Sha256}; a report must judge a run with the profile it ran with."
            );
        }

        if (plan.BaselineCommit != profile.ApprovedReferenceCommit)
        {
            invalid.Add(
                $"run plan: the baseline {plan.BaselineCommit} is not the approved reference {profile.ApprovedReferenceCommit}."
            );
        }

        if (plan.Purpose != Candidate && plan.CandidateCommit != plan.BaselineCommit)
        {
            invalid.Add(
                $"run plan: purpose {plan.Purpose} requires baseline = candidate = the approved reference; "
                    + $"the candidate is {plan.CandidateCommit}."
            );
        }

        // Exactly one baseline and one candidate per pair, pairs 1..N, odd pairs baseline first.
        int pairs = plan.PairCount;
        bool orderValid =
            plan.Order.Count == pairs * 2
            && Enumerable
                .Range(1, pairs)
                .All(pair =>
                {
                    int baselinePosition = pair % 2 == 1 ? 1 : 2;
                    return plan.Order.Contains(
                            new PerfBenchmarkPlannedSide(
                                pair,
                                PerfBenchmarkSideResult.BaselineRole,
                                baselinePosition
                            )
                        )
                        && plan.Order.Contains(
                            new PerfBenchmarkPlannedSide(
                                pair,
                                PerfBenchmarkSideResult.CandidateRole,
                                3 - baselinePosition
                            )
                        );
                });
        if (!orderValid)
        {
            invalid.Add(
                "run plan: the order must hold one baseline and one candidate for each pair 1..N, alternating "
                    + "(odd pairs run the baseline first)."
            );
        }

        Dictionary<(int, string), PerfBenchmarkSideEvidence> found = [];
        foreach (PerfBenchmarkSideEvidence side in input.Sides)
        {
            (int, string) key = (side.Side.Pair, side.Side.Role);
            PerfBenchmarkPlannedSide? planned = plan.Order.FirstOrDefault(entry =>
                entry.Pair == side.Side.Pair && entry.Role == side.Side.Role
            );
            if (planned is null || !found.TryAdd(key, side))
            {
                invalid.Add(
                    $"pair {side.Side.Pair} {side.Side.Role}: the side is not in the run plan or is duplicated."
                );
                continue;
            }

            CheckSideIdentity($"pair {planned.Pair} {planned.Role}", side, planned, input, invalid);
        }

        foreach (
            PerfBenchmarkPlannedSide planned in plan.Order.Where(entry =>
                !found.ContainsKey((entry.Pair, entry.Role))
            )
        )
        {
            invalid.Add($"pair {planned.Pair} {planned.Role}: the planned side is missing.");
        }

        return found;
    }

    private static void CheckSideIdentity(
        string at,
        PerfBenchmarkSideEvidence side,
        PerfBenchmarkPlannedSide planned,
        PerfBenchmarkComparisonInput input,
        List<string> invalid
    )
    {
        PerfBenchmarkRunPlan plan = input.Plan;
        if (side.Side.RunId != plan.RunId || side.Environment.RunId != plan.RunId)
        {
            invalid.Add(
                $"{at}: the side belongs to run {side.Side.RunId} (environment {side.Environment.RunId}), not "
                    + $"{plan.RunId}; sides from another attempt are never compared."
            );
        }

        if (side.Environment.Pair != planned.Pair || side.Environment.Role != planned.Role)
        {
            invalid.Add(
                $"{at}: environment.json describes pair {side.Environment.Pair} {side.Environment.Role}."
            );
        }

        if (side.Side.Position != planned.Position)
        {
            invalid.Add($"{at}: ran at position {side.Side.Position}; the plan says {planned.Position}.");
        }

        if (side.Side.ProfileSha256 != plan.ProfileSha256)
        {
            invalid.Add(
                $"{at}: profile identity: the side ran with profile {side.Side.ProfileSha256}, not {plan.ProfileSha256}."
            );
        }

        if (side.Reads is null)
        {
            return;
        }

        string expectedSubject =
            planned.Role == PerfBenchmarkSideResult.BaselineRole ? plan.BaselineCommit : plan.CandidateCommit;
        if (side.Reads.SubjectCommit != expectedSubject)
        {
            invalid.Add(
                $"{at}: subject commit {side.Reads.SubjectCommit} is not the declared {expectedSubject}."
            );
        }

        if (side.Reads.RunnerCommit != plan.HarnessCommit)
        {
            invalid.Add(
                $"{at}: runner commit {side.Reads.RunnerCommit} is not the harness commit {plan.HarnessCommit}."
            );
        }
    }

    /// <summary>
    /// Check 2: the workload the side observably ran equals the profile's (or the smoke
    /// workload), whatever the sides agree on between themselves.
    /// </summary>
    private static void CheckWorkload(
        string at,
        PerfBenchmarkSideEvidence side,
        PerfBenchmarkSideWorkload expected,
        List<string> invalid
    )
    {
        if (side.Side.Workload != expected)
        {
            invalid.Add(
                $"{at}: workload conformance: the side ran {Describe(side.Side.Workload)}; the profile requires {Describe(expected)}."
            );
        }

        PerfBenchmarkReadRun? reads = side.Reads;
        if (reads is null)
        {
            return;
        }

        IReadOnlyList<PerfExecutedCell> expectedCells = PerfScenarioExecutor.CellsInExecutionOrder(
            expected.DeepOffset
        );
        bool cellsConform =
            reads.Provider == expected.Provider
            && reads.FixtureId == expected.Fixture
            && reads.DeepOffset == expected.DeepOffset
            && reads.WarmupIterations == expected.Reads.WarmupIterations
            && reads.MeasuredIterations == expected.Reads.MeasuredIterations
            && reads.CellExecutionOrder.SequenceEqual(expectedCells)
            && reads.Cells.Count == expectedCells.Count
            && expectedCells.All(cell =>
                reads.Cells.Any(row =>
                    row.ScenarioId == cell.ScenarioId
                    && row.PageSize == cell.PageSize
                    && row.Offset == cell.Offset
                    && row.WarmupIterations == expected.Reads.WarmupIterations
                    && row.MeasuredIterations == expected.Reads.MeasuredIterations
                    && row.LatencyMs.SamplesMs.Count == expected.Reads.MeasuredIterations
                )
            );
        if (!cellsConform)
        {
            invalid.Add(
                $"{at}: workload conformance: the paging run's provider, fixture, offsets, page sizes or read "
                    + $"iterations differ from {Describe(expected)}."
            );
        }
    }

    /// <summary>
    /// Check 3: the side completed every planned request and its verifications.
    /// </summary>
    private static void CheckValidity(
        string at,
        PerfBenchmarkSideEvidence side,
        PerfBenchmarkSideWorkload expected,
        List<string> invalid
    )
    {
        PerfBenchmarkSideResult result = side.Side;
        if (result.Status.State != PerfBenchmarkSideStatus.Completed)
        {
            invalid.Add($"{at}: the side failed in phase {result.Status.Phase}: {result.Status.Reason}");
            return;
        }

        // The side writes its result before NUnit teardown, so a completed result does not
        // prove the side process succeeded; the wrapper's recorded exit code does.
        if (side.Environment.SideExitCode is not 0)
        {
            string code =
                side.Environment.SideExitCode?.ToString(CultureInfo.InvariantCulture) ?? "no recorded code";
            invalid.Add($"{at}: the side process exited with {code}; see side.log.");
        }

        if (
            result.ReadCells.Any(cell =>
                cell.WarmupFailed + cell.MeasuredFailed != 0
                || cell.MeasuredCompleted != expected.Reads.MeasuredIterations
            )
        )
        {
            invalid.Add(
                $"{at}: failed read operations, or completed reads other than {expected.Reads.MeasuredIterations} per cell."
            );
        }

        if (result.Write.Failed != 0 || result.Write.MeasuredCompleted != expected.Writes.MeasuredIterations)
        {
            invalid.Add(
                $"{at}: failed write operations, or completed writes other than {expected.Writes.MeasuredIterations}."
            );
        }

        if (side.Reads is null)
        {
            invalid.Add($"{at}: the paging run's artifacts are missing.");
        }
    }

    /// <summary>
    /// Check 4: the required values, asserted on every side. Agreement between sides is not
    /// enough: two sides that agree on a disallowed value are still invalid.
    /// </summary>
    private static void CheckRequiredValues(
        string at,
        PerfBenchmarkSideEvidence side,
        PerfBenchmarkProfile profile,
        string provider,
        List<string> invalid
    )
    {
        PerfBenchmarkSideResult result = side.Side;
        PerfBenchmarkEnvironment environment = side.Environment;
        PerfBenchmarkRequiredEnvironment required = profile.RequiredEnvironment;
        List<string> violations = [];

        if (result.EffectiveLogLevel != profile.Workload.LogLevel)
        {
            violations.Add($"log level {result.EffectiveLogLevel}, not {profile.Workload.LogLevel}");
        }

        if (!result.OwnershipPublished)
        {
            violations.Add("data-store ownership was not published");
        }

        if (result.EffectiveConnection != profile.Workload.Connection[provider])
        {
            violations.Add(
                $"effective connection settings {result.EffectiveConnection} are not workload.connection.{provider}"
            );
        }

        if (environment.OsFamily != required.OsFamily || environment.Architecture != required.Architecture)
        {
            violations.Add(
                $"platform {environment.OsFamily} {environment.Architecture}, not {required.OsFamily} {required.Architecture}"
            );
        }

        if (environment.AcPowerOnline != required.AcPowerOnline)
        {
            violations.Add("AC power is not online");
        }

        if (environment.StorageMountType != required.DatabaseStorageMountType)
        {
            violations.Add(
                $"database storage on a {environment.StorageMountType} mount, not a {required.DatabaseStorageMountType}"
            );
        }

        if (violations.Count > 0)
        {
            invalid.Add($"{at}: required values: {string.Join("; ", violations)}.");
        }
    }

    /// <summary>
    /// Check 5: no injection on baseline sides, exactly the planned spec on candidate sides,
    /// and affected counts equal to each injected scenario's warmup + measured requests.
    /// </summary>
    private static void CheckInjection(
        string at,
        PerfBenchmarkSideEvidence side,
        PerfBenchmarkRunPlan plan,
        PerfBenchmarkSideWorkload expected,
        List<string> invalid
    )
    {
        PerfBenchmarkInjection injection = side.Side.Injection;
        IReadOnlyDictionary<string, decimal> planned =
            side.Side.Role == PerfBenchmarkSideResult.CandidateRole
                ? plan.Injection
                : new Dictionary<string, decimal>();

        if (!SameFactors(injection.Factors, planned))
        {
            invalid.Add(
                $"{at}: injection {Describe(injection.Factors)} differs from the run plan's {Describe(planned)} for this role."
            );
            return;
        }

        if (side.Side.Status.State != PerfBenchmarkSideStatus.Completed)
        {
            return;
        }

        foreach (string scenario in PerfBenchmarkScenarios.AllIds)
        {
            int requests =
                scenario == PerfBenchmarkScenarios.StudentPutCollectionUpdate
                    ? expected.Writes.WarmupIterations + expected.Writes.MeasuredIterations
                    : expected.Reads.WarmupIterations + expected.Reads.MeasuredIterations;
            int expectedAffected = planned.ContainsKey(scenario) ? requests : 0;
            int affected = injection.AffectedRequests.GetValueOrDefault(scenario);
            if (affected != expectedAffected)
            {
                invalid.Add(
                    $"{at}: injection affected {affected} {scenario} requests; expected {expectedAffected}."
                );
            }
        }
    }

    /// <summary>
    /// Check 7: the environment rules over the collector's samples. Workload CPU and I/O are
    /// recorded but never invalidate a run.
    /// </summary>
    private static void CheckRules(
        string at,
        PerfBenchmarkSideEvidence side,
        PerfBenchmarkEnvironmentRules rules,
        List<string> invalid
    )
    {
        PerfBenchmarkEnvironment environment = side.Environment;
        IReadOnlyList<PerfBenchmarkSample> samples = side.Samples;

        if (environment.CompetingHarnessProcess)
        {
            invalid.Add($"{at}: rule: a competing harness process was running.");
        }

        if (environment.OtherProviderContainerRunning)
        {
            invalid.Add($"{at}: rule: the other provider's database container was running.");
        }

        if (environment.StorageMountType == "tmpfs")
        {
            invalid.Add($"{at}: rule: the database ran on tmpfs.");
        }

        CheckIdleCpu(at, samples, PerfBenchmarkMetrics.HostCpuPercent, rules.MaxIdleHostCpuPercent, invalid);
        CheckIdleCpu(
            at,
            samples,
            PerfBenchmarkMetrics.DatabaseCpuPercent,
            rules.MaxIdleDatabaseCpuPercent,
            invalid
        );

        double[] available = [.. Values(samples, PerfBenchmarkMetrics.HostAvailableMb)];
        if (available.Length > 0 && available.Min() < rules.MinHostAvailableMemoryMb)
        {
            invalid.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{at}: rule: host available memory fell to {available.Min()} MB (minimum {rules.MinHostAvailableMemoryMb})."
                )
            );
        }

        double[] databaseMemory = [.. Values(samples, PerfBenchmarkMetrics.DatabaseMemoryPercent)];
        if (databaseMemory.Length > 0 && databaseMemory.Max() > (double)rules.MaxDatabaseMemoryPercentOfLimit)
        {
            invalid.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{at}: rule: database memory reached {databaseMemory.Max()}% of its limit (maximum {rules.MaxDatabaseMemoryPercentOfLimit}%)."
                )
            );
        }

        CheckCompleteness(at, environment, samples, rules, invalid);
        CheckCoverage(at, side, rules, invalid);
    }

    private static void CheckIdleCpu(
        string at,
        IReadOnlyList<PerfBenchmarkSample> samples,
        string metric,
        decimal maximum,
        List<string> invalid
    )
    {
        double[] idle = [.. Values(samples, metric, PerfBenchmarkMetrics.IdlePhase)];
        if (idle.Length > 0 && idle.Average() > (double)maximum)
        {
            invalid.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{at}: rule: idle {metric} averaged {idle.Average():0.##}% (maximum {maximum}%)."
                )
            );
        }
    }

    /// <summary>
    /// Completeness for every metric and phase: samples ≥ the minimum percentage of the phase
    /// duration divided by that metric's nominal interval.
    /// </summary>
    private static void CheckCompleteness(
        string at,
        PerfBenchmarkEnvironment environment,
        IReadOnlyList<PerfBenchmarkSample> samples,
        PerfBenchmarkEnvironmentRules rules,
        List<string> invalid
    )
    {
        foreach (string phase in PerfBenchmarkMetrics.Phases)
        {
            PerfBenchmarkPhaseWindow? window = environment.Phases.FirstOrDefault(entry =>
                entry.Name == phase
            );
            if (window is null)
            {
                invalid.Add($"{at}: rule: environment.json records no {phase} phase.");
                continue;
            }

            double seconds = (window.EndUtc - window.StartUtc).TotalSeconds;
            foreach (string metric in PerfBenchmarkMetrics.All)
            {
                double expected = seconds / IntervalOf(metric, rules).TotalSeconds;
                int observed = samples.Count(sample => sample.Metric == metric && sample.Phase == phase);
                if (observed < expected * (double)rules.MinCompletenessPercent / 100)
                {
                    invalid.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{at}: rule: incomplete samples for {metric} in the {phase} phase: {observed} of {expected:0.#} expected (minimum {rules.MinCompletenessPercent}%)."
                        )
                    );
                }
            }
        }
    }

    /// <summary>
    /// Measured-window coverage, separate from phase completeness: for every scenario's
    /// measured window and every metric, with i its nominal interval, a sample in
    /// [start − i, start], one in [end, end + i], and no gap longer than 2i across
    /// [start − i, end + i]. A window shorter than one interval is covered by its brackets.
    /// </summary>
    private static void CheckCoverage(
        string at,
        PerfBenchmarkSideEvidence side,
        PerfBenchmarkEnvironmentRules rules,
        List<string> invalid
    )
    {
        List<(string Scenario, PerfMeasuredWindow Window)> windows =
        [
            .. side
                .Side.ReadCells.Where(cell => cell.MeasuredWindow is not null)
                .Select(cell => (cell.CellId, cell.MeasuredWindow!)),
        ];
        if (side.Side.Write.MeasuredWindow is { } writeWindow)
        {
            windows.Add((side.Side.Write.ScenarioId, writeWindow));
        }

        foreach (string metric in PerfBenchmarkMetrics.All)
        {
            TimeSpan interval = IntervalOf(metric, rules);
            DateTimeOffset[] times =
            [
                .. side
                    .Samples.Where(sample => sample.Metric == metric)
                    .Select(sample => sample.TimestampUtc)
                    .Order(),
            ];
            foreach ((string scenario, PerfMeasuredWindow window) in windows)
            {
                DateTimeOffset from = window.StartUtc - interval;
                DateTimeOffset to = window.EndUtc + interval;
                bool startBracket = Array.Exists(times, time => time >= from && time <= window.StartUtc);
                bool endBracket = Array.Exists(times, time => time >= window.EndUtc && time <= to);
                DateTimeOffset[] inRange = [.. times.Where(time => time >= from && time <= to)];
                bool gap = inRange.Zip(inRange.Skip(1)).Any(pair => pair.Second - pair.First > interval * 2);
                if (!startBracket || !endBracket || gap)
                {
                    invalid.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{at}: rule: measured window of {scenario} is not covered by {metric} samples (start bracket {startBracket}, end bracket {endBracket}, gap over {interval.TotalSeconds * 2} s {gap})."
                        )
                    );
                }
            }
        }
    }

    /// <summary>
    /// Check 4b: every side agrees on everything except the permitted differences, which
    /// are listed when they differ.
    /// </summary>
    private static void CheckConsistency(
        IEnumerable<PerfBenchmarkSideEvidence> sideEvidence,
        List<string> invalid,
        List<string> permitted
    )
    {
        PerfBenchmarkSideEvidence[] sides = [.. sideEvidence];
        if (sides.Length < 2)
        {
            return;
        }

        void Agree(string field, Func<PerfBenchmarkSideEvidence, string> value, bool isPermitted = false)
        {
            if (sides.Select(value).Distinct().Count() <= 1)
            {
                return;
            }

            if (isPermitted)
            {
                permitted.Add(field);
            }
            else
            {
                invalid.Add($"consistency: the sides disagree on {field}.");
            }
        }

        Agree("profile SHA-256", side => side.Side.ProfileSha256);
        Agree("harness commit", side => side.Reads?.RunnerCommit ?? string.Empty);
        Agree("fixture", side => side.Side.Workload.Fixture);
        Agree("log level", side => side.Side.EffectiveLogLevel);
        Agree("ownership-published flag", side => side.Side.OwnershipPublished.ToString());
        Agree("effective connection settings", side => side.Side.EffectiveConnection.ToString());
        Agree("instrumentation", side => side.Side.Instrumentation.ToString());

        foreach (string field in FingerprintFields(FingerprintOf(sides[0])).Keys)
        {
            Agree($"fingerprint field {field}", side => FingerprintFields(FingerprintOf(side))[field]);
        }

        Agree("subjectCommit", side => side.Reads?.SubjectCommit ?? string.Empty, isPermitted: true);
        Agree("effectiveSchemaHash", side => side.Side.EffectiveSchemaHash, isPermitted: true);
        Agree("osBuild", side => side.Environment.OsBuild, isPermitted: true);
        Agree("dotnetVersion", side => side.Reads?.Host.DotnetVersion ?? string.Empty, isPermitted: true);
        Agree("dockerVersion", side => side.Environment.DockerVersion, isPermitted: true);
    }

    /// <summary>
    /// Check 6: a provider with a qualified fingerprint must match it on every field; without
    /// one the result is labelled uncalibrated. Calibration and Sizing derive the replacement
    /// thresholds, fingerprint and factors after an environment change, so a mismatch only
    /// labels them uncalibrated; they are diagnostics and never qualification evidence.
    /// </summary>
    private static void CheckQualifiedEnvironment(
        IEnumerable<PerfBenchmarkSideEvidence> sides,
        PerfBenchmarkProfile profile,
        PerfBenchmarkRunPlan plan,
        List<string> invalid,
        List<string> labels
    )
    {
        string provider = plan.Provider;
        PerfBenchmarkEnvironmentFingerprint? qualified =
            profile.Environment.QualifiedEnvironments.GetValueOrDefault(provider);
        if (qualified is null)
        {
            labels.Add(PerfBenchmarkComparisonResult.UncalibratedLabel);
            return;
        }

        bool recalibrating = plan.Purpose is Calibration or Sizing;
        IReadOnlyDictionary<string, string> expected = FingerprintFields(qualified);
        foreach (PerfBenchmarkSideEvidence side in sides)
        {
            IReadOnlyDictionary<string, string> observed = FingerprintFields(FingerprintOf(side));
            string[] differing =
            [
                .. expected.Keys.Where(field => observed.GetValueOrDefault(field) != expected[field]),
            ];
            if (differing.Length > 0 && recalibrating)
            {
                if (!labels.Contains(PerfBenchmarkComparisonResult.UncalibratedLabel))
                {
                    labels.Add(PerfBenchmarkComparisonResult.UncalibratedLabel);
                }
            }
            else if (differing.Length > 0)
            {
                invalid.Add(
                    $"pair {side.Side.Pair} {side.Side.Role}: qualified environment: the fingerprint differs from "
                        + $"qualifiedEnvironments.{provider} in {string.Join(", ", differing)}."
                );
            }
        }
    }

    /// <summary>
    /// Check 8: per scenario, the per-pair ratios and their median against the threshold.
    /// Computed whenever every pair has both sides' samples, even for an invalid comparison.
    /// </summary>
    private static IReadOnlyList<PerfBenchmarkScenarioComparison> Statistics(
        PerfBenchmarkRunPlan plan,
        PerfBenchmarkProfile profile,
        Dictionary<(int Pair, string Role), PerfBenchmarkSideEvidence> sides,
        List<string> invalid
    )
    {
        List<PerfBenchmarkScenarioComparison> scenarios = [];
        int pairs = plan.PairCount;
        foreach (string scenario in PerfBenchmarkScenarios.AllIds)
        {
            List<PerfBenchmarkPairStatistics> pairStatistics = [];
            for (int pair = 1; pair <= pairs; pair++)
            {
                ScenarioSample? baseline = SampleOf(
                    sides.GetValueOrDefault((pair, PerfBenchmarkSideResult.BaselineRole)),
                    scenario
                );
                ScenarioSample? candidate = SampleOf(
                    sides.GetValueOrDefault((pair, PerfBenchmarkSideResult.CandidateRole)),
                    scenario
                );
                if (baseline is null || candidate is null)
                {
                    break;
                }

                if (baseline.Latency.P50Ms <= 0 || baseline.Latency.P95Ms <= 0)
                {
                    invalid.Add($"pair {pair} {scenario}: the baseline latency is not positive.");
                    break;
                }

                pairStatistics.Add(
                    new PerfBenchmarkPairStatistics(
                        pair,
                        candidate.Latency.SamplesMs.Count,
                        baseline.Latency.P50Ms,
                        candidate.Latency.P50Ms,
                        Ratio(candidate.Latency.P50Ms, baseline.Latency.P50Ms),
                        baseline.Latency.P95Ms,
                        candidate.Latency.P95Ms,
                        Ratio(candidate.Latency.P95Ms, baseline.Latency.P95Ms),
                        P99(baseline.Latency),
                        P99(candidate.Latency),
                        baseline.Latency.MeanMs,
                        candidate.Latency.MeanMs,
                        baseline.CommandCounts,
                        candidate.CommandCounts,
                        baseline.DriverExecuteP50Ms,
                        candidate.DriverExecuteP50Ms
                    )
                );
            }

            if (pairStatistics.Count != pairs || pairs == 0)
            {
                continue;
            }

            decimal threshold = profile.Comparison.P50Thresholds[plan.Provider][scenario];
            decimal medianP50 = Median(pairStatistics.Select(entry => entry.P50Ratio));
            decimal medianP95 = Median(pairStatistics.Select(entry => entry.P95Ratio));
            decimal spread = pairStatistics.Max(entry => Math.Abs(entry.P50Ratio - 1));
            bool flagged = medianP50 - 1 > threshold;
            bool p95Flagged = profile.Comparison.P95WarningsEnabled && medianP95 - 1 > threshold;
            bool suspected = flagged || p95Flagged;
            string reason = string.Create(
                CultureInfo.InvariantCulture,
                $"median p50 ratio {medianP50:0.####} is within 1 + {threshold:0.##}"
            );
            if (flagged)
            {
                reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"median p50 ratio {medianP50:0.####} exceeds 1 + {threshold:0.##}"
                );
            }
            else if (p95Flagged)
            {
                reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"median p95 ratio {medianP95:0.####} exceeds 1 + {threshold:0.##}"
                );
            }
            scenarios.Add(
                new PerfBenchmarkScenarioComparison(
                    scenario,
                    threshold,
                    medianP50,
                    medianP95,
                    spread,
                    flagged,
                    p95Flagged,
                    suspected
                        ? PerfBenchmarkComparisonResult.SuspectedRegression
                        : PerfBenchmarkComparisonResult.Acceptable,
                    reason,
                    pairStatistics
                )
            );
        }

        return scenarios;
    }

    /// <summary>
    /// Check 6b: qualification eligibility, re-derived from the profile and never trusted from
    /// the run plan. Returns why the run is not eligible (empty when it is).
    /// </summary>
    private static List<string> Eligibility(
        PerfBenchmarkRunPlan plan,
        PerfBenchmarkProfile profile,
        bool valid
    )
    {
        List<string> reasons = [];
        if (!valid)
        {
            reasons.Add("the comparison is invalid");
        }

        if (plan.Purpose is not (Repeatability or Detection))
        {
            reasons.Add($"purpose {plan.Purpose} is a diagnostic");
            return reasons;
        }

        if (!profile.IsFrozen)
        {
            reasons.Add("the profile is not frozen");
        }

        int minimumPairs = Math.Max(MinimumQualificationPairs, profile.Comparison.Pairs);
        if (plan.PairCount < minimumPairs)
        {
            reasons.Add($"{plan.PairCount} pairs; qualification needs at least {minimumPairs}");
        }

        if (plan.Smoke)
        {
            reasons.Add("the smoke workload is not evidence");
        }

        if (
            plan.BaselineCommit != profile.ApprovedReferenceCommit
            || plan.CandidateCommit != plan.BaselineCommit
        )
        {
            reasons.Add("qualification needs same-commit sides on the approved reference");
        }

        if (plan.Purpose == Repeatability && plan.Injection.Count > 0)
        {
            reasons.Add("a repeatability run injects nothing");
        }

        if (plan.Purpose == Detection)
        {
            Dictionary<string, decimal> frozen = [];
            if (
                profile.Detection.InjectionFactors.TryGetValue(
                    plan.Provider,
                    out IReadOnlyDictionary<string, decimal?>? factors
                )
            )
            {
                foreach ((string scenario, decimal? factor) in factors)
                {
                    if (factor is not null)
                    {
                        frozen[scenario] = factor.Value;
                    }
                }
            }

            if (frozen.Count != profile.Detection.Scenarios.Count || !SameFactors(plan.Injection, frozen))
            {
                reasons.Add(
                    "a detection run must inject exactly the frozen detection.injectionFactors on every demonstration scenario"
                );
            }
        }

        return reasons;
    }

    private static PerfBenchmarkCalibration CalibrationOf(
        IReadOnlyList<PerfBenchmarkScenarioComparison> scenarios,
        PerfBenchmarkEnvironmentFingerprint fingerprint
    ) =>
        new(
            scenarios.ToDictionary(
                scenario => scenario.ScenarioId,
                scenario => Math.Ceiling(Math.Max(MinimumDerivedThreshold, 2 * scenario.Spread) * 100) / 100
            ),
            scenarios.ToDictionary(scenario => scenario.ScenarioId, scenario => scenario.Spread),
            fingerprint
        );

    private static PerfBenchmarkDetection DetectionOf(
        IReadOnlyList<PerfBenchmarkScenarioComparison> scenarios,
        PerfBenchmarkProfile profile
    )
    {
        PerfBenchmarkDetectionRules rules = profile.Detection;
        List<PerfBenchmarkDetectionScenario> demonstrations = [];
        foreach (string scenarioId in rules.Scenarios)
        {
            PerfBenchmarkScenarioComparison? scenario = scenarios.FirstOrDefault(entry =>
                entry.ScenarioId == scenarioId
            );
            if (scenario is null)
            {
                continue;
            }

            decimal observed = scenario.MedianP50Ratio - 1;
            decimal low = Math.Max(rules.TargetIncrease - rules.Tolerance, scenario.Threshold + rules.Margin);
            decimal high = rules.TargetIncrease + rules.CapAboveTarget;
            bool inBand = observed >= low && observed <= high;
            demonstrations.Add(
                new PerfBenchmarkDetectionScenario(
                    scenarioId,
                    observed,
                    low,
                    high,
                    scenario.Flagged,
                    inBand,
                    scenario.Flagged && inBand
                )
            );
        }

        string[] others =
        [
            .. scenarios
                .Where(scenario => scenario.Flagged && !rules.Scenarios.Contains(scenario.ScenarioId))
                .Select(scenario => scenario.ScenarioId),
        ];
        bool passed =
            demonstrations.Count == rules.Scenarios.Count
            && demonstrations.TrueForAll(scenario => scenario.Passed)
            && others.Length == 0;
        return new PerfBenchmarkDetection(passed, demonstrations, others);
    }

    /// <summary>
    /// The sizing block, per demonstration scenario and independently: in band when the
    /// observed increase is within the tolerance of the target; otherwise a suggestion of
    /// <c>factor × target / observed</c> when the increase is at least 2 pp and the suggestion
    /// lies in (0, 5]; otherwise inconclusive, retrying the same factor.
    /// </summary>
    private static IReadOnlyList<PerfBenchmarkSizing> SizingOf(
        IReadOnlyList<PerfBenchmarkScenarioComparison> scenarios,
        PerfBenchmarkProfile profile,
        PerfBenchmarkRunPlan plan
    )
    {
        PerfBenchmarkDetectionRules rules = profile.Detection;
        List<PerfBenchmarkSizing> sizing = [];
        foreach (string scenarioId in rules.Scenarios)
        {
            PerfBenchmarkScenarioComparison? scenario = scenarios.FirstOrDefault(entry =>
                entry.ScenarioId == scenarioId
            );
            if (!plan.Injection.TryGetValue(scenarioId, out decimal input) || scenario is null)
            {
                sizing.Add(
                    new PerfBenchmarkSizing(
                        scenarioId,
                        null,
                        null,
                        PerfBenchmarkSizing.Inconclusive,
                        null,
                        null,
                        "the scenario was not injected"
                    )
                );
                continue;
            }

            decimal observed = scenario.MedianP50Ratio - 1;
            if (Math.Abs(observed - rules.TargetIncrease) <= rules.Tolerance)
            {
                sizing.Add(
                    new PerfBenchmarkSizing(
                        scenarioId,
                        input,
                        observed,
                        PerfBenchmarkSizing.InBand,
                        null,
                        input,
                        null
                    )
                );
                continue;
            }

            if (observed < MinimumSizingIncrease)
            {
                string why = observed switch
                {
                    0 => "the observed increase is zero",
                    < 0 => "the observed increase is negative",
                    _ => string.Create(
                        CultureInfo.InvariantCulture,
                        $"the observed increase is below {MinimumSizingIncrease:0.##}"
                    ),
                };
                sizing.Add(
                    new PerfBenchmarkSizing(
                        scenarioId,
                        input,
                        observed,
                        PerfBenchmarkSizing.Inconclusive,
                        null,
                        input,
                        why
                    )
                );
                continue;
            }

            decimal suggested = Math.Round(
                input * rules.TargetIncrease / observed,
                4,
                MidpointRounding.AwayFromZero
            );
            if (suggested <= 0 || suggested > PerfBenchmarkProfileLoader.MaximumInjectionFactor)
            {
                sizing.Add(
                    new PerfBenchmarkSizing(
                        scenarioId,
                        input,
                        observed,
                        PerfBenchmarkSizing.Inconclusive,
                        null,
                        input,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"the suggested factor {suggested} is outside (0, {PerfBenchmarkProfileLoader.MaximumInjectionFactor}]"
                        )
                    )
                );
                continue;
            }

            sizing.Add(
                new PerfBenchmarkSizing(
                    scenarioId,
                    input,
                    observed,
                    PerfBenchmarkSizing.Adjust,
                    suggested,
                    suggested,
                    null
                )
            );
        }

        return sizing;
    }

    private static PerfBenchmarkComparisonResult Finish(
        PerfBenchmarkComparisonInput input,
        List<string> invalid,
        List<string> permitted,
        List<string> labels,
        IReadOnlyList<PerfBenchmarkScenarioComparison> scenarios,
        bool eligible,
        List<string> notEligible,
        PerfBenchmarkCalibration? calibration,
        PerfBenchmarkDetection? detection,
        IReadOnlyList<PerfBenchmarkSizing>? sizing
    )
    {
        bool valid = invalid.Count == 0;
        string result = PerfBenchmarkComparisonResult.Acceptable;
        if (!valid)
        {
            result = PerfBenchmarkComparisonResult.Invalid;
        }
        else if (
            scenarios.Any(scenario => scenario.Result == PerfBenchmarkComparisonResult.SuspectedRegression)
        )
        {
            result = PerfBenchmarkComparisonResult.SuspectedRegression;
        }
        return new PerfBenchmarkComparisonResult(
            result,
            valid ? 0 : 2,
            input.Plan.RunId,
            input.Plan.Provider,
            input.Plan.Purpose,
            Evidence: !input.Plan.Smoke,
            QualificationEligible: valid && eligible,
            labels,
            input.Plan.ProfileSha256,
            invalid,
            notEligible,
            permitted,
            scenarios,
            calibration,
            detection,
            sizing
        );
    }

    private sealed record ScenarioSample(
        PerfLatencySummary Latency,
        string CommandCounts,
        double? DriverExecuteP50Ms
    );

    private static ScenarioSample? SampleOf(PerfBenchmarkSideEvidence? side, string scenario)
    {
        if (side is null)
        {
            return null;
        }

        if (scenario == PerfBenchmarkScenarios.StudentPutCollectionUpdate)
        {
            PerfWriteScenarioResult write = side.Side.Write;
            return write.LatencyMs is null
                ? null
                : new ScenarioSample(
                    write.LatencyMs,
                    string.Join(
                        " ",
                        write.MeasuredCommandCounts.Select(entry =>
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{entry.CommandCount}x{entry.Requests}"
                            )
                        )
                    ),
                    null
                );
        }

        PerfBenchmarkReadCell? cell = side.Reads?.Cells.FirstOrDefault(row =>
            PerfBenchmarkScenarios.ReadCellId(row.ScenarioId, row.PageSize) == scenario
        );
        return cell is null
            ? null
            : new ScenarioSample(
                cell.LatencyMs,
                cell.CommandCountPerRequest.ToString(CultureInfo.InvariantCulture),
                cell.DriverExecuteMs.P50Ms
            );
    }

    private static IEnumerable<double> Values(
        IReadOnlyList<PerfBenchmarkSample> samples,
        string metric,
        string? phase = null
    ) =>
        samples
            .Where(sample => sample.Metric == metric && (phase is null || sample.Phase == phase))
            .Select(sample => sample.Value);

    private static TimeSpan IntervalOf(string metric, PerfBenchmarkEnvironmentRules rules) =>
        TimeSpan.FromSeconds(
            PerfBenchmarkMetrics.IsHostMetric(metric)
                ? rules.HostSampleIntervalSeconds
                : rules.DockerSampleIntervalSeconds
        );

    /// <summary>
    /// Ratios in decimal, so a ratio of exactly 1.10 sits exactly on a 10% threshold.
    /// </summary>
    private static decimal Ratio(double candidate, double baseline) => (decimal)candidate / (decimal)baseline;

    private static decimal Median(IEnumerable<decimal> values)
    {
        decimal[] sorted = [.. values.Order()];
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// Nearest-rank p99, descriptive only: at 60 samples it is the maximum.
    /// </summary>
    private static double P99(PerfLatencySummary latency)
    {
        double[] sorted = [.. latency.SamplesMs.Order()];
        int rank = (int)Math.Ceiling(0.99 * sorted.Length);
        return sorted.Length == 0 ? 0 : sorted[Math.Max(rank, 1) - 1];
    }

    private static bool SameFactors(
        IReadOnlyDictionary<string, decimal> left,
        IReadOnlyDictionary<string, decimal> right
    ) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out decimal value) && value == entry.Value);

    private static string Describe(IReadOnlyDictionary<string, decimal> factors) =>
        factors.Count == 0
            ? "none"
            : string.Join(
                ";",
                factors
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key}={entry.Value.ToString(CultureInfo.InvariantCulture)}")
            );

    private static string Describe(PerfBenchmarkSideWorkload workload) =>
        $"{workload.Provider} {workload.Fixture} deep offset {workload.DeepOffset}, reads "
        + $"{workload.Reads.WarmupIterations}/{workload.Reads.MeasuredIterations}, writes "
        + $"{workload.Writes.WarmupIterations}/{workload.Writes.MeasuredIterations}";
}

/// <summary>
/// Reads a comparison directory: <c>run-plan.json</c>, then <c>pairs/NN/&lt;role&gt;/</c> holding
/// <c>benchmark-side.json</c>, <c>environment.json</c>, <c>samples.csv</c> and the paging run
/// that <c>benchmark-side.json</c> names. A side whose artifacts cannot be read becomes a load error (and a missing side),
/// never an exception; only an unreadable run plan stops the report.
/// </summary>
public static class PerfBenchmarkComparisonLoader
{
    public static string SideDirectory(string comparisonDirectory, int pair, string role) =>
        Path.Combine(comparisonDirectory, "pairs", pair.ToString("D2", CultureInfo.InvariantCulture), role);

    public static PerfBenchmarkComparisonInput Load(
        string comparisonDirectory,
        PerfLoadedBenchmarkProfile profile
    )
    {
        PerfBenchmarkRunPlan plan = PerfBenchmarkJson.Read<PerfBenchmarkRunPlan>(
            File.ReadAllText(Path.Combine(comparisonDirectory, PerfBenchmarkRunPlan.FileName)),
            PerfBenchmarkRunPlan.FileName
        );

        List<PerfBenchmarkSideEvidence> sides = [];
        List<string> errors = [];
        foreach (PerfBenchmarkPlannedSide planned in plan.Order)
        {
            string directory = SideDirectory(comparisonDirectory, planned.Pair, planned.Role);
            if (!File.Exists(Path.Combine(directory, PerfBenchmarkSideResult.FileName)))
            {
                continue;
            }

            try
            {
                sides.Add(LoadSide(directory));
            }
            catch (Exception exception)
                when (exception is PerfArtifactValidationException or IOException or JsonException)
            {
                errors.Add($"pair {planned.Pair} {planned.Role}: unreadable artifacts: {exception.Message}");
            }
        }

        return new PerfBenchmarkComparisonInput(plan, profile, sides, errors);
    }

    private static PerfBenchmarkSideEvidence LoadSide(string directory)
    {
        PerfBenchmarkSideResult side = PerfBenchmarkSideResults.Read(
            File.ReadAllText(Path.Combine(directory, PerfBenchmarkSideResult.FileName))
        );
        PerfBenchmarkEnvironment environment = PerfBenchmarkJson.Read<PerfBenchmarkEnvironment>(
            File.ReadAllText(Path.Combine(directory, PerfBenchmarkEnvironment.FileName)),
            PerfBenchmarkEnvironment.FileName
        );
        IReadOnlyList<PerfBenchmarkSample> samples = PerfBenchmarkMetrics.ParseCsv(
            File.ReadLines(Path.Combine(directory, PerfBenchmarkMetrics.SamplesFileName))
        );

        // Only the paging run the side named is read; a side that failed before its reads
        // produced one has none.
        PerfBenchmarkReadRun? reads = null;
        if (side.ReadRun is { } readRun)
        {
            string runDirectory = Path.Combine(directory, readRun.RunId);
            PerfRunManifest manifest = PerfArtifactJson.Deserialize<PerfRunManifest>(
                File.ReadAllText(Path.Combine(runDirectory, "run-manifest.json"))
            );
            PerfResultsDocument results = PerfArtifactJson.Deserialize<PerfResultsDocument>(
                File.ReadAllText(Path.Combine(runDirectory, PerfBenchmarkReadRunReference.ResultsFileName))
            );

            // The projection keeps the manifest's identities and the rows' latencies, so the
            // pair must be internally consistent: rows from the manifest's build, summaries
            // the raw samples explain.
            PerfArtifactValidator.EnsureValid(manifest, results);

            // Commits cannot tell apart the sides of one build, so the side's own record binds
            // the paging run: its manifest and the exact results the side measured.
            List<string> errors = [];
            if (manifest.Run.RunId != readRun.RunId)
            {
                errors.Add(
                    $"{runDirectory}: run-manifest.json is run {manifest.Run.RunId}, not the side's paging run {readRun.RunId}."
                );
            }

            if (PerfBenchmarkReadRunReference.ResultsSha256Of(runDirectory) != readRun.ResultsSha256)
            {
                errors.Add(
                    $"{runDirectory}: results.json is not the one the side measured (SHA-256 differs from benchmark-side.json)."
                );
            }

            if (errors.Count > 0)
            {
                throw new PerfArtifactValidationException(errors);
            }

            reads = PerfBenchmarkReadRun.From(manifest, results);
        }

        return new PerfBenchmarkSideEvidence(side, environment, reads, samples);
    }
}
