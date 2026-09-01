// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// A validated descriptor-run configuration: the final-gate descriptor fixture run has no
/// deep offset and selects its fixture from the descriptor catalog. Construct through
/// <see cref="Load" /> so every value has passed validation.
/// </summary>
public sealed record PerfDescriptorRunConfiguration(
    string ResultsDirectory,
    string RunnerCommit,
    PerfDescriptorFixtureKind Fixture,
    int WarmupIterations,
    int MeasuredIterations
)
{
    public static PerfDescriptorRunConfiguration FromEnvironment() =>
        Load(Environment.GetEnvironmentVariable);

    public static PerfDescriptorRunConfiguration Load(Func<string, string?> readVariable)
    {
        List<string> errors = [];

        // A variable holding only whitespace is treated as absent: a cleared environment
        // variable can survive as an empty string rather than disappearing.
        string? Read(string name)
        {
            string? value = readVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        string? resultsDirectory = PerfRunConfigurationLoader.ReadFullyQualifiedDirectory(
            PerfEnvironmentVariables.ResultsDirectory,
            Read,
            errors
        );
        string? runnerCommit = PerfRunConfigurationLoader.ReadRunnerCommit(Read, errors);

        PerfDescriptorFixtureKind fixture = PerfDescriptorFixtureKind.Descriptors25k;
        string? fixtureId = Read(PerfEnvironmentVariables.DescriptorFixture);
        if (fixtureId is not null)
        {
            PerfDescriptorFixtureKind? found = PerfDescriptorFixtureKind.FindById(fixtureId);
            if (found is null)
            {
                errors.Add(
                    $"{PerfEnvironmentVariables.DescriptorFixture} must be one of: "
                        + $"{string.Join(", ", PerfDescriptorFixtureKind.All.Select(known => known.Id))}; "
                        + $"got '{fixtureId}'."
                );
            }
            else
            {
                fixture = found;
            }
        }

        int warmupIterations = PerfRunConfigurationLoader.ReadIterations(
            PerfEnvironmentVariables.WarmupIterations,
            PerfRunConfigurationLoader.MinimumWarmupIterations,
            Read,
            errors
        );
        int measuredIterations = PerfRunConfigurationLoader.ReadIterations(
            PerfEnvironmentVariables.MeasuredIterations,
            PerfRunConfigurationLoader.MinimumMeasuredIterations,
            Read,
            errors
        );

        if (errors.Count > 0)
        {
            throw new PerfConfigurationException(errors);
        }

        return new PerfDescriptorRunConfiguration(
            resultsDirectory!,
            runnerCommit!,
            fixture,
            warmupIterations,
            measuredIterations
        );
    }
}
