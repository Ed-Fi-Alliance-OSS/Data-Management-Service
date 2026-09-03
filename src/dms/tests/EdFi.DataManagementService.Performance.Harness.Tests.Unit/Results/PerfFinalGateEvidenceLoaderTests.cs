// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_The_Final_Gate_Evidence_Loader
{
    private string _rootDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-perf-final-gate-loader-test",
            Guid.NewGuid().ToString("N")
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private string WriteFinalGateRun()
    {
        string runDirectory = Path.Combine(_rootDirectory, "final-primary");
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();
        PerfFinalGateArtifactWriter.Write(
            runDirectory,
            FinalGateResultSamples.PrimaryManifest(),
            document,
            PerfArtifactJson.Serialize(
                PerfFixtureManifest.Create(new PerfFixtureDefinition(PerfFixtureKind.Smoke10k)) with
                {
                    SchemaVersion = PerfFinalGateArtifactSchema.Version,
                }
            ),
            [.. document.Results.Select(row => new PerfArtifactFile(row.PlanFile, "{}"))]
        );
        return runDirectory;
    }

    private string WriteBaselineRun()
    {
        string runDirectory = Path.Combine(_rootDirectory, "baseline");
        PerfResultsDocument document = ResultSamples.PostgresqlDocument();
        PerfRunArtifactWriter.Write(
            runDirectory,
            ResultSamples.Manifest(),
            document,
            PerfFixtureManifest.Create(new PerfFixtureDefinition(PerfFixtureKind.Primary500k)),
            [.. document.Results.Select(row => new PerfArtifactFile(row.PlanFile, "{}"))]
        );
        return runDirectory;
    }

    [Test]
    public void It_round_trips_a_written_final_gate_run()
    {
        string runDirectory = WriteFinalGateRun();

        PerfFinalGateRunArtifacts loaded = PerfFinalGateEvidenceLoader.LoadFinalGate(runDirectory);

        loaded.Manifest.RunKind.Should().Be(PerfFinalGateRunKinds.Primary);
        loaded.Results.Results.Should().HaveCount(PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.Count);
        loaded.RunDirectory.Should().Be(runDirectory);
    }

    [Test]
    public void It_round_trips_a_written_baseline_run()
    {
        string runDirectory = WriteBaselineRun();

        PerfBaselineRunArtifacts loaded = PerfFinalGateEvidenceLoader.LoadBaseline(runDirectory);

        loaded.Manifest.Fixture.FixtureId.Should().Be("primary-500k");
        loaded.Results.Results.Should().HaveCount(6);
    }

    private string WriteMssqlFinalGateRun()
    {
        string runDirectory = Path.Combine(_rootDirectory, "final-primary-mssql");
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument("mssql");
        PerfFinalGateArtifactWriter.Write(
            runDirectory,
            FinalGateResultSamples.PrimaryManifest("mssql"),
            document,
            PerfArtifactJson.Serialize(
                PerfFixtureManifest.Create(new PerfFixtureDefinition(PerfFixtureKind.Smoke10k)) with
                {
                    SchemaVersion = PerfFinalGateArtifactSchema.Version,
                }
            ),
            [.. document.Results.SelectMany(row => MssqlPlanFiles(row.PlanFile))]
        );
        return runDirectory;
    }

    private string WriteMssqlBaselineRun()
    {
        string runDirectory = Path.Combine(_rootDirectory, "baseline-mssql");
        PerfResultsDocument document = ResultSamples.MssqlDocument();
        PerfRunArtifactWriter.Write(
            runDirectory,
            ResultSamples.Manifest("mssql"),
            document,
            PerfFixtureManifest.Create(new PerfFixtureDefinition(PerfFixtureKind.Primary500k)),
            [.. document.Results.SelectMany(row => MssqlPlanFiles(row.PlanFile))]
        );
        return runDirectory;
    }

    /// <summary>
    /// One SQL-Server-style plan set per cell: the .plans.json index plus the .sqlplan and
    /// statistics files it references, exactly as the capture writes them.
    /// </summary>
    private static IEnumerable<PerfArtifactFile> MssqlPlanFiles(string indexPath)
    {
        string baseName = indexPath[..^".plans.json".Length];
        string planFile = $"{baseName}.plan01.sqlplan";
        string statisticsFile = $"{baseName}.stats.txt";
        yield return new PerfArtifactFile(
            indexPath,
            MssqlPlanCapture.PlanIndexJson([planFile], statisticsFile)
        );
        yield return new PerfArtifactFile(planFile, "<plan />");
        yield return new PerfArtifactFile(statisticsFile, "statistics");
    }

    [Test]
    public void It_refuses_a_final_gate_run_missing_a_file_its_plan_index_references()
    {
        string runDirectory = WriteMssqlFinalGateRun();
        string firstSqlPlan = Directory.GetFiles(Path.Combine(runDirectory, "plans"), "*.sqlplan")[0];
        File.Delete(firstSqlPlan);

        Action act = () => PerfFinalGateEvidenceLoader.LoadFinalGate(runDirectory);

        act.Should().Throw<PerfArtifactValidationException>().WithMessage("*referenced by*");
    }

    [Test]
    public void It_refuses_a_final_gate_run_missing_an_indexed_statistics_file()
    {
        string runDirectory = WriteMssqlFinalGateRun();
        string firstStatistics = Directory.GetFiles(Path.Combine(runDirectory, "plans"), "*.stats.txt")[0];
        File.Delete(firstStatistics);

        Action act = () => PerfFinalGateEvidenceLoader.LoadFinalGate(runDirectory);

        act.Should().Throw<PerfArtifactValidationException>().WithMessage("*referenced by*");
    }

    [Test]
    public void It_refuses_a_final_gate_run_with_a_malformed_plan_index()
    {
        string runDirectory = WriteMssqlFinalGateRun();
        string firstIndex = Directory.GetFiles(Path.Combine(runDirectory, "plans"), "*.plans.json")[0];
        File.WriteAllText(firstIndex, "{}");

        Action act = () => PerfFinalGateEvidenceLoader.LoadFinalGate(runDirectory);

        act.Should().Throw<PerfArtifactValidationException>().WithMessage("*planFiles*");
    }

    [Test]
    public void It_refuses_a_baseline_run_missing_a_file_its_plan_index_references()
    {
        string runDirectory = WriteMssqlBaselineRun();
        string firstSqlPlan = Directory.GetFiles(Path.Combine(runDirectory, "plans"), "*.sqlplan")[0];
        File.Delete(firstSqlPlan);

        Action act = () => PerfFinalGateEvidenceLoader.LoadBaseline(runDirectory);

        act.Should().Throw<PerfArtifactValidationException>().WithMessage("*referenced by*");
    }

    [Test]
    public void It_round_trips_complete_mssql_style_evidence()
    {
        PerfFinalGateEvidenceLoader
            .LoadFinalGate(WriteMssqlFinalGateRun())
            .Results.Results.Should()
            .HaveCount(PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.Count);
        PerfFinalGateEvidenceLoader
            .LoadBaseline(WriteMssqlBaselineRun())
            .Results.Results.Should()
            .HaveCount(6);
    }

    [Test]
    public void It_refuses_a_final_gate_run_whose_plan_evidence_is_missing()
    {
        string runDirectory = WriteFinalGateRun();
        string firstPlanFile = Directory.GetFiles(Path.Combine(runDirectory, "plans"))[0];
        File.Delete(firstPlanFile);

        Action act = () => PerfFinalGateEvidenceLoader.LoadFinalGate(runDirectory);

        act.Should().Throw<PerfArtifactValidationException>().WithMessage("*plan evidence*");
    }

    [Test]
    public void It_refuses_a_baseline_run_whose_plan_evidence_is_missing()
    {
        string runDirectory = WriteBaselineRun();
        string firstPlanFile = Directory.GetFiles(Path.Combine(runDirectory, "plans"))[0];
        File.Delete(firstPlanFile);

        Action act = () => PerfFinalGateEvidenceLoader.LoadBaseline(runDirectory);

        act.Should().Throw<PerfArtifactValidationException>().WithMessage("*plan evidence*");
    }
}
