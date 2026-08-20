// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal static class ValidatorTestSupport
{
    public static PerfResultsDocument WithRow(
        PerfResultsDocument document,
        int index,
        Func<PerfScenarioResult, PerfScenarioResult> mutate
    ) => document with { Results = [.. document.Results.Select((row, i) => i == index ? mutate(row) : row)] };
}

[TestFixture]
public class Given_A_Valid_Postgresql_Artifact_Pair
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        _errors = PerfArtifactValidator.Validate(
            ResultSamples.Manifest(),
            ResultSamples.PostgresqlDocument()
        );
    }

    [Test]
    public void It_reports_no_errors()
    {
        _errors.Should().BeEmpty();
    }

    [Test]
    public void It_passes_ensure_valid()
    {
        FluentActions
            .Invoking(() =>
                PerfArtifactValidator.EnsureValid(
                    ResultSamples.Manifest(),
                    ResultSamples.PostgresqlDocument()
                )
            )
            .Should()
            .NotThrow();
    }
}

[TestFixture]
public class Given_A_Valid_Mssql_Artifact_Pair
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        _errors = PerfArtifactValidator.Validate(
            ResultSamples.Manifest("mssql"),
            ResultSamples.MssqlDocument()
        );
    }

    [Test]
    public void It_reports_no_errors()
    {
        _errors.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_Wrong_Schema_Versions
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        _errors = PerfArtifactValidator.Validate(
            ResultSamples.Manifest() with
            {
                SchemaVersion = "0.9.0",
            },
            ResultSamples.PostgresqlDocument() with
            {
                SchemaVersion = "0.9.0",
            }
        );
    }

    [Test]
    public void It_rejects_the_manifest_version()
    {
        _errors.Should().Contain(error => error.StartsWith("manifest: schema version"));
    }

    [Test]
    public void It_rejects_the_results_version()
    {
        _errors.Should().Contain(error => error.StartsWith("results: schema version"));
    }
}

[TestFixture]
public class Given_Result_Rows_With_Broken_Measurements
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ResultSamples.PostgresqlDocument();
        document = ValidatorTestSupport.WithRow(
            document,
            0,
            row => row with { ReturnedRows = row.PageSize - 1 }
        );
        document = ValidatorTestSupport.WithRow(document, 1, row => row with { CommandCountPerRequest = 2 });
        document = ValidatorTestSupport.WithRow(
            document,
            2,
            row =>
                row with
                {
                    LatencyMs = row.LatencyMs with { SamplesMs = [.. row.LatencyMs.SamplesMs.Take(5)] },
                }
        );
        document = ValidatorTestSupport.WithRow(
            document,
            3,
            row => row with { DbCommandMs = row.DbCommandMs with { P50Ms = row.DbCommandMs.P95Ms + 1 } }
        );
        document = ValidatorTestSupport.WithRow(
            document,
            4,
            row => row with { PageSelectionSqlSha256 = "xyz" }
        );
        document = ValidatorTestSupport.WithRow(document, 5, row => row with { PlanFile = " " });
        _errors = PerfArtifactValidator.Validate(ResultSamples.Manifest(), document);
    }

    [Test]
    public void It_rejects_returned_rows_not_matching_page_size()
    {
        _errors.Should().Contain(error => error.StartsWith("results[0]") && error.Contains("returned rows"));
    }

    [Test]
    public void It_rejects_more_than_one_command_per_request()
    {
        _errors.Should().Contain(error => error.StartsWith("results[1]") && error.Contains("command count"));
    }

    [Test]
    public void It_rejects_a_short_sample_list()
    {
        _errors.Should().Contain(error => error.StartsWith("results[2]") && error.Contains("sample count"));
    }

    [Test]
    public void It_rejects_inverted_percentiles()
    {
        _errors
            .Should()
            .Contain(error => error.StartsWith("results[3]") && error.Contains("min <= p50 <= p95 <= max"));
    }

    [Test]
    public void It_rejects_a_malformed_sql_hash()
    {
        _errors
            .Should()
            .Contain(error => error.StartsWith("results[4]") && error.Contains("64 lowercase hex"));
    }

    [Test]
    public void It_rejects_a_blank_plan_file()
    {
        _errors.Should().Contain(error => error.StartsWith("results[5]") && error.Contains("plan file"));
    }
}

[TestFixture]
public class Given_Result_Rows_With_Commit_Problems
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ResultSamples.PostgresqlDocument();
        document = ValidatorTestSupport.WithRow(
            document,
            0,
            row => row with { RunnerCommit = new string('d', 40) }
        );
        document = ValidatorTestSupport.WithRow(document, 1, row => row with { SubjectCommit = "BAD" });
        _errors = PerfArtifactValidator.Validate(ResultSamples.Manifest(), document);
    }

    [Test]
    public void It_rejects_a_runner_commit_that_differs_from_the_manifest()
    {
        _errors
            .Should()
            .Contain(error => error.StartsWith("results[0]") && error.Contains("runner commit must match"));
    }

    [Test]
    public void It_rejects_a_malformed_subject_commit()
    {
        _errors
            .Should()
            .Contain(error => error.StartsWith("results[1]") && error.Contains("40 lowercase hex"));
    }
}

[TestFixture]
public class Given_Metrics_On_The_Wrong_Provider_Side
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ValidatorTestSupport.WithRow(
            ResultSamples.PostgresqlDocument(),
            0,
            row => row with { Database = ResultSamples.Mssql().Database }
        );
        _errors = PerfArtifactValidator.Validate(ResultSamples.Manifest(), document);
    }

    [Test]
    public void It_requires_the_postgresql_metrics()
    {
        _errors
            .Should()
            .Contain(error =>
                error.StartsWith("results[0]") && error.Contains("postgresql database metrics")
            );
    }

    [Test]
    public void It_rejects_the_sql_server_metrics()
    {
        _errors
            .Should()
            .Contain(error =>
                error.StartsWith("results[0]") && error.Contains("sql server metrics must be absent")
            );
    }
}

[TestFixture]
public class Given_A_Missing_Result_Cell
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ResultSamples.PostgresqlDocument();
        _errors = PerfArtifactValidator.Validate(
            ResultSamples.Manifest(),
            document with
            {
                Results = [.. document.Results.Take(5)],
            }
        );
    }

    [Test]
    public void It_requires_exactly_six_cells()
    {
        _errors.Should().Contain(error => error.Contains("exactly 6 cells; got 5"));
    }

    [Test]
    public void It_names_the_missing_cell()
    {
        _errors.Should().Contain(error => error.StartsWith("results: missing cell"));
    }
}

[TestFixture]
public class Given_A_Duplicated_Result_Cell
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ResultSamples.PostgresqlDocument();
        _errors = PerfArtifactValidator.Validate(
            ResultSamples.Manifest(),
            document with
            {
                Results = [.. document.Results.Take(5), document.Results[0]],
            }
        );
    }

    [Test]
    public void It_names_the_duplicate_cell()
    {
        _errors.Should().Contain(error => error.StartsWith("results: duplicate cell"));
    }
}

[TestFixture]
public class Given_A_Result_Row_From_Another_Provider
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ValidatorTestSupport.WithRow(
            ResultSamples.PostgresqlDocument(),
            0,
            row => row with { Provider = "mssql" }
        );
        _errors = PerfArtifactValidator.Validate(ResultSamples.Manifest(), document);
    }

    [Test]
    public void It_rejects_the_provider_mismatch()
    {
        _errors
            .Should()
            .Contain(error =>
                error.StartsWith("results[0]") && error.Contains("must match the run provider")
            );
    }
}

[TestFixture]
public class Given_A_Result_Row_With_A_Wrong_Offset
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ValidatorTestSupport.WithRow(
            ResultSamples.PostgresqlDocument(),
            0,
            row => row with { Offset = 5 }
        );
        _errors = PerfArtifactValidator.Validate(ResultSamples.Manifest(), document);
    }

    [Test]
    public void It_rejects_the_offset()
    {
        _errors.Should().Contain(error => error.StartsWith("results[0]") && error.Contains("offset 5"));
    }
}

[TestFixture]
public class Given_A_Result_Row_With_Mismatched_Iterations
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ValidatorTestSupport.WithRow(
            ResultSamples.PostgresqlDocument(),
            0,
            row => row with { WarmupIterations = 6 }
        );
        _errors = PerfArtifactValidator.Validate(ResultSamples.Manifest(), document);
    }

    [Test]
    public void It_rejects_the_warmup_mismatch()
    {
        _errors
            .Should()
            .Contain(error => error.StartsWith("results[0]") && error.Contains("warmup iterations 6"));
    }
}

[TestFixture]
public class Given_A_Manifest_With_A_Missing_Cell
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfRunManifest manifest = ResultSamples.Manifest();
        manifest = manifest with
        {
            Iterations = manifest.Iterations with
            {
                CellExecutionOrder = [.. manifest.Iterations.CellExecutionOrder.Take(5)],
            },
        };
        _errors = PerfArtifactValidator.Validate(manifest, ResultSamples.PostgresqlDocument());
    }

    [Test]
    public void It_requires_exactly_six_cells()
    {
        _errors
            .Should()
            .Contain(error => error.StartsWith("manifest: cell execution order") && error.Contains("got 5"));
    }
}

[TestFixture]
public class Given_A_Manifest_With_A_Wrong_Cell_Offset
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfRunManifest manifest = ResultSamples.Manifest();
        manifest = manifest with
        {
            Iterations = manifest.Iterations with
            {
                CellExecutionOrder =
                [
                    .. manifest.Iterations.CellExecutionOrder.Select(
                        (cell, index) => index == 2 ? cell with { Offset = 7 } : cell
                    ),
                ],
            },
        };
        _errors = PerfArtifactValidator.Validate(manifest, ResultSamples.PostgresqlDocument());
    }

    [Test]
    public void It_rejects_the_cell_offset()
    {
        _errors
            .Should()
            .ContainSingle(error => error.StartsWith("manifest: cell") && error.Contains("offset 7"));
    }
}

[TestFixture]
public class Given_A_Manifest_With_A_Broken_Environment
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfRunManifest manifest = ResultSamples.Manifest();
        manifest = manifest with
        {
            Environment = manifest.Environment with
            {
                Server = manifest.Environment.Server with
                {
                    ImageDigest = "sha256:xyz",
                    ConnectionStringShape = "host=localhost;password=hunter2;database=perf",
                },
                Host = manifest.Environment.Host with { CpuModel = " ", TotalMemoryBytes = 0 },
            },
        };
        _errors = PerfArtifactValidator.Validate(manifest, ResultSamples.PostgresqlDocument());
    }

    [Test]
    public void It_rejects_a_malformed_image_digest()
    {
        _errors.Should().Contain(error => error.Contains("image digest"));
    }

    [Test]
    public void It_rejects_an_unredacted_password()
    {
        _errors.Should().Contain(error => error.Contains("redact secrets"));
    }

    [Test]
    public void It_requires_the_cpu_model()
    {
        _errors.Should().Contain(error => error.Contains("cpu model"));
    }

    [Test]
    public void It_requires_positive_total_memory()
    {
        _errors.Should().Contain(error => error.Contains("total memory"));
    }
}

[TestFixture]
public class Given_A_Manifest_With_A_Broken_Identity
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfRunManifest manifest = ResultSamples.Manifest();
        manifest = manifest with
        {
            Run = manifest.Run with { RunId = " ", CapturedAtUtc = "yesterday" },
            Commits = manifest.Commits with { WorktreeDirtyPaths = [" "] },
            Fixture = manifest.Fixture with { RowCount = 400_000 },
            Iterations = manifest.Iterations with { WarmupIterations = 4 },
        };
        _errors = PerfArtifactValidator.Validate(manifest, ResultSamples.PostgresqlDocument());
    }

    [Test]
    public void It_requires_a_run_id()
    {
        _errors.Should().Contain("manifest: run id is required.");
    }

    [Test]
    public void It_rejects_a_non_iso_timestamp()
    {
        _errors.Should().Contain(error => error.Contains("ISO-8601"));
    }

    [Test]
    public void It_rejects_blank_dirty_path_entries()
    {
        _errors.Should().Contain(error => error.Contains("dirty path entries"));
    }

    [Test]
    public void It_rejects_a_fixture_row_count_mismatch()
    {
        _errors.Should().Contain(error => error.Contains("fixture row count 400000"));
    }

    [Test]
    public void It_rejects_lowered_warmup_iterations()
    {
        _errors.Should().Contain(error => error.Contains("warmup iterations must be at least 5"));
    }
}

[TestFixture]
public class Given_A_Manifest_With_An_Unknown_Fixture
{
    private IReadOnlyList<string> _errors = null!;

    [SetUp]
    public void Setup()
    {
        PerfRunManifest manifest = ResultSamples.Manifest();
        manifest = manifest with { Fixture = manifest.Fixture with { FixtureId = "primary-1m" } };
        _errors = PerfArtifactValidator.Validate(manifest, ResultSamples.PostgresqlDocument());
    }

    [Test]
    public void It_names_the_known_fixtures()
    {
        _errors
            .Should()
            .Contain(error => error.Contains("fixture id must be one of") && error.Contains("primary-1m"));
    }
}

[TestFixture]
public class Given_Ensure_Valid_On_Invalid_Artifacts
{
    private PerfArtifactValidationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        PerfResultsDocument document = ResultSamples.PostgresqlDocument();
        _exception = Assert.Throws<PerfArtifactValidationException>(() =>
            PerfArtifactValidator.EnsureValid(
                ResultSamples.Manifest(),
                document with
                {
                    Results = [.. document.Results.Take(3)],
                }
            )
        );
    }

    [Test]
    public void It_carries_the_errors()
    {
        _exception.Errors.Should().NotBeEmpty();
    }
}
