// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// Live smoke for the DocumentCache qualification pipeline. It keeps the representative
/// public entry point strict, but exercises the same lifecycle phases with the 10k fixture
/// and reduced write/contention counts so developers can verify phase artifacts locally.
/// </summary>
internal static class DocumentCacheQualificationRunSmoke
{
    private const int SmokePageSize = 1_000;
    private const int SmokeProjectorConcurrency = 2;
    private const int SmokeWarmupSamples = 0;
    private const int SmokeMeasuredSamples = 2;
    private const int SmokeOutageWrites = 3;
    private const int SmokeSameDocumentContenders = 2;

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
        PerfFixtureKind fixture = PerfFixtureKind.Smoke10k;
        string providerName = PerfProviders.ArtifactName(provider);
        string resultsDirectoryBase = ResultsDirectoryBase();
        string operatorMetricsFile = Path.Combine(resultsDirectoryBase, "operator-cpu-io.json");
        Directory.CreateDirectory(resultsDirectoryBase);
        await File.WriteAllTextAsync(
            operatorMetricsFile,
            PerfArtifactJson.Serialize(
                DocumentCacheOperatorMetricsEvidence.CreateSample(
                    PerfProviders.ArtifactName(PerfProvider.Postgresql),
                    PerfProviders.ArtifactName(PerfProvider.Mssql)
                )
            )
        );
        DocumentCacheRepresentativeRunConfiguration configuration = new(
            provider,
            resultsDirectoryBase,
            GitIdentity.HeadCommit(AppContext.BaseDirectory),
            fixture,
            SmokePageSize,
            fixture.RowCount,
            SmokeProjectorConcurrency,
            SmokeWarmupSamples,
            SmokeMeasuredSamples,
            SmokeOutageWrites,
            SmokeSameDocumentContenders,
            operatorMetricsFile,
            OperatorNote: "Smoke-scale DocumentCache qualification pipeline run.",
            new PerfEvidenceRunSettings(
                imageTag,
                imageDigest,
                storageNote,
                AllowCi: true,
                AllowedDirtyPrefixes: [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix],
                AllowAnyDirtyPath: true
            )
        );

        string runDirectory = await DocumentCacheQualificationRunPipeline.RunSmokeAsync(
            harness,
            provider,
            openReplayConnectionAsync,
            leasedConnectionString,
            configuration
        );

        AssertRequiredPhaseArtifacts(runDirectory, providerName);
        AssertInterruptedRestartEvidence(runDirectory, providerName);
        File.Exists(Path.Combine(runDirectory, "run-manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(runDirectory, "fixture-manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(runDirectory, "qualification-summary.md")).Should().BeTrue();
        File.Exists(Path.Combine(runDirectory, DocumentCacheOperatorMetricsEvidence.RelativePath))
            .Should()
            .BeTrue();
        File.Exists(
                Path.Combine(
                    runDirectory,
                    DocumentCacheProviderMetricSummary
                        .RelativePath(providerName)
                        .Replace('/', Path.DirectorySeparatorChar)
                )
            )
            .Should()
            .BeTrue();
        File.Exists(Path.Combine(runDirectory, "provider-metrics", "postgresql-wal-vacuum-bloat.md"))
            .Should()
            .Be(provider == PerfProvider.Postgresql);
        File.Exists(Path.Combine(runDirectory, "provider-metrics", "mssql-log-ghost-index.md"))
            .Should()
            .Be(provider == PerfProvider.Mssql);

        await TestContext.Out.WriteLineAsync(
            $"DocumentCache qualification smoke artifacts written to {runDirectory}"
        );
    }

    private static string ResultsDirectoryBase()
    {
        string? configured = Environment.GetEnvironmentVariable(PerfEnvironmentVariables.ResultsDirectory);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Path.GetTempPath(),
                "dms-document-cache-qualification-smoke",
                Guid.NewGuid().ToString("N")
            )
            : Path.GetFullPath(configured);
    }

    private static void AssertRequiredPhaseArtifacts(string runDirectory, string providerName)
    {
        IReadOnlyList<string> metricPaths = DocumentCacheQualificationRunPipeline.PhaseMetricRelativePaths(
            providerName
        );
        IReadOnlyList<string> transcriptPaths =
            DocumentCacheQualificationRunPipeline.CommandTranscriptRelativePaths(providerName);

        metricPaths.Should().HaveSameCount(transcriptPaths);
        foreach (string path in metricPaths.Concat(transcriptPaths))
        {
            File.Exists(Path.Combine(runDirectory, path)).Should().BeTrue(path);
        }
    }

    private static void AssertInterruptedRestartEvidence(string runDirectory, string providerName)
    {
        string metricPath = Path.Combine(
            runDirectory,
            "phase-metrics",
            $"{providerName}-interrupted-rebuild-restart-from-beginning.json"
        );
        JsonArray metrics = JsonNode.Parse(File.ReadAllText(metricPath))!["metrics"]!.AsArray();

        MetricValue(metrics, "interruptedLifecycleState").Should().Be("Rebuilding");
        MetricValue(metrics, "restartFromBeginningCompleted").Should().Be("True");
    }

    private static string MetricValue(JsonArray metrics, string name) =>
        metrics.Single(metric => metric!["name"]!.GetValue<string>() == name)!["value"]!.GetValue<string>();
}
