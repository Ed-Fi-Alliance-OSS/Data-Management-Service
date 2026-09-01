// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Evaluates the epic's acceptance gates over loaded evidence: the DMS-1391 traditional
/// baseline (schema 1.3.0) and the final-gate primary and descriptor runs (schema 2.0.0),
/// per provider. Latency gates are app-level p50/p95 ratios. Cross-run gates — the
/// traditional regression and the first-cursor entry cost — additionally require consistent
/// fixtures and a comparable environment identity; when either precondition fails, those
/// gates report Inconclusive rather than judging numbers measured on different ground.
/// Within-run gates (depth insensitivity, partition count insensitivity) stay decidable
/// regardless. Deep-offset results are reported as an observation, never a gate.
/// </summary>
public static class PerfFinalGateEvaluator
{
    public const double P50RatioLimit = 1.20;
    public const double P95RatioLimit = 1.30;
    public const double PartitionP50RatioLimit = 1.25;

    public static readonly IReadOnlyList<string> ExpectedProviders = ["postgresql", "mssql"];

    public static PerfFinalGateEvaluation Evaluate(IReadOnlyList<PerfFinalGateProviderEvidence> providers)
    {
        List<PerfEvaluatedRun> runs = [];
        List<PerfGateOutcome> gates = [];

        List<string> presentProviders = [];
        foreach (PerfFinalGateProviderEvidence evidence in providers)
        {
            string provider = evidence.Primary.Manifest.Run.Provider;
            presentProviders.Add(provider);
            runs.AddRange(DescribeRuns(provider, evidence));
            gates.AddRange(EvaluateProvider(provider, evidence));
        }

        IReadOnlyList<string> missingProviders =
        [
            .. ExpectedProviders.Where(expected => !presentProviders.Contains(expected)),
        ];
        gates.Add(
            new PerfGateOutcome(
                "provider-coverage",
                "all",
                "PostgreSQL and real SQL Server satisfy every gate independently.",
                missingProviders.Count == 0 ? PerfGateStatus.Pass : PerfGateStatus.Inconclusive,
                missingProviders.Count == 0
                    ? [$"providers evaluated: {string.Join(", ", presentProviders)}"]
                    : [$"missing provider evidence: {string.Join(", ", missingProviders)}"],
                []
            )
        );

        return PerfFinalGateEvaluation.Create(runs, gates);
    }

    private static IEnumerable<PerfEvaluatedRun> DescribeRuns(
        string provider,
        PerfFinalGateProviderEvidence evidence
    )
    {
        yield return new PerfEvaluatedRun(
            provider,
            "traditional-baseline",
            evidence.Baseline.Manifest.Run.RunId,
            evidence.Baseline.RunDirectory,
            evidence.Baseline.Manifest.Fixture.FixtureId,
            evidence.Baseline.Manifest.Commits.RunnerCommit,
            evidence.Baseline.Manifest.Commits.SubjectCommit,
            evidence.Baseline.Manifest.Environment.Host.MachineFingerprint,
            evidence.Baseline.Manifest.Environment.Server.ImageDigest
        );
        yield return DescribeFinalGateRun(provider, evidence.Primary);
        yield return DescribeFinalGateRun(provider, evidence.Descriptors);
    }

    private static PerfEvaluatedRun DescribeFinalGateRun(string provider, PerfFinalGateRunArtifacts run) =>
        new(
            provider,
            run.Manifest.RunKind,
            run.Manifest.Run.RunId,
            run.RunDirectory,
            run.Manifest.Fixture.FixtureId,
            run.Manifest.Commits.RunnerCommit,
            run.Manifest.Commits.SubjectCommit,
            run.Manifest.Environment.Host.MachineFingerprint,
            run.Manifest.Environment.Server.ImageDigest
        );

    private static IEnumerable<PerfGateOutcome> EvaluateProvider(
        string provider,
        PerfFinalGateProviderEvidence evidence
    )
    {
        PerfGateOutcome consistency = EvaluateEvidenceConsistency(provider, evidence);
        yield return consistency;

        PerfGateOutcome comparability = EvaluateEnvironmentComparability(provider, evidence);
        yield return comparability;

        bool crossRunDecidable =
            consistency.Status == PerfGateStatus.Pass && comparability.Status == PerfGateStatus.Pass;

        yield return EvaluateTraditionalSqlTextualGate(provider, evidence);
        yield return EvaluateTraditionalShallowRegression(provider, evidence, crossRunDecidable);
        yield return EvaluateCursorFirstEntry(provider, evidence, crossRunDecidable);

        foreach (PerfFinalGateVariant variant in Enum.GetValues<PerfFinalGateVariant>())
        {
            yield return EvaluateCursorDepthInsensitivity(provider, evidence, variant);
        }

        yield return EvaluatePartitionCountInsensitivity(provider, evidence);
        yield return EvaluateSingleCommandStructure(provider, evidence);
        yield return EvaluateDeepOffsetObservation(provider, evidence, crossRunDecidable);
    }

    private static PerfGateOutcome EvaluateEvidenceConsistency(
        string provider,
        PerfFinalGateProviderEvidence evidence
    )
    {
        List<string> problems = [];

        if (evidence.Baseline.Manifest.Run.Provider != provider)
        {
            problems.Add($"baseline provider '{evidence.Baseline.Manifest.Run.Provider}' differs.");
        }

        if (evidence.Descriptors.Manifest.Run.Provider != provider)
        {
            problems.Add($"descriptor provider '{evidence.Descriptors.Manifest.Run.Provider}' differs.");
        }

        if (evidence.Primary.Manifest.RunKind != PerfFinalGateRunKinds.Primary)
        {
            problems.Add($"primary run kind is '{evidence.Primary.Manifest.RunKind}'.");
        }

        if (evidence.Descriptors.Manifest.RunKind != PerfFinalGateRunKinds.Descriptors)
        {
            problems.Add($"descriptor run kind is '{evidence.Descriptors.Manifest.RunKind}'.");
        }

        if (
            evidence.Primary.Manifest.Commits.SubjectCommit
            != evidence.Descriptors.Manifest.Commits.SubjectCommit
        )
        {
            problems.Add("the primary and descriptor runs measured different subject commits.");
        }

        PerfManifestFixture baselineFixture = evidence.Baseline.Manifest.Fixture;
        PerfFinalGateManifestFixture primaryFixture = evidence.Primary.Manifest.Fixture;
        if (baselineFixture.FixtureId != primaryFixture.FixtureId)
        {
            problems.Add(
                $"baseline fixture '{baselineFixture.FixtureId}' differs from the final run's "
                    + $"'{primaryFixture.FixtureId}' — the comparison is not like-for-like."
            );
        }

        if (baselineFixture.RowCount != primaryFixture.RowCount)
        {
            problems.Add("baseline and final fixture row counts differ.");
        }

        if (primaryFixture.DeepOffset != baselineFixture.DeepOffset)
        {
            problems.Add("baseline and final deep offsets differ; the deep comparison is not aligned.");
        }

        return new PerfGateOutcome(
            "evidence-consistency",
            provider,
            "Baseline and final-gate runs describe the same provider, fixture, and comparison frame.",
            problems.Count == 0 ? PerfGateStatus.Pass : PerfGateStatus.Fail,
            problems.Count == 0 ? ["baseline and final-gate identities are consistent"] : problems,
            []
        );
    }

    private static PerfGateOutcome EvaluateEnvironmentComparability(
        string provider,
        PerfFinalGateProviderEvidence evidence
    )
    {
        List<string> differences = [];
        PerfEnvironmentIdentity baseline = evidence.Baseline.Manifest.Environment;
        PerfEnvironmentIdentity primary = evidence.Primary.Manifest.Environment;
        PerfEnvironmentIdentity descriptors = evidence.Descriptors.Manifest.Environment;

        if (baseline.Host.MachineFingerprint != primary.Host.MachineFingerprint)
        {
            differences.Add(
                $"machine fingerprint '{primary.Host.MachineFingerprint}' differs from the baseline's "
                    + $"'{baseline.Host.MachineFingerprint}'."
            );
        }

        if (baseline.Server.ImageDigest != primary.Server.ImageDigest)
        {
            differences.Add("the pinned server image digest differs from the baseline's.");
        }

        if (baseline.Server.ServerVersion != primary.Server.ServerVersion)
        {
            differences.Add(
                $"server version '{primary.Server.ServerVersion}' differs from the baseline's "
                    + $"'{baseline.Server.ServerVersion}'."
            );
        }

        if (descriptors.Host.MachineFingerprint != primary.Host.MachineFingerprint)
        {
            differences.Add("the descriptor run's machine fingerprint differs from the primary run's.");
        }

        return new PerfGateOutcome(
            "environment-comparability",
            provider,
            "Cross-run latency ratios assume the same machine and pinned server as the baseline.",
            differences.Count == 0 ? PerfGateStatus.Pass : PerfGateStatus.Inconclusive,
            differences.Count == 0
                ?
                [
                    $"machine fingerprint {primary.Host.MachineFingerprint} and image digest match the baseline",
                ]
                : differences,
            []
        );
    }

    private static PerfGateOutcome EvaluateTraditionalSqlTextualGate(
        string provider,
        PerfFinalGateProviderEvidence evidence
    )
    {
        List<string> details = [];
        List<string> evidenceRows = [];
        bool pass = true;

        foreach (PerfScenarioResult baselineRow in evidence.Baseline.Results.Results)
        {
            PerfFinalGateScenarioResult? finalRow = evidence.Primary.Results.Results.FirstOrDefault(row =>
                row.Family == "traditional"
                && row.ScenarioId == baselineRow.ScenarioId
                && row.PageSize == baselineRow.PageSize
            );
            string cell = $"{baselineRow.ScenarioId}/{baselineRow.PageSize}";
            evidenceRows.Add(cell);
            if (finalRow is null)
            {
                pass = false;
                details.Add($"{cell}: no matching final-gate traditional row.");
            }
            else if (finalRow.SelectionSqlSha256 != baselineRow.PageSelectionSqlSha256)
            {
                pass = false;
                details.Add(
                    $"{cell}: page-selection SQL hash changed from the baseline — the traditional "
                        + "text is no longer byte-identical."
                );
            }
        }

        if (pass)
        {
            details.Add("every traditional page-selection SQL hash is byte-identical to the baseline");
        }

        return new PerfGateOutcome(
            "traditional-sql-textual",
            provider,
            "Existing limit/offset page-selection SQL remains textually unchanged.",
            pass ? PerfGateStatus.Pass : PerfGateStatus.Fail,
            details,
            evidenceRows
        );
    }

    private static PerfGateOutcome EvaluateTraditionalShallowRegression(
        string provider,
        PerfFinalGateProviderEvidence evidence,
        bool crossRunDecidable
    )
    {
        return RatioGate(
            "traditional-shallow-regression",
            provider,
            "Shallow-offset traditional paging costs at most 1.20x p50 / 1.30x p95 of its pre-change baseline.",
            crossRunDecidable,
            PerfScenarios.PageSizes.Select(pageSize =>
            {
                PerfFinalGateScenarioResult numerator = PrimaryRow(
                    evidence,
                    PerfScenarios.TraditionalOffsetShallow,
                    pageSize
                );
                PerfScenarioResult denominator = BaselineRow(
                    evidence,
                    PerfScenarios.TraditionalOffsetShallow,
                    pageSize
                );
                return new RatioCheck(
                    $"{PerfScenarios.TraditionalOffsetShallow}/{pageSize}",
                    numerator.LatencyMs,
                    denominator.LatencyMs
                );
            })
        );
    }

    private static PerfGateOutcome EvaluateCursorFirstEntry(
        string provider,
        PerfFinalGateProviderEvidence evidence,
        bool crossRunDecidable
    )
    {
        return RatioGate(
            "cursor-first-entry",
            provider,
            "A first cursor page costs at most 1.20x p50 / 1.30x p95 of the offset-zero baseline.",
            crossRunDecidable,
            PerfScenarios.PageSizes.Select(pageSize =>
            {
                PerfFinalGateScenarioResult numerator = PrimaryRow(
                    evidence,
                    PerfFinalGateScenarios.CursorScenarioId(
                        PerfFinalGateVariant.Unfiltered,
                        PerfCursorRange.First
                    ),
                    pageSize
                );
                PerfScenarioResult denominator = BaselineRow(
                    evidence,
                    PerfScenarios.TraditionalOffsetZero,
                    pageSize
                );
                return new RatioCheck(
                    $"cursor-unfiltered-first vs {PerfScenarios.TraditionalOffsetZero}/{pageSize}",
                    numerator.LatencyMs,
                    denominator.LatencyMs
                );
            })
        );
    }

    private static PerfGateOutcome EvaluateCursorDepthInsensitivity(
        string provider,
        PerfFinalGateProviderEvidence evidence,
        PerfFinalGateVariant variant
    )
    {
        PerfFinalGateRunArtifacts run =
            variant == PerfFinalGateVariant.Descriptor ? evidence.Descriptors : evidence.Primary;

        List<RatioCheck> checks = [];
        foreach (int pageSize in PerfScenarios.PageSizes)
        {
            PerfFinalGateScenarioResult first = RowOf(
                run,
                PerfFinalGateScenarios.CursorScenarioId(variant, PerfCursorRange.First),
                pageSize
            );
            foreach (
                PerfCursorRange range in (PerfCursorRange[])[PerfCursorRange.Middle, PerfCursorRange.Last]
            )
            {
                PerfFinalGateScenarioResult deeper = RowOf(
                    run,
                    PerfFinalGateScenarios.CursorScenarioId(variant, range),
                    pageSize
                );
                checks.Add(
                    new RatioCheck(
                        $"{deeper.ScenarioId}/{pageSize} vs first",
                        deeper.LatencyMs,
                        first.LatencyMs
                    )
                );
            }
        }

        return RatioGate(
            $"cursor-depth-insensitivity-{PerfFinalGateScenarios.VariantName(variant)}",
            provider,
            "Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.",
            decidable: true,
            checks
        );
    }

    private static PerfGateOutcome EvaluatePartitionCountInsensitivity(
        string provider,
        PerfFinalGateProviderEvidence evidence
    )
    {
        PerfFinalGateScenarioResult one = PartitionRow(evidence.Primary, 1);
        PerfFinalGateScenarioResult twoHundred = PartitionRow(evidence.Primary, 200);

        List<string> details = [];
        List<string> evidenceRows = [one.ScenarioId, twoHundred.ScenarioId];
        PerfGateStatus status;
        if (one.LatencyMs.P50Ms <= 0)
        {
            status = PerfGateStatus.Inconclusive;
            details.Add("the number=1 p50 is not a positive value; the ratio is not computable.");
        }
        else
        {
            double ratio = twoHundred.LatencyMs.P50Ms / one.LatencyMs.P50Ms;
            bool pass = ratio <= PartitionP50RatioLimit;
            status = pass ? PerfGateStatus.Pass : PerfGateStatus.Fail;
            details.Add(
                FormatRatio(
                    "number=200 vs number=1",
                    "p50",
                    twoHundred.LatencyMs.P50Ms,
                    one.LatencyMs.P50Ms,
                    ratio,
                    PartitionP50RatioLimit,
                    pass
                )
            );
        }

        return new PerfGateOutcome(
            "partition-count-insensitivity",
            provider,
            "Requesting 200 partitions costs at most 1.25x p50 of requesting 1 over the same candidate set.",
            status,
            details,
            evidenceRows
        );
    }

    private static PerfGateOutcome EvaluateSingleCommandStructure(
        string provider,
        PerfFinalGateProviderEvidence evidence
    )
    {
        IReadOnlyList<PerfFinalGateScenarioResult> allRows =
        [
            .. evidence.Primary.Results.Results,
            .. evidence.Descriptors.Results.Results,
        ];
        IReadOnlyList<string> offenders =
        [
            .. allRows
                .Where(row => row.CommandCountPerRequest != 1)
                .Select(row => $"{row.ScenarioId}/{row.CellKey}"),
        ];

        return new PerfGateOutcome(
            "single-command-structure",
            provider,
            "Cursor hydration adds no roundtrip and partition boundary selection is one command.",
            offenders.Count == 0 ? PerfGateStatus.Pass : PerfGateStatus.Fail,
            offenders.Count == 0
                ? [$"all {allRows.Count} measured cells observed exactly one database command per request"]
                : [.. offenders.Select(cell => $"{cell}: more than one command per request.")],
            [.. allRows.Select(row => $"{row.ScenarioId}/{row.CellKey}")]
        );
    }

    private static PerfGateOutcome EvaluateDeepOffsetObservation(
        string provider,
        PerfFinalGateProviderEvidence evidence,
        bool crossRunDecidable
    )
    {
        List<string> details = [];
        List<string> evidenceRows = [];
        foreach (int pageSize in PerfScenarios.PageSizes)
        {
            PerfFinalGateScenarioResult finalDeep = PrimaryRow(
                evidence,
                PerfScenarios.TraditionalOffsetDeep,
                pageSize
            );
            PerfScenarioResult baselineDeep = BaselineRow(
                evidence,
                PerfScenarios.TraditionalOffsetDeep,
                pageSize
            );
            evidenceRows.Add($"{PerfScenarios.TraditionalOffsetDeep}/{pageSize}");
            if (crossRunDecidable && baselineDeep.LatencyMs.P50Ms > 0)
            {
                double ratio = finalDeep.LatencyMs.P50Ms / baselineDeep.LatencyMs.P50Ms;
                details.Add(
                    $"deep/{pageSize}: p50 {Ms(finalDeep.LatencyMs.P50Ms)} vs baseline "
                        + $"{Ms(baselineDeep.LatencyMs.P50Ms)} ({Ratio(ratio)})"
                );
            }
            else
            {
                details.Add(
                    $"deep/{pageSize}: p50 {Ms(finalDeep.LatencyMs.P50Ms)} recorded; the baseline "
                        + "comparison is not environment-comparable."
                );
            }
        }

        details.Add("deep-offset results are recorded for comparison and are not a cursor acceptance gate");

        return new PerfGateOutcome(
            "deep-offset-observation",
            provider,
            "Deep-offset traditional results, recorded but never gated.",
            PerfGateStatus.Pass,
            details,
            evidenceRows
        );
    }

    private sealed record RatioCheck(
        string Label,
        PerfLatencySummary Numerator,
        PerfLatencySummary Denominator
    );

    private static PerfGateOutcome RatioGate(
        string gateId,
        string provider,
        string description,
        bool decidable,
        IEnumerable<RatioCheck> checks
    )
    {
        List<string> details = [];
        List<string> evidenceRows = [];
        bool anyFail = false;
        bool anyIncomputable = false;

        foreach (RatioCheck check in checks)
        {
            evidenceRows.Add(check.Label);
            if (check.Denominator.P50Ms <= 0 || check.Denominator.P95Ms <= 0)
            {
                anyIncomputable = true;
                details.Add($"{check.Label}: the comparison denominator is not positive.");
                continue;
            }

            double p50Ratio = check.Numerator.P50Ms / check.Denominator.P50Ms;
            double p95Ratio = check.Numerator.P95Ms / check.Denominator.P95Ms;
            bool p50Pass = p50Ratio <= P50RatioLimit;
            bool p95Pass = p95Ratio <= P95RatioLimit;
            anyFail |= !p50Pass || !p95Pass;
            details.Add(
                FormatRatio(
                    check.Label,
                    "p50",
                    check.Numerator.P50Ms,
                    check.Denominator.P50Ms,
                    p50Ratio,
                    P50RatioLimit,
                    p50Pass
                )
            );
            details.Add(
                FormatRatio(
                    check.Label,
                    "p95",
                    check.Numerator.P95Ms,
                    check.Denominator.P95Ms,
                    p95Ratio,
                    P95RatioLimit,
                    p95Pass
                )
            );
        }

        PerfGateStatus status;
        if (!decidable)
        {
            status = PerfGateStatus.Inconclusive;
        }
        else if (anyFail)
        {
            status = PerfGateStatus.Fail;
        }
        else
        {
            status = anyIncomputable ? PerfGateStatus.Inconclusive : PerfGateStatus.Pass;
        }

        if (!decidable)
        {
            details.Insert(
                0,
                "evidence consistency or environment comparability failed, so this cross-run "
                    + "gate is provisional — the ratios below are informational only."
            );
        }

        return new PerfGateOutcome(gateId, provider, description, status, details, evidenceRows);
    }

    private static string FormatRatio(
        string label,
        string layer,
        double numerator,
        double denominator,
        double ratio,
        double limit,
        bool pass
    ) =>
        $"{label}: {layer} {Ms(numerator)} / {Ms(denominator)} = {Ratio(ratio)} "
        + $"(limit {Ratio(limit)}) {(pass ? "within limit" : "OVER LIMIT")}";

    private static string Ms(double value) => value.ToString("F3", CultureInfo.InvariantCulture) + "ms";

    private static string Ratio(double value) => value.ToString("F3", CultureInfo.InvariantCulture) + "x";

    private static PerfFinalGateScenarioResult PrimaryRow(
        PerfFinalGateProviderEvidence evidence,
        string scenarioId,
        int pageSize
    ) => RowOf(evidence.Primary, scenarioId, pageSize);

    private static PerfFinalGateScenarioResult RowOf(
        PerfFinalGateRunArtifacts run,
        string scenarioId,
        int pageSize
    ) =>
        run.Results.Results.FirstOrDefault(row => row.ScenarioId == scenarioId && row.PageSize == pageSize)
        ?? throw new PerfArtifactValidationException([
            $"final-gate row '{scenarioId}/{pageSize}' is missing from {run.RunDirectory}.",
        ]);

    private static PerfFinalGateScenarioResult PartitionRow(
        PerfFinalGateRunArtifacts run,
        int requestedNumber
    ) =>
        run.Results.Results.FirstOrDefault(row =>
            row.Family == "partition"
            && row.Variant == "unfiltered"
            && row.RequestedPartitionNumber == requestedNumber
        )
        ?? throw new PerfArtifactValidationException([
            $"the unfiltered partition number={requestedNumber} row is missing from {run.RunDirectory}.",
        ]);

    private static PerfScenarioResult BaselineRow(
        PerfFinalGateProviderEvidence evidence,
        string scenarioId,
        int pageSize
    ) =>
        evidence.Baseline.Results.Results.FirstOrDefault(row =>
            row.ScenarioId == scenarioId && row.PageSize == pageSize
        )
        ?? throw new PerfArtifactValidationException([
            $"baseline row '{scenarioId}/{pageSize}' is missing from {evidence.Baseline.RunDirectory}.",
        ]);
}
