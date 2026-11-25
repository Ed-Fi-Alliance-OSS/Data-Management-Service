// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Renders a comparison as <c>comparison.json</c> (the wrapper reads its <c>exitCode</c>),
/// <c>comparison.csv</c> (one row per scenario × pair plus a <c>combined</c> row) and
/// <c>comparison.md</c> for reviewers. Rendering is pure and culture-invariant; <see cref="Write" />
/// saves the three files, UTF-8 without BOM, LF-only.
/// </summary>
public static class PerfBenchmarkComparisonWriter
{
    public const string SchemaVersion = "benchmark-comparison 1.0.0";
    public const string JsonFileName = "comparison.json";
    public const string CsvFileName = "comparison.csv";
    public const string MarkdownFileName = "comparison.md";
    public const string CombinedPair = "combined";

    /// <summary>
    /// The fixed CSV columns. Pair rows leave the verdict columns blank and the combined row
    /// leaves the per-pair columns blank. The <c>diagnostic_</c> columns are read diagnostics
    /// (driver execute time), blank for the write scenario.
    /// </summary>
    public static readonly IReadOnlyList<string> CsvHeader =
    [
        "provider",
        "scenario_id",
        "pair",
        "sample_count",
        "baseline_p50_ms",
        "candidate_p50_ms",
        "p50_ratio",
        "baseline_p95_ms",
        "candidate_p95_ms",
        "p95_ratio",
        "baseline_p99_ms",
        "candidate_p99_ms",
        "baseline_mean_ms",
        "candidate_mean_ms",
        "baseline_command_counts",
        "candidate_command_counts",
        "diagnostic_baseline_driver_execute_p50_ms",
        "diagnostic_candidate_driver_execute_p50_ms",
        "threshold",
        "median_p50_ratio",
        "median_p95_ratio",
        "spread",
        "result",
        "reason",
    ];

    public static readonly IReadOnlyList<string> StandingLimits =
    [
        "Warnings judge p50 only (p95 warnings are off unless the profile enables them), so a regression that only affects the tail can be missed.",
        "p99 is descriptive and limited by the sample size: nearest-rank p99 rests on the slowest few measured requests.",
        "Command counts are driver command executions per request, not network round trips.",
        "This is serial latency at concurrency 1, not capacity or throughput.",
    ];

    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true, NewLine = "\n" };

    public static void Write(
        string directory,
        PerfBenchmarkRunPlan plan,
        PerfBenchmarkComparisonResult result
    )
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, JsonFileName), RenderJson(result), _utf8NoBom);
        File.WriteAllText(Path.Combine(directory, CsvFileName), RenderCsv(result), _utf8NoBom);
        File.WriteAllText(
            Path.Combine(directory, MarkdownFileName),
            RenderMarkdown(plan, result),
            _utf8NoBom
        );
    }

    /// <summary>
    /// The result's own camelCase serialization, led by <c>schemaVersion</c>.
    /// </summary>
    public static string RenderJson(PerfBenchmarkComparisonResult result)
    {
        JsonObject document = new() { ["schemaVersion"] = SchemaVersion };
        foreach (
            (string name, JsonNode? value) in JsonNode.Parse(PerfBenchmarkJson.Serialize(result))!.AsObject()
        )
        {
            document[name] = value?.DeepClone();
        }

        return document.ToJsonString(_indented) + "\n";
    }

    public static string RenderCsv(PerfBenchmarkComparisonResult result)
    {
        StringBuilder builder = new();
        AppendCsvRow(builder, CsvHeader);
        foreach (PerfBenchmarkScenarioComparison scenario in result.Scenarios)
        {
            foreach (PerfBenchmarkPairStatistics pair in scenario.Pairs)
            {
                AppendCsvRow(
                    builder,
                    [
                        result.Provider,
                        scenario.ScenarioId,
                        Integer(pair.Pair),
                        Integer(pair.SampleCount),
                        Milliseconds(pair.BaselineP50Ms),
                        Milliseconds(pair.CandidateP50Ms),
                        Ratio(pair.P50Ratio),
                        Milliseconds(pair.BaselineP95Ms),
                        Milliseconds(pair.CandidateP95Ms),
                        Ratio(pair.P95Ratio),
                        Milliseconds(pair.BaselineP99Ms),
                        Milliseconds(pair.CandidateP99Ms),
                        Milliseconds(pair.BaselineMeanMs),
                        Milliseconds(pair.CandidateMeanMs),
                        pair.BaselineCommandCounts,
                        pair.CandidateCommandCounts,
                        Milliseconds(pair.BaselineDriverExecuteP50Ms),
                        Milliseconds(pair.CandidateDriverExecuteP50Ms),
                        "",
                        "",
                        "",
                        "",
                        "",
                        "",
                    ]
                );
            }

            AppendCsvRow(
                builder,
                [
                    result.Provider,
                    scenario.ScenarioId,
                    CombinedPair,
                    .. Enumerable.Repeat("", 15),
                    Ratio(scenario.Threshold),
                    Ratio(scenario.MedianP50Ratio),
                    Ratio(scenario.MedianP95Ratio),
                    Ratio(scenario.Spread),
                    scenario.Result,
                    scenario.Reason,
                ]
            );
        }

        return builder.ToString();
    }

    public static string RenderMarkdown(PerfBenchmarkRunPlan plan, PerfBenchmarkComparisonResult result)
    {
        StringBuilder md = new();
        md.Append("# Benchmark comparison\n\n");
        md.Append(
            Invariant(
                $"Result: **{result.Result}** (exit code {result.ExitCode}). Purpose: {result.Purpose}. "
            )
        );
        md.Append(
            result.QualificationEligible
                ? "Qualification eligible.\n\n"
                : "Not qualification eligible (diagnostic).\n\n"
        );

        md.Append("## Identities\n\n| Field | Value |\n| --- | --- |\n");
        Row(md, "Run id", result.RunId);
        Row(md, "Provider", result.Provider);
        Row(md, "Purpose", result.Purpose);
        Row(md, "Workload", plan.Smoke ? "smokeWorkload (not evidence)" : "workload");
        Row(md, "Evidence", YesNo(result.Evidence));
        Row(md, "Pairs", Integer(plan.PairCount));
        Row(md, "Baseline commit", plan.BaselineCommit);
        Row(md, "Candidate commit", plan.CandidateCommit);
        Row(md, "Harness commit", plan.HarnessCommit);
        Row(md, "Profile SHA-256 (run plan)", result.RunPlanProfileSha256);
        Row(md, "Injection (candidate sides)", Factors(plan.Injection));
        Row(md, "Labels", result.Labels.Count == 0 ? "none" : string.Join(", ", result.Labels));
        Row(md, "Qualification eligible", YesNo(result.QualificationEligible));
        foreach (string reason in result.NotEligibleReasons)
        {
            Row(md, "Not eligible because", reason);
        }

        string[] ruleReasons = [.. result.InvalidReasons.Where(IsRuleReason)];
        string[] conformanceReasons = [.. result.InvalidReasons.Where(reason => !IsRuleReason(reason))];

        md.Append("\n## Conformance\n\n");
        Bullets(
            md,
            conformanceReasons,
            "Every run-plan, workload, validity, required-value, consistency, injection and qualified-environment check passed."
        );

        md.Append("\n## Compatibility\n\n");
        Bullets(
            md,
            [.. result.PermittedDifferences.Select(field => $"Permitted difference: {field}")],
            "The sides agree on every compared field; no permitted difference was observed."
        );

        md.Append("\n## Environment rules\n\n");
        Bullets(md, ruleReasons, "Every environment rule and measured-window coverage check passed.");

        AppendScenarios(md, result);
        AppendCalibration(md, result.Calibration);
        AppendDetection(md, result.Detection);
        AppendSizing(md, result.Sizing);

        md.Append("\n## Standing limits\n\n");
        Bullets(md, StandingLimits, "");
        return md.ToString();
    }

    private static void AppendScenarios(StringBuilder md, PerfBenchmarkComparisonResult result)
    {
        md.Append("\n## Scenarios\n\n");
        if (result.Scenarios.Count == 0)
        {
            md.Append("No scenario statistics: not every pair has both sides' samples.\n");
            return;
        }

        md.Append(
            "Absolute values are the medians of the per-pair p50s; the change is the median per-pair p50 ratio − 1.\n\n"
        );
        md.Append(
            "| Scenario | Baseline p50 (ms) | Candidate p50 (ms) | Change | Median p95 ratio | Spread | Threshold | Result | Reason |\n"
        );
        md.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (PerfBenchmarkScenarioComparison scenario in result.Scenarios)
        {
            md.Append(
                $"| {Cell(scenario.ScenarioId)} | {Milliseconds(Median(scenario.Pairs.Select(pair => pair.BaselineP50Ms)))} "
                    + $"| {Milliseconds(Median(scenario.Pairs.Select(pair => pair.CandidateP50Ms)))} "
                    + $"| {Percent(scenario.MedianP50Ratio - 1, signed: true)} | {Ratio(scenario.MedianP95Ratio)} "
                    + $"| {Percent(scenario.Spread)} | {Percent(scenario.Threshold)} | {scenario.Result} "
                    + $"| {Cell(scenario.Reason)} |\n"
            );
        }

        md.Append("\n### Pairs\n\n");
        md.Append(
            "p99 is descriptive; driver execute p50 is a read diagnostic. Command counts are per request "
                + "(the write lists `count`x`requests`).\n\n"
        );
        md.Append(
            "| Scenario | Pair | Samples | p50 B / C (ms) | p50 ratio | p95 B / C (ms) | p95 ratio | p99 B / C (ms) "
                + "| Mean B / C (ms) | Commands B / C | Driver execute p50 B / C (ms, diagnostic) |\n"
        );
        md.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (PerfBenchmarkScenarioComparison scenario in result.Scenarios)
        {
            foreach (PerfBenchmarkPairStatistics pair in scenario.Pairs)
            {
                md.Append(
                    $"| {Cell(scenario.ScenarioId)} | {Integer(pair.Pair)} | {Integer(pair.SampleCount)} "
                        + $"| {Milliseconds(pair.BaselineP50Ms)} / {Milliseconds(pair.CandidateP50Ms)} | {Ratio(pair.P50Ratio)} "
                        + $"| {Milliseconds(pair.BaselineP95Ms)} / {Milliseconds(pair.CandidateP95Ms)} | {Ratio(pair.P95Ratio)} "
                        + $"| {Milliseconds(pair.BaselineP99Ms)} / {Milliseconds(pair.CandidateP99Ms)} "
                        + $"| {Milliseconds(pair.BaselineMeanMs)} / {Milliseconds(pair.CandidateMeanMs)} "
                        + $"| {Cell(pair.BaselineCommandCounts)} / {Cell(pair.CandidateCommandCounts)} "
                        + $"| {OrDash(Milliseconds(pair.BaselineDriverExecuteP50Ms))} / "
                        + $"{OrDash(Milliseconds(pair.CandidateDriverExecuteP50Ms))} |\n"
                );
            }
        }
    }

    private static void AppendCalibration(StringBuilder md, PerfBenchmarkCalibration? calibration)
    {
        if (calibration is null)
        {
            return;
        }

        md.Append("\n## Calibration\n\n");
        md.Append(
            "Same-build, not injected, every required value satisfied. Derived threshold = "
                + "max(5%, 2 × spread), rounded up to a whole percent.\n\n"
        );
        md.Append("| Scenario | Spread | Derived threshold |\n| --- | --- | --- |\n");
        foreach ((string scenario, decimal threshold) in calibration.DerivedThresholds)
        {
            md.Append(
                $"| {Cell(scenario)} | {Percent(calibration.Spreads.GetValueOrDefault(scenario))} | {Percent(threshold)} |\n"
            );
        }

        md.Append("\nObserved environment fingerprint:\n\n| Field | Value |\n| --- | --- |\n");
        foreach (
            (string field, string value) in PerfBenchmarkComparison.FingerprintFields(calibration.Fingerprint)
        )
        {
            Row(md, field, value);
        }
    }

    private static void AppendDetection(StringBuilder md, PerfBenchmarkDetection? detection)
    {
        if (detection is null)
        {
            return;
        }

        md.Append("\n## Detection\n\n");
        md.Append(
            $"Frozen detection rule: **{(detection.Passed ? "passed" : "failed")}**. Each demonstration scenario "
                + "must be flagged with its observed increase in the band, and no other scenario flagged.\n\n"
        );
        md.Append("| Scenario | Observed increase | Band | Flagged | In band | Passed |\n");
        md.Append("| --- | --- | --- | --- | --- | --- |\n");
        foreach (PerfBenchmarkDetectionScenario scenario in detection.Scenarios)
        {
            md.Append(
                $"| {Cell(scenario.ScenarioId)} | {Percent(scenario.ObservedIncrease, signed: true)} "
                    + $"| [{Percent(scenario.BandLow)}, {Percent(scenario.BandHigh)}] | {YesNo(scenario.Flagged)} "
                    + $"| {YesNo(scenario.InBand)} | {YesNo(scenario.Passed)} |\n"
            );
        }

        md.Append(
            "\nOther flagged scenarios: "
                + (
                    detection.OtherFlaggedScenarios.Count == 0
                        ? "none"
                        : string.Join(", ", detection.OtherFlaggedScenarios)
                )
                + ".\n"
        );
    }

    private static void AppendSizing(StringBuilder md, IReadOnlyList<PerfBenchmarkSizing>? sizing)
    {
        if (sizing is null)
        {
            return;
        }

        md.Append("\n## Sizing\n\n");
        md.Append(
            "Each demonstration scenario is sized independently; `nextFactor` is the next run's factor.\n\n"
        );
        md.Append(
            "| Scenario | Input factor | Observed increase | Status | Suggested factor | Next factor | Reason |\n"
        );
        md.Append("| --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (PerfBenchmarkSizing scenario in sizing)
        {
            md.Append(
                $"| {Cell(scenario.ScenarioId)} | {OrDash(Factor(scenario.InputFactor))} "
                    + $"| {OrDash(scenario.ObservedIncrease is { } observed ? Percent(observed, signed: true) : "")} "
                    + $"| {scenario.Status} | {OrDash(Factor(scenario.SuggestedFactor))} "
                    + $"| {OrDash(Factor(scenario.NextFactor))} | {OrDash(Cell(scenario.Reason ?? ""))} |\n"
            );
        }
    }

    /// <summary>
    /// Environment-rule failures carry the engine's <c>rule:</c> marker; every other invalid
    /// reason is a conformance failure.
    /// </summary>
    private static bool IsRuleReason(string reason) => reason.Contains(": rule: ", StringComparison.Ordinal);

    private static void Bullets(StringBuilder md, IEnumerable<string> lines, string whenEmpty)
    {
        bool any = false;
        foreach (string line in lines)
        {
            md.Append($"- {line}\n");
            any = true;
        }

        if (!any && whenEmpty.Length > 0)
        {
            md.Append($"{whenEmpty}\n");
        }
    }

    private static void Row(StringBuilder md, string field, string value) =>
        md.Append($"| {Cell(field)} | {Cell(value)} |\n");

    private static void AppendCsvRow(StringBuilder builder, IEnumerable<string> fields)
    {
        builder.Append(string.Join(',', fields.Select(Escape)));
        builder.Append('\n');
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Milliseconds(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string Milliseconds(double? value) => value is null ? "" : Milliseconds(value.Value);

    private static string Ratio(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Factor(decimal? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";

    private static string Percent(decimal value, bool signed = false) =>
        (value * 100).ToString(signed ? "+0.0;-0.0;0.0" : "0.0", CultureInfo.InvariantCulture) + "%";

    private static string Factors(IReadOnlyDictionary<string, decimal> factors) =>
        factors.Count == 0
            ? "none"
            : string.Join(
                "; ",
                factors
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key} × {Factor(entry.Value)}")
            );

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string OrDash(string value) => value.Length == 0 ? "—" : value;

    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\n", " ");

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
