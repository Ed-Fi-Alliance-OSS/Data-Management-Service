// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Manifest for one representative DocumentCache qualification run. The benchmark pipeline
/// will fill this beside threshold-results.json so the measured evidence carries enough
/// provider, workload, storage, and commit identity to be reviewable after the run.
/// </summary>
public sealed record DocumentCacheQualificationRunManifest(
    string SchemaVersion,
    string RunId,
    string CapturedAtUtc,
    IReadOnlyList<string> Providers,
    int CanonicalDocumentCount,
    int OutageDistinctDocumentWrites,
    int SameDocumentContention,
    string StorageNote,
    string RunnerCommit,
    string SubjectCommit,
    IReadOnlyList<string> DirtyPathAllowlist,
    IReadOnlyList<string> WorktreeDirtyPaths,
    IReadOnlyList<DocumentCacheQualificationProviderIdentity> ProviderIdentities,
    string? OperatorNote,
    IReadOnlyList<DocumentCacheQualificationArtifact> Artifacts
)
{
    public static DocumentCacheQualificationRunManifest Create(
        string runId,
        string capturedAtUtc,
        IReadOnlyList<PerfProvider> providers,
        int canonicalDocumentCount,
        int outageDistinctDocumentWrites,
        int sameDocumentContention,
        string storageNote,
        string runnerCommit,
        string subjectCommit,
        IReadOnlyList<string> dirtyPathAllowlist,
        IReadOnlyList<string> worktreeDirtyPaths,
        IReadOnlyList<DocumentCacheQualificationProviderIdentity> providerIdentities,
        string? operatorNote
    ) =>
        new(
            PerfArtifactSchema.Version,
            runId,
            capturedAtUtc,
            [.. providers.Select(PerfProviders.ArtifactName)],
            canonicalDocumentCount,
            outageDistinctDocumentWrites,
            sameDocumentContention,
            storageNote,
            runnerCommit,
            subjectCommit,
            dirtyPathAllowlist,
            worktreeDirtyPaths,
            providerIdentities,
            operatorNote,
            DocumentCacheQualificationArtifact.RequiredRepresentativeArtifacts()
        );
}

/// <summary>
/// Row counts captured after the fixture loader verifies the source data and before any
/// DocumentCache lifecycle measurement mutates cache or work state.
/// </summary>
public sealed record DocumentCacheInitialTableCounts(
    string Provider,
    long SourceDocumentRows,
    long DmsDocumentRows,
    long DocumentCacheRows,
    long DocumentProjectionWorkRows
);

/// <summary>
/// Provider-specific database identity and clean starting state recorded in the
/// DocumentCache run manifest.
/// </summary>
public sealed record DocumentCacheQualificationProviderIdentity(
    string Provider,
    PerfEnvironmentIdentity DatabaseIdentity,
    DocumentCacheInitialTableCounts InitialCounts
);

/// <summary>
/// Phase-metrics artifact written immediately after source fixture setup, before later
/// lifecycle phases begin.
/// </summary>
public sealed record DocumentCacheFixtureSetupMetrics(
    string SchemaVersion,
    string Provider,
    string CapturedAtUtc,
    PerfFixtureManifest Fixture,
    DocumentCacheInitialTableCounts InitialCounts
)
{
    public static DocumentCacheFixtureSetupMetrics Create(
        string provider,
        string capturedAtUtc,
        PerfFixtureManifest fixture,
        DocumentCacheInitialTableCounts initialCounts
    ) => new(PerfArtifactSchema.Version, provider, capturedAtUtc, fixture, initialCounts);
}

/// <summary>
/// Counts and singleton lifecycle state captured around one DocumentCache qualification phase.
/// Lifecycle is recorded as text to keep phase metrics provider-agnostic and stable even if
/// the core lifecycle enum later adds JSON converters.
/// </summary>
public sealed record DocumentCacheQualificationPhaseCounts(
    string Provider,
    long SourceDocumentRows,
    long DmsDocumentRows,
    long DocumentCacheRows,
    long DocumentProjectionWorkRows,
    string ProjectionLifecycleState,
    bool CacheAheadRecoveryRequired
);

/// <summary>
/// One scalar measurement inside a phase metrics artifact. Values stay textual so later
/// threshold assembly can choose decimal/integer/string interpretation explicitly.
/// </summary>
public sealed record DocumentCacheQualificationPhaseMetricValue(string Name, string Value, string Unit);

/// <summary>
/// Failed HTTP write evidence retained without storing every successful request body.
/// </summary>
public sealed record DocumentCacheQualificationWriteFailure(
    long Ordinal,
    string DocumentUuid,
    int? StatusCode,
    string Message
);

/// <summary>
/// HTTP write batch evidence. Latency samples include measured successful writes only; failures
/// are separated so a representative run can fail fast while still leaving diagnostic artifacts.
/// </summary>
public sealed record DocumentCacheQualificationWriteBatchMetrics(
    long FirstOrdinal,
    long LastOrdinal,
    int WarmupCount,
    int MeasuredCount,
    int SuccessfulMeasuredCount,
    IReadOnlyList<DocumentCacheQualificationWriteFailure> Failures,
    PerfLatencySummary? Latency
);

/// <summary>
/// One manually invoked projection drain slice used by outage and contention phases.
/// </summary>
public sealed record DocumentCacheQualificationDrainSliceMetrics(
    int SliceNumber,
    string SchedulerStatus,
    string? DrainOutcome,
    int? ProcessedItemCount,
    int? AcknowledgedOrRemovedItemCount,
    int? DocumentScopedFailureCount,
    double ElapsedMilliseconds,
    string? AdministrativeFailureStatus = null,
    string? AdministrativeFailureClassification = null,
    string? AdministrativeFailureDiagnosticCategory = null,
    string? AdministrativeFailureMessage = null,
    bool? AdministrativeFailureRetryable = null,
    IReadOnlyList<long>? AdministrativeFailureAffectedDocumentIds = null
);

/// <summary>
/// Structured JSON for one benchmark phase. Threshold rows are intentionally generated later;
/// these phase artifacts retain the observed operational evidence from the run itself.
/// </summary>
public sealed record DocumentCacheQualificationPhaseMetrics(
    string SchemaVersion,
    string Provider,
    string Phase,
    string CapturedAtUtc,
    double ElapsedMilliseconds,
    DocumentCacheQualificationPhaseCounts CountsBefore,
    DocumentCacheQualificationPhaseCounts CountsAfter,
    IReadOnlyList<DocumentCacheQualificationPhaseMetricValue> Metrics,
    PerfLatencySummary? Latency = null,
    DocumentCacheQualificationWriteBatchMetrics? WriteBatch = null,
    DocumentCacheAdministrativeCommandResult? CommandResult = null,
    DocumentCacheStatusResponse? StatusSnapshot = null,
    IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics>? DrainSlices = null
)
{
    public static DocumentCacheQualificationPhaseMetrics Create(
        string provider,
        string phase,
        string capturedAtUtc,
        TimeSpan elapsed,
        DocumentCacheQualificationPhaseCounts countsBefore,
        DocumentCacheQualificationPhaseCounts countsAfter,
        IEnumerable<DocumentCacheQualificationPhaseMetricValue> metrics,
        PerfLatencySummary? latency = null,
        DocumentCacheQualificationWriteBatchMetrics? writeBatch = null,
        DocumentCacheAdministrativeCommandResult? commandResult = null,
        DocumentCacheStatusResponse? statusSnapshot = null,
        IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics>? drainSlices = null
    ) =>
        new(
            PerfArtifactSchema.Version,
            provider,
            phase,
            capturedAtUtc,
            elapsed.TotalMilliseconds,
            countsBefore,
            countsAfter,
            [.. metrics],
            latency,
            writeBatch,
            commandResult,
            statusSnapshot,
            drainSlices
        );
}

/// <summary>
/// One row in threshold-results.json. Nullable fields are intentional: deserialization of
/// incomplete JSON must succeed far enough for the validator to report every missing field.
/// </summary>
public sealed record DocumentCacheQualificationResult(
    string? Provider,
    string? ThresholdId,
    string? Area,
    string? Measurement,
    decimal? MeasuredValue,
    decimal? Maximum,
    string? Unit,
    bool? Passed,
    string? EvidencePath,
    string? ReviewerNote,
    string? DurableBaselineCursorTicket = null
);

/// <summary>
/// A run-directory-relative artifact reference used by manifests and validator diagnostics.
/// </summary>
public sealed record DocumentCacheQualificationArtifact(
    string RelativePath,
    string Description,
    bool Required,
    bool IsDirectory
)
{
    public static IReadOnlyList<DocumentCacheQualificationArtifact> RequiredRepresentativeArtifacts() =>
        [
            .. DocumentCacheQualification.RequiredRepresentativeArtifacts.Select(
                RequiredRepresentativeArtifact
            ),
        ];

    private static DocumentCacheQualificationArtifact RequiredRepresentativeArtifact(string path) =>
        new(path, DescriptionFor(path), Required: true, IsDirectory: path.EndsWith('/'));

    private static string DescriptionFor(string path) =>
        path switch
        {
            "qualification-summary.md" => "Human-readable qualification summary and production disposition.",
            "threshold-results.json" => "Machine-readable pass/fail rows for every provider threshold.",
            "query-plan-guards/" => "Bounded query-plan guard output.",
            "writer-contention-evidence/" => "Explicit writer contention evidence.",
            "outage-drain-evidence/" => "Outage replay, backlog growth, drain, and final status evidence.",
            "provider-metrics/postgresql-wal-vacuum-bloat.md" =>
                "PostgreSQL WAL, vacuum, bloat, and dead-tuple observations.",
            "provider-metrics/mssql-log-ghost-index.md" =>
                "SQL Server log, ghost-row, and index-fragmentation observations.",
            "command-transcripts/" => "Command transcripts for lifecycle and administrative phases.",
            "phase-metrics/" => "Structured phase metrics used to generate threshold rows.",
            _ => "DocumentCache qualification artifact.",
        };
}

/// <summary>
/// One validation failure for a representative DocumentCache qualification result directory.
/// </summary>
public sealed record DocumentCacheQualificationValidationFailure(
    string Code,
    string Message,
    string? ArtifactPath = null,
    string? ThresholdId = null
)
{
    public override string ToString()
    {
        string location = ArtifactPath ?? ThresholdId ?? string.Empty;
        return string.IsNullOrWhiteSpace(location) ? $"{Code}: {Message}" : $"{Code}: {location}: {Message}";
    }
}
