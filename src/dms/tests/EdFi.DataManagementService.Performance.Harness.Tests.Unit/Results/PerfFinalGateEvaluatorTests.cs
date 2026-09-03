// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_Complete_Comparable_Evidence
{
    private PerfFinalGateEvaluation _evaluation = null!;

    [SetUp]
    public void Setup()
    {
        _evaluation = PerfFinalGateEvaluator.Evaluate([
            FinalGateEvaluationSamples.Evidence("postgresql"),
            FinalGateEvaluationSamples.Evidence("mssql"),
        ]);
    }

    [Test]
    public void It_passes_overall()
    {
        _evaluation.OverallStatus.Should().Be(PerfGateStatus.Pass);
    }

    [Test]
    public void It_evaluates_every_gate_for_both_providers()
    {
        foreach (string provider in (string[])["postgresql", "mssql"])
        {
            _evaluation
                .Gates.Where(gate => gate.Provider == provider)
                .Select(gate => gate.GateId)
                .Should()
                .Contain([
                    "evidence-consistency",
                    "environment-comparability",
                    "traditional-sql-textual",
                    "traditional-shallow-regression",
                    "cursor-first-entry",
                    "cursor-depth-insensitivity-unfiltered",
                    "cursor-depth-insensitivity-authorized",
                    "cursor-depth-insensitivity-filtered",
                    "cursor-depth-insensitivity-descriptor",
                    "partition-count-insensitivity",
                    "single-command-structure",
                    "deep-offset-observation",
                ]);
        }

        _evaluation.Gates.Should().OnlyContain(gate => gate.Status == PerfGateStatus.Pass);
    }

    [Test]
    public void It_describes_all_six_runs()
    {
        _evaluation.Runs.Should().HaveCount(6);
        _evaluation
            .Runs.Select(run => run.Kind)
            .Distinct()
            .Should()
            .BeEquivalentTo("traditional-baseline", "final-primary", "final-descriptors");
    }
}

[TestFixture]
public class Given_Defective_Or_Incomparable_Evidence
{
    private static PerfGateOutcome GateOf(PerfFinalGateEvaluation evaluation, string gateId) =>
        evaluation.Gates.Single(gate => gate.GateId == gateId && gate.Provider == "postgresql");

    [Test]
    public void It_is_inconclusive_when_a_provider_is_missing()
    {
        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate([
            FinalGateEvaluationSamples.Evidence("postgresql"),
        ]);

        evaluation.OverallStatus.Should().Be(PerfGateStatus.Inconclusive);
        evaluation
            .Gates.Single(gate => gate.GateId == "provider-coverage")
            .Status.Should()
            .Be(PerfGateStatus.Inconclusive);
    }

    [Test]
    public void It_fails_the_depth_gate_when_a_middle_range_is_slow()
    {
        PerfFinalGateProviderEvidence evidence = FinalGateEvaluationSamples.Evidence();
        evidence = evidence with
        {
            Primary = evidence.Primary with
            {
                Results = FinalGateEvaluationSamples.WithRowLatency(
                    evidence.Primary.Results,
                    row => row.ScenarioId == "cursor-unfiltered-middle" && row.PageSize == 25,
                    milliseconds: 60.0
                ),
            },
        };

        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate([evidence]);

        evaluation.OverallStatus.Should().Be(PerfGateStatus.Fail);
        GateOf(evaluation, "cursor-depth-insensitivity-unfiltered").Status.Should().Be(PerfGateStatus.Fail);
        GateOf(evaluation, "cursor-depth-insensitivity-authorized").Status.Should().Be(PerfGateStatus.Pass);
    }

    [Test]
    public void It_fails_the_partition_gate_on_a_count_sensitive_boundary()
    {
        PerfFinalGateProviderEvidence evidence = FinalGateEvaluationSamples.Evidence();
        evidence = evidence with
        {
            Primary = evidence.Primary with
            {
                Results = FinalGateEvaluationSamples.WithRowLatency(
                    evidence.Primary.Results,
                    row => row.ScenarioId == "partition-unfiltered-200",
                    milliseconds: 60.0
                ),
            },
        };

        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate([evidence]);

        GateOf(evaluation, "partition-count-insensitivity").Status.Should().Be(PerfGateStatus.Fail);
    }

    [Test]
    public void It_fails_the_traditional_regression_gate_on_a_slow_shallow_page()
    {
        PerfFinalGateProviderEvidence evidence = FinalGateEvaluationSamples.Evidence();
        evidence = evidence with
        {
            Primary = evidence.Primary with
            {
                Results = FinalGateEvaluationSamples.WithRowLatency(
                    evidence.Primary.Results,
                    row => row.ScenarioId == "traditional-offset-shallow" && row.PageSize == 25,
                    milliseconds: 60.0
                ),
            },
        };

        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate([evidence]);

        GateOf(evaluation, "traditional-shallow-regression").Status.Should().Be(PerfGateStatus.Fail);
    }

    [Test]
    public void It_fails_the_textual_gate_when_the_traditional_sql_hash_moved()
    {
        PerfFinalGateProviderEvidence evidence = FinalGateEvaluationSamples.Evidence();
        List<PerfFinalGateScenarioResult> rows = [.. evidence.Primary.Results.Results];
        int index = rows.FindIndex(row => row.Family == "traditional");
        rows[index] = rows[index] with { SelectionSqlSha256 = new string('b', 64) };
        evidence = evidence with
        {
            Primary = evidence.Primary with { Results = evidence.Primary.Results with { Results = rows } },
        };

        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate([evidence]);

        GateOf(evaluation, "traditional-sql-textual").Status.Should().Be(PerfGateStatus.Fail);
    }

    [Test]
    public void It_makes_cross_run_gates_provisional_on_a_different_machine()
    {
        PerfFinalGateProviderEvidence evidence = FinalGateEvaluationSamples.Evidence();
        PerfFinalGateRunManifest manifest = evidence.Primary.Manifest;
        evidence = evidence with
        {
            Primary = evidence.Primary with
            {
                Manifest = manifest with
                {
                    Environment = manifest.Environment with
                    {
                        Host = manifest.Environment.Host with { MachineFingerprint = "0123456789abcdef" },
                    },
                },
            },
        };

        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate([evidence]);

        evaluation.OverallStatus.Should().Be(PerfGateStatus.Inconclusive);
        GateOf(evaluation, "environment-comparability").Status.Should().Be(PerfGateStatus.Inconclusive);
        GateOf(evaluation, "traditional-shallow-regression").Status.Should().Be(PerfGateStatus.Inconclusive);
        GateOf(evaluation, "cursor-first-entry").Status.Should().Be(PerfGateStatus.Inconclusive);
        GateOf(evaluation, "cursor-depth-insensitivity-unfiltered").Status.Should().Be(PerfGateStatus.Pass);
        GateOf(evaluation, "traditional-sql-textual").Status.Should().Be(PerfGateStatus.Pass);
    }

    [Test]
    public void It_fails_evidence_consistency_on_a_fixture_mismatch()
    {
        PerfFinalGateProviderEvidence evidence = FinalGateEvaluationSamples.Evidence();
        evidence = evidence with
        {
            Baseline = evidence.Baseline with
            {
                Manifest = evidence.Baseline.Manifest with
                {
                    Fixture = new PerfManifestFixture("primary-500k", 500_000, 450_000),
                },
            },
        };

        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate([evidence]);

        evaluation.OverallStatus.Should().Be(PerfGateStatus.Fail);
        GateOf(evaluation, "evidence-consistency").Status.Should().Be(PerfGateStatus.Fail);
        GateOf(evaluation, "traditional-shallow-regression").Status.Should().Be(PerfGateStatus.Inconclusive);
    }

    [Test]
    public void It_keeps_the_deep_offset_as_an_observation_even_when_slow()
    {
        PerfFinalGateProviderEvidence evidence = FinalGateEvaluationSamples.Evidence();
        evidence = evidence with
        {
            Primary = evidence.Primary with
            {
                Results = FinalGateEvaluationSamples.WithRowLatency(
                    evidence.Primary.Results,
                    row => row.ScenarioId == "traditional-offset-deep",
                    milliseconds: 500.0
                ),
            },
        };

        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate([evidence]);

        GateOf(evaluation, "deep-offset-observation").Status.Should().Be(PerfGateStatus.Pass);
    }
}
