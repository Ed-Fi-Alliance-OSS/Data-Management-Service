// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The complete baseline run: guardrails, fixture load and verification, the six measured
/// cells, plan replay on a dedicated out-of-band connection, environment capture, assembly,
/// and validated artifact writing. Guardrails run first — a CI database on tmpfs or a
/// contaminated subject worktree must fail before any measurement happens, because artifacts
/// from such a run could pass every later check while resting on an invalid environment.
/// </summary>
public static class PerfBaselineRunPipeline
{
    public static async Task<string> RunAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        PerfFixtureDefinition definition,
        long deepOffset,
        int warmupIterations,
        int measuredIterations,
        string resultsDirectoryBase,
        string runnerCommit,
        PerfEvidenceRunSettings settings
    )
    {
        GuardCiEnvironment(settings.AllowCi, Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        string subjectCommit = GitIdentity.HeadCommit(AppContext.BaseDirectory);
        IReadOnlyList<string> dirtyPaths = GitIdentity.DirtyPaths(AppContext.BaseDirectory);
        if (!settings.AllowAnyDirtyPath)
        {
            GuardDirtyPaths(dirtyPaths, settings.AllowedDirtyPrefixes);
        }

        await PerfFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);

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
                settings.ImageTag,
                settings.ImageDigest,
                settings.StorageNote,
                leasedConnectionString
            );
        }

        DateTime capturedAt = DateTime.UtcNow;
        string runId =
            $"{providerName}-{definition.Kind.Id}-{capturedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        PerfAssembledRun assembled = PerfBaselineArtifactAssembler.Assemble(
            provider,
            definition,
            deepOffset,
            warmupIterations,
            measuredIterations,
            evidence,
            new PerfRunIdentity(
                runId,
                capturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                providerName
            ),
            new PerfCommitIdentity(runnerCommit, subjectCommit, dirtyPaths),
            environment
        );

        string runDirectory = Path.Combine(resultsDirectoryBase, runId);
        PerfRunArtifactWriter.Write(
            runDirectory,
            assembled.Manifest,
            assembled.Results,
            assembled.FixtureManifest,
            assembled.AuxiliaryFiles
        );

        // Reload what was written and validate again: the committed evidence is the files,
        // not the in-memory objects.
        PerfRunManifest reloadedManifest = PerfArtifactJson.Deserialize<PerfRunManifest>(
            await File.ReadAllTextAsync(Path.Combine(runDirectory, "run-manifest.json"))
        );
        PerfResultsDocument reloadedResults = PerfArtifactJson.Deserialize<PerfResultsDocument>(
            await File.ReadAllTextAsync(Path.Combine(runDirectory, "results.json"))
        );
        PerfArtifactValidator.EnsureValid(reloadedManifest, reloadedResults);

        return runDirectory;
    }

    public static void GuardCiEnvironment(bool allowCi, string? gitHubActionsValue)
    {
        if (!allowCi && !string.IsNullOrWhiteSpace(gitHubActionsValue))
        {
            throw new PerfObservationException(
                "Refusing to run on CI: its databases run on tmpfs, which invalidates "
                    + "I/O-sensitive measurement. Set PERF_ALLOW_CI=true only for non-evidence runs."
            );
        }
    }

    /// <summary>
    /// A dirty path is allowed only when it equals an allowed prefix or sits below it across
    /// a path-segment boundary. A raw prefix match would accept sibling directories that
    /// merely share the prefix text, such as the Tests.Unit project beside the harness.
    /// </summary>
    public static void GuardDirtyPaths(
        IReadOnlyList<string> dirtyPaths,
        IReadOnlyList<string> allowedPrefixes
    )
    {
        List<string> violations = [];
        foreach (string dirtyPath in dirtyPaths)
        {
            string normalizedPath = dirtyPath.Replace('\\', '/').TrimEnd('/');
            bool allowed = allowedPrefixes.Any(prefix =>
            {
                string normalizedPrefix = prefix.Replace('\\', '/').TrimEnd('/');
                return normalizedPath == normalizedPrefix
                    || normalizedPath.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal);
            });
            if (!allowed)
            {
                violations.Add(dirtyPath);
            }
        }

        if (violations.Count > 0)
        {
            throw new PerfObservationException(
                "The subject worktree is dirty outside the approved overlay: " + string.Join(", ", violations)
            );
        }
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
                cell.HydrationBatchSql,
                cell.PageSelection.ParameterValues
            );
            string planFile = $"{baseName}.explain.json";
            return new PerfCellEvidence(
                cell,
                capture.Metrics,
                planFile,
                [new PerfArtifactFile(planFile, capture.PlanArtifactJson)]
            );
        }

        MssqlPlanCaptureResult mssqlCapture = await MssqlPlanCapture.CaptureAsync(
            replayConnection,
            cell.HydrationBatchSql,
            cell.PageSelection.ParameterValues
        );
        List<string> sqlPlanFiles =
        [
            .. mssqlCapture.ShowplanXmlDocuments.Select(
                (_, index) => $"{baseName}.plan{index + 1:D2}.sqlplan"
            ),
        ];
        string statisticsFile = $"{baseName}.stats.txt";
        string planIndexFile = $"{baseName}.plans.json";
        return new PerfCellEvidence(
            cell,
            mssqlCapture.Metrics,
            planIndexFile,
            [
                new PerfArtifactFile(
                    planIndexFile,
                    MssqlPlanCapture.PlanIndexJson(sqlPlanFiles, statisticsFile)
                ),
                .. sqlPlanFiles.Select(
                    (path, index) => new PerfArtifactFile(path, mssqlCapture.ShowplanXmlDocuments[index])
                ),
                new PerfArtifactFile(statisticsFile, mssqlCapture.StatisticsText),
            ]
        );
    }
}
