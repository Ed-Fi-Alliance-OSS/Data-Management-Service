// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_A_Valid_Final_Gate_Artifact_Set
{
    [Test]
    public void It_accepts_the_primary_run()
    {
        PerfFinalGateArtifactValidator
            .Validate(FinalGateResultSamples.PrimaryManifest(), FinalGateResultSamples.PrimaryDocument())
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_accepts_the_descriptor_run()
    {
        PerfFinalGateArtifactValidator
            .Validate(
                FinalGateResultSamples.DescriptorManifest(),
                FinalGateResultSamples.DescriptorDocument()
            )
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_accepts_the_sql_server_side()
    {
        PerfFinalGateArtifactValidator
            .Validate(
                FinalGateResultSamples.PrimaryManifest("mssql"),
                FinalGateResultSamples.PrimaryDocument("mssql")
            )
            .Should()
            .BeEmpty();
    }
}

[TestFixture]
public class Given_A_Damaged_Final_Gate_Artifact_Set
{
    [Test]
    public void It_rejects_a_wrong_schema_version()
    {
        PerfFinalGateRunManifest manifest = FinalGateResultSamples.PrimaryManifest() with
        {
            SchemaVersion = "1.3.0",
        };

        PerfFinalGateArtifactValidator
            .Validate(manifest, FinalGateResultSamples.PrimaryDocument())
            .Should()
            .Contain(error => error.Contains("schema version"));
    }

    [Test]
    public void It_rejects_an_unknown_run_kind()
    {
        PerfFinalGateRunManifest manifest = FinalGateResultSamples.PrimaryManifest() with
        {
            RunKind = "final-everything",
        };

        PerfFinalGateArtifactValidator
            .Validate(manifest, FinalGateResultSamples.PrimaryDocument())
            .Should()
            .Contain(error => error.Contains("run kind"));
    }

    [Test]
    public void It_rejects_rows_out_of_catalog_order()
    {
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();
        List<PerfFinalGateScenarioResult> reordered = [.. document.Results];
        // Swap two cells with different scenario ids so the position rule is what fires.
        (reordered[0], reordered[2]) = (reordered[2], reordered[0]);

        PerfFinalGateArtifactValidator
            .Validate(FinalGateResultSamples.PrimaryManifest(), document with { Results = reordered })
            .Should()
            .Contain(error => error.Contains("must be") && error.Contains("at this position"));
    }

    [Test]
    public void It_rejects_a_missing_row()
    {
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();

        PerfFinalGateArtifactValidator
            .Validate(
                FinalGateResultSamples.PrimaryManifest(),
                document with
                {
                    Results = [.. document.Results.Skip(1)],
                }
            )
            .Should()
            .Contain(error => error.Contains("exactly"));
    }

    [Test]
    public void It_rejects_a_wrong_replay_parameter_source()
    {
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();
        List<PerfFinalGateScenarioResult> rows = [.. document.Results];
        int partitionIndex = rows.FindIndex(row => row.Family == "partition");
        rows[partitionIndex] = rows[partitionIndex] with
        {
            ReplayParameterSource = PerfFinalGateReplaySources.HydrationKeyset,
        };

        PerfFinalGateArtifactValidator
            .Validate(FinalGateResultSamples.PrimaryManifest(), document with { Results = rows })
            .Should()
            .Contain(error => error.Contains("replay parameter source"));
    }

    [Test]
    public void It_rejects_a_token_count_above_the_requested_number()
    {
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();
        List<PerfFinalGateScenarioResult> rows = [.. document.Results];
        int partitionIndex = rows.FindIndex(row =>
            row.Family == "partition" && row.RequestedPartitionNumber == 1
        );
        rows[partitionIndex] = rows[partitionIndex] with { ReturnedTokenCount = 2 };

        PerfFinalGateArtifactValidator
            .Validate(FinalGateResultSamples.PrimaryManifest(), document with { Results = rows })
            .Should()
            .Contain(error => error.Contains("returned token count"));
    }

    [Test]
    public void It_rejects_a_cursor_row_carrying_an_offset()
    {
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();
        List<PerfFinalGateScenarioResult> rows = [.. document.Results];
        int cursorIndex = rows.FindIndex(row => row.Family == "cursor");
        rows[cursorIndex] = rows[cursorIndex] with { Offset = 25 };

        PerfFinalGateArtifactValidator
            .Validate(FinalGateResultSamples.PrimaryManifest(), document with { Results = rows })
            .Should()
            .Contain(error => error.Contains("carries no offset"));
    }

    [Test]
    public void It_rejects_a_wrong_traditional_offset()
    {
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();
        List<PerfFinalGateScenarioResult> rows = [.. document.Results];
        rows[0] = rows[0] with { Offset = 7 };

        PerfFinalGateArtifactValidator
            .Validate(FinalGateResultSamples.PrimaryManifest(), document with { Results = rows })
            .Should()
            .Contain(error => error.Contains("offset 7"));
    }

    [Test]
    public void It_rejects_a_primary_manifest_without_the_two_mutation_entries()
    {
        PerfFinalGateRunManifest manifest = FinalGateResultSamples.PrimaryManifest() with { PhaseLog = [] };

        PerfFinalGateArtifactValidator
            .Validate(manifest, FinalGateResultSamples.PrimaryDocument())
            .Should()
            .Contain(error => error.Contains("phase log"));
    }

    [Test]
    public void It_rejects_a_descriptor_manifest_with_a_deep_offset_or_phase_log()
    {
        PerfFinalGateRunManifest manifest = FinalGateResultSamples.DescriptorManifest();

        PerfFinalGateArtifactValidator
            .Validate(
                manifest with
                {
                    Fixture = manifest.Fixture with { DeepOffset = 1_000 },
                },
                FinalGateResultSamples.DescriptorDocument()
            )
            .Should()
            .Contain(error => error.Contains("no deep offset"));

        PerfFinalGateArtifactValidator
            .Validate(
                manifest with
                {
                    PhaseLog = FinalGateResultSamples.PrimaryManifest().PhaseLog,
                },
                FinalGateResultSamples.DescriptorDocument()
            )
            .Should()
            .Contain(error => error.Contains("phase log must be empty"));
    }

    [Test]
    public void It_rejects_a_manifest_cell_order_that_departs_from_the_catalog()
    {
        PerfFinalGateRunManifest manifest = FinalGateResultSamples.PrimaryManifest();
        List<PerfFinalGateExecutedCell> cells = [.. manifest.Iterations.CellExecutionOrder];
        (cells[0], cells[1]) = (cells[1], cells[0]);

        PerfFinalGateArtifactValidator
            .Validate(
                manifest with
                {
                    Iterations = manifest.Iterations with { CellExecutionOrder = cells },
                },
                FinalGateResultSamples.PrimaryDocument()
            )
            .Should()
            .Contain(error => error.Contains("cell execution order"));
    }

    [Test]
    public void It_rejects_a_plan_file_named_for_another_cell()
    {
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();
        List<PerfFinalGateScenarioResult> rows = [.. document.Results];
        rows[0] = rows[0] with { PlanFile = "plans/postgresql.other-cell.25.explain.json" };

        PerfFinalGateArtifactValidator
            .Validate(FinalGateResultSamples.PrimaryManifest(), document with { Results = rows })
            .Should()
            .Contain(error => error.Contains("plan file"));
    }

    [Test]
    public void It_rejects_mismatched_provider_metrics()
    {
        PerfFinalGateResultsDocument document = FinalGateResultSamples.PrimaryDocument();
        List<PerfFinalGateScenarioResult> rows = [.. document.Results];
        rows[0] = rows[0] with { Database = rows[0].Database with { LogicalReads = 10 } };

        PerfFinalGateArtifactValidator
            .Validate(FinalGateResultSamples.PrimaryManifest(), document with { Results = rows })
            .Should()
            .Contain(error => error.Contains("sql server metrics must be absent"));
    }
}
