// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// The evidence directories the report step evaluates for one provider: the DMS-1391
/// traditional baseline, the final-gate primary run, and the final-gate descriptor run.
/// </summary>
public sealed record PerfFinalGateReportProviderDirectories(
    string Provider,
    string BaselineDirectory,
    string PrimaryDirectory,
    string DescriptorsDirectory
);

/// <summary>
/// Environment-supplied settings for the report step: where to write final-report.md/json
/// and which evidence directories to evaluate. A provider participates only when all three
/// of its directory variables are set — a partial triplet is a configuration error, never a
/// silently narrowed evaluation — and at least one provider is required. A missing provider
/// is legitimate (the evaluator reports it as inconclusive coverage).
/// </summary>
public sealed record PerfFinalGateReportSettings(
    string ReportDirectory,
    IReadOnlyList<PerfFinalGateReportProviderDirectories> Providers
)
{
    private static readonly IReadOnlyList<(
        string Provider,
        string BaselineVariable,
        string PrimaryVariable,
        string DescriptorsVariable
    )> _providerVariables =
    [
        (
            "postgresql",
            PerfEnvironmentVariables.BaselineDirectoryPostgresql,
            PerfEnvironmentVariables.FinalPrimaryDirectoryPostgresql,
            PerfEnvironmentVariables.FinalDescriptorsDirectoryPostgresql
        ),
        (
            "mssql",
            PerfEnvironmentVariables.BaselineDirectoryMssql,
            PerfEnvironmentVariables.FinalPrimaryDirectoryMssql,
            PerfEnvironmentVariables.FinalDescriptorsDirectoryMssql
        ),
    ];

    public static PerfFinalGateReportSettings FromEnvironment() => Load(Environment.GetEnvironmentVariable);

    public static PerfFinalGateReportSettings Load(Func<string, string?> readVariable)
    {
        List<string> errors = [];

        // A variable holding only whitespace is treated as absent: a cleared environment
        // variable can survive as an empty string rather than disappearing.
        string? Read(string name)
        {
            string? value = readVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        string? reportDirectory = PerfRunConfigurationLoader.ReadFullyQualifiedDirectory(
            PerfEnvironmentVariables.ReportDirectory,
            Read,
            errors
        );

        List<PerfFinalGateReportProviderDirectories> providers = [];
        foreach (
            (
                string provider,
                string baselineVariable,
                string primaryVariable,
                string descriptorsVariable
            ) in _providerVariables
        )
        {
            string? baseline = Read(baselineVariable);
            string? primary = Read(primaryVariable);
            string? descriptors = Read(descriptorsVariable);
            if (baseline is null && primary is null && descriptors is null)
            {
                continue;
            }

            if (baseline is null || primary is null || descriptors is null)
            {
                errors.Add(
                    $"{provider}: {baselineVariable}, {primaryVariable}, and {descriptorsVariable} must "
                        + "either all be set or all be absent."
                );
                continue;
            }

            providers.Add(
                new PerfFinalGateReportProviderDirectories(provider, baseline, primary, descriptors)
            );
        }

        if (providers.Count == 0)
        {
            errors.Add("at least one provider's evidence directories must be set for the report step.");
        }

        if (errors.Count > 0)
        {
            throw new PerfConfigurationException(errors);
        }

        return new PerfFinalGateReportSettings(reportDirectory!, providers);
    }
}
