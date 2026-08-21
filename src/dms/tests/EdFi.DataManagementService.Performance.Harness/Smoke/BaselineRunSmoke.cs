// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
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
                AllowedDirtyPrefixes: [""]
            )
        );

        (await File.ReadAllLinesAsync(Path.Combine(runDirectory, "results.csv"))).Should().HaveCount(7);
        File.Exists(Path.Combine(runDirectory, "fixture-manifest.json")).Should().BeTrue();
        Directory
            .GetFiles(Path.Combine(runDirectory, "plans"))
            .Should()
            .HaveCount(provider == PerfProvider.Postgresql ? 6 : 12);
        Directory.GetFiles(Path.Combine(runDirectory, "sql")).Should().HaveCount(3);

        await TestContext.Out.WriteLineAsync($"Validated artifacts written to {runDirectory}");
    }
}
