// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal static class ComparisonAssertions
{
    public static void ShouldBeInvalidBecause(this PerfBenchmarkComparisonResult result, string fragment)
    {
        result.Result.Should().Be(PerfBenchmarkComparisonResult.Invalid);
        result.ExitCode.Should().Be(2);
        result.QualificationEligible.Should().BeFalse();
        result.InvalidReasons.Should().Contain(reason => reason.Contains(fragment));
    }

    public static void ShouldBeValid(this PerfBenchmarkComparisonResult result)
    {
        result.InvalidReasons.Should().BeEmpty();
        result.ExitCode.Should().Be(0);
    }

    public static PerfBenchmarkScenarioComparison Scenario(
        this PerfBenchmarkComparisonResult result,
        string scenario
    ) => result.Scenarios.Single(entry => entry.ScenarioId == scenario);

    public static PerfBenchmarkSizing Sizing(this PerfBenchmarkComparisonResult result, string scenario) =>
        result.Sizing!.Single(entry => entry.ScenarioId == scenario);
}

// ---------------------------------------------------------------- verdicts

[TestFixture]
public class Given_Same_Build_Pairs_Within_Threshold
{
    private PerfBenchmarkComparisonResult _result = null!;

    [SetUp]
    public void Setup()
    {
        ComparisonBuilder builder = new() { Purpose = PerfBenchmarkComparison.Calibration };
        builder.CandidateRatios[ComparisonBuilder.ZeroSmall] = [1.02m, 0.98m, 1.01m, 0.99m, 1.03m];
        _result = builder.Compare();
    }

    [Test]
    public void It_is_acceptable_with_exit_code_0()
    {
        _result.ShouldBeValid();
        _result.Result.Should().Be(PerfBenchmarkComparisonResult.Acceptable);
        _result.Scenarios.Should().HaveCount(7).And.OnlyContain(scenario => !scenario.Flagged);
    }

    [Test]
    public void It_emits_a_calibration_block_with_thresholds_derived_from_the_spread()
    {
        _result.Calibration.Should().NotBeNull();
        _result.Calibration!.Spreads[ComparisonBuilder.ZeroSmall].Should().Be(0.03m);
        _result.Calibration.DerivedThresholds[ComparisonBuilder.ZeroSmall].Should().Be(0.06m);
        _result.Calibration.DerivedThresholds[ComparisonBuilder.Write].Should().Be(0.05m);
    }

    [Test]
    public void It_rounds_a_derived_threshold_up_to_a_whole_percent()
    {
        ComparisonBuilder builder = new() { Purpose = PerfBenchmarkComparison.Calibration };
        builder.CandidateRatios[ComparisonBuilder.Write] = [1.031m, 1m, 1m, 1m, 1m];

        builder.Compare().Calibration!.DerivedThresholds[ComparisonBuilder.Write].Should().Be(0.07m);
    }

    [Test]
    public void It_records_the_observed_fingerprint_and_labels_the_profile_uncalibrated()
    {
        _result.Calibration!.Fingerprint.CpuModel.Should().Be("Example CPU");
        _result.Labels.Should().Contain(PerfBenchmarkComparisonResult.UncalibratedLabel);
    }
}

[TestFixture]
public class Given_A_Candidate_Slower_In_One_Scenario
{
    [Test]
    public void It_flags_only_that_scenario_by_the_median_of_pair_ratios()
    {
        ComparisonBuilder builder = new();
        builder.CandidateRatios[ComparisonBuilder.DeepLarge] = [1.30m, 1.02m, 1.31m, 1.29m, 1.01m];

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.ShouldBeValid();
        result.Result.Should().Be(PerfBenchmarkComparisonResult.SuspectedRegression);
        result
            .Scenarios.Where(scenario => scenario.Flagged)
            .Select(scenario => scenario.ScenarioId)
            .Should()
            .Equal(ComparisonBuilder.DeepLarge);
        result.Scenario(ComparisonBuilder.DeepLarge).MedianP50Ratio.Should().Be(1.29m);
        result.Calibration.Should().BeNull();
    }

    [Test]
    public void It_does_not_flag_two_slow_pairs_out_of_five()
    {
        ComparisonBuilder builder = new();
        builder.CandidateRatios[ComparisonBuilder.DeepLarge] = [1.30m, 1.01m, 1.02m, 1.31m, 1.00m];

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.Result.Should().Be(PerfBenchmarkComparisonResult.Acceptable);
        result.Scenario(ComparisonBuilder.DeepLarge).MedianP50Ratio.Should().Be(1.02m);
        result.Scenario(ComparisonBuilder.DeepLarge).Spread.Should().Be(0.31m);
    }
}

[TestFixture]
public class Given_A_Median_Ratio_At_The_Threshold_Boundary
{
    [TestCase("1.10", false)]
    [TestCase("1.1001", true)]
    public void It_flags_only_a_ratio_strictly_above_one_plus_the_threshold(string ratio, bool flagged)
    {
        ComparisonBuilder builder = new();
        decimal value = decimal.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture);
        builder.CandidateRatios[ComparisonBuilder.Write] = [value, value, value, value, value];

        builder.Compare().Scenario(ComparisonBuilder.Write).Flagged.Should().Be(flagged);
    }
}

[TestFixture]
public class Given_P95_Warnings_Disabled
{
    // The profile's 60 measured reads: 56 at 10 ms and 4 at 20 ms, so p50 is 10 ms and the
    // nearest-rank p95 (rank 57) is 20 ms.
    internal static double[] TailHeavy => [.. Enumerable.Repeat(10.0, 56), .. Enumerable.Repeat(20.0, 4)];

    [Test]
    public void It_reports_the_p95_ratio_without_a_warning()
    {
        ComparisonBuilder builder = new();
        builder.CandidateSamples[ComparisonBuilder.ZeroSmall] = TailHeavy;

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.Scenario(ComparisonBuilder.ZeroSmall).MedianP95Ratio.Should().Be(2m);
        result.Scenario(ComparisonBuilder.ZeroSmall).P95Flagged.Should().BeFalse();
        result.Result.Should().Be(PerfBenchmarkComparisonResult.Acceptable);
    }
}

[TestFixture]
public class Given_P95_Warning_Enabled
{
    [Test]
    public void It_flags_the_p95_regression()
    {
        ComparisonBuilder builder = new()
        {
            Profile = ComparisonProfiles.Edited(profile =>
                profile["comparison"]!["p95WarningsEnabled"] = true
            ),
        };
        builder.CandidateSamples[ComparisonBuilder.ZeroSmall] = Given_P95_Warnings_Disabled.TailHeavy;

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.Scenario(ComparisonBuilder.ZeroSmall).P95Flagged.Should().BeTrue();
        result.Scenario(ComparisonBuilder.ZeroSmall).Flagged.Should().BeFalse();
        result.Scenario(ComparisonBuilder.ZeroSmall).Reason.Should().Contain("p95");
        result.Result.Should().Be(PerfBenchmarkComparisonResult.SuspectedRegression);
    }
}

[TestFixture]
public class Given_Raw_Samples
{
    [Test]
    public void It_reports_nearest_rank_p99_descriptively()
    {
        ComparisonBuilder builder = new();
        builder.CandidateSamples[ComparisonBuilder.ZeroSmall] = [.. Enumerable.Repeat(10.0, 59), 50.0];

        PerfBenchmarkScenarioComparison scenario = builder.Compare().Scenario(ComparisonBuilder.ZeroSmall);

        scenario
            .Pairs.Should()
            .AllSatisfy(pair =>
            {
                pair.CandidateP99Ms.Should().Be(50);
                pair.CandidateP95Ms.Should().Be(10);
                pair.SampleCount.Should().Be(60);
            });
        scenario.Flagged.Should().BeFalse();
    }

    [Test]
    public void It_reports_command_counts_and_read_driver_diagnostics()
    {
        PerfBenchmarkComparisonResult result = new ComparisonBuilder().Compare();

        result.Scenario(ComparisonBuilder.Write).Pairs[0].CandidateCommandCounts.Should().Be("2x200");
        result.Scenario(ComparisonBuilder.Write).Pairs[0].CandidateDriverExecuteP50Ms.Should().BeNull();
        result.Scenario(ComparisonBuilder.ZeroSmall).Pairs[0].CandidateCommandCounts.Should().Be("1");
        result.Scenario(ComparisonBuilder.ZeroSmall).Pairs[0].CandidateDriverExecuteP50Ms.Should().Be(1);
    }
}

// ---------------------------------------------------------------- conformance

[TestFixture]
public class Given_Sides_That_Ran_30_Reads_Under_A_60_Read_Profile
{
    [Test]
    public void It_is_invalid_even_though_both_sides_agree()
    {
        new ComparisonBuilder { SidesRunSmoke = true }
            .Compare()
            .ShouldBeInvalidBecause("workload conformance");
    }
}

[TestFixture]
public class Given_A_Wrong_Deep_Offset_Or_Fixture
{
    [Test]
    public void It_rejects_a_side_reporting_another_deep_offset()
    {
        new ComparisonBuilder()
            .EditSide(
                2,
                "candidate",
                side =>
                    side with
                    {
                        Side = side.Side with { Workload = side.Side.Workload with { DeepOffset = 400_000 } },
                    }
            )
            .Compare()
            .ShouldBeInvalidBecause("pair 2 candidate: workload conformance");
    }

    [Test]
    public void It_rejects_a_paging_run_on_another_fixture()
    {
        new ComparisonBuilder()
            .EditSide(
                3,
                "baseline",
                side => side with { Reads = side.Reads! with { FixtureId = "smoke-10k" } }
            )
            .Compare()
            .ShouldBeInvalidBecause("pair 3 baseline: workload conformance");
    }
}

[TestFixture]
public class Given_A_Missing_Or_Extra_Pair_Role_Position
{
    [Test]
    public void It_rejects_a_missing_side()
    {
        ComparisonBuilder builder = new();
        builder.OmittedSides.Add((3, "candidate"));

        builder.Compare().ShouldBeInvalidBecause("pair 3 candidate: the planned side is missing");
    }

    [Test]
    public void It_rejects_a_side_outside_the_plan()
    {
        ComparisonBuilder builder = new();
        builder.PlanEdits.Add(plan => plan with { Order = [.. plan.Order.Where(side => side.Pair != 5)] });

        builder.Compare().ShouldBeInvalidBecause("pair 5 baseline: the side is not in the run plan");
    }

    [Test]
    public void It_rejects_a_side_at_the_wrong_position()
    {
        new ComparisonBuilder()
            .EditSide(1, "candidate", side => side with { Side = side.Side with { Position = 1 } })
            .Compare()
            .ShouldBeInvalidBecause("pair 1 candidate: ran at position 1");
    }
}

[TestFixture]
public class Given_Non_Alternating_Order
{
    [Test]
    public void It_is_invalid()
    {
        ComparisonBuilder builder = new();
        builder.PlanEdits.Add(plan =>
            plan with
            {
                Order =
                [
                    .. plan.Order.Select(side => side with { Position = side.Role == "baseline" ? 1 : 2 }),
                ],
            }
        );

        builder.Compare().ShouldBeInvalidBecause("alternating");
    }
}

[TestFixture]
public class Given_A_Baseline_Other_Than_The_Approved_Reference
{
    [Test]
    public void It_is_invalid()
    {
        new ComparisonBuilder { BaselineCommit = new string('b', 40) }
            .Compare()
            .ShouldBeInvalidBecause("is not the approved reference");
    }
}

[TestFixture]
public class Given_A_Subject_Or_Runner_Commit_Mismatch
{
    [Test]
    public void It_rejects_a_subject_commit_other_than_the_declared_one()
    {
        new ComparisonBuilder()
            .EditSide(
                2,
                "candidate",
                side => side with { Reads = side.Reads! with { SubjectCommit = new string('e', 40) } }
            )
            .Compare()
            .ShouldBeInvalidBecause("pair 2 candidate: subject commit");
    }

    [Test]
    public void It_rejects_a_runner_commit_other_than_the_harness_commit()
    {
        new ComparisonBuilder()
            .EditSides(side => side with { Reads = side.Reads! with { RunnerCommit = new string('e', 40) } })
            .Compare()
            .ShouldBeInvalidBecause("is not the harness commit");
    }
}

[TestFixture]
public class Given_The_Smoke_Workload
{
    [Test]
    public void It_is_valid_but_never_evidence()
    {
        PerfBenchmarkComparisonResult result = new ComparisonBuilder { Smoke = true }.Compare();

        result.ShouldBeValid();
        result.Evidence.Should().BeFalse();
        result.Labels.Should().Contain(PerfBenchmarkComparisonResult.SmokeLabel);
        result.Scenario(ComparisonBuilder.Write).Pairs[0].SampleCount.Should().Be(20);
    }

    [Test]
    public void It_is_evidence_on_the_full_workload()
    {
        new ComparisonBuilder().Compare().Evidence.Should().BeTrue();
    }
}

// ---------------------------------------------------------------- required values

[TestFixture]
public class Given_Required_Values_Both_Sides_Agree_On
{
    private static IEnumerable<TestCaseData> Cases()
    {
        yield return Case(
            "Given_Both_Sides_Logging_At_Information",
            side => side with { Side = side.Side with { EffectiveLogLevel = "Information" } },
            "log level Information"
        );
        yield return Case(
            "Given_Both_Sides_Without_Published_Ownership",
            side => side with { Side = side.Side with { OwnershipPublished = false } },
            "ownership was not published"
        );
        yield return Case(
            "Given_Both_Sides_With_Pooling_Disabled_And_Ownership_Published",
            side =>
                side with
                {
                    Side = side.Side with
                    {
                        EffectiveConnection = side.Side.EffectiveConnection with { Pooling = false },
                    },
                },
            "effective connection settings"
        );
        yield return Case(
            "Given_Both_Sides_With_A_Changed_Max_Pool_Size",
            side =>
                side with
                {
                    Side = side.Side with
                    {
                        EffectiveConnection = side.Side.EffectiveConnection with { MaxPoolSize = 50 },
                    },
                },
            "effective connection settings"
        );
        yield return Case(
            "Given_Both_Sides_On_Battery",
            side => side with { Environment = side.Environment with { AcPowerOnline = false } },
            "AC power"
        );
        yield return Case(
            "Given_Both_Sides_On_A_Bind_Mount",
            side => side with { Environment = side.Environment with { StorageMountType = "bind" } },
            "bind mount"
        );
        yield return Case(
            "Given_Both_Sides_On_Tmpfs",
            side => side with { Environment = side.Environment with { StorageMountType = "tmpfs" } },
            "tmpfs"
        );
        yield return Case(
            "Given_A_Non_Windows_Platform",
            side => side with { Environment = side.Environment with { OsFamily = "Linux" } },
            "platform Linux"
        );
    }

    private static TestCaseData Case(
        string name,
        Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit,
        string reason
    ) => new TestCaseData(edit, reason).SetName(name);

    [TestCaseSource(nameof(Cases))]
    public void It_is_invalid_and_emits_no_calibration(
        Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit,
        string reason
    )
    {
        PerfBenchmarkComparisonResult result = new ComparisonBuilder
        {
            Purpose = PerfBenchmarkComparison.Calibration,
        }
            .EditSides(edit)
            .Compare();

        result.ShouldBeInvalidBecause(reason);
        result.Calibration.Should().BeNull();
    }
}

// ---------------------------------------------------------------- purpose eligibility

[TestFixture]
public class Given_A_Repeatability_Run_With_Two_Pairs_Under_A_Two_Pair_Profile
{
    [Test]
    public void It_is_not_eligible_because_of_the_hard_minimum()
    {
        ComparisonBuilder builder = new() { Purpose = PerfBenchmarkComparison.Repeatability, Pairs = 2 };
        builder.Profile = ComparisonProfiles.FrozenFor(
            builder,
            profile => profile["comparison"]!["pairs"] = 2
        );

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.ShouldBeValid();
        result.QualificationEligible.Should().BeFalse();
        result.NotEligibleReasons.Should().Contain(reason => reason.Contains("at least 5"));
    }
}

[TestFixture]
public class Given_A_Repeatability_Run_With_Four_Pairs
{
    [Test]
    public void It_is_not_eligible()
    {
        ComparisonBuilder builder = new() { Purpose = PerfBenchmarkComparison.Repeatability, Pairs = 4 };
        builder.Profile = ComparisonProfiles.FrozenFor(builder);

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.QualificationEligible.Should().BeFalse();
        result.NotEligibleReasons.Should().Contain(reason => reason.Contains("4 pairs"));
    }
}

[TestFixture]
public class Given_A_Non_Candidate_Purpose_With_Different_Commits
{
    [TestCase(
        PerfBenchmarkComparison.Detection,
        TestName = "Given_A_Detection_Run_With_Different_Baseline_And_Candidate_Commits"
    )]
    [TestCase(PerfBenchmarkComparison.Sizing, TestName = "Given_A_Sizing_Run_With_Different_Commits")]
    [TestCase(
        PerfBenchmarkComparison.BoundaryProbe,
        TestName = "Given_A_Boundary_Probe_With_Different_Commits"
    )]
    public void It_is_invalid(string purpose)
    {
        new ComparisonBuilder
        {
            Purpose = purpose,
            CandidateCommit = ComparisonBuilder.OtherCommit,
            Injection = ComparisonProfiles.FrozenInjection,
        }
            .Compare()
            .ShouldBeInvalidBecause($"purpose {purpose} requires baseline = candidate");
    }
}

[TestFixture]
public class Given_A_Side_With_A_Different_Run_Id
{
    [Test]
    public void It_is_invalid_because_stale_sides_are_rejected()
    {
        new ComparisonBuilder()
            .EditSide(4, "baseline", side => side with { Side = side.Side with { RunId = "run-0000" } })
            .Compare()
            .ShouldBeInvalidBecause("pair 4 baseline: the side belongs to run run-0000");
    }

    [Test]
    public void It_rejects_a_stale_environment_file_too()
    {
        new ComparisonBuilder()
            .EditSide(
                4,
                "baseline",
                side => side with { Environment = side.Environment with { RunId = "run-0000" } }
            )
            .Compare()
            .ShouldBeInvalidBecause("environment run-0000");
    }
}

[TestFixture]
public class Given_A_Sizing_Run
{
    internal static ComparisonBuilder Sizing(
        decimal writeRatio,
        decimal readRatio = 1.20m,
        decimal factor = 0.20m
    )
    {
        ComparisonBuilder builder = new()
        {
            Purpose = PerfBenchmarkComparison.Sizing,
            Pairs = 2,
            Injection = new Dictionary<string, decimal>
            {
                [ComparisonBuilder.Write] = factor,
                [ComparisonBuilder.ZeroSmall] = 0.20m,
            },
        };
        builder.CandidateRatios[ComparisonBuilder.Write] = [writeRatio, writeRatio];
        builder.CandidateRatios[ComparisonBuilder.ZeroSmall] = [readRatio, readRatio];
        return builder;
    }

    [TestCase("1.00", "zero", TestName = "Given_A_Sizing_Run_Observing_Zero")]
    [TestCase("0.95", "negative", TestName = "Given_A_Sizing_Run_Observing_Negative")]
    [TestCase("1.01", "below 0.02", TestName = "Given_A_Sizing_Run_Observing_1_Percent")]
    public void It_is_inconclusive_without_a_suggestion(string ratio, string reason)
    {
        PerfBenchmarkSizing sizing = Sizing(
                decimal.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture)
            )
            .Compare()
            .Sizing(ComparisonBuilder.Write);

        sizing.Status.Should().Be(PerfBenchmarkSizing.Inconclusive);
        sizing.SuggestedFactor.Should().BeNull();
        sizing.NextFactor.Should().Be(0.20m);
        sizing.Reason.Should().Contain(reason);
    }

    [Test]
    public void Given_A_Sizing_Run_Whose_Suggested_Factor_Exceeds_5()
    {
        PerfBenchmarkSizing sizing = Sizing(1.10m, factor: 3.0m).Compare().Sizing(ComparisonBuilder.Write);

        sizing.Status.Should().Be(PerfBenchmarkSizing.Inconclusive);
        sizing.SuggestedFactor.Should().BeNull();
        sizing.NextFactor.Should().Be(3.0m);
        sizing.Reason.Should().Contain("outside (0, 5]");
    }

    [Test]
    public void Given_A_Sizing_Run_Observing_10_Percent_At_Factor_0_20()
    {
        PerfBenchmarkSizing sizing = Sizing(1.10m).Compare().Sizing(ComparisonBuilder.Write);

        sizing.Status.Should().Be(PerfBenchmarkSizing.Adjust);
        sizing.SuggestedFactor.Should().Be(0.40m);
        sizing.NextFactor.Should().Be(0.40m);
    }

    [Test]
    public void Given_A_Sizing_Run_Landing_In_The_Band()
    {
        PerfBenchmarkComparisonResult result = Sizing(1.20m).Compare();

        result.ShouldBeValid();
        result.Sizing(ComparisonBuilder.Write).Status.Should().Be(PerfBenchmarkSizing.InBand);
        result.Detection.Should().NotBeNull();
        result.Detection!.Passed.Should().BeTrue();
        result.QualificationEligible.Should().BeFalse();
        result.NotEligibleReasons.Should().Contain(reason => reason.Contains("diagnostic"));
    }
}

[TestFixture]
public class Given_Detection_And_Repeatability_Eligibility
{
    internal static ComparisonBuilder Eligible(
        string purpose,
        IReadOnlyDictionary<string, decimal>? injection = null,
        int pairs = 5,
        bool smoke = false
    )
    {
        ComparisonBuilder builder = new()
        {
            Purpose = purpose,
            Pairs = pairs,
            Smoke = smoke,
            Injection =
                injection
                ?? (
                    purpose == PerfBenchmarkComparison.Detection
                        ? ComparisonProfiles.FrozenInjection
                        : new Dictionary<string, decimal>()
                ),
        };
        decimal[] ratios =
        [
            .. Enumerable.Repeat(purpose == PerfBenchmarkComparison.Detection ? 1.20m : 1m, pairs),
        ];
        builder.CandidateRatios[ComparisonBuilder.Write] = ratios;
        builder.CandidateRatios[ComparisonBuilder.ZeroSmall] = ratios;
        builder.Profile = ComparisonProfiles.FrozenFor(builder);
        return builder;
    }

    [Test]
    public void Given_An_Eligible_Detection_Run()
    {
        PerfBenchmarkComparisonResult result = Eligible(PerfBenchmarkComparison.Detection).Compare();

        result.ShouldBeValid();
        result.QualificationEligible.Should().BeTrue();
        result.Detection!.Passed.Should().BeTrue();
        result.Labels.Should().NotContain(PerfBenchmarkComparisonResult.UncalibratedLabel);
    }

    [Test]
    public void Given_An_Eligible_Repeatability_Run()
    {
        PerfBenchmarkComparisonResult result = Eligible(PerfBenchmarkComparison.Repeatability).Compare();

        result.ShouldBeValid();
        result.QualificationEligible.Should().BeTrue();
        result.NotEligibleReasons.Should().BeEmpty();
    }

    [Test]
    public void Given_A_Detection_Run_With_Non_Frozen_Factors()
    {
        Eligible(
                PerfBenchmarkComparison.Detection,
                new Dictionary<string, decimal>
                {
                    [ComparisonBuilder.Write] = 0.5m,
                    [ComparisonBuilder.ZeroSmall] = 0.4m,
                }
            )
            .Compare()
            .QualificationEligible.Should()
            .BeFalse();
    }

    [Test]
    public void Given_A_Detection_Run_Injecting_One_Scenario()
    {
        PerfBenchmarkComparisonResult result = Eligible(
                PerfBenchmarkComparison.Detection,
                new Dictionary<string, decimal> { [ComparisonBuilder.Write] = 0.4m }
            )
            .Compare();

        result.QualificationEligible.Should().BeFalse();
        result
            .NotEligibleReasons.Should()
            .Contain(reason => reason.Contains("frozen detection.injectionFactors"));
    }

    [Test]
    public void Given_A_Detection_Run_With_Fewer_Pairs_Than_The_Profile()
    {
        ComparisonBuilder builder = Eligible(PerfBenchmarkComparison.Detection);
        builder.Profile = ComparisonProfiles.FrozenFor(
            builder,
            profile => profile["comparison"]!["pairs"] = 7
        );

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.QualificationEligible.Should().BeFalse();
        result.NotEligibleReasons.Should().Contain(reason => reason.Contains("at least 7"));
    }

    [Test]
    public void Given_A_Detection_Run_On_An_Unfrozen_Profile()
    {
        ComparisonBuilder builder = Eligible(PerfBenchmarkComparison.Detection);
        builder.Profile = ComparisonProfiles.Committed();

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.QualificationEligible.Should().BeFalse();
        result.NotEligibleReasons.Should().Contain("the profile is not frozen");
    }

    [Test]
    public void Given_A_Repeatability_Run_With_Injection()
    {
        PerfBenchmarkComparisonResult result = Eligible(
                PerfBenchmarkComparison.Repeatability,
                ComparisonProfiles.FrozenInjection
            )
            .Compare();

        result.QualificationEligible.Should().BeFalse();
        result.NotEligibleReasons.Should().Contain("a repeatability run injects nothing");
    }

    [Test]
    public void Given_A_Smoke_Detection_Run()
    {
        PerfBenchmarkComparisonResult result = Eligible(PerfBenchmarkComparison.Detection, smoke: true)
            .Compare();

        result.ShouldBeValid();
        result.Evidence.Should().BeFalse();
        result.QualificationEligible.Should().BeFalse();
        result.NotEligibleReasons.Should().Contain("the smoke workload is not evidence");
    }
}

// ---------------------------------------------------------------- validity

[TestFixture]
public class Given_Invalid_Work
{
    [Test]
    public void Given_A_Failed_Side()
    {
        new ComparisonBuilder()
            .EditSide(
                2,
                "baseline",
                side =>
                    side with
                    {
                        Side = side.Side with
                        {
                            Status = new PerfBenchmarkSideStatus(
                                PerfBenchmarkSideStatus.Failed,
                                PerfBenchmarkSideStatus.PostReadPhase,
                                "plan replay failed"
                            ),
                        },
                    }
            )
            .Compare()
            .ShouldBeInvalidBecause(
                "pair 2 baseline: the side failed in phase post-read: plan replay failed"
            );
    }

    [Test]
    public void Given_A_Missing_Side()
    {
        ComparisonBuilder builder = new();
        builder.OmittedSides.Add((5, "baseline"));

        PerfBenchmarkComparisonResult result = builder.Compare();

        result.ShouldBeInvalidBecause("pair 5 baseline: the planned side is missing");
        result.Scenarios.Should().BeEmpty();
    }

    [Test]
    public void Given_Failed_Read_Operations()
    {
        new ComparisonBuilder()
            .EditSide(
                1,
                "candidate",
                side =>
                    side with
                    {
                        Side = side.Side with
                        {
                            ReadCells =
                            [
                                side.Side.ReadCells[0] with
                                {
                                    MeasuredCompleted = 59,
                                    MeasuredFailed = 1,
                                },
                                .. side.Side.ReadCells.Skip(1),
                            ],
                        },
                    }
            )
            .Compare()
            .ShouldBeInvalidBecause("failed read operations");
    }

    [Test]
    public void Given_Failed_Write_Operations()
    {
        new ComparisonBuilder()
            .EditSide(
                1,
                "candidate",
                side =>
                    side with
                    {
                        Side = side.Side with
                        {
                            Write = side.Side.Write with { MeasuredCompleted = 199, Failed = 1 },
                        },
                    }
            )
            .Compare()
            .ShouldBeInvalidBecause("failed write operations");
    }

    [Test]
    public void Given_A_Failed_Write_Verification()
    {
        new ComparisonBuilder()
            .EditSide(
                3,
                "candidate",
                side =>
                    side with
                    {
                        Side = side.Side with
                        {
                            Status = new PerfBenchmarkSideStatus(
                                PerfBenchmarkSideStatus.Failed,
                                PerfBenchmarkSideStatus.WritesPhase,
                                "Write verification failed: updated-other-names-total: expected 220, got 219."
                            ),
                        },
                    }
            )
            .Compare()
            .ShouldBeInvalidBecause("Write verification failed");
    }

    [Test]
    public void Given_A_Side_Without_Its_Paging_Run()
    {
        new ComparisonBuilder()
            .EditSide(3, "candidate", side => side with { Reads = null })
            .Compare()
            .ShouldBeInvalidBecause("the paging run's artifacts are missing");
    }

    [Test]
    public void Given_A_Load_Error()
    {
        PerfBenchmarkComparisonInput input = new ComparisonBuilder().Build();

        PerfBenchmarkComparison
            .Compare(input with { LoadErrors = ["pair 1 baseline: unreadable artifacts: boom"] })
            .ShouldBeInvalidBecause("unreadable artifacts");
    }
}

// ---------------------------------------------------------------- consistency

[TestFixture]
public class Given_A_Mismatch
{
    private static IEnumerable<TestCaseData> Cases()
    {
        yield return Case(
            "ProfileHash",
            side => side with { Side = side.Side with { ProfileSha256 = new string('f', 64) } },
            "profile SHA-256"
        );
        yield return Case(
            "HarnessCommit",
            side => side with { Reads = side.Reads! with { RunnerCommit = new string('e', 40) } },
            "harness commit"
        );
        yield return Case(
            "Fixture",
            side =>
                side with
                {
                    Side = side.Side with { Workload = side.Side.Workload with { Fixture = "smoke-10k" } },
                },
            "fixture"
        );
        yield return Case(
            "LogLevel",
            side => side with { Side = side.Side with { EffectiveLogLevel = "Information" } },
            "log level"
        );
        yield return Case(
            "OwnershipFlag",
            side => side with { Side = side.Side with { OwnershipPublished = false } },
            "ownership-published flag"
        );
        yield return Case(
            "Instrumentation",
            side =>
                side with
                {
                    Side = side.Side with { Instrumentation = new PerfBenchmarkInstrumentation(false, true) },
                },
            "instrumentation"
        );
        yield return Fingerprint(
            "osFamily",
            side => side with { Environment = side.Environment with { OsFamily = "Linux" } }
        );
        yield return Fingerprint(
            "architecture",
            side => side with { Environment = side.Environment with { Architecture = "arm64" } }
        );
        yield return Fingerprint(
            "machineFingerprint",
            side =>
                side with
                {
                    Reads = side.Reads! with { Host = side.Reads.Host with { MachineFingerprint = "other" } },
                }
        );
        yield return Fingerprint(
            "cpuModel",
            side =>
                side with
                {
                    Reads = side.Reads! with { Host = side.Reads.Host with { CpuModel = "Other CPU" } },
                }
        );
        yield return Fingerprint(
            "logicalCores",
            side =>
                side with
                {
                    Reads = side.Reads! with { Host = side.Reads.Host with { LogicalCores = 8 } },
                }
        );
        yield return Fingerprint(
            "totalMemoryBytes",
            side =>
                side with
                {
                    Reads = side.Reads! with { Host = side.Reads.Host with { TotalMemoryBytes = 1 } },
                }
        );
        yield return Fingerprint(
            "powerPlan",
            side => side with { Environment = side.Environment with { PowerPlan = "Balanced" } }
        );
        yield return Fingerprint(
            "acPowerOnline",
            side => side with { Environment = side.Environment with { AcPowerOnline = false } }
        );
        yield return Fingerprint(
            "dockerVmCpus",
            side => side with { Environment = side.Environment with { DockerVmCpus = 4 } }
        );
        yield return Fingerprint(
            "dockerVmMemoryBytes",
            side => side with { Environment = side.Environment with { DockerVmMemoryBytes = 1 } }
        );
        yield return Fingerprint(
            "databaseCpuLimit",
            side => side with { Environment = side.Environment with { DatabaseCpuLimit = 2 } }
        );
        yield return Fingerprint(
            "databaseMemoryLimitBytes",
            side => side with { Environment = side.Environment with { DatabaseMemoryLimitBytes = 1 } }
        );
        yield return Fingerprint(
            "imageDigest",
            side =>
                side with
                {
                    Reads = side.Reads! with
                    {
                        Server = side.Reads.Server with { ImageDigest = "sha256:" + new string('8', 64) },
                    },
                }
        );
        yield return Fingerprint(
            "serverVersion",
            side =>
                side with
                {
                    Reads = side.Reads! with
                    {
                        Server = side.Reads.Server with { ServerVersion = "PostgreSQL 18.7" },
                    },
                }
        );
        yield return Fingerprint(
            "databaseSettings",
            side =>
                side with
                {
                    Reads = side.Reads! with
                    {
                        Server = side.Reads.Server with { Settings = [new PerfSetting("work_mem", "8192")] },
                    },
                }
        );
        yield return Fingerprint(
            "storageMountType",
            side => side with { Environment = side.Environment with { StorageMountType = "bind" } }
        );
        yield return Fingerprint(
            "connection",
            side =>
                side with
                {
                    Side = side.Side with
                    {
                        EffectiveConnection = side.Side.EffectiveConnection with { MaxPoolSize = 50 },
                    },
                }
        );
    }

    private static TestCaseData Case(
        string field,
        Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit,
        string reason
    ) => new TestCaseData(edit, reason).SetName($"Given_A_Mismatch_In_{field}");

    private static TestCaseData Fingerprint(
        string field,
        Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit
    ) => Case($"Fingerprint_{field}", edit, $"fingerprint field {field}");

    [TestCaseSource(nameof(Cases))]
    public void It_is_invalid(Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit, string reason)
    {
        new ComparisonBuilder()
            .EditSide(2, "candidate", edit)
            .Compare()
            .ShouldBeInvalidBecause($"consistency: the sides disagree on {reason}");
    }
}

[TestFixture]
public class Given_Permitted_Differences
{
    [Test]
    public void It_is_valid_and_lists_them()
    {
        PerfBenchmarkComparisonResult result = new ComparisonBuilder()
            .EditSide(
                1,
                "candidate",
                side =>
                    side with
                    {
                        Side = side.Side with { EffectiveSchemaHash = "other-hash" },
                        Environment = side.Environment with
                        {
                            OsBuild = "10.0.26301",
                            DockerVersion = "28.0.2",
                        },
                        Reads = side.Reads! with
                        {
                            Host = side.Reads.Host with { DotnetVersion = "10.0.16" },
                        },
                    }
            )
            .Compare();

        result.ShouldBeValid();
        result
            .PermittedDifferences.Should()
            .BeEquivalentTo(
                "subjectCommit",
                "effectiveSchemaHash",
                "osBuild",
                "dotnetVersion",
                "dockerVersion"
            );
    }
}

// ---------------------------------------------------------------- injection

[TestFixture]
public class Given_An_Injected_Candidate
{
    [Test]
    public void It_is_valid_and_labelled_demonstration()
    {
        PerfBenchmarkComparisonResult result = new ComparisonBuilder
        {
            Injection = new Dictionary<string, decimal> { [ComparisonBuilder.Write] = 1.0m },
        }.Compare();

        result.ShouldBeValid();
        result.Labels.Should().Contain(PerfBenchmarkComparisonResult.DemonstrationLabel);
        result.Detection.Should().NotBeNull();
    }

    [Test]
    public void Given_Injection_On_A_Baseline_Side()
    {
        new ComparisonBuilder()
            .EditSide(
                2,
                "baseline",
                side =>
                    side with
                    {
                        Side = side.Side with
                        {
                            Injection = side.Side.Injection with
                            {
                                Factors = new Dictionary<string, decimal>
                                {
                                    [ComparisonBuilder.Write] = 1.0m,
                                },
                            },
                        },
                    }
            )
            .Compare()
            .ShouldBeInvalidBecause("pair 2 baseline: injection");
    }

    [Test]
    public void Given_An_Injection_Spec_Different_From_The_Run_Plan()
    {
        new ComparisonBuilder
        {
            Injection = new Dictionary<string, decimal> { [ComparisonBuilder.Write] = 1.0m },
        }
            .EditSide(
                4,
                "candidate",
                side =>
                    side with
                    {
                        Side = side.Side with
                        {
                            Injection = side.Side.Injection with
                            {
                                Factors = new Dictionary<string, decimal>
                                {
                                    [ComparisonBuilder.Write] = 0.5m,
                                },
                            },
                        },
                    }
            )
            .Compare()
            .ShouldBeInvalidBecause("differs from the run plan's");
    }

    [Test]
    public void Given_Affected_Counts_Short_For_One_Scenario()
    {
        new ComparisonBuilder
        {
            Injection = new Dictionary<string, decimal> { [ComparisonBuilder.Write] = 1.0m },
        }
            .EditSide(
                4,
                "candidate",
                side =>
                    side with
                    {
                        Side = side.Side with
                        {
                            Injection = side.Side.Injection with
                            {
                                AffectedRequests = PerfBenchmarkScenarios.AllIds.ToDictionary(
                                    id => id,
                                    id => id == ComparisonBuilder.Write ? 1199 : 0
                                ),
                            },
                        },
                    }
            )
            .Compare()
            .ShouldBeInvalidBecause(
                "injection affected 1199 student-put-collection-update requests; expected 1200"
            );
    }
}

// ---------------------------------------------------------------- fingerprint

[TestFixture]
public class Given_A_Profile_Frozen_From_A_Calibration_Run
{
    [Test]
    public void It_matches_a_later_run_with_a_new_profile_hash_and_harness_commit()
    {
        PerfBenchmarkComparisonResult calibration = new ComparisonBuilder
        {
            Purpose = PerfBenchmarkComparison.Calibration,
        }.Compare();
        PerfLoadedBenchmarkProfile frozen = ComparisonProfiles.Frozen(
            "postgresql",
            calibration.Calibration!.Fingerprint
        );

        ComparisonBuilder later = new() { Profile = frozen };
        later.PlanEdits.Add(plan => plan with { HarnessCommit = new string('9', 40) });
        later.EditSides(side =>
            side with
            {
                Reads = side.Reads! with { RunnerCommit = new string('9', 40) },
            }
        );
        PerfBenchmarkComparisonResult result = later.Compare();

        frozen.Sha256.Should().NotBe(ComparisonProfiles.Committed().Sha256);
        result.ShouldBeValid();
        result.Labels.Should().NotContain(PerfBenchmarkComparisonResult.UncalibratedLabel);
    }
}

[TestFixture]
public class Given_A_Fingerprint_For_The_Other_Provider_Only
{
    [Test]
    public void It_labels_this_provider_uncalibrated()
    {
        ComparisonBuilder mssql = new() { Provider = "mssql" };
        PerfLoadedBenchmarkProfile profile = ComparisonProfiles.Edited(json =>
            json["environment"]!["qualifiedEnvironments"]!["mssql"] = System.Text.Json.Nodes.JsonNode.Parse(
                PerfBenchmarkJson.Serialize(mssql.Fingerprint())
            )
        );

        PerfBenchmarkComparisonResult result = new ComparisonBuilder { Profile = profile }.Compare();

        result.ShouldBeValid();
        result.Labels.Should().Contain(PerfBenchmarkComparisonResult.UncalibratedLabel);
    }
}

[TestFixture]
public class Given_A_Different_Environment
{
    [Test]
    public void It_is_invalid_naming_the_fields()
    {
        ComparisonBuilder builder = new();
        builder.Profile = ComparisonProfiles.FrozenFor(builder);
        builder.EditSides(side =>
            side with
            {
                Reads = side.Reads! with { Host = side.Reads.Host with { CpuModel = "Other CPU" } },
            }
        );

        builder.Compare().ShouldBeInvalidBecause("differs from qualifiedEnvironments.postgresql in cpuModel");
    }

    [Test]
    public void Given_A_Qualified_Fingerprint_And_A_Later_Run_With_Changed_Connection_Settings()
    {
        ComparisonBuilder builder = new();
        builder.Profile = ComparisonProfiles.FrozenFor(builder);
        builder.EditSides(side =>
            side with
            {
                Side = side.Side with
                {
                    EffectiveConnection = side.Side.EffectiveConnection with { MinPoolSize = 5 },
                },
            }
        );

        builder.Compare().ShouldBeInvalidBecause("qualifiedEnvironments.postgresql in connection");
    }
}

// ---------------------------------------------------------------- rules

[TestFixture]
public class Given_A_Rule_Violated
{
    private static IEnumerable<TestCaseData> Cases()
    {
        yield return Case(
            "Idle_Host_Cpu",
            ComparisonBuilder.SetSamples(
                PerfBenchmarkMetrics.HostCpuPercent,
                50,
                second => second is >= 30 and < 40
            ),
            "idle host-cpu-percent"
        );
        yield return Case(
            "Idle_Database_Cpu",
            ComparisonBuilder.SetSamples(
                PerfBenchmarkMetrics.DatabaseCpuPercent,
                20,
                second => second is >= 30 and < 40
            ),
            "idle db-cpu-percent"
        );
        yield return Case(
            "Low_Host_Memory",
            ComparisonBuilder.SetSamples(PerfBenchmarkMetrics.HostAvailableMb, 1_000, second => second == 70),
            "host available memory"
        );
        yield return Case(
            "Database_Memory",
            ComparisonBuilder.SetSamples(
                PerfBenchmarkMetrics.DatabaseMemoryPercent,
                95,
                second => second == 70
            ),
            "database memory"
        );
        yield return Case(
            "Competing_Process",
            side => side with { Environment = side.Environment with { CompetingHarnessProcess = true } },
            "a competing harness process"
        );
        yield return Case(
            "Other_Container",
            side =>
                side with
                {
                    Environment = side.Environment with { OtherProviderContainerRunning = true },
                },
            "the other provider's database container"
        );
        yield return Case(
            "Tmpfs",
            side => side with { Environment = side.Environment with { StorageMountType = "tmpfs" } },
            "the database ran on tmpfs"
        );
        yield return Case(
            "No_Idle_Phase_Samples",
            ComparisonBuilder.DropSamples(Enumerable.Range(30, 10), [.. PerfBenchmarkMetrics.All]),
            "incomplete samples for host-cpu-percent in the idle phase"
        );
        yield return Case(
            "Missing_Phase",
            side =>
                side with
                {
                    Environment = side.Environment with
                    {
                        Phases = [.. side.Environment.Phases.Where(phase => phase.Name != "settle")],
                    },
                },
            "environment.json records no settle phase"
        );
    }

    private static TestCaseData Case(
        string name,
        Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit,
        string reason
    ) => new TestCaseData(edit, reason).SetName($"Given_{name}_Violated");

    [TestCaseSource(nameof(Cases))]
    public void It_is_invalid(Func<PerfBenchmarkSideEvidence, PerfBenchmarkSideEvidence> edit, string reason)
    {
        new ComparisonBuilder()
            .EditSide(3, "baseline", edit)
            .Compare()
            .ShouldBeInvalidBecause($"pair 3 baseline: rule: {reason}");
    }

    [Test]
    public void Given_High_Workload_Cpu_During_Measurement()
    {
        new ComparisonBuilder()
            .EditSides(
                ComparisonBuilder.SetSamples(PerfBenchmarkMetrics.HostCpuPercent, 100, second => second >= 40)
            )
            .EditSides(
                ComparisonBuilder.SetSamples(
                    PerfBenchmarkMetrics.DatabaseCpuPercent,
                    400,
                    second => second >= 40
                )
            )
            .Compare()
            .ShouldBeValid();
    }
}

[TestFixture]
public class Given_Incomplete_Samples
{
    [Test]
    public void Given_Incomplete_Samples_For_Host_Cpu_In_Settle()
    {
        // 30 expected at 1 s; 26 left is below 90%.
        new ComparisonBuilder()
            .EditSide(
                1,
                "baseline",
                ComparisonBuilder.DropSamples([1, 3, 5, 7], PerfBenchmarkMetrics.HostCpuPercent)
            )
            .Compare()
            .ShouldBeInvalidBecause("incomplete samples for host-cpu-percent in the settle phase: 26 of 30");
    }

    [Test]
    public void Given_Incomplete_Samples_For_Database_Memory_In_Idle()
    {
        // 5 expected at 2 s; 4 left is below 90%.
        new ComparisonBuilder()
            .EditSide(
                1,
                "baseline",
                ComparisonBuilder.DropSamples([32], PerfBenchmarkMetrics.DatabaseMemoryPercent)
            )
            .Compare()
            .ShouldBeInvalidBecause("incomplete samples for db-memory-percent in the idle phase: 4 of 5");
    }

    [Test]
    public void It_accepts_complete_docker_samples_at_their_2_second_interval()
    {
        // Judged at 1 s, five idle Docker samples would be 50% complete.
        new ComparisonBuilder()
            .Compare()
            .ShouldBeValid();
    }
}

[TestFixture]
public class Given_Measured_Window_Coverage
{
    private static readonly string[] _dockerMetrics =
    [
        PerfBenchmarkMetrics.DatabaseCpuPercent,
        PerfBenchmarkMetrics.DatabaseMemoryPercent,
        PerfBenchmarkMetrics.DatabaseBlockIoBytes,
    ];

    [Test]
    public void Given_Side_Phase_Completeness_Above_90_Percent_But_No_Samples_In_A_Measured_Window()
    {
        // The first cell's window is side seconds 50-53; dropping 49-55 keeps the side phase at
        // 93% (host) and 94% (Docker) complete.
        new ComparisonBuilder()
            .EditSide(
                2,
                "candidate",
                ComparisonBuilder.DropSamples(Enumerable.Range(49, 7), [.. PerfBenchmarkMetrics.All])
            )
            .Compare()
            .ShouldBeInvalidBecause(
                "measured window of traditional-offset-zero/25 is not covered by host-cpu-percent"
            );
    }

    [Test]
    public void Given_A_Gap_Longer_Than_Twice_The_Interval_Inside_A_Measured_Window()
    {
        // The write window is 110-113; dropping 110 and 112 leaves 108 → 114, a 6 s gap.
        new ComparisonBuilder()
            .EditSide(2, "candidate", ComparisonBuilder.DropSamples([110, 112], _dockerMetrics))
            .Compare()
            .ShouldBeInvalidBecause(
                "measured window of student-put-collection-update is not covered by db-cpu-percent samples (start bracket True, end bracket True, gap over 4 s True)"
            );
    }

    [Test]
    public void Given_A_Missing_Bracketing_Sample_At_Window_End()
    {
        // The first cell's window ends at 53; the only Docker sample in [53, 55] is at 54.
        new ComparisonBuilder()
            .EditSide(2, "candidate", ComparisonBuilder.DropSamples([54], _dockerMetrics))
            .Compare()
            .ShouldBeInvalidBecause("end bracket False");
    }

    [Test]
    public void Given_A_Measured_Window_Shorter_Than_One_Docker_Interval_With_Bracketing_Samples()
    {
        new ComparisonBuilder()
            .EditSides(side =>
                side with
                {
                    Side = side.Side with
                    {
                        Write = side.Side.Write with
                        {
                            MeasuredWindow = new PerfMeasuredWindow(
                                ComparisonBuilder.SideStart(side.Side.Pair, side.Side.Role).AddSeconds(110.5),
                                ComparisonBuilder.SideStart(side.Side.Pair, side.Side.Role).AddSeconds(111),
                                1
                            ),
                        },
                    },
                }
            )
            .Compare()
            .ShouldBeValid();
    }

    [Test]
    public void Given_Docker_Samples_Every_2s_And_Host_Samples_Every_1s()
    {
        // A 4 s Docker gap is within 2 × 2 s; the same gap for host samples is beyond 2 × 1 s.
        new ComparisonBuilder()
            .EditSide(2, "candidate", ComparisonBuilder.DropSamples([52], _dockerMetrics))
            .Compare()
            .ShouldBeValid();

        new ComparisonBuilder()
            .EditSide(
                2,
                "candidate",
                ComparisonBuilder.DropSamples([51, 52], PerfBenchmarkMetrics.HostAvailableMb)
            )
            .Compare()
            .ShouldBeInvalidBecause("not covered by host-available-mb");
    }
}

// ---------------------------------------------------------------- detection

[TestFixture]
public class Given_An_Injected_Run
{
    private static PerfBenchmarkComparisonResult Observing(
        decimal demonstrationRatio,
        decimal otherRatio = 1m
    )
    {
        ComparisonBuilder builder = new() { Injection = ComparisonProfiles.FrozenInjection };
        builder.CandidateRatios[ComparisonBuilder.Write] = [.. Enumerable.Repeat(demonstrationRatio, 5)];
        builder.CandidateRatios[ComparisonBuilder.ZeroSmall] = [.. Enumerable.Repeat(demonstrationRatio, 5)];
        builder.CandidateRatios[ComparisonBuilder.DeepLarge] = [.. Enumerable.Repeat(otherRatio, 5)];
        return builder.Compare();
    }

    [Test]
    public void Given_An_Injected_Run_Observing_11_Percent_With_A_10_Percent_Threshold()
    {
        PerfBenchmarkComparisonResult result = Observing(1.11m);

        result.Scenario(ComparisonBuilder.Write).Flagged.Should().BeTrue();
        result.Detection!.Passed.Should().BeFalse();
        result
            .Detection.Scenarios.Should()
            .AllSatisfy(scenario =>
            {
                scenario.InBand.Should().BeFalse();
                scenario.BandLow.Should().Be(0.18m);
                scenario.BandHigh.Should().Be(0.25m);
            });
    }

    [Test]
    public void Given_An_Injected_Run_Observing_20_Percent()
    {
        PerfBenchmarkComparisonResult result = Observing(1.20m);

        result.Detection!.Passed.Should().BeTrue();
        result
            .Detection.Scenarios.Should()
            .OnlyContain(scenario => scenario.Passed && scenario.ObservedIncrease == 0.20m);
    }

    [Test]
    public void Given_An_Injected_Run_Observing_40_Percent()
    {
        Observing(1.40m).Detection!.Passed.Should().BeFalse();
    }

    [Test]
    public void Given_An_Injected_Run_Flagging_A_Non_Demonstration_Scenario()
    {
        PerfBenchmarkComparisonResult result = Observing(1.20m, otherRatio: 1.30m);

        result.Detection!.Passed.Should().BeFalse();
        result.Detection.OtherFlaggedScenarios.Should().Equal(ComparisonBuilder.DeepLarge);
    }
}

[TestFixture]
public class Given_A_Sizing_Run_With_The_Read_Inconclusive_And_The_Write_Adjusting
{
    [Test]
    public void It_emits_both_next_factors()
    {
        PerfBenchmarkComparisonResult result = Given_A_Sizing_Run
            .Sizing(writeRatio: 1.10m, readRatio: 1.00m)
            .Compare();

        result.Sizing(ComparisonBuilder.ZeroSmall).Status.Should().Be(PerfBenchmarkSizing.Inconclusive);
        result.Sizing(ComparisonBuilder.ZeroSmall).NextFactor.Should().Be(0.20m);
        result.Sizing(ComparisonBuilder.Write).Status.Should().Be(PerfBenchmarkSizing.Adjust);
        result.Sizing(ComparisonBuilder.Write).NextFactor.Should().Be(0.40m);
    }
}

[TestFixture]
public class Given_A_Sizing_Run_With_One_Scenario_In_Band
{
    [Test]
    public void It_keeps_that_scenarios_factor()
    {
        PerfBenchmarkComparisonResult result = Given_A_Sizing_Run
            .Sizing(writeRatio: 1.21m, readRatio: 1.10m)
            .Compare();

        result.Sizing(ComparisonBuilder.Write).Status.Should().Be(PerfBenchmarkSizing.InBand);
        result.Sizing(ComparisonBuilder.Write).NextFactor.Should().Be(0.20m);
        result.Sizing(ComparisonBuilder.ZeroSmall).NextFactor.Should().Be(0.40m);
    }
}

// ---------------------------------------------------------------- profile identity

[TestFixture]
public class Given_Sides_That_Agree_But_A_Report_Profile_That_Differs
{
    [Test]
    public void It_is_invalid_naming_the_profile_mismatch()
    {
        ComparisonBuilder builder = new()
        {
            ReportProfile = ComparisonProfiles.Edited(profile =>
                profile["comparison"]!["p50Thresholds"]!["postgresql"]![ComparisonBuilder.Write] = 0.13m
            ),
        };

        builder.Compare().ShouldBeInvalidBecause("profile identity: the run plan recorded profile");
    }
}

[TestFixture]
public class Given_A_Run_Plan_Hash_Different_From_The_Sides
{
    [Test]
    public void It_is_invalid()
    {
        ComparisonBuilder builder = new();
        builder.PlanEdits.Add(plan => plan with { ProfileSha256 = new string('e', 64) });

        builder
            .Compare()
            .ShouldBeInvalidBecause("pair 1 baseline: profile identity: the side ran with profile");
    }
}

// ---------------------------------------------------------------- loading

[TestFixture]
public class Given_A_Comparison_Directory
{
    private string _directory = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dms-comparison-tests", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void It_loads_the_same_verdict_the_input_produces()
    {
        ComparisonBuilder builder = new();
        builder.CandidateRatios[ComparisonBuilder.DeepLarge] = [1.30m, 1.02m, 1.31m, 1.29m, 1.01m];
        PerfBenchmarkComparisonInput input = builder.Build();
        ComparisonBuilder.WriteDirectory(_directory, input);

        PerfBenchmarkComparisonResult loaded = PerfBenchmarkComparison.Compare(
            PerfBenchmarkComparisonLoader.Load(_directory, builder.Profile)
        );

        loaded.ShouldBeValid();
        loaded.Result.Should().Be(PerfBenchmarkComparisonResult.SuspectedRegression);
        loaded.Scenario(ComparisonBuilder.DeepLarge).MedianP50Ratio.Should().Be(1.29m);
    }

    [Test]
    public void It_reports_unreadable_side_artifacts_as_a_load_error()
    {
        PerfBenchmarkComparisonInput input = new ComparisonBuilder().Build();
        ComparisonBuilder.WriteDirectory(_directory, input);
        File.WriteAllText(
            Path.Combine(
                PerfBenchmarkComparisonLoader.SideDirectory(_directory, 2, "baseline"),
                PerfBenchmarkMetrics.SamplesFileName
            ),
            "time,value\n"
        );

        PerfBenchmarkComparisonInput loaded = PerfBenchmarkComparisonLoader.Load(_directory, input.Profile);

        loaded
            .LoadErrors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("pair 2 baseline: unreadable artifacts");
        PerfBenchmarkComparison
            .Compare(loaded)
            .ShouldBeInvalidBecause("pair 2 baseline: the planned side is missing");
    }
}
