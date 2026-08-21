// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

internal static class LoaderTestValues
{
    // Computed at runtime so the path is fully qualified on whichever platform runs the
    // tests. CI unit tests run on ubuntu-latest while development happens on Windows.
    public static readonly string FullyQualifiedResultsDirectory = Path.Combine(
        Path.GetTempPath(),
        "perf-results"
    );

    public const string BaselineCommit = "5656477957eb2f18e827b7969e5079b424596ae0";

    public static Dictionary<string, string?> Valid() =>
        new()
        {
            [PerfEnvironmentVariables.ResultsDirectory] = FullyQualifiedResultsDirectory,
            [PerfEnvironmentVariables.RunnerCommit] = BaselineCommit,
            [PerfEnvironmentVariables.Fixture] = PerfFixtureKind.Primary500k.Id,
        };

    public static Func<string, string?> ReaderFor(Dictionary<string, string?> values) =>
        name => values.TryGetValue(name, out string? value) ? value : null;
}

[TestFixture]
public class Given_A_Complete_Valid_Configuration
{
    private PerfRunConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = PerfRunConfigurationLoader.Load(
            LoaderTestValues.ReaderFor(LoaderTestValues.Valid())
        );
    }

    [Test]
    public void It_parses_the_results_directory()
    {
        _configuration.ResultsDirectory.Should().Be(LoaderTestValues.FullyQualifiedResultsDirectory);
    }

    [Test]
    public void It_parses_the_runner_commit()
    {
        _configuration.RunnerCommit.Should().Be(LoaderTestValues.BaselineCommit);
    }

    [Test]
    public void It_parses_the_fixture()
    {
        _configuration.Fixture.Should().Be(PerfFixtureKind.Primary500k);
    }

    [Test]
    public void It_defaults_warmup_iterations_to_the_minimum()
    {
        _configuration.WarmupIterations.Should().Be(PerfRunConfigurationLoader.MinimumWarmupIterations);
    }

    [Test]
    public void It_defaults_measured_iterations_to_the_minimum()
    {
        _configuration.MeasuredIterations.Should().Be(PerfRunConfigurationLoader.MinimumMeasuredIterations);
    }

    [Test]
    public void It_defaults_the_deep_offset_to_ninety_percent_of_the_fixture()
    {
        _configuration.DeepOffset.Should().Be(450_000);
    }
}

[TestFixture]
public class Given_An_Uppercase_Runner_Commit
{
    private PerfRunConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.RunnerCommit] = LoaderTestValues.BaselineCommit.ToUpperInvariant();
        _configuration = PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values));
    }

    [Test]
    public void It_normalizes_the_runner_commit_to_lowercase()
    {
        _configuration.RunnerCommit.Should().Be(LoaderTestValues.BaselineCommit);
    }
}

[TestFixture]
public class Given_Explicit_Valid_Overrides
{
    private PerfRunConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.Fixture] = PerfFixtureKind.Smoke10k.Id;
        values[PerfEnvironmentVariables.WarmupIterations] = "10";
        values[PerfEnvironmentVariables.MeasuredIterations] = "60";
        values[PerfEnvironmentVariables.DeepOffset] = "9500";
        _configuration = PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values));
    }

    [Test]
    public void It_accepts_raised_warmup_iterations()
    {
        _configuration.WarmupIterations.Should().Be(10);
    }

    [Test]
    public void It_accepts_raised_measured_iterations()
    {
        _configuration.MeasuredIterations.Should().Be(60);
    }

    [Test]
    public void It_accepts_an_in_bounds_deep_offset()
    {
        _configuration.DeepOffset.Should().Be(9_500);
    }

    [Test]
    public void It_parses_the_smoke_fixture()
    {
        _configuration.Fixture.Should().Be(PerfFixtureKind.Smoke10k);
    }
}

[TestFixture]
public class Given_The_Smoke_Fixture_Without_A_Deep_Offset
{
    private PerfRunConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.Fixture] = PerfFixtureKind.Smoke10k.Id;
        _configuration = PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values));
    }

    [Test]
    public void It_scales_the_default_deep_offset_to_the_fixture()
    {
        _configuration.DeepOffset.Should().Be(9_000);
    }
}

[TestFixture]
public class Given_Missing_Required_Values
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(_ => null)
        );
    }

    [Test]
    public void It_reports_the_results_directory()
    {
        _exception.Errors.Should().Contain($"{PerfEnvironmentVariables.ResultsDirectory} is required.");
    }

    [Test]
    public void It_reports_the_runner_commit()
    {
        _exception.Errors.Should().Contain($"{PerfEnvironmentVariables.RunnerCommit} is required.");
    }

    [Test]
    public void It_reports_the_fixture()
    {
        _exception.Errors.Should().Contain($"{PerfEnvironmentVariables.Fixture} is required.");
    }

    [Test]
    public void It_reports_only_the_required_values()
    {
        _exception.Errors.Should().HaveCount(3);
    }
}

[TestFixture]
public class Given_Blank_Required_Values
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        // A cleared environment variable can survive as whitespace rather than disappearing.
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(_ => "   ")
        );
    }

    [Test]
    public void It_treats_blank_as_absent()
    {
        _exception
            .Errors.Should()
            .BeEquivalentTo(
                $"{PerfEnvironmentVariables.ResultsDirectory} is required.",
                $"{PerfEnvironmentVariables.RunnerCommit} is required.",
                $"{PerfEnvironmentVariables.Fixture} is required."
            );
    }
}

[TestFixture]
public class Given_A_Relative_Results_Directory
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.ResultsDirectory] = "relative/results";
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values))
        );
    }

    [Test]
    public void It_requires_a_fully_qualified_path()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.ResultsDirectory) && error.Contains("absolute path")
            );
    }
}

[TestFixture]
public class Given_A_Drive_Relative_Results_Directory
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        // Drive-relative on Windows, and not fully qualified on Unix either, so this
        // rejection holds on both platforms.
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.ResultsDirectory] = "C:perf-results";
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values))
        );
    }

    [Test]
    public void It_rejects_the_drive_relative_form()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.ResultsDirectory) && error.Contains("fully qualified")
            );
    }
}

[TestFixture]
[Platform("Win")]
public class Given_A_Root_Relative_Results_Directory_On_Windows
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        // "/perf-results" resolves against the current drive on Windows; on Unix the same
        // string is a fully qualified path, so this fixture is Windows-only.
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.ResultsDirectory] = "/perf-results";
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values))
        );
    }

    [Test]
    public void It_rejects_the_root_relative_form()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.ResultsDirectory) && error.Contains("fully qualified")
            );
    }
}

[TestFixture]
public class Given_A_Malformed_Runner_Commit
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.RunnerCommit] = "not-a-sha";
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values))
        );
    }

    [Test]
    public void It_requires_a_forty_hex_sha()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.RunnerCommit) && error.Contains("40-character")
            );
    }
}

[TestFixture]
public class Given_An_Unknown_Fixture
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.Fixture] = "primary-1m";
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values))
        );
    }

    [Test]
    public void It_names_the_known_fixtures()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.Fixture)
                && error.Contains(PerfFixtureKind.Primary500k.Id)
                && error.Contains(PerfFixtureKind.Smoke10k.Id)
            );
    }
}

[TestFixture]
public class Given_Iterations_Below_The_Minimums
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.WarmupIterations] = "4";
        values[PerfEnvironmentVariables.MeasuredIterations] = "29";
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values))
        );
    }

    [Test]
    public void It_rejects_lowered_warmup_iterations()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.WarmupIterations) && error.Contains("at least 5")
            );
    }

    [Test]
    public void It_rejects_lowered_measured_iterations()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.MeasuredIterations) && error.Contains("at least 30")
            );
    }
}

[TestFixture]
public class Given_Non_Numeric_Iterations_And_Offset
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.WarmupIterations] = "five";
        values[PerfEnvironmentVariables.MeasuredIterations] = "3.5";
        values[PerfEnvironmentVariables.DeepOffset] = "-1";
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values))
        );
    }

    [Test]
    public void It_rejects_a_non_numeric_warmup()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error => error.Contains(PerfEnvironmentVariables.WarmupIterations));
    }

    [Test]
    public void It_rejects_a_non_integer_measured_count()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error => error.Contains(PerfEnvironmentVariables.MeasuredIterations));
    }

    [Test]
    public void It_rejects_a_negative_deep_offset()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.DeepOffset) && error.Contains("non-negative integer")
            );
    }
}

[TestFixture]
public class Given_A_Deep_Offset_Beyond_The_Fixture
{
    private PerfConfigurationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.DeepOffset] = "499501";
        _exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values))
        );
    }

    [Test]
    public void It_bounds_the_deep_offset_so_a_full_page_fits()
    {
        _exception
            .Errors.Should()
            .ContainSingle(error =>
                error.Contains(PerfEnvironmentVariables.DeepOffset) && error.Contains("499500")
            );
    }
}

[TestFixture]
public class Given_The_Last_Valid_Deep_Offset
{
    private PerfRunConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        Dictionary<string, string?> values = LoaderTestValues.Valid();
        values[PerfEnvironmentVariables.DeepOffset] = "499500";
        _configuration = PerfRunConfigurationLoader.Load(LoaderTestValues.ReaderFor(values));
    }

    [Test]
    public void It_accepts_the_boundary_value()
    {
        _configuration.DeepOffset.Should().Be(499_500);
    }
}

[TestFixture]
public class Given_A_Fixture_Too_Small_For_Its_Default_Deep_Offset
{
    // Synthetic: no catalog fixture is this small, but the computed default must flow
    // through the same bound check an explicit value gets, so the failure mode stays
    // reachable and tested.
    private static readonly PerfFixtureKind _tiny = new("tiny-100", 100);

    [Test]
    public void Its_computed_default_fails_the_shared_bound_check()
    {
        long defaultDeepOffset = PerfRunConfigurationLoader.DefaultDeepOffset(_tiny);
        defaultDeepOffset.Should().Be(90);
        PerfRunConfigurationLoader.IsWithinDeepOffsetBounds(_tiny, defaultDeepOffset).Should().BeFalse();
    }
}

[TestFixture]
public class Given_The_Catalog_Fixtures
{
    [Test]
    public void Every_computed_default_deep_offset_is_within_bounds()
    {
        foreach (PerfFixtureKind fixture in PerfFixtureKind.All)
        {
            PerfRunConfigurationLoader
                .IsWithinDeepOffsetBounds(fixture, PerfRunConfigurationLoader.DefaultDeepOffset(fixture))
                .Should()
                .BeTrue(fixture.Id);
        }
    }
}
