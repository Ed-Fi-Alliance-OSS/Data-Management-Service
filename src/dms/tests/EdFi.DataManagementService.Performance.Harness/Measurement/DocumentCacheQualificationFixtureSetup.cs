// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

public sealed record DocumentCacheQualificationFixtureSetupResult(
    string RunDirectory,
    DocumentCacheQualificationRunManifest RunManifest,
    PerfFixtureManifest FixtureManifest,
    DocumentCacheFixtureSetupMetrics FixtureSetupMetrics
);

/// <summary>
/// Loads and records the representative source fixture for the DocumentCache qualification
/// before the later lifecycle benchmark phases mutate cache/work state.
/// </summary>
public static class DocumentCacheQualificationFixtureSetup
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<DocumentCacheQualificationFixtureSetupResult> PrepareAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        DocumentCacheRepresentativeRunConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(openReplayConnectionAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(leasedConnectionString);
        ArgumentNullException.ThrowIfNull(configuration);

        GuardRepresentativeEvidenceEnvironment(
            configuration,
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS")
        );

        string subjectCommit = GitIdentity.HeadCommit(AppContext.BaseDirectory);
        IReadOnlyList<string> dirtyPaths = GitIdentity.DirtyPaths(AppContext.BaseDirectory);
        if (!configuration.EvidenceSettings.AllowAnyDirtyPath)
        {
            PerfBaselineRunPipeline.GuardDirtyPaths(
                dirtyPaths,
                configuration.EvidenceSettings.AllowedDirtyPrefixes
            );
        }

        PerfFixtureDefinition definition = new(PerfFixtureKind.Primary500k);
        await PerfFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);

        string providerName = PerfProviders.ArtifactName(provider);
        DateTime capturedAt = DateTime.UtcNow;
        string capturedAtUtc = capturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        DocumentCacheInitialTableCounts initialCounts = await CaptureInitialCountsAsync(
            harness.DbConnection,
            provider,
            providerName
        );
        PerfFixtureManifest fixtureManifest = PerfFixtureManifest.Create(definition);

        PerfEnvironmentIdentity environment;
        await using (DbConnection replayConnection = await openReplayConnectionAsync())
        {
            environment = await PerfEnvironmentCapture.CaptureAsync(
                replayConnection,
                provider,
                configuration.EvidenceSettings.ImageTag,
                configuration.EvidenceSettings.ImageDigest,
                configuration.EvidenceSettings.StorageNote,
                leasedConnectionString
            );
        }

        string runId =
            $"{providerName}-{definition.Kind.Id}-document-cache-{capturedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        string runDirectory = Path.Combine(configuration.ResultsDirectory, runId);
        DocumentCacheQualificationRunManifest runManifest = DocumentCacheQualificationRunManifest.Create(
            runId,
            capturedAtUtc,
            [provider],
            configuration.EvidenceSettings.StorageNote,
            configuration.RunnerCommit,
            subjectCommit,
            configuration.EvidenceSettings.AllowedDirtyPrefixes,
            dirtyPaths,
            [new DocumentCacheQualificationProviderIdentity(providerName, environment, initialCounts)],
            configuration.OperatorNote
        );
        DocumentCacheFixtureSetupMetrics fixtureSetupMetrics = DocumentCacheFixtureSetupMetrics.Create(
            providerName,
            capturedAtUtc,
            fixtureManifest,
            initialCounts
        );

        WriteFixtureSetupArtifacts(
            runDirectory,
            providerName,
            runManifest,
            fixtureManifest,
            fixtureSetupMetrics
        );

        return new DocumentCacheQualificationFixtureSetupResult(
            runDirectory,
            runManifest,
            fixtureManifest,
            fixtureSetupMetrics
        );
    }

    public static void GuardRepresentativeEvidenceEnvironment(
        DocumentCacheRepresentativeRunConfiguration configuration,
        string? gitHubActionsValue
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);

        GuardRepresentativeFixture(configuration.Fixture);
        PerfBaselineRunPipeline.GuardCiEnvironment(allowCi: false, gitHubActionsValue);
        GuardRepresentativeStorageNote(configuration.EvidenceSettings.StorageNote);
    }

    public static void GuardRepresentativeFixture(PerfFixtureKind fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (fixture != PerfFixtureKind.Primary500k)
        {
            throw new PerfObservationException(
                "DocumentCache representative qualification requires PERF_FIXTURE="
                    + $"{PerfFixtureKind.Primary500k.Id} with "
                    + $"{DocumentCacheQualification.RepresentativeDocumentCount.ToString("N0", CultureInfo.InvariantCulture)} "
                    + $"canonical documents; got {fixture.Id} with {fixture.RowCount.ToString("N0", CultureInfo.InvariantCulture)}."
            );
        }
    }

    public static void GuardRepresentativeStorageNote(string storageNote)
    {
        if (string.IsNullOrWhiteSpace(storageNote))
        {
            throw new PerfObservationException(
                "PERF_STORAGE_NOTE is required for DocumentCache qualification."
            );
        }

        string normalized = storageNote.Trim().ToLowerInvariant();
        if (
            normalized.Contains("tmpfs", StringComparison.Ordinal)
            && !normalized.Contains("not tmpfs", StringComparison.Ordinal)
            && !normalized.Contains("non-tmpfs", StringComparison.Ordinal)
            && !normalized.Contains("not-tmpfs", StringComparison.Ordinal)
        )
        {
            throw new PerfObservationException(
                "Refusing representative DocumentCache qualification on tmpfs storage."
            );
        }
    }

    private static async Task<DocumentCacheInitialTableCounts> CaptureInitialCountsAsync(
        DbConnection connection,
        PerfProvider provider,
        string providerName
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            provider == PerfProvider.Postgresql
                ? """
                    SELECT
                        (SELECT COUNT(*) FROM "edfi"."Student") AS "SourceDocumentRows",
                        (SELECT COUNT(*) FROM "dms"."Document") AS "DmsDocumentRows",
                        (SELECT COUNT(*) FROM "dms"."DocumentCache") AS "DocumentCacheRows",
                        (SELECT COUNT(*) FROM "dms"."DocumentProjectionWork") AS "DocumentProjectionWorkRows";
                    """
                : """
                    SELECT
                        (SELECT COUNT_BIG(1) FROM [edfi].[Student]) AS [SourceDocumentRows],
                        (SELECT COUNT_BIG(1) FROM [dms].[Document]) AS [DmsDocumentRows],
                        (SELECT COUNT_BIG(1) FROM [dms].[DocumentCache]) AS [DocumentCacheRows],
                        (SELECT COUNT_BIG(1) FROM [dms].[DocumentProjectionWork]) AS [DocumentProjectionWorkRows];
                    """;

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new PerfObservationException("DocumentCache fixture setup count query returned no rows.");
        }

        return new DocumentCacheInitialTableCounts(
            providerName,
            RequiredInt64(reader, "SourceDocumentRows"),
            RequiredInt64(reader, "DmsDocumentRows"),
            RequiredInt64(reader, "DocumentCacheRows"),
            RequiredInt64(reader, "DocumentProjectionWorkRows")
        );
    }

    private static long RequiredInt64(DbDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            throw new PerfObservationException($"DocumentCache fixture setup count '{name}' was null.");
        }

        return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static void WriteFixtureSetupArtifacts(
        string runDirectory,
        string providerName,
        DocumentCacheQualificationRunManifest runManifest,
        PerfFixtureManifest fixtureManifest,
        DocumentCacheFixtureSetupMetrics fixtureSetupMetrics
    )
    {
        Directory.CreateDirectory(runDirectory);
        WriteText(runDirectory, "run-manifest.json", PerfArtifactJson.Serialize(runManifest));
        WriteText(runDirectory, "fixture-manifest.json", PerfArtifactJson.Serialize(fixtureManifest));
        WriteText(
            runDirectory,
            $"phase-metrics/{providerName}-fixture-setup.json",
            PerfArtifactJson.Serialize(fixtureSetupMetrics)
        );
        WriteText(
            runDirectory,
            $"command-transcripts/{providerName}-fixture-setup.md",
            "# DocumentCache fixture setup\n\n"
                + "- Loaded via `PerfFixtureLoader.LoadAndVerifyAsync`.\n"
                + $"- Fixture: `{fixtureManifest.FixtureId}`.\n"
                + $"- Source document rows: {fixtureSetupMetrics.InitialCounts.SourceDocumentRows.ToString(CultureInfo.InvariantCulture)}.\n"
                + $"- `dms.Document` rows: {fixtureSetupMetrics.InitialCounts.DmsDocumentRows.ToString(CultureInfo.InvariantCulture)}.\n"
                + $"- `dms.DocumentCache` rows: {fixtureSetupMetrics.InitialCounts.DocumentCacheRows.ToString(CultureInfo.InvariantCulture)}.\n"
                + $"- `dms.DocumentProjectionWork` rows: {fixtureSetupMetrics.InitialCounts.DocumentProjectionWorkRows.ToString(CultureInfo.InvariantCulture)}.\n"
        );
    }

    private static void WriteText(string runDirectory, string relativePath, string content)
    {
        string fullPath = Path.Combine(
            runDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
                ?? throw new PerfArtifactValidationException([
                    $"DocumentCache fixture setup artifact path '{relativePath}' has no directory.",
                ])
        );
        File.WriteAllText(fullPath, content, _utf8NoBom);
    }
}
