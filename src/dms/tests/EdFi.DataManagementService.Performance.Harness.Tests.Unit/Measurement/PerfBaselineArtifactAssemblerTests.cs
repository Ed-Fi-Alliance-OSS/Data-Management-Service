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

    public static PerfMeasuredCell Cell(
        string scenarioId,
        int pageSize,
        string? batchSql = null,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    ) =>
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
                parameterValues ?? new Dictionary<string, object?>(),
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
public class Given_Cells_With_Extra_Bound_Parameters
{
    private string _boundParametersJson = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, object?> parameterValues = new()
        {
            ["offset"] = 0L,
            ["limit"] = 25L,
            ["filter_studentUniqueId"] = "perf-000000001",
            ["minChangeVersion"] = 42L,
        };
        List<PerfCellEvidence> evidence = [.. AssemblerSamples.Evidence()];
        PerfCellEvidence first = evidence[0];
        evidence[0] = first with
        {
            Cell = AssemblerSamples.Cell(
                first.Cell.ScenarioId,
                first.Cell.PageSize,
                parameterValues: parameterValues
            ),
        };

        PerfAssembledRun assembled = AssemblerSamples.Assemble(evidence);
        _boundParametersJson = assembled
            .AuxiliaryFiles.Single(file => file.RelativePath == "sql/postgresql.bound-parameters.json")
            .Content;
    }

    [Test]
    public void It_preserves_non_paging_parameters()
    {
        _boundParametersJson.Should().Contain("\"filter_studentUniqueId\": \"perf-000000001\"");
        _boundParametersJson.Should().Contain("\"minChangeVersion\": 42");
    }

    [Test]
    public void It_uses_lf_only_newlines()
    {
        _boundParametersJson.Should().NotContain("\r");
    }

    [Test]
    public void It_orders_parameter_keys_deterministically()
    {
        int filterIndex = _boundParametersJson.IndexOf("filter_studentUniqueId", StringComparison.Ordinal);
        int limitIndex = _boundParametersJson.IndexOf("\"limit\"", StringComparison.Ordinal);
        int minChangeVersionIndex = _boundParametersJson.IndexOf(
            "minChangeVersion",
            StringComparison.Ordinal
        );
        int offsetIndexInParameters = _boundParametersJson.IndexOf(
            "\"offset\"",
            filterIndex,
            StringComparison.Ordinal
        );
        filterIndex.Should().BeGreaterThan(0);
        limitIndex.Should().BeGreaterThan(filterIndex);
        minChangeVersionIndex.Should().BeGreaterThan(limitIndex);
        offsetIndexInParameters.Should().BeGreaterThan(minChangeVersionIndex);
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
