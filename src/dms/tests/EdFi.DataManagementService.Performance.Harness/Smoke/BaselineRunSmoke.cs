// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The full pipeline end to end at smoke scale: load, measure all six cells at the epic's
/// minimum iteration counts, replay plans on a dedicated out-of-band connection, capture the
/// environment, assemble, write, and re-validate the reloaded artifacts. This produces
/// genuinely valid artifacts — the same code path the baseline capture uses, differing only
/// in fixture size and the externally supplied image pins.
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
        await PerfFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);

        long deepOffset = definition.RowCount * 9 / 10;
        const int warmupIterations = PerfRunConfigurationLoader.MinimumWarmupIterations;
        const int measuredIterations = PerfRunConfigurationLoader.MinimumMeasuredIterations;

        IReadOnlyList<PerfMeasuredCell> cells = await PerfScenarioExecutor.RunAsync(
            harness,
            provider,
            deepOffset,
            warmupIterations,
            measuredIterations
        );

        string providerName = PerfProviders.ArtifactName(provider);
        List<PerfCellEvidence> evidence = [];
        PerfEnvironmentIdentity environment;

        await using (DbConnection replayConnection = await openReplayConnectionAsync())
        {
            foreach (PerfMeasuredCell cell in cells)
            {
                evidence.Add(await CaptureCellEvidenceAsync(replayConnection, provider, providerName, cell));
            }

            environment = await PerfEnvironmentCapture.CaptureAsync(
                replayConnection,
                provider,
                imageTag,
                imageDigest,
                storageNote,
                leasedConnectionString
            );
        }

        string headCommit = GitIdentity.HeadCommit(AppContext.BaseDirectory);
        PerfAssembledRun assembled = PerfBaselineArtifactAssembler.Assemble(
            provider,
            definition,
            deepOffset,
            warmupIterations,
            measuredIterations,
            evidence,
            new PerfRunIdentity(
                $"{providerName}-{definition.Kind.Id}-smoke",
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                providerName
            ),
            new PerfCommitIdentity(headCommit, headCommit, GitIdentity.DirtyPaths(AppContext.BaseDirectory)),
            environment
        );

        string resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-perf-harness-smoke",
            $"{providerName}-{Guid.NewGuid():N}"
        );
        PerfRunArtifactWriter.Write(
            resultsDirectory,
            assembled.Manifest,
            assembled.Results,
            assembled.FixtureManifest,
            assembled.AuxiliaryFiles
        );

        PerfRunManifest reloadedManifest = PerfArtifactJson.Deserialize<PerfRunManifest>(
            await File.ReadAllTextAsync(Path.Combine(resultsDirectory, "run-manifest.json"))
        );
        PerfResultsDocument reloadedResults = PerfArtifactJson.Deserialize<PerfResultsDocument>(
            await File.ReadAllTextAsync(Path.Combine(resultsDirectory, "results.json"))
        );
        PerfArtifactValidator.EnsureValid(reloadedManifest, reloadedResults);

        (await File.ReadAllLinesAsync(Path.Combine(resultsDirectory, "results.csv"))).Should().HaveCount(7);
        File.Exists(Path.Combine(resultsDirectory, "fixture-manifest.json")).Should().BeTrue();
        Directory
            .GetFiles(Path.Combine(resultsDirectory, "plans"))
            .Should()
            .HaveCount(provider == PerfProvider.Postgresql ? 6 : 12);
        Directory.GetFiles(Path.Combine(resultsDirectory, "sql")).Should().HaveCount(3);

        await TestContext.Out.WriteLineAsync($"Validated artifacts written to {resultsDirectory}");
    }

    private static async Task<PerfCellEvidence> CaptureCellEvidenceAsync(
        DbConnection replayConnection,
        PerfProvider provider,
        string providerName,
        PerfMeasuredCell cell
    )
    {
        string baseName = $"plans/{providerName}.{cell.ScenarioId}.{cell.PageSize}";
        if (provider == PerfProvider.Postgresql)
        {
            PgsqlPlanCaptureResult capture = await PgsqlPlanCapture.CaptureAsync(
                replayConnection,
                cell.PageSelection
            );
            string planFile = $"{baseName}.explain.json";
            return new PerfCellEvidence(
                cell,
                capture.Metrics,
                planFile,
                [new PerfArtifactFile(planFile, capture.ExplainJson)]
            );
        }

        MssqlPlanCaptureResult mssqlCapture = await MssqlPlanCapture.CaptureAsync(
            replayConnection,
            cell.PageSelection
        );
        string sqlPlanFile = $"{baseName}.sqlplan";
        return new PerfCellEvidence(
            cell,
            mssqlCapture.Metrics,
            sqlPlanFile,
            [
                new PerfArtifactFile(sqlPlanFile, mssqlCapture.ShowplanXml),
                new PerfArtifactFile($"{baseName}.stats.txt", mssqlCapture.StatisticsText),
            ]
        );
    }
}
