// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

[TestFixture]
public class Given_The_Descriptor_Run_Configuration_Loader
{
    private static readonly string _resultsDirectory = Path.Combine(Path.GetTempPath(), "perf-results");

    private static Dictionary<string, string?> ValidVariables() =>
        new()
        {
            [PerfEnvironmentVariables.ResultsDirectory] = _resultsDirectory,
            [PerfEnvironmentVariables.RunnerCommit] = new string('a', 40),
        };

    private static PerfDescriptorRunConfiguration Load(Dictionary<string, string?> variables) =>
        PerfDescriptorRunConfiguration.Load(name => variables.GetValueOrDefault(name));

    [Test]
    public void It_defaults_to_the_25k_fixture_and_minimum_iterations()
    {
        PerfDescriptorRunConfiguration configuration = Load(ValidVariables());

        configuration.Fixture.Should().Be(PerfDescriptorFixtureKind.Descriptors25k);
        configuration.WarmupIterations.Should().Be(PerfRunConfigurationLoader.MinimumWarmupIterations);
        configuration.MeasuredIterations.Should().Be(PerfRunConfigurationLoader.MinimumMeasuredIterations);
        configuration.ResultsDirectory.Should().Be(_resultsDirectory);
        configuration.RunnerCommit.Should().Be(new string('a', 40));
    }

    [Test]
    public void It_accepts_the_smoke_descriptor_fixture_and_raised_iterations()
    {
        Dictionary<string, string?> variables = ValidVariables();
        variables[PerfEnvironmentVariables.DescriptorFixture] = "descriptors-smoke-2k";
        variables[PerfEnvironmentVariables.WarmupIterations] = "8";
        variables[PerfEnvironmentVariables.MeasuredIterations] = "40";

        PerfDescriptorRunConfiguration configuration = Load(variables);

        configuration.Fixture.Should().Be(PerfDescriptorFixtureKind.DescriptorsSmoke2k);
        configuration.WarmupIterations.Should().Be(8);
        configuration.MeasuredIterations.Should().Be(40);
    }

    [Test]
    public void It_rejects_an_unknown_descriptor_fixture()
    {
        Dictionary<string, string?> variables = ValidVariables();
        variables[PerfEnvironmentVariables.DescriptorFixture] = "primary-500k";

        Action act = () => Load(variables);

        act.Should().Throw<PerfConfigurationException>().WithMessage("*PERF_DESCRIPTOR_FIXTURE*");
    }

    [Test]
    public void It_rejects_lowered_iterations()
    {
        Dictionary<string, string?> variables = ValidVariables();
        variables[PerfEnvironmentVariables.MeasuredIterations] = "5";

        Action act = () => Load(variables);

        act.Should().Throw<PerfConfigurationException>().WithMessage("*at least 30*");
    }

    [Test]
    public void It_reports_every_missing_required_variable_at_once()
    {
        Action act = () => Load([]);

        act.Should()
            .Throw<PerfConfigurationException>()
            .WithMessage("*PERF_RESULTS_DIR*")
            .WithMessage("*PERF_RUNNER_COMMIT*");
    }
}
