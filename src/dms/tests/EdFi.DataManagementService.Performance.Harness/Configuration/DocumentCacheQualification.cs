// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// A scale qualification threshold for DMS-1317 DocumentCache release evidence.
/// </summary>
public sealed record DocumentCacheQualificationThreshold(
    PerfProvider Provider,
    string Id,
    string Area,
    string Measurement,
    decimal Maximum,
    string Unit,
    string EvidenceSource,
    string FailureAction
);

/// <summary>
/// Fixed DMS-1317 DocumentCache scale qualification contract. The CI guards are intentionally
/// bounded; representative-scale timing and provider maintenance observations are collected
/// outside ordinary CI through the documented performance qualification entrypoint.
/// </summary>
public static class DocumentCacheQualification
{
    public const int CiGuardDocumentCount = 160;
    public const int CiGuardWorkRowCount = 80;
    public const int RepresentativeDocumentCount = 500_000;
    public const int RepresentativeOutageDistinctDocumentWrites = 50_000;
    public const int RepresentativeSameDocumentContention = 32;

    public static readonly IReadOnlyList<string> RequiredThresholdAreas =
    [
        "baselineCompletion",
        "rebuildCompletion",
        "restartFromBeginning",
        "databaseCpu",
        "databaseIo",
        "databaseLog",
        "queueDmlAmplification",
        "statusOldestWorkLatency",
        "canonicalWriteOverhead",
        "outageQueueGrowth",
        "outageDrain",
        "sameDocumentLockWait",
        "providerMaintenance",
    ];

    public static readonly IReadOnlyList<string> CiGuardCommands =
    [
        "dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj --filter FullyQualifiedName~DocumentCacheQueryPlan",
        "dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj --filter FullyQualifiedName~DocumentCacheQueryPlan",
    ];

    public static readonly IReadOnlyList<string> RequiredRepresentativeArtifacts =
    [
        "qualification-summary.md",
        "threshold-results.json",
        "query-plan-guards/",
        "writer-contention-evidence/",
        "outage-drain-evidence/",
        "provider-metrics/operator-cpu-io.json",
        "provider-metrics/postgresql-wal-vacuum-bloat.md",
        "provider-metrics/mssql-log-ghost-index.md",
        "command-transcripts/",
        "phase-metrics/",
    ];

    public static readonly IReadOnlyList<DocumentCacheQualificationThreshold> Thresholds =
    [
        new(
            PerfProvider.Postgresql,
            "postgresql-baseline-completion-minutes",
            "baselineCompletion",
            "Offline activation or first baseline completes for 500,000 documents.",
            30,
            "minutes",
            "DocumentCacheAdmin activation/rebuild run log plus status caught-up evidence.",
            "Tune page size/worker count/storage or create durable-baseline-cursor Jira ticket."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-online-rebuild-completion-minutes",
            "rebuildCompletion",
            "Online rebuild clears DocumentCache only, preserves pending work, reseeds, drains, and returns to Tracking.",
            40,
            "minutes",
            "DocumentCacheAdmin rebuild-online run log plus final status JSON.",
            "Tune page size/worker count/storage or create durable-baseline-cursor Jira ticket."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-restart-from-beginning-completion-minutes",
            "restartFromBeginning",
            "Interrupted Rebuilding owner restart completes the replacement full baseline.",
            40,
            "minutes",
            "Interrupted rebuild transcript with before/after active command and status JSON.",
            "Create durable-baseline-cursor Jira ticket before production qualification."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-average-db-cpu-percent",
            "databaseCpu",
            "Average database CPU during baseline/rebuild representative run.",
            70,
            "percent",
            "Host or managed-database CPU metric sampled for the measured run window.",
            "Tune projection concurrency or storage before production qualification."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-shared-read-blocks-per-document",
            "databaseIo",
            "Shared read blocks per projected document during baseline/rebuild.",
            16,
            "blocks/document",
            "EXPLAIN (ANALYZE, BUFFERS) aggregate from sampled projection and status statements.",
            "Investigate missing indexes, source windowing, or cache/work scans."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-wal-bytes-per-document",
            "databaseLog",
            "WAL growth per projected document during baseline/rebuild.",
            32_768,
            "bytes/document",
            "pg_wal_lsn_diff before/after the measured run.",
            "Tune batch sizing or storage; defer production if WAL retention is unsafe."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-queue-dml-amplification-ratio",
            "queueDmlAmplification",
            "DocumentProjectionWork insert/update/delete attempts per distinct source document.",
            1.25m,
            "ratio",
            "Queue DML counter deltas captured during outage distinct-document writes.",
            "Investigate repeated queue DML before production qualification."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-status-oldest-work-p95-ms",
            "statusOldestWorkLatency",
            "p95 status/oldest-work observation latency at representative cardinality.",
            250,
            "milliseconds",
            "Repeated dms-document-cache status measurements while backlog exists.",
            "Fix status query plan before production qualification."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-canonical-write-overhead-ratio",
            "canonicalWriteOverhead",
            "p95 canonical write latency with Tracking enqueue compared to Disabled.",
            1.20m,
            "ratio",
            "Matched API write sample with lifecycle Disabled and Tracking.",
            "Investigate enqueue trigger overhead and lock waits."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-outage-backlog-row-amplification-ratio",
            "outageQueueGrowth",
            "Backlog rows after outage compared to distinct touched documents.",
            1.05m,
            "ratio",
            "Outage replay unique-document count and DocumentProjectionWork count.",
            "Investigate coalescing or duplicate enqueue behavior."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-outage-drain-minutes",
            "outageDrain",
            "Drain 50,000 distinct-document outage backlog to caught-up.",
            15,
            "minutes",
            "Projector drain transcript and final status JSON.",
            "Tune projector concurrency/page size or provision storage."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-same-document-lock-wait-p95-ms",
            "sameDocumentLockWait",
            "p95 same-document enqueue/ack lock wait under 32 contenders.",
            500,
            "milliseconds",
            "DocumentCache writer telemetry same-document wait histogram.",
            "Investigate same-document lock scope before production qualification."
        ),
        new(
            PerfProvider.Postgresql,
            "postgresql-dead-tuple-ratio-after-vacuum-percent",
            "providerMaintenance",
            "DocumentCache and DocumentProjectionWork dead tuple ratio after maintenance.",
            1,
            "percent",
            "pg_stat_user_tables before run, after run, and after VACUUM evidence.",
            "Tune autovacuum or maintenance procedure before production qualification."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-baseline-completion-minutes",
            "baselineCompletion",
            "Offline activation or first baseline completes for 500,000 documents.",
            45,
            "minutes",
            "DocumentCacheAdmin activation/rebuild run log plus status caught-up evidence.",
            "Tune page size/worker count/storage or create durable-baseline-cursor Jira ticket."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-online-rebuild-completion-minutes",
            "rebuildCompletion",
            "Online rebuild clears DocumentCache only, preserves pending work, reseeds, drains, and returns to Tracking.",
            60,
            "minutes",
            "DocumentCacheAdmin rebuild-online run log plus final status JSON.",
            "Tune page size/worker count/storage or create durable-baseline-cursor Jira ticket."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-restart-from-beginning-completion-minutes",
            "restartFromBeginning",
            "Interrupted Rebuilding owner restart completes the replacement full baseline.",
            60,
            "minutes",
            "Interrupted rebuild transcript with before/after active command and status JSON.",
            "Create durable-baseline-cursor Jira ticket before production qualification."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-average-db-cpu-percent",
            "databaseCpu",
            "Average database CPU during baseline/rebuild representative run.",
            70,
            "percent",
            "Host or managed-database CPU metric sampled for the measured run window.",
            "Tune projection concurrency or storage before production qualification."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-logical-reads-per-document",
            "databaseIo",
            "Logical reads per projected document during baseline/rebuild.",
            24,
            "pages/document",
            "SET STATISTICS IO aggregate from sampled projection and status statements.",
            "Investigate missing indexes, source windowing, or cache/work scans."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-transaction-log-bytes-per-document",
            "databaseLog",
            "Transaction log growth per projected document during baseline/rebuild.",
            49_152,
            "bytes/document",
            "sys.dm_db_log_stats before/after the measured run.",
            "Tune batch sizing or storage; defer production if log growth is unsafe."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-queue-dml-amplification-ratio",
            "queueDmlAmplification",
            "DocumentProjectionWork insert/update/delete attempts per distinct source document.",
            1.25m,
            "ratio",
            "Queue DML counter deltas captured during outage distinct-document writes.",
            "Investigate repeated queue DML before production qualification."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-status-oldest-work-p95-ms",
            "statusOldestWorkLatency",
            "p95 status/oldest-work observation latency at representative cardinality.",
            250,
            "milliseconds",
            "Repeated dms-document-cache status measurements while backlog exists.",
            "Fix status query plan before production qualification."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-canonical-write-overhead-ratio",
            "canonicalWriteOverhead",
            "p95 canonical write latency with Tracking enqueue compared to Disabled.",
            1.20m,
            "ratio",
            "Matched API write sample with lifecycle Disabled and Tracking.",
            "Investigate enqueue trigger overhead and lock waits."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-outage-backlog-row-amplification-ratio",
            "outageQueueGrowth",
            "Backlog rows after outage compared to distinct touched documents.",
            1.05m,
            "ratio",
            "Outage replay unique-document count and DocumentProjectionWork count.",
            "Investigate coalescing or duplicate enqueue behavior."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-outage-drain-minutes",
            "outageDrain",
            "Drain 50,000 distinct-document outage backlog to caught-up.",
            20,
            "minutes",
            "Projector drain transcript and final status JSON.",
            "Tune projector concurrency/page size or provision storage."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-same-document-lock-wait-p95-ms",
            "sameDocumentLockWait",
            "p95 same-document enqueue/ack lock wait under 32 contenders.",
            750,
            "milliseconds",
            "DocumentCache writer telemetry same-document wait histogram.",
            "Investigate same-document lock scope before production qualification."
        ),
        new(
            PerfProvider.Mssql,
            "mssql-ghost-row-ratio-after-cleanup-percent",
            "providerMaintenance",
            "DocumentCache and DocumentProjectionWork ghost row ratio after cleanup.",
            1,
            "percent",
            "sys.dm_db_index_physical_stats and ghost cleanup observation.",
            "Tune index maintenance or cleanup procedure before production qualification."
        ),
    ];

    public static IReadOnlyList<DocumentCacheQualificationThreshold> OrderedThresholds() =>
        [
            .. Thresholds
                .OrderBy(threshold => threshold.Provider)
                .ThenBy(threshold => threshold.Id, StringComparer.Ordinal),
        ];

    public static IReadOnlyList<DocumentCacheQualificationThreshold> ThresholdsFor(PerfProvider provider) =>
        Thresholds.Where(threshold => threshold.Provider == provider).ToArray();
}
