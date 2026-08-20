// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// Reads and validates the harness run configuration from environment variables, reporting
/// every validation error at once through <see cref="PerfConfigurationException" />.
/// </summary>
public static class PerfRunConfigurationLoader
{
    public const int MinimumWarmupIterations = 5;
    public const int MinimumMeasuredIterations = 30;

    public static PerfRunConfiguration FromEnvironment() => Load(Environment.GetEnvironmentVariable);

    public static PerfRunConfiguration Load(Func<string, string?> readVariable)
    {
        List<string> errors = [];

        // A variable holding only whitespace is treated as absent: a cleared environment
        // variable can survive as an empty string rather than disappearing.
        string? Read(string name)
        {
            string? value = readVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        string? resultsDirectory = Read(PerfEnvironmentVariables.ResultsDirectory);
        if (resultsDirectory is null)
        {
            errors.Add($"{PerfEnvironmentVariables.ResultsDirectory} is required.");
        }
        else if (!Path.IsPathFullyQualified(resultsDirectory))
        {
            // Merely rooted is not enough: on Windows, drive-relative (C:results) and
            // root-relative (\results) paths still resolve against ambient process state,
            // which could scatter evidence artifacts somewhere unintended.
            errors.Add(
                $"{PerfEnvironmentVariables.ResultsDirectory} must be a fully qualified absolute path; got '{resultsDirectory}'."
            );
        }

        string? runnerCommit = Read(PerfEnvironmentVariables.RunnerCommit);
        if (runnerCommit is null)
        {
            errors.Add($"{PerfEnvironmentVariables.RunnerCommit} is required.");
        }
        else if (!IsFortyHexCharacters(runnerCommit))
        {
            errors.Add(
                $"{PerfEnvironmentVariables.RunnerCommit} must be a 40-character hex commit SHA; got '{runnerCommit}'."
            );
        }
        else
        {
            runnerCommit = runnerCommit.ToLowerInvariant();
        }

        PerfFixtureKind? fixture = null;
        string? fixtureId = Read(PerfEnvironmentVariables.Fixture);
        if (fixtureId is null)
        {
            errors.Add($"{PerfEnvironmentVariables.Fixture} is required.");
        }
        else
        {
            fixture = PerfFixtureKind.FindById(fixtureId);
            if (fixture is null)
            {
                errors.Add(
                    $"{PerfEnvironmentVariables.Fixture} must be one of: "
                        + $"{string.Join(", ", PerfFixtureKind.All.Select(known => known.Id))}; got '{fixtureId}'."
                );
            }
        }

        int warmupIterations = ReadIterations(
            PerfEnvironmentVariables.WarmupIterations,
            MinimumWarmupIterations,
            Read,
            errors
        );

        int measuredIterations = ReadIterations(
            PerfEnvironmentVariables.MeasuredIterations,
            MinimumMeasuredIterations,
            Read,
            errors
        );

        long deepOffset = 0;
        string? deepOffsetText = Read(PerfEnvironmentVariables.DeepOffset);
        if (deepOffsetText is null)
        {
            if (fixture is not null)
            {
                deepOffset = fixture.RowCount * 9 / 10;
            }
        }
        else if (
            !long.TryParse(deepOffsetText, NumberStyles.None, CultureInfo.InvariantCulture, out deepOffset)
        )
        {
            errors.Add(
                $"{PerfEnvironmentVariables.DeepOffset} must be a non-negative integer; got '{deepOffsetText}'."
            );
        }
        else if (fixture is not null && deepOffset > fixture.RowCount - PerfScenarios.MaximumPageSize)
        {
            errors.Add(
                $"{PerfEnvironmentVariables.DeepOffset} must be between 0 and "
                    + $"{fixture.RowCount - PerfScenarios.MaximumPageSize} for fixture '{fixture.Id}'; got {deepOffset}."
            );
        }

        if (errors.Count > 0)
        {
            throw new PerfConfigurationException(errors);
        }

        return new PerfRunConfiguration(
            resultsDirectory!,
            runnerCommit!,
            fixture!,
            warmupIterations,
            measuredIterations,
            deepOffset
        );
    }

    private static int ReadIterations(
        string variableName,
        int minimum,
        Func<string, string?> read,
        List<string> errors
    )
    {
        string? text = read(variableName);
        if (text is null)
        {
            return minimum;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            errors.Add($"{variableName} must be a positive integer; got '{text}'.");
            return minimum;
        }

        if (value < minimum)
        {
            errors.Add($"{variableName} must be at least {minimum}; got {value}.");
            return minimum;
        }

        return value;
    }

    private static bool IsFortyHexCharacters(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
}
