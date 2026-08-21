// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The full evidence pipeline end to end at smoke scale: the same
/// <see cref="PerfBaselineRunPipeline" /> the baseline capture uses, differing only in the
/// 10,000-row fixture, temp-directory output, relaxed guardrails (a development tree is
/// dirty), and literal image pins. Asserts the written artifact set's file inventory on top
/// of the pipeline's own reload-and-validate step.
/// </summary>
internal static class BaselineRunSmoke
{
    public static async Task RunAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        string imageTag,
        string imageDigest,
        string storageNote
    )
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        string resultsDirectoryBase = Path.Combine(
            Path.GetTempPath(),
            "dms-perf-harness-smoke",
            Guid.NewGuid().ToString("N")
        );

        string runDirectory = await PerfBaselineRunPipeline.RunAsync(
            harness,
            provider,
            openReplayConnectionAsync,
            leasedConnectionString,
            definition,
            deepOffset: definition.RowCount * 9 / 10,
            warmupIterations: PerfRunConfigurationLoader.MinimumWarmupIterations,
            measuredIterations: PerfRunConfigurationLoader.MinimumMeasuredIterations,
            resultsDirectoryBase,
            runnerCommit: GitIdentity.HeadCommit(AppContext.BaseDirectory),
            new PerfEvidenceRunSettings(
                imageTag,
                imageDigest,
                storageNote,
                AllowCi: true,
                AllowedDirtyPrefixes: [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix],
                AllowAnyDirtyPath: true
            )
        );

        (await File.ReadAllLinesAsync(Path.Combine(runDirectory, "results.csv"))).Should().HaveCount(7);
        File.Exists(Path.Combine(runDirectory, "fixture-manifest.json")).Should().BeTrue();
        AssertPlanEvidence(runDirectory, provider);
        Directory.GetFiles(Path.Combine(runDirectory, "sql")).Should().HaveCount(3);

        await TestContext.Out.WriteLineAsync($"Validated artifacts written to {runDirectory}");
    }

    /// <summary>
    /// PostgreSQL writes one self-contained explain document per cell. SQL Server writes one
    /// plan index per cell; every file the six indexes reference must exist, and together
    /// with the indexes they must account for the whole plans directory.
    /// </summary>
    private static void AssertPlanEvidence(string runDirectory, PerfProvider provider)
    {
        string plansDirectory = Path.Combine(runDirectory, "plans");
        if (provider == PerfProvider.Postgresql)
        {
            Directory.GetFiles(plansDirectory, "*.explain.json").Should().HaveCount(6);
            Directory.GetFiles(plansDirectory).Should().HaveCount(6);
            return;
        }

        string[] indexFiles = Directory.GetFiles(plansDirectory, "*.plans.json");
        indexFiles.Should().HaveCount(6);
        List<string> referencedPaths = [];
        foreach (string indexFile in indexFiles)
        {
            JsonNode index = JsonNode.Parse(File.ReadAllText(indexFile))!;
            List<string> planFiles =
            [
                .. index["planFiles"]!.AsArray().Select(node => node!.GetValue<string>()),
            ];
            planFiles.Should().NotBeEmpty();
            referencedPaths.AddRange(planFiles);
            referencedPaths.Add(index["statisticsFile"]!.GetValue<string>());
        }

        referencedPaths.Should().OnlyHaveUniqueItems();
        foreach (string referencedPath in referencedPaths)
        {
            File.Exists(Path.Combine(runDirectory, referencedPath)).Should().BeTrue(referencedPath);
        }

        Directory.GetFiles(plansDirectory).Should().HaveCount(indexFiles.Length + referencedPaths.Count);
    }
}
