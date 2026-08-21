// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

internal static class AssemblerSamples
{
    public const string PageSelectionSql = "SELECT r.\"DocumentId\" FROM x ORDER BY r.\"DocumentId\";";
    public const string HydrationBatchSql = "WITH page_ids AS (SELECT 1) INSERT INTO page SELECT 1;";
    public const long DeepOffset = 9_000;

    public static PerfMeasuredCell Cell(string scenarioId, int pageSize, string? batchSql = null) =>
        new(
            scenarioId,
            pageSize,
            PerfScenarioExecutor.OffsetFor(scenarioId, pageSize, DeepOffset),
            pageSize,
            1,
            PerfLatencyMeasurement.Summarize([.. Enumerable.Range(1, 30).Select(value => (double)value)]),
            PerfLatencyMeasurement.Summarize([.. Enumerable.Range(1, 30).Select(value => value * 0.5)]),
            new PageSelectionQueryCapture(
                PageSelectionSql,
                new Dictionary<string, object?>(),
                PageSelectionCapture.Sha256Lowercase(PageSelectionSql)
            ),
            batchSql ?? HydrationBatchSql
        );

    public static IReadOnlyList<PerfCellEvidence> Evidence() =>
        [
            .. PerfScenarios.AllIds.SelectMany(scenarioId =>
                PerfScenarios.PageSizes.Select(pageSize =>
                {
                    PerfMeasuredCell cell = Cell(scenarioId, pageSize);
                    string planFile = $"plans/postgresql.{scenarioId}.{pageSize}.explain.json";
                    return new PerfCellEvidence(
                        cell,
                        ResultSamples.Postgresql().Database,
                        planFile,
                        [new PerfArtifactFile(planFile, "[]")]
                    );
                })
            ),
        ];

    public static PerfAssembledRun Assemble(IReadOnlyList<PerfCellEvidence>? evidence = null)
    {
        PerfRunManifest sample = ResultSamples.Manifest();
        return PerfBaselineArtifactAssembler.Assemble(
            PerfProvider.Postgresql,
            new PerfFixtureDefinition(PerfFixtureKind.Smoke10k),
            DeepOffset,
            warmupIterations: 5,
            measuredIterations: 30,
            evidence ?? Evidence(),
            new PerfRunIdentity("postgresql-smoke-10k-unit", "2026-08-20T12:00:00Z", "postgresql"),
            new PerfCommitIdentity(ResultSamples.RunnerCommit, ResultSamples.SubjectCommit, []),
            sample.Environment
        );
    }
}

[TestFixture]
public class Given_An_Assembled_Run
{
    private PerfAssembledRun _assembled = null!;

    [SetUp]
    public void Setup()
    {
        _assembled = AssemblerSamples.Assemble();
    }

    [Test]
    public void It_passes_the_artifact_validator()
    {
        PerfArtifactValidator.Validate(_assembled.Manifest, _assembled.Results).Should().BeEmpty();
    }

    [Test]
    public void It_writes_the_deduplicated_sql_artifacts()
    {
        _assembled
            .AuxiliaryFiles.Select(file => file.RelativePath)
            .Should()
            .Contain([
                "sql/postgresql.page-selection.sql",
                "sql/postgresql.hydration-batch.sql",
                "sql/postgresql.bound-parameters.json",
            ]);
        _assembled
            .AuxiliaryFiles.Single(file => file.RelativePath == "sql/postgresql.hydration-batch.sql")
            .Content.Should()
            .Be(AssemblerSamples.HydrationBatchSql);
    }

    [Test]
    public void It_records_one_plan_file_per_cell()
    {
        _assembled.AuxiliaryFiles.Count(file => file.RelativePath.StartsWith("plans/")).Should().Be(6);
        _assembled.Results.Results.Select(row => row.PlanFile).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void It_echoes_the_fixture_manifest()
    {
        _assembled.FixtureManifest.FixtureId.Should().Be("smoke-10k");
        _assembled.FixtureManifest.GapCount.Should().Be(1_112);
        _assembled.FixtureManifest.Verified.Should().BeTrue();
    }
}

[TestFixture]
public class Given_Cells_With_Diverging_Sql_Texts
{
    [Test]
    public void It_rejects_diverging_hydration_batch_sql()
    {
        List<PerfCellEvidence> evidence = [.. AssemblerSamples.Evidence()];
        PerfCellEvidence last = evidence[^1];
        evidence[^1] = last with
        {
            Cell = AssemblerSamples.Cell(
                last.Cell.ScenarioId,
                last.Cell.PageSize,
                batchSql: "DIFFERENT BATCH"
            ),
        };

        FluentActions
            .Invoking(() => AssemblerSamples.Assemble(evidence))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*hydration batch SQL*");
    }
}
