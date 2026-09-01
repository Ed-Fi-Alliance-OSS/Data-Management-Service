// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_The_Final_Gate_Report_Writer
{
    private static PerfFinalGateEvaluation Evaluation() =>
        PerfFinalGateEvaluator.Evaluate([
            FinalGateEvaluationSamples.Evidence("postgresql"),
            FinalGateEvaluationSamples.Evidence("mssql"),
        ]);

    [Test]
    public void It_renders_the_overall_status_runs_and_every_gate()
    {
        PerfFinalGateEvaluation evaluation = Evaluation();

        string markdown = PerfFinalGateReportWriter.RenderMarkdown(evaluation);

        markdown.Should().StartWith("# Performance Final Gate Report");
        markdown.Should().Contain("Overall status: **PASS**");
        foreach (PerfEvaluatedRun run in evaluation.Runs)
        {
            markdown.Should().Contain(run.RunId);
        }

        foreach (PerfGateOutcome gate in evaluation.Gates)
        {
            markdown.Should().Contain($"### {gate.GateId} ({gate.Provider})");
        }

        markdown.Should().NotContain("\r");
    }

    [Test]
    public void It_renders_a_failing_evaluation_as_fail()
    {
        PerfFinalGateProviderEvidence evidence = FinalGateEvaluationSamples.Evidence();
        evidence = evidence with
        {
            Primary = evidence.Primary with
            {
                Results = FinalGateEvaluationSamples.WithRowLatency(
                    evidence.Primary.Results,
                    row => row.ScenarioId == "cursor-unfiltered-last" && row.PageSize == 500,
                    milliseconds: 60.0
                ),
            },
        };

        string markdown = PerfFinalGateReportWriter.RenderMarkdown(
            PerfFinalGateEvaluator.Evaluate([evidence])
        );

        markdown.Should().Contain("Overall status: **FAIL**");
        markdown.Should().Contain("OVER LIMIT");
    }

    [Test]
    public void It_writes_both_report_files_and_the_json_round_trips()
    {
        string reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-perf-final-gate-report-test",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            PerfFinalGateEvaluation evaluation = Evaluation();

            PerfFinalGateReportWriter.Write(reportDirectory, evaluation);

            File.Exists(Path.Combine(reportDirectory, PerfFinalGateReportWriter.MarkdownFileName))
                .Should()
                .BeTrue();
            PerfFinalGateEvaluation reloaded = PerfArtifactJson.Deserialize<PerfFinalGateEvaluation>(
                File.ReadAllText(Path.Combine(reportDirectory, PerfFinalGateReportWriter.JsonFileName))
            );
            reloaded.Gates.Should().HaveCount(evaluation.Gates.Count);
            reloaded.OverallStatus.Should().Be(evaluation.OverallStatus);
        }
        finally
        {
            if (Directory.Exists(reportDirectory))
            {
                Directory.Delete(reportDirectory, recursive: true);
            }
        }
    }
}
