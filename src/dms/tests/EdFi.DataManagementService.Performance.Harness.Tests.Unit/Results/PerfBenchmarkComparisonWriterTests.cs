// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal static class ComparisonReport
{
    /// <summary>
    /// The CSV as rows keyed by column name, honoring quoted fields.
    /// </summary>
    public static List<Dictionary<string, string>> CsvRows(string csv)
    {
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<string> header = Fields(lines[0]);
        return
        [
            .. lines
                .Skip(1)
                .Select(line =>
                    header.Zip(Fields(line)).ToDictionary(pair => pair.First, pair => pair.Second)
                ),
        ];
    }

    public static Dictionary<string, string> CsvRow(string csv, string scenario, string pair) =>
        CsvRows(csv).Single(row => row["scenario_id"] == scenario && row["pair"] == pair);

    /// <summary>
    /// The text of one <c>## </c> section of the Markdown, up to the next one.
    /// </summary>
    public static string Section(string markdown, string heading)
    {
        int start = markdown.IndexOf($"\n## {heading}\n", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the report has a '{heading}' section");
        int end = markdown.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? markdown[start..] : markdown[start..end];
    }

    public static JsonObject Json(PerfBenchmarkComparisonResult result) =>
        JsonNode.Parse(PerfBenchmarkComparisonWriter.RenderJson(result))!.AsObject();

    private static List<string> Fields(string line)
    {
        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;
        int index = 0;
        while (index < line.Length)
        {
            char character = line[index++];
            if (quoted && character == '"' && index < line.Length && line[index] == '"')
            {
                field.Append('"');
                index++;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}

[TestFixture]
public class Given_A_Comparison
{
    private PerfBenchmarkRunPlan _plan = null!;
    private PerfBenchmarkComparisonResult _result = null!;

    [SetUp]
    public void Setup()
    {
        ComparisonBuilder builder = new();
        builder.CandidateRatios[ComparisonBuilder.ZeroSmall] = [1.02m, 1.05m, 1.06m, 1.04m, 1.08m];
        builder.CandidateRatios[ComparisonBuilder.Write] = [1.12m, 1.14m, 1.11m, 1.13m, 1.15m];
        PerfBenchmarkComparisonInput input = builder.Build();
        _plan = input.Plan;
        _result = PerfBenchmarkComparison.Compare(input);
    }

    [Test]
    public void It_writes_the_fixed_csv_header()
    {
        PerfBenchmarkComparisonWriter
            .RenderCsv(_result)
            .Split('\n')[0]
            .Should()
            .Be(
                "provider,scenario_id,pair,sample_count,baseline_p50_ms,candidate_p50_ms,p50_ratio,"
                    + "baseline_p95_ms,candidate_p95_ms,p95_ratio,baseline_p99_ms,candidate_p99_ms,"
                    + "baseline_mean_ms,candidate_mean_ms,baseline_command_counts,candidate_command_counts,"
                    + "diagnostic_baseline_driver_execute_p50_ms,diagnostic_candidate_driver_execute_p50_ms,"
                    + "threshold,median_p50_ratio,median_p95_ratio,spread,result,reason"
            );
    }

    [Test]
    public void It_writes_each_scenarios_pairs_in_order_followed_by_a_combined_row()
    {
        List<Dictionary<string, string>> rows = ComparisonReport.CsvRows(
            PerfBenchmarkComparisonWriter.RenderCsv(_result)
        );

        rows.Should().HaveCount(7 * 6);
        foreach (PerfBenchmarkScenarioComparison scenario in _result.Scenarios)
        {
            rows.Where(row => row["scenario_id"] == scenario.ScenarioId)
                .Select(row => row["pair"])
                .Should()
                .Equal("1", "2", "3", "4", "5", PerfBenchmarkComparisonWriter.CombinedPair);
        }
    }

    [Test]
    public void It_puts_per_pair_values_on_pair_rows_and_the_verdict_on_the_combined_row()
    {
        string csv = PerfBenchmarkComparisonWriter.RenderCsv(_result);

        Dictionary<string, string> pair = ComparisonReport.CsvRow(csv, ComparisonBuilder.ZeroSmall, "2");
        pair["p50_ratio"].Should().Be("1.0500");
        pair["result"].Should().BeEmpty();
        pair["diagnostic_candidate_driver_execute_p50_ms"].Should().NotBeEmpty();

        Dictionary<string, string> combined = ComparisonReport.CsvRow(
            csv,
            ComparisonBuilder.Write,
            PerfBenchmarkComparisonWriter.CombinedPair
        );
        combined["p50_ratio"].Should().BeEmpty();
        combined["threshold"].Should().Be("0.1000");
        combined["median_p50_ratio"].Should().Be("1.1300");
        combined["spread"].Should().Be("0.1500");
        combined["result"].Should().Be(PerfBenchmarkComparisonResult.SuspectedRegression);
        combined["reason"].Should().Be("median p50 ratio 1.13 exceeds 1 + 0.1");
    }

    [Test]
    public void It_leaves_the_read_diagnostics_blank_for_the_write()
    {
        ComparisonReport
            .CsvRow(PerfBenchmarkComparisonWriter.RenderCsv(_result), ComparisonBuilder.Write, "1")[
                "diagnostic_baseline_driver_execute_p50_ms"
            ]
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_round_trips_the_json()
    {
        JsonObject json = ComparisonReport.Json(_result);

        json["schemaVersion"]!.GetValue<string>().Should().Be(PerfBenchmarkComparisonWriter.SchemaVersion);
        json["result"]!.GetValue<string>().Should().Be(PerfBenchmarkComparisonResult.SuspectedRegression);
        json["exitCode"]!.GetValue<int>().Should().Be(0);
        json["runPlanProfileSha256"]!.GetValue<string>().Should().Be(_plan.ProfileSha256);

        json.Remove("schemaVersion");
        PerfBenchmarkJson
            .Read<PerfBenchmarkComparisonResult>(
                json.ToJsonString(),
                PerfBenchmarkComparisonWriter.JsonFileName
            )
            .Should()
            .BeEquivalentTo(_result);
    }

    [Test]
    public void It_renders_the_identities_scenario_table_and_standing_limits()
    {
        string markdown = PerfBenchmarkComparisonWriter.RenderMarkdown(_plan, _result);

        string identities = ComparisonReport.Section(markdown, "Identities");
        identities.Should().Contain($"| Run id | {ComparisonBuilder.RunId} |");
        identities.Should().Contain($"| Baseline commit | {ComparisonBuilder.Reference} |");
        identities.Should().Contain($"| Candidate commit | {ComparisonBuilder.OtherCommit} |");
        identities.Should().Contain($"| Harness commit | {ComparisonBuilder.HarnessCommit} |");
        identities.Should().Contain($"| Profile SHA-256 (run plan) | {_plan.ProfileSha256} |");
        identities.Should().Contain("| Labels | uncalibrated |");

        string scenarios = ComparisonReport.Section(markdown, "Scenarios");
        scenarios
            .Should()
            .Contain(
                $"| {ComparisonBuilder.Write} | 10.000 | 11.300 | +13.0% | 1.1300 | 15.0% | 10.0% "
                    + "| suspected-regression | median p50 ratio 1.13 exceeds 1 + 0.1 |"
            );

        string limits = ComparisonReport.Section(markdown, "Standing limits");
        foreach (string limit in PerfBenchmarkComparisonWriter.StandingLimits)
        {
            limits.Should().Contain(limit);
        }
    }

    [Test]
    public void It_writes_the_three_files_without_a_bom()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"benchmark-writer-{Guid.NewGuid():N}");
        try
        {
            PerfBenchmarkComparisonWriter.Write(directory, _plan, _result);

            File.ReadAllText(Path.Combine(directory, PerfBenchmarkComparisonWriter.JsonFileName))
                .Should()
                .Be(PerfBenchmarkComparisonWriter.RenderJson(_result));
            File.ReadAllText(Path.Combine(directory, PerfBenchmarkComparisonWriter.CsvFileName))
                .Should()
                .Be(PerfBenchmarkComparisonWriter.RenderCsv(_result));
            File.ReadAllText(Path.Combine(directory, PerfBenchmarkComparisonWriter.MarkdownFileName))
                .Should()
                .Be(PerfBenchmarkComparisonWriter.RenderMarkdown(_plan, _result));
            File.ReadAllBytes(Path.Combine(directory, PerfBenchmarkComparisonWriter.JsonFileName))[0]
                .Should()
                .Be((byte)'{');
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

[TestFixture]
public class Given_A_Comparison_Under_A_Comma_Decimal_Culture
{
    private CultureInfo _originalCulture = null!;

    [SetUp]
    public void Setup()
    {
        _originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
    }

    [TearDown]
    public void TearDown() => CultureInfo.CurrentCulture = _originalCulture;

    [Test]
    public void It_compares_and_renders_with_invariant_numbers()
    {
        ComparisonBuilder builder = new();
        builder.CandidateRatios[ComparisonBuilder.Write] = [1.125m, 1.125m, 1.125m, 1.125m, 1.125m];
        PerfBenchmarkComparisonInput input = builder.Build();
        PerfBenchmarkComparisonResult result = PerfBenchmarkComparison.Compare(input);

        string csv = PerfBenchmarkComparisonWriter.RenderCsv(result);
        Dictionary<string, string> combined = ComparisonReport.CsvRow(
            csv,
            ComparisonBuilder.Write,
            PerfBenchmarkComparisonWriter.CombinedPair
        );
        combined["median_p50_ratio"].Should().Be("1.1250");
        combined["reason"].Should().Be("median p50 ratio 1.125 exceeds 1 + 0.1");
        ComparisonReport.CsvRow(csv, ComparisonBuilder.Write, "1")["baseline_p50_ms"].Should().Be("10.000");

        string markdown = PerfBenchmarkComparisonWriter.RenderMarkdown(input.Plan, result);
        markdown.Should().Contain("+12.5%").And.Contain("1 + 0.1 |").And.NotContain("1,125");
    }
}

[TestFixture]
public class Given_A_Sizing_Comparison
{
    private PerfBenchmarkRunPlan _plan = null!;
    private PerfBenchmarkComparisonResult _result = null!;

    [SetUp]
    public void Setup()
    {
        PerfBenchmarkComparisonInput input = Given_A_Sizing_Run
            .Sizing(writeRatio: 1.10m, readRatio: 1.00m)
            .Build();
        _plan = input.Plan;
        _result = PerfBenchmarkComparison.Compare(input);
    }

    [Test]
    public void It_serializes_the_purpose_eligibility_and_sizing_block()
    {
        JsonObject json = ComparisonReport.Json(_result);

        json["purpose"]!.GetValue<string>().Should().Be("Sizing");
        json["qualificationEligible"]!.GetValue<bool>().Should().BeFalse();
        json["evidence"]!.GetValue<bool>().Should().BeTrue();
        json["exitCode"]!.GetValue<int>().Should().Be(0);
        json["labels"]!
            .AsArray()
            .Select(label => label!.GetValue<string>())
            .Should()
            .Contain("demonstration");

        JsonObject[] sizing = [.. json["sizing"]!.AsArray().Select(entry => entry!.AsObject())];
        JsonObject write = sizing.Single(entry =>
            entry["scenarioId"]!.GetValue<string>() == ComparisonBuilder.Write
        );
        write["status"]!.GetValue<string>().Should().Be(PerfBenchmarkSizing.Adjust);
        write["inputFactor"]!.GetValue<decimal>().Should().Be(0.20m);
        write["nextFactor"]!.GetValue<decimal>().Should().Be(0.40m);
        JsonObject read = sizing.Single(entry =>
            entry["scenarioId"]!.GetValue<string>() == ComparisonBuilder.ZeroSmall
        );
        read["status"]!.GetValue<string>().Should().Be(PerfBenchmarkSizing.Inconclusive);
        read["nextFactor"]!.GetValue<decimal>().Should().Be(0.20m);
        read["reason"]!.GetValue<string>().Should().Be("the observed increase is zero");
        json.ContainsKey("calibration").Should().BeFalse();
    }

    [Test]
    public void It_renders_the_sizing_block_and_the_reasons_it_is_not_eligible()
    {
        string markdown = PerfBenchmarkComparisonWriter.RenderMarkdown(_plan, _result);

        ComparisonReport
            .Section(markdown, "Sizing")
            .Should()
            .Contain($"| {ComparisonBuilder.Write} | 0.2 | +10.0% | adjust | 0.4 | 0.4 | — |")
            .And.Contain(
                $"| {ComparisonBuilder.ZeroSmall} | 0.2 | 0.0% | inconclusive | — | 0.2 | the observed increase is zero |"
            );
        ComparisonReport
            .Section(markdown, "Identities")
            .Should()
            .Contain("| Not eligible because | purpose Sizing is a diagnostic |")
            .And.Contain(
                $"| Injection (candidate sides) | {ComparisonBuilder.Write} × 0.2; {ComparisonBuilder.ZeroSmall} × 0.2 |"
            );
    }
}

[TestFixture]
public class Given_A_Qualifying_Detection_Comparison
{
    private PerfBenchmarkRunPlan _plan = null!;
    private PerfBenchmarkComparisonResult _result = null!;

    [SetUp]
    public void Setup()
    {
        PerfBenchmarkComparisonInput input = Given_Detection_And_Repeatability_Eligibility
            .Eligible(PerfBenchmarkComparison.Detection)
            .Build();
        _plan = input.Plan;
        _result = PerfBenchmarkComparison.Compare(input);
    }

    [Test]
    public void It_serializes_the_purpose_eligibility_and_detection_block()
    {
        JsonObject json = ComparisonReport.Json(_result);

        json["purpose"]!.GetValue<string>().Should().Be("Detection");
        json["qualificationEligible"]!.GetValue<bool>().Should().BeTrue();
        json["notEligibleReasons"]!.AsArray().Should().BeEmpty();
        json["result"]!.GetValue<string>().Should().Be(PerfBenchmarkComparisonResult.SuspectedRegression);
        json["exitCode"]!.GetValue<int>().Should().Be(0);
        json["detection"]!["passed"]!.GetValue<bool>().Should().BeTrue();
        json["detection"]!["scenarios"]!.AsArray().Should().HaveCount(2);
        json.ContainsKey("sizing").Should().BeFalse();
    }

    [Test]
    public void It_renders_the_detection_block()
    {
        string markdown = PerfBenchmarkComparisonWriter.RenderMarkdown(_plan, _result);

        string detection = ComparisonReport.Section(markdown, "Detection");
        detection.Should().Contain("Frozen detection rule: **passed**");
        detection
            .Should()
            .Contain($"| {ComparisonBuilder.Write} | +20.0% | [18.0%, 25.0%] | yes | yes | yes |");
        detection.Should().Contain("Other flagged scenarios: none.");
        markdown.Should().Contain("Qualification eligible.");
    }
}

[TestFixture]
public class Given_A_Calibration_Comparison
{
    [Test]
    public void It_renders_the_derived_thresholds_and_the_fingerprint()
    {
        ComparisonBuilder builder = new() { Purpose = PerfBenchmarkComparison.Calibration };
        builder.CandidateRatios[ComparisonBuilder.ZeroSmall] = [1.02m, 0.98m, 1.01m, 0.99m, 1.03m];
        PerfBenchmarkComparisonInput input = builder.Build();
        PerfBenchmarkComparisonResult result = PerfBenchmarkComparison.Compare(input);

        string calibration = ComparisonReport.Section(
            PerfBenchmarkComparisonWriter.RenderMarkdown(input.Plan, result),
            "Calibration"
        );
        calibration.Should().Contain($"| {ComparisonBuilder.ZeroSmall} | 3.0% | 6.0% |");
        calibration.Should().Contain($"| {ComparisonBuilder.Write} | 0.0% | 5.0% |");
        calibration.Should().Contain("| osFamily | \"Windows\" |");
        ComparisonReport.Json(result)["calibration"]!["derivedThresholds"]![ComparisonBuilder.ZeroSmall]!
            .GetValue<decimal>()
            .Should()
            .Be(0.06m);
    }
}

[TestFixture]
public class Given_An_Invalid_Comparison
{
    [Test]
    public void It_separates_conformance_failures_from_rule_failures()
    {
        ComparisonBuilder builder = new();
        builder
            .EditSide(
                1,
                PerfBenchmarkSideResult.BaselineRole,
                side => side with { Side = side.Side with { EffectiveLogLevel = "Information" } }
            )
            .EditSide(
                2,
                PerfBenchmarkSideResult.CandidateRole,
                side => side with { Environment = side.Environment with { CompetingHarnessProcess = true } }
            );
        PerfBenchmarkComparisonInput input = builder.Build();
        PerfBenchmarkComparisonResult result = PerfBenchmarkComparison.Compare(input);

        JsonObject json = ComparisonReport.Json(result);
        json["result"]!.GetValue<string>().Should().Be(PerfBenchmarkComparisonResult.Invalid);
        json["exitCode"]!.GetValue<int>().Should().Be(2);

        string markdown = PerfBenchmarkComparisonWriter.RenderMarkdown(input.Plan, result);
        string conformance = ComparisonReport.Section(markdown, "Conformance");
        string rules = ComparisonReport.Section(markdown, "Environment rules");
        conformance.Should().Contain("pair 1 baseline: required values").And.NotContain("competing");
        rules.Should().Contain("- pair 2 candidate: rule: a competing harness process was running.");
        markdown.Should().Contain("Result: **invalid** (exit code 2)");
        markdown.Should().NotContain("## Calibration");
    }
}
