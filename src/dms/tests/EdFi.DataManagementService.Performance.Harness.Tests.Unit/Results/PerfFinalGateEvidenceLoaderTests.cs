// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
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
