// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// Environment-driven configuration for the long-running DocumentCache representative
/// qualification entry points. Provider is supplied by the concrete NUnit fixture and may be
/// echoed through PERF_DOCUMENTCACHE_PROVIDER as a guard against launching the wrong fixture.
/// </summary>
public sealed record DocumentCacheRepresentativeRunConfiguration(
    PerfProvider Provider,
    string ResultsDirectory,
    string RunnerCommit,
    PerfFixtureKind Fixture,
    int PageSize,
    long HighWaterMark,
    int ProjectorConcurrency,
    int WarmupStatusSamples,
    int MeasuredStatusSamples,
    int OutageDistinctDocumentWrites,
    int SameDocumentContention,
    string OperatorMetricsFile,
    string? OperatorNote,
    PerfEvidenceRunSettings EvidenceSettings
);

public static class DocumentCacheRepresentativeRunConfigurationLoader
{
    public const int DefaultPageSize = 1_000;
    public const int DefaultProjectorConcurrency = 4;
    public const int DefaultWarmupStatusSamples = 5;
    public const int DefaultMeasuredStatusSamples = 30;

    public static DocumentCacheRepresentativeRunConfiguration FromEnvironment(PerfProvider provider) =>
        Load(provider, Environment.GetEnvironmentVariable);

    public static DocumentCacheRepresentativeRunConfiguration Load(
        PerfProvider provider,
        Func<string, string?> readVariable
    )
    {
        List<string> errors = [];

        string? Read(string name)
        {
            string? value = readVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        string? configuredProvider = Read(PerfEnvironmentVariables.DocumentCacheProvider);
        if (configuredProvider is not null)
        {
            try
            {
                PerfProvider parsedProvider = PerfProviders.Parse(configuredProvider);
                if (parsedProvider != provider)
                {
                    errors.Add(
                        $"{PerfEnvironmentVariables.DocumentCacheProvider} is '{configuredProvider}', but this fixture is for {PerfProviders.ArtifactName(provider)}."
                    );
                }
            }
            catch (ArgumentException)
            {
                errors.Add(
                    $"{PerfEnvironmentVariables.DocumentCacheProvider} must be 'postgresql' or 'mssql'; got '{configuredProvider}'."
                );
            }
        }

        string? resultsDirectory = Read(PerfEnvironmentVariables.ResultsDirectory);
        if (resultsDirectory is null)
        {
            errors.Add($"{PerfEnvironmentVariables.ResultsDirectory} is required.");
        }
        else if (!Path.IsPathFullyQualified(resultsDirectory))
        {
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

        PerfFixtureKind fixture = PerfFixtureKind.Primary500k;
        string? fixtureId = Read(PerfEnvironmentVariables.Fixture);
        if (fixtureId is not null)
        {
            PerfFixtureKind? parsedFixture = PerfFixtureKind.FindById(fixtureId);
            if (parsedFixture is null)
            {
                errors.Add(
                    $"{PerfEnvironmentVariables.Fixture} must be one of: "
                        + $"{string.Join(", ", PerfFixtureKind.All.Select(known => known.Id))}; got '{fixtureId}'."
                );
            }
            else
            {
                fixture = parsedFixture;
            }
        }

        int pageSize = ReadPositiveInt(
            PerfEnvironmentVariables.DocumentCachePageSize,
            DefaultPageSize,
            Read,
            errors
        );
        long highWaterMark = ReadPositiveLong(
            PerfEnvironmentVariables.DocumentCacheHighWaterMark,
            fixture.RowCount,
            Read,
            errors
        );
        int projectorConcurrency = ReadPositiveInt(
            PerfEnvironmentVariables.DocumentCacheProjectorConcurrency,
            DefaultProjectorConcurrency,
            Read,
            errors
        );
        int warmupStatusSamples = ReadNonNegativeInt(
            PerfEnvironmentVariables.DocumentCacheWarmupStatusSamples,
            DefaultWarmupStatusSamples,
            Read,
            errors
        );
        int measuredStatusSamples = ReadPositiveInt(
            PerfEnvironmentVariables.DocumentCacheMeasuredStatusSamples,
            DefaultMeasuredStatusSamples,
            Read,
            errors
        );
        int outageDistinctDocumentWrites = ReadPositiveInt(
            PerfEnvironmentVariables.DocumentCacheOutageWrites,
            DocumentCacheQualification.RepresentativeOutageDistinctDocumentWrites,
            Read,
            errors
        );
        int sameDocumentContention = ReadPositiveInt(
            PerfEnvironmentVariables.DocumentCacheSameDocumentContenders,
            DocumentCacheQualification.RepresentativeSameDocumentContention,
            Read,
            errors
        );
        string? operatorMetricsFile = Read(PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile);
        if (operatorMetricsFile is null)
        {
            errors.Add($"{PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile} is required.");
        }
        else if (!Path.IsPathFullyQualified(operatorMetricsFile))
        {
            errors.Add(
                $"{PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile} must be a fully qualified absolute path; got '{operatorMetricsFile}'."
            );
        }
        else if (!File.Exists(operatorMetricsFile))
        {
            errors.Add(
                $"{PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile} file does not exist: '{operatorMetricsFile}'."
            );
        }
        else
        {
            IReadOnlyList<string> metricsFailures = DocumentCacheOperatorMetricsEvidence.ValidateFile(
                operatorMetricsFile,
                PerfProviders.ArtifactName(provider)
            );
            errors.AddRange(
                metricsFailures.Select(failure =>
                    $"{PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile}: {failure}"
                )
            );
        }

        if (highWaterMark > fixture.RowCount)
        {
            errors.Add(
                $"{PerfEnvironmentVariables.DocumentCacheHighWaterMark} must be less than or equal to fixture row count {fixture.RowCount}; got {highWaterMark}."
            );
        }

        if (outageDistinctDocumentWrites > fixture.RowCount)
        {
            errors.Add(
                $"{PerfEnvironmentVariables.DocumentCacheOutageWrites} must be less than or equal to fixture row count {fixture.RowCount}; got {outageDistinctDocumentWrites}."
            );
        }

        PerfEvidenceRunSettings? evidenceSettings = null;
        try
        {
            evidenceSettings = PerfEvidenceRunSettings.Load(readVariable);
        }
        catch (PerfConfigurationException exception)
        {
            errors.AddRange(exception.Errors);
        }

        if (errors.Count > 0)
        {
            throw new PerfConfigurationException(errors);
        }

        return new DocumentCacheRepresentativeRunConfiguration(
            provider,
            resultsDirectory!,
            runnerCommit!,
            fixture,
            pageSize,
            highWaterMark,
            projectorConcurrency,
            warmupStatusSamples,
            measuredStatusSamples,
            outageDistinctDocumentWrites,
            sameDocumentContention,
            operatorMetricsFile!,
            Read(PerfEnvironmentVariables.OperatorNote),
            evidenceSettings!
        );
    }

    private static int ReadPositiveInt(
        string variableName,
        int defaultValue,
        Func<string, string?> read,
        List<string> errors
    )
    {
        string? text = read(variableName);
        if (text is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            errors.Add($"{variableName} must be a positive integer; got '{text}'.");
            return defaultValue;
        }

        if (value < 1)
        {
            errors.Add($"{variableName} must be at least 1; got {value}.");
            return defaultValue;
        }

        return value;
    }

    private static int ReadNonNegativeInt(
        string variableName,
        int defaultValue,
        Func<string, string?> read,
        List<string> errors
    )
    {
        string? text = read(variableName);
        if (text is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            errors.Add($"{variableName} must be a non-negative integer; got '{text}'.");
            return defaultValue;
        }

        if (value < 0)
        {
            errors.Add($"{variableName} must be non-negative; got {value}.");
            return defaultValue;
        }

        return value;
    }

    private static long ReadPositiveLong(
        string variableName,
        long defaultValue,
        Func<string, string?> read,
        List<string> errors
    )
    {
        string? text = read(variableName);
        if (text is null)
        {
            return defaultValue;
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
        {
            errors.Add($"{variableName} must be a positive integer; got '{text}'.");
            return defaultValue;
        }

        if (value < 1)
        {
            errors.Add($"{variableName} must be at least 1; got {value}.");
            return defaultValue;
        }

        return value;
    }

    private static bool IsFortyHexCharacters(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
}
