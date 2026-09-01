// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

internal interface IDocumentCacheProviderMetricCapture
{
    Task InitializeAsync();

    Task<DocumentCacheProviderMetricPhaseScope> BeginPhaseAsync(string phase);

    Task EndPhaseAsync(DocumentCacheProviderMetricPhaseScope scope, long projectedDocumentCount);

    Task CaptureQuerySamplesAsync();

    Task CompleteAsync();
}

internal sealed record DocumentCacheProviderMetricPhaseScope(string Phase, object Snapshot);

internal sealed record DocumentCacheProviderPlanSample(
    string Name,
    string SqlFilePath,
    string PlanFilePath,
    string? StatisticsFilePath,
    PerfDatabaseMetrics Metrics
);

internal abstract class DocumentCacheProviderMetricCapture(
    DbConnection connection,
    string runDirectory,
    string providerName,
    DocumentCacheRepresentativeRunConfiguration configuration
) : IDocumentCacheProviderMetricCapture
{
    protected const int CommandTimeoutSeconds = 600;

    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    protected DbConnection Connection { get; } =
        connection ?? throw new ArgumentNullException(nameof(connection));

    protected string RunDirectory { get; } =
        string.IsNullOrWhiteSpace(runDirectory)
            ? throw new ArgumentException("Run directory is required.", nameof(runDirectory))
            : runDirectory;

    protected string ProviderName { get; } =
        string.IsNullOrWhiteSpace(providerName)
            ? throw new ArgumentException("Provider name is required.", nameof(providerName))
            : providerName;

    protected DocumentCacheRepresentativeRunConfiguration Configuration { get; } =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    public static IDocumentCacheProviderMetricCapture Create(
        DbConnection connection,
        PerfProvider provider,
        string runDirectory,
        DocumentCacheRepresentativeRunConfiguration configuration
    )
    {
        string providerName = PerfProviders.ArtifactName(provider);
        return provider switch
        {
            PerfProvider.Postgresql => new PostgresqlDocumentCacheProviderMetricCapture(
                connection,
                runDirectory,
                providerName,
                configuration
            ),
            PerfProvider.Mssql => new MssqlDocumentCacheProviderMetricCapture(
                connection,
                runDirectory,
                providerName,
                configuration
            ),
            _ => throw new PerfObservationException($"Unsupported DocumentCache provider: {provider}."),
        };
    }

    public abstract Task InitializeAsync();

    public abstract Task<DocumentCacheProviderMetricPhaseScope> BeginPhaseAsync(string phase);

    public abstract Task EndPhaseAsync(
        DocumentCacheProviderMetricPhaseScope scope,
        long projectedDocumentCount
    );

    public abstract Task CaptureQuerySamplesAsync();

    public abstract Task CompleteAsync();

    protected async Task EnsureConnectionOpenAsync()
    {
        if (Connection.State != ConnectionState.Open)
        {
            await Connection.OpenAsync();
        }
    }

    protected async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> QueryRowsAsync(
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        await EnsureConnectionOpenAsync();
        await using DbCommand command = Connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        List<IReadOnlyDictionary<string, string>> rows = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Dictionary<string, string> row = new(StringComparer.Ordinal);
            for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                object? value = await reader.IsDBNullAsync(ordinal) ? null : reader.GetValue(ordinal);
                row[reader.GetName(ordinal)] = FormatScalar(value);
            }

            rows.Add(row);
        }

        return rows;
    }

    protected async Task ExecuteNonQueryAsync(string sql)
    {
        await EnsureConnectionOpenAsync();
        await using DbCommand command = Connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }

    protected async Task<string> ScalarStringAsync(
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        object? value = await ScalarAsync(sql, parameters);
        return Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? throw new PerfObservationException($"Scalar query returned no value: {sql}");
    }

    protected async Task<bool> ScalarBoolAsync(string sql)
    {
        object? value = await ScalarAsync(sql);
        return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    protected async Task<decimal> ScalarDecimalAsync(
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        object? value = await ScalarAsync(sql, parameters);
        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    protected void WriteText(string relativePath, string content)
    {
        string fullPath = Path.Combine(
            RunDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
                ?? throw new PerfArtifactValidationException([
                    $"DocumentCache provider metric artifact path '{relativePath}' has no directory.",
                ])
        );
        File.WriteAllText(fullPath, content, _utf8NoBom);
    }

    protected DocumentCacheOperatorMetricsEvidence CopyOperatorMetricsEvidence()
    {
        DocumentCacheOperatorMetricsEvidence evidence = DocumentCacheOperatorMetricsEvidence.LoadFromFile(
            Configuration.OperatorMetricsFile,
            ProviderName
        );
        WriteText(DocumentCacheOperatorMetricsEvidence.RelativePath, PerfArtifactJson.Serialize(evidence));
        return evidence;
    }

    protected static string RenderRows(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        if (rows.Count == 0)
        {
            return "_No rows returned._\n";
        }

        IReadOnlyList<string> headers =
        [
            .. rows.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal),
        ];
        StringBuilder builder = new();
        builder.Append("| ").Append(string.Join(" | ", headers.Select(EscapePipe))).Append(" |\n");
        builder.Append("| ").Append(string.Join(" | ", headers.Select(_ => "---"))).Append(" |\n");
        foreach (IReadOnlyDictionary<string, string> row in rows)
        {
            builder
                .Append("| ")
                .Append(
                    string.Join(
                        " | ",
                        headers.Select(header =>
                            row.TryGetValue(header, out string? value) ? EscapePipe(value) : string.Empty
                        )
                    )
                )
                .Append(" |\n");
        }

        return builder.ToString();
    }

    protected static string MetricValue(decimal? value, string format = "F3") =>
        value is null ? "n/a" : value.Value.ToString(format, CultureInfo.InvariantCulture);

    protected static string MetricValue(double? value, string format = "F3") =>
        value is null ? "n/a" : value.Value.ToString(format, CultureInfo.InvariantCulture);

    protected static string MetricValue(long? value) =>
        value is null ? "n/a" : value.Value.ToString(CultureInfo.InvariantCulture);

    protected static string TrimDiagnostic(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }

    protected static long ReadLong(IReadOnlyDictionary<string, string> row, string key, long fallback = 0) =>
        row.TryGetValue(key, out string? value)
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : fallback;

    private async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await EnsureConnectionOpenAsync();
        await using DbCommand command = Connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteScalarAsync()
            ?? throw new PerfObservationException($"Scalar query returned no value: {sql}");
    }

    private static string FormatScalar(object? value) =>
        value switch
        {
            null or DBNull => string.Empty,
            DateTimeOffset dateTimeOffset => dateTimeOffset
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            DateTime dateTime => DateTime
                .SpecifyKind(dateTime, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString("G17", CultureInfo.InvariantCulture),
            float floatValue => floatValue.ToString("G9", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static string EscapePipe(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}

internal sealed record PgsqlWalSnapshot(string CapturedAtUtc, string Lsn);

internal sealed record PgsqlWalPhaseObservation(
    string Phase,
    string BeforeLsn,
    string AfterLsn,
    long ProjectedDocumentCount,
    decimal WalBytes,
    decimal? WalBytesPerProjectedDocument
);

internal sealed class PostgresqlDocumentCacheProviderMetricCapture(
    DbConnection connection,
    string runDirectory,
    string providerName,
    DocumentCacheRepresentativeRunConfiguration configuration
) : DocumentCacheProviderMetricCapture(connection, runDirectory, providerName, configuration)
{
    private readonly List<PgsqlWalPhaseObservation> _walPhases = [];
    private readonly List<DocumentCacheProviderPlanSample> _planSamples = [];
    private IReadOnlyList<IReadOnlyDictionary<string, string>> _tableStatsBeforeRun = [];
    private IReadOnlyList<IReadOnlyDictionary<string, string>> _tableStatsAfterRun = [];
    private IReadOnlyList<IReadOnlyDictionary<string, string>> _tableStatsAfterVacuum = [];
    private string _bloatEstimatorEvidence = "Bloat estimator was not captured.";

    public override async Task InitializeAsync()
    {
        _tableStatsBeforeRun = await CaptureTableStatsAsync();
    }

    public override async Task<DocumentCacheProviderMetricPhaseScope> BeginPhaseAsync(string phase) =>
        new(phase, await CaptureWalSnapshotAsync());

    public override async Task EndPhaseAsync(
        DocumentCacheProviderMetricPhaseScope scope,
        long projectedDocumentCount
    )
    {
        PgsqlWalSnapshot before =
            scope.Snapshot as PgsqlWalSnapshot
            ?? throw new PerfObservationException(
                $"PostgreSQL WAL phase '{scope.Phase}' was started with the wrong snapshot type."
            );
        PgsqlWalSnapshot after = await CaptureWalSnapshotAsync();
        decimal walBytes = await WalBytesBetweenAsync(before.Lsn, after.Lsn);
        _walPhases.Add(
            new PgsqlWalPhaseObservation(
                scope.Phase,
                before.Lsn,
                after.Lsn,
                projectedDocumentCount,
                walBytes,
                projectedDocumentCount <= 0 ? null : walBytes / projectedDocumentCount
            )
        );
    }

    public override async Task CaptureQuerySamplesAsync()
    {
        await CapturePlanSampleAsync(
            "projection",
            ProjectionSampleSql,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["pageSize"] = Configuration.PageSize }
        );
        await CapturePlanSampleAsync("status", StatusSampleSql);
        await CapturePlanSampleAsync("oldest-work", OldestWorkSampleSql);
    }

    public override async Task CompleteAsync()
    {
        _tableStatsAfterRun = await CaptureTableStatsAsync();
        await ExecuteNonQueryAsync("""VACUUM "dms"."DocumentCache";""");
        await ExecuteNonQueryAsync("""VACUUM "dms"."DocumentProjectionWork";""");
        _tableStatsAfterVacuum = await CaptureTableStatsAsync();
        _bloatEstimatorEvidence = await CaptureBloatEstimatorEvidenceAsync();
        DocumentCacheOperatorMetricsEvidence operatorMetrics = CopyOperatorMetricsEvidence();
        WriteText(
            DocumentCacheProviderMetricSummary.RelativePath(ProviderName),
            PerfArtifactJson.Serialize(BuildSummary())
        );
        WriteText(
            "provider-metrics/postgresql-wal-vacuum-bloat.md",
            BuildMarkdown(operatorMetrics.MetricsFor(ProviderName))
        );
    }

    private const string ProjectionSampleSql = """
        SELECT
            document."DocumentId",
            document."ContentVersion" AS "SourceContentVersion",
            cache."ContentVersion" AS "CacheContentVersion",
            work."RequiredContentVersion",
            work."FirstEnqueuedAt"
        FROM "dms"."DocumentProjectionWork" AS work
        INNER JOIN "dms"."Document" AS document
            ON document."DocumentId" = work."DocumentId"
        LEFT JOIN "dms"."DocumentCache" AS cache
            ON cache."DocumentId" = work."DocumentId"
        ORDER BY work."FirstEnqueuedAt", work."DocumentId"
        LIMIT @pageSize;
        """;

    private const string StatusSampleSql = """
        WITH durable_clock AS (
            SELECT statement_timestamp() AS "DurableObservedAt"
        ),
        state_row AS (
            SELECT state."ProjectionLifecycleState", state."CacheAheadRecoveryRequired"
            FROM "dms"."DocumentCacheState" AS state
            WHERE state."StateId" = 1
        ),
        oldest_work AS (
            SELECT work."DocumentId", work."FirstEnqueuedAt"
            FROM "dms"."DocumentProjectionWork" AS work
            ORDER BY work."FirstEnqueuedAt", work."DocumentId"
            LIMIT 1
        )
        SELECT
            durable_clock."DurableObservedAt",
            state_row."ProjectionLifecycleState",
            state_row."CacheAheadRecoveryRequired",
            oldest_work."DocumentId" IS NOT NULL AS "HasWork",
            oldest_work."FirstEnqueuedAt" AS "OldestWorkFirstEnqueuedAt"
        FROM durable_clock
        LEFT JOIN state_row ON TRUE
        LEFT JOIN oldest_work ON TRUE;
        """;

    private const string OldestWorkSampleSql = """
        SELECT work."DocumentId", work."FirstEnqueuedAt"
        FROM "dms"."DocumentProjectionWork" AS work
        ORDER BY work."FirstEnqueuedAt", work."DocumentId"
        LIMIT 1;
        """;

    private async Task CapturePlanSampleAsync(
        string name,
        string sql,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    )
    {
        string sqlFile = $"provider-metrics/postgresql-{name}.sql";
        string planFile = $"provider-metrics/postgresql-{name}.explain.json";
        WriteText(sqlFile, sql);
        PgsqlPlanCaptureResult capture = await PgsqlPlanCapture.CaptureAsync(
            Connection,
            sql,
            parameterValues ?? new Dictionary<string, object?>(StringComparer.Ordinal)
        );
        WriteText(planFile, capture.PlanArtifactJson);
        _planSamples.Add(new DocumentCacheProviderPlanSample(name, sqlFile, planFile, null, capture.Metrics));
    }

    private async Task<PgsqlWalSnapshot> CaptureWalSnapshotAsync() =>
        new(UtcTimestamp(), await ScalarStringAsync("SELECT pg_current_wal_lsn()::text;"));

    private async Task<decimal> WalBytesBetweenAsync(string beforeLsn, string afterLsn) =>
        await ScalarDecimalAsync(
            "SELECT pg_wal_lsn_diff(@afterLsn::pg_lsn, @beforeLsn::pg_lsn);",
            ("afterLsn", afterLsn),
            ("beforeLsn", beforeLsn)
        );

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> CaptureTableStatsAsync() =>
        await QueryRowsAsync(
            """
            SELECT
                schemaname AS "SchemaName",
                relname AS "TableName",
                n_live_tup AS "LiveTuples",
                n_dead_tup AS "DeadTuples",
                seq_scan AS "SeqScan",
                seq_tup_read AS "SeqTupleRead",
                idx_scan AS "IndexScan",
                idx_tup_fetch AS "IndexTupleFetch",
                n_tup_ins AS "InsertedTuples",
                n_tup_upd AS "UpdatedTuples",
                n_tup_del AS "DeletedTuples",
                vacuum_count AS "VacuumCount",
                autovacuum_count AS "AutoVacuumCount",
                pg_total_relation_size(relid) AS "TotalRelationBytes"
            FROM pg_stat_user_tables
            WHERE schemaname = 'dms'
              AND relname IN ('DocumentCache', 'DocumentProjectionWork')
            ORDER BY relname;
            """
        );

    private async Task<string> CaptureBloatEstimatorEvidenceAsync()
    {
        bool pgStatTupleInstalled = await ScalarBoolAsync(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgstattuple');"
        );
        if (!pgStatTupleInstalled)
        {
            return "Approved reason no bloat estimator was available: `pgstattuple` extension is not installed; using `pg_stat_user_tables` dead-tuple evidence after `VACUUM` instead.";
        }

        try
        {
            IReadOnlyList<IReadOnlyDictionary<string, string>> rows = await QueryRowsAsync(
                """
                SELECT 'dms.DocumentCache' AS "TableName", *
                FROM pgstattuple('"dms"."DocumentCache"')
                UNION ALL
                SELECT 'dms.DocumentProjectionWork' AS "TableName", *
                FROM pgstattuple('"dms"."DocumentProjectionWork"');
                """
            );
            return RenderRows(rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Approved reason no bloat estimator was available: `pgstattuple` was installed but could not be executed by the qualification user; "
                + TrimDiagnostic(ex.Message);
        }
    }

    private string BuildMarkdown(DocumentCacheOperatorProviderMetrics operatorMetrics)
    {
        StringBuilder builder = new();
        builder.Append("# PostgreSQL DocumentCache Provider Metrics\n\n");
        builder.Append(
            "Provider metrics captured for DMS-1317 representative qualification. Database CPU and I/O utilization come from the strict operator-supplied metrics file because PostgreSQL does not expose reliable per-database CPU or host I/O utilization from this connection.\n\n"
        );
        builder.Append("## Operator CPU/IO Metrics\n\n");
        builder
            .Append("- Evidence file: `")
            .Append(DocumentCacheOperatorMetricsEvidence.RelativePath)
            .Append("`.\n");
        builder
            .Append("- Average database CPU: `")
            .Append(MetricValue(operatorMetrics.AverageDatabaseCpuPercent))
            .Append("` percent.\n");
        builder
            .Append("- Average database I/O utilization: `")
            .Append(MetricValue(operatorMetrics.AverageDatabaseIoUtilizationPercent))
            .Append("` percent.\n");
        builder
            .Append("- Sample count: `")
            .Append(operatorMetrics.SampleCount?.ToString(CultureInfo.InvariantCulture) ?? "n/a")
            .Append("`.\n");
        builder.Append("- Reviewer note: ").Append(operatorMetrics.ReviewerNote).Append("\n\n");

        builder.Append("## WAL Snapshots\n\n");
        builder.Append("WAL byte deltas use `pg_wal_lsn_diff(after_lsn, before_lsn)`.\n\n");
        builder.Append(
            "| Phase | Before LSN | After LSN | Projected documents | WAL bytes | WAL bytes/document |\n"
        );
        builder.Append("| --- | --- | --- | --- | --- | --- |\n");
        foreach (PgsqlWalPhaseObservation phase in _walPhases)
        {
            builder
                .Append("| `")
                .Append(phase.Phase)
                .Append("` | `")
                .Append(phase.BeforeLsn)
                .Append("` | `")
                .Append(phase.AfterLsn)
                .Append("` | ")
                .Append(phase.ProjectedDocumentCount.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(phase.WalBytes.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(MetricValue(phase.WalBytesPerProjectedDocument))
                .Append(" |\n");
        }

        builder.Append("\n## EXPLAIN Samples\n\n");
        builder.Append(
            "The harness records `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` samples for projection, status, and oldest-work queries while outage work is present.\n\n"
        );
        builder.Append(
            "| Sample | SQL | Plan | Shared read blocks | Shared hit blocks | DB execution ms |\n"
        );
        builder.Append("| --- | --- | --- | --- | --- | --- |\n");
        foreach (DocumentCacheProviderPlanSample sample in _planSamples)
        {
            builder
                .Append("| `")
                .Append(sample.Name)
                .Append("` | `")
                .Append(sample.SqlFilePath)
                .Append("` | `")
                .Append(sample.PlanFilePath)
                .Append("` | ")
                .Append(MetricValue(sample.Metrics.BuffersRead))
                .Append(" | ")
                .Append(MetricValue(sample.Metrics.BuffersHit))
                .Append(" | ")
                .Append(MetricValue(sample.Metrics.DbExecutionMs))
                .Append(" |\n");
        }

        builder.Append("\n## pg_stat_user_tables Before Run\n\n");
        builder.Append(RenderRows(_tableStatsBeforeRun)).Append('\n');
        builder.Append("## pg_stat_user_tables After Run\n\n");
        builder.Append(RenderRows(_tableStatsAfterRun)).Append('\n');
        builder.Append("## pg_stat_user_tables After VACUUM\n\n");
        builder.Append(RenderRows(_tableStatsAfterVacuum)).Append('\n');
        builder.Append("## Bloat Estimation\n\n");
        builder.Append(_bloatEstimatorEvidence).Append('\n');
        builder.Append("\n## Maintenance Ratio\n\n");
        builder
            .Append("- Dead tuple ratio after `VACUUM`: `")
            .Append(MetricValue(DeadTupleRatioPercent(_tableStatsAfterVacuum)))
            .Append("` percent.\n");

        return builder.ToString();
    }

    private DocumentCacheProviderMetricSummary BuildSummary() =>
        new(
            PerfArtifactSchema.Version,
            ProviderName,
            UtcTimestamp(),
            [
                .. _walPhases.Select(phase => new DocumentCacheProviderLogMetric(
                    phase.Phase,
                    phase.ProjectedDocumentCount,
                    phase.WalBytes,
                    phase.WalBytesPerProjectedDocument
                )),
            ],
            [
                .. _planSamples.Select(sample => new DocumentCacheProviderQueryMetric(
                    sample.Name,
                    sample.SqlFilePath,
                    sample.PlanFilePath,
                    sample.StatisticsFilePath,
                    sample.Metrics.BuffersRead,
                    sample.Metrics.BuffersHit,
                    sample.Metrics.LogicalReads,
                    sample.Metrics.PhysicalReads,
                    SharedReadBlocksPerProjectedDocument(sample),
                    null,
                    sample.Metrics.DbExecutionMs,
                    sample.Metrics.DbCpuMs,
                    sample.Metrics.DbElapsedMs
                )),
            ],
            DeadTupleRatioPercent(_tableStatsAfterVacuum)
        );

    private decimal? SharedReadBlocksPerProjectedDocument(DocumentCacheProviderPlanSample sample) =>
        sample.Name == "projection" && sample.Metrics.BuffersRead is { } sharedReadBlocks
            ? (decimal)sharedReadBlocks / Configuration.PageSize
            : null;

    private static decimal DeadTupleRatioPercent(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        long live = rows.Sum(row => ReadLong(row, "LiveTuples"));
        long dead = rows.Sum(row => ReadLong(row, "DeadTuples"));
        long total = live + dead;
        return total == 0 ? 0 : (decimal)dead / total * 100;
    }

    private static string UtcTimestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

internal sealed record MssqlLogSnapshot(
    string CapturedAtUtc,
    decimal TotalLogSizeMb,
    decimal ActiveLogSizeMb,
    decimal LogSinceLastCheckpointMb,
    decimal LogSinceLastLogBackupMb,
    long TotalLogSizeBytes,
    long UsedLogSpaceBytes,
    long LogSpaceBytesSinceLastBackup,
    string LogTruncationHoldupReason
);

internal sealed record MssqlLogPhaseObservation(
    string Phase,
    MssqlLogSnapshot Before,
    MssqlLogSnapshot After,
    long ProjectedDocumentCount,
    decimal LogBytes,
    decimal? LogBytesPerProjectedDocument
);

internal sealed class MssqlDocumentCacheProviderMetricCapture(
    DbConnection connection,
    string runDirectory,
    string providerName,
    DocumentCacheRepresentativeRunConfiguration configuration
) : DocumentCacheProviderMetricCapture(connection, runDirectory, providerName, configuration)
{
    private readonly List<MssqlLogPhaseObservation> _logPhases = [];
    private readonly List<DocumentCacheProviderPlanSample> _planSamples = [];
    private IReadOnlyList<IReadOnlyDictionary<string, string>> _indexStatsAfterRun = [];
    private IReadOnlyList<IReadOnlyDictionary<string, string>> _indexStatsAfterMaintenance = [];

    public override Task InitializeAsync() => Task.CompletedTask;

    public override async Task<DocumentCacheProviderMetricPhaseScope> BeginPhaseAsync(string phase) =>
        new(phase, await CaptureLogSnapshotAsync());

    public override async Task EndPhaseAsync(
        DocumentCacheProviderMetricPhaseScope scope,
        long projectedDocumentCount
    )
    {
        MssqlLogSnapshot before =
            scope.Snapshot as MssqlLogSnapshot
            ?? throw new PerfObservationException(
                $"SQL Server log phase '{scope.Phase}' was started with the wrong snapshot type."
            );
        MssqlLogSnapshot after = await CaptureLogSnapshotAsync();
        decimal logBytes = Math.Max(
            0,
            after.LogSpaceBytesSinceLastBackup - before.LogSpaceBytesSinceLastBackup
        );
        _logPhases.Add(
            new MssqlLogPhaseObservation(
                scope.Phase,
                before,
                after,
                projectedDocumentCount,
                logBytes,
                projectedDocumentCount <= 0 ? null : logBytes / projectedDocumentCount
            )
        );
    }

    public override async Task CaptureQuerySamplesAsync()
    {
        await CapturePlanSampleAsync(
            "projection",
            ProjectionSampleSql,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["@pageSize"] = Configuration.PageSize }
        );
        await CapturePlanSampleAsync("status", StatusSampleSql);
        await CapturePlanSampleAsync("oldest-work", OldestWorkSampleSql);
    }

    public override async Task CompleteAsync()
    {
        _indexStatsAfterRun = await CaptureIndexPhysicalStatsAsync();
        await ExecuteNonQueryAsync("""ALTER INDEX ALL ON [dms].[DocumentCache] REORGANIZE;""");
        await ExecuteNonQueryAsync("""ALTER INDEX ALL ON [dms].[DocumentProjectionWork] REORGANIZE;""");
        _indexStatsAfterMaintenance = await CaptureIndexPhysicalStatsAsync();
        DocumentCacheOperatorMetricsEvidence operatorMetrics = CopyOperatorMetricsEvidence();
        WriteText(
            DocumentCacheProviderMetricSummary.RelativePath(ProviderName),
            PerfArtifactJson.Serialize(BuildSummary())
        );
        WriteText(
            "provider-metrics/mssql-log-ghost-index.md",
            BuildMarkdown(operatorMetrics.MetricsFor(ProviderName))
        );
    }

    private const string ProjectionSampleSql = """
        SELECT TOP (@pageSize)
            [document].[DocumentId],
            [document].[ContentVersion] AS [SourceContentVersion],
            [cache].[ContentVersion] AS [CacheContentVersion],
            [work].[RequiredContentVersion],
            [work].[FirstEnqueuedAt]
        FROM [dms].[DocumentProjectionWork] AS [work]
        INNER JOIN [dms].[Document] AS [document]
            ON [document].[DocumentId] = [work].[DocumentId]
        LEFT JOIN [dms].[DocumentCache] AS [cache]
            ON [cache].[DocumentId] = [work].[DocumentId]
        ORDER BY [work].[FirstEnqueuedAt], [work].[DocumentId];
        """;

    private const string StatusSampleSql = """
        WITH durable_clock AS (
            SELECT SYSUTCDATETIME() AS [DurableObservedAt]
        ),
        state_row AS (
            SELECT [state].[ProjectionLifecycleState], [state].[CacheAheadRecoveryRequired]
            FROM [dms].[DocumentCacheState] AS [state]
            WHERE [state].[StateId] = 1
        ),
        oldest_work AS (
            SELECT TOP (1) [work].[DocumentId], [work].[FirstEnqueuedAt]
            FROM [dms].[DocumentProjectionWork] AS [work]
            ORDER BY [work].[FirstEnqueuedAt], [work].[DocumentId]
        )
        SELECT
            durable_clock.[DurableObservedAt],
            state_row.[ProjectionLifecycleState],
            state_row.[CacheAheadRecoveryRequired],
            CAST(CASE WHEN oldest_work.[DocumentId] IS NOT NULL THEN 1 ELSE 0 END AS bit) AS [HasWork],
            oldest_work.[FirstEnqueuedAt] AS [OldestWorkFirstEnqueuedAt]
        FROM durable_clock
        LEFT JOIN state_row ON 1 = 1
        LEFT JOIN oldest_work ON 1 = 1;
        """;

    private const string OldestWorkSampleSql = """
        SELECT TOP (1) [work].[DocumentId], [work].[FirstEnqueuedAt]
        FROM [dms].[DocumentProjectionWork] AS [work]
        ORDER BY [work].[FirstEnqueuedAt], [work].[DocumentId];
        """;

    private async Task CapturePlanSampleAsync(
        string name,
        string sql,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    )
    {
        string sqlFile = $"provider-metrics/mssql-{name}.sql";
        string statisticsFile = $"provider-metrics/mssql-{name}.stats.txt";
        string planIndexFile = $"provider-metrics/mssql-{name}.plans.json";
        WriteText(sqlFile, sql);

        MssqlPlanCaptureResult capture = await MssqlPlanCapture.CaptureAsync(
            Connection,
            sql,
            parameterValues ?? new Dictionary<string, object?>(StringComparer.Ordinal)
        );
        IReadOnlyList<string> planFiles =
        [
            .. capture.ShowplanXmlDocuments.Select(
                (_, index) => $"provider-metrics/mssql-{name}.plan{index + 1:D2}.sqlplan"
            ),
        ];
        WriteText(planIndexFile, MssqlPlanCapture.PlanIndexJson(planFiles, statisticsFile));
        for (int index = 0; index < planFiles.Count; index++)
        {
            WriteText(planFiles[index], capture.ShowplanXmlDocuments[index]);
        }

        WriteText(statisticsFile, capture.StatisticsText);
        _planSamples.Add(
            new DocumentCacheProviderPlanSample(name, sqlFile, planIndexFile, statisticsFile, capture.Metrics)
        );
    }

    private async Task<MssqlLogSnapshot> CaptureLogSnapshotAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows = await QueryRowsAsync(
            """
            SELECT
                CAST(stats.total_log_size_mb AS decimal(18, 3)) AS [TotalLogSizeMb],
                CAST(stats.active_log_size_mb AS decimal(18, 3)) AS [ActiveLogSizeMb],
                CAST(stats.log_since_last_checkpoint_mb AS decimal(18, 3)) AS [LogSinceLastCheckpointMb],
                CAST(stats.log_since_last_log_backup_mb AS decimal(18, 3)) AS [LogSinceLastLogBackupMb],
                CAST(space.total_log_size_in_bytes AS bigint) AS [TotalLogSizeBytes],
                CAST(space.used_log_space_in_bytes AS bigint) AS [UsedLogSpaceBytes],
                CAST(space.log_space_in_bytes_since_last_backup AS bigint) AS [LogSpaceBytesSinceLastBackup],
                COALESCE(stats.log_truncation_holdup_reason, N'') AS [LogTruncationHoldupReason]
            FROM sys.dm_db_log_stats(DB_ID()) AS stats
            CROSS JOIN sys.dm_db_log_space_usage AS space;
            """
        );

        IReadOnlyDictionary<string, string> row = rows.Single();
        return new MssqlLogSnapshot(
            UtcTimestamp(),
            ReadDecimal(row, "TotalLogSizeMb"),
            ReadDecimal(row, "ActiveLogSizeMb"),
            ReadDecimal(row, "LogSinceLastCheckpointMb"),
            ReadDecimal(row, "LogSinceLastLogBackupMb"),
            ReadLong(row, "TotalLogSizeBytes"),
            ReadLong(row, "UsedLogSpaceBytes"),
            ReadLong(row, "LogSpaceBytesSinceLastBackup"),
            row["LogTruncationHoldupReason"]
        );
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> CaptureIndexPhysicalStatsAsync() =>
        await QueryRowsAsync(
            """
            SELECT
                OBJECT_SCHEMA_NAME(physical_stats.object_id) AS [SchemaName],
                OBJECT_NAME(physical_stats.object_id) AS [TableName],
                indexes.name AS [IndexName],
                physical_stats.index_id AS [IndexId],
                physical_stats.index_type_desc AS [IndexType],
                physical_stats.alloc_unit_type_desc AS [AllocationUnit],
                physical_stats.page_count AS [PageCount],
                physical_stats.record_count AS [RecordCount],
                physical_stats.ghost_record_count AS [GhostRecordCount],
                physical_stats.avg_fragmentation_in_percent AS [AverageFragmentationPercent]
            FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'DETAILED') AS physical_stats
            INNER JOIN sys.indexes AS indexes
                ON indexes.object_id = physical_stats.object_id
               AND indexes.index_id = physical_stats.index_id
            WHERE physical_stats.object_id IN (OBJECT_ID(N'dms.DocumentCache'), OBJECT_ID(N'dms.DocumentProjectionWork'))
              AND physical_stats.index_level = 0
            ORDER BY [SchemaName], [TableName], [IndexId], [AllocationUnit];
            """
        );

    private string BuildMarkdown(DocumentCacheOperatorProviderMetrics operatorMetrics)
    {
        StringBuilder builder = new();
        builder.Append("# SQL Server DocumentCache Provider Metrics\n\n");
        builder.Append(
            "Provider metrics captured for DMS-1317 representative qualification. Database CPU and I/O utilization come from the strict operator-supplied metrics file because SQL Server DMV counters do not provide a portable per-database host CPU or storage I/O utilization window from this connection.\n\n"
        );
        builder.Append("## Operator CPU/IO Metrics\n\n");
        builder
            .Append("- Evidence file: `")
            .Append(DocumentCacheOperatorMetricsEvidence.RelativePath)
            .Append("`.\n");
        builder
            .Append("- Average database CPU: `")
            .Append(MetricValue(operatorMetrics.AverageDatabaseCpuPercent))
            .Append("` percent.\n");
        builder
            .Append("- Average database I/O utilization: `")
            .Append(MetricValue(operatorMetrics.AverageDatabaseIoUtilizationPercent))
            .Append("` percent.\n");
        builder
            .Append("- Sample count: `")
            .Append(operatorMetrics.SampleCount?.ToString(CultureInfo.InvariantCulture) ?? "n/a")
            .Append("`.\n");
        builder.Append("- Reviewer note: ").Append(operatorMetrics.ReviewerNote).Append("\n\n");

        builder.Append("## sys.dm_db_log_stats(DB_ID()) Snapshots\n\n");
        builder.Append(
            "| Phase | Before used log bytes | After used log bytes | Projected documents | Log bytes | Log bytes/document | Holdup after |\n"
        );
        builder.Append("| --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (MssqlLogPhaseObservation phase in _logPhases)
        {
            builder
                .Append("| `")
                .Append(phase.Phase)
                .Append("` | ")
                .Append(phase.Before.UsedLogSpaceBytes.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(phase.After.UsedLogSpaceBytes.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(phase.ProjectedDocumentCount.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(phase.LogBytes.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(MetricValue(phase.LogBytesPerProjectedDocument))
                .Append(" | `")
                .Append(phase.After.LogTruncationHoldupReason)
                .Append("` |\n");
        }

        builder.Append("\n## SET STATISTICS IO And Actual Plans\n\n");
        builder.Append(
            "The harness records `SET STATISTICS IO`, `SET STATISTICS TIME`, and actual plan XML samples for projection, status, and oldest-work queries while outage work is present.\n\n"
        );
        builder.Append(
            "| Sample | SQL | Plan index | Statistics IO/TIME | Logical reads | Physical reads | DB CPU ms | DB elapsed ms |\n"
        );
        builder.Append("| --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (DocumentCacheProviderPlanSample sample in _planSamples)
        {
            builder
                .Append("| `")
                .Append(sample.Name)
                .Append("` | `")
                .Append(sample.SqlFilePath)
                .Append("` | `")
                .Append(sample.PlanFilePath)
                .Append("` | `")
                .Append(sample.StatisticsFilePath)
                .Append("` | ")
                .Append(MetricValue(sample.Metrics.LogicalReads))
                .Append(" | ")
                .Append(MetricValue(sample.Metrics.PhysicalReads))
                .Append(" | ")
                .Append(MetricValue(sample.Metrics.DbCpuMs))
                .Append(" | ")
                .Append(MetricValue(sample.Metrics.DbElapsedMs))
                .Append(" |\n");
        }

        builder.Append("\n## sys.dm_db_index_physical_stats After Run\n\n");
        builder.Append(RenderRows(_indexStatsAfterRun)).Append('\n');
        builder.Append("## sys.dm_db_index_physical_stats After Index Maintenance\n\n");
        builder.Append(RenderRows(_indexStatsAfterMaintenance)).Append('\n');
        builder.Append("## Ghost Row And Fragmentation Observations\n\n");
        builder
            .Append("- Ghost row ratio after index maintenance: `")
            .Append(MetricValue(GhostRowRatioPercent(_indexStatsAfterMaintenance)))
            .Append("` percent.\n");
        builder
            .Append("- Maximum fragmentation after index maintenance: `")
            .Append(MetricValue(MaxFragmentationPercent(_indexStatsAfterMaintenance)))
            .Append("` percent.\n");

        return builder.ToString();
    }

    private DocumentCacheProviderMetricSummary BuildSummary() =>
        new(
            PerfArtifactSchema.Version,
            ProviderName,
            UtcTimestamp(),
            [
                .. _logPhases.Select(phase => new DocumentCacheProviderLogMetric(
                    phase.Phase,
                    phase.ProjectedDocumentCount,
                    phase.LogBytes,
                    phase.LogBytesPerProjectedDocument
                )),
            ],
            [
                .. _planSamples.Select(sample => new DocumentCacheProviderQueryMetric(
                    sample.Name,
                    sample.SqlFilePath,
                    sample.PlanFilePath,
                    sample.StatisticsFilePath,
                    sample.Metrics.BuffersRead,
                    sample.Metrics.BuffersHit,
                    sample.Metrics.LogicalReads,
                    sample.Metrics.PhysicalReads,
                    null,
                    LogicalReadsPerProjectedDocument(sample),
                    sample.Metrics.DbExecutionMs,
                    sample.Metrics.DbCpuMs,
                    sample.Metrics.DbElapsedMs
                )),
            ],
            GhostRowRatioPercent(_indexStatsAfterMaintenance)
        );

    private decimal? LogicalReadsPerProjectedDocument(DocumentCacheProviderPlanSample sample) =>
        sample.Name == "projection" && sample.Metrics.LogicalReads is { } logicalReads
            ? (decimal)logicalReads / Configuration.PageSize
            : null;

    private static decimal ReadDecimal(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value)
        && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
            ? parsed
            : 0;

    private static decimal GhostRowRatioPercent(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        long records = rows.Sum(row => ReadLong(row, "RecordCount"));
        long ghosts = rows.Sum(row => ReadLong(row, "GhostRecordCount"));
        return records == 0 ? 0 : (decimal)ghosts / records * 100;
    }

    private static decimal MaxFragmentationPercent(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        decimal max = 0;
        foreach (IReadOnlyDictionary<string, string> row in rows)
        {
            if (
                row.TryGetValue("AverageFragmentationPercent", out string? value)
                && decimal.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out decimal parsed
                )
                && parsed > max
            )
            {
                max = parsed;
            }
        }

        return max;
    }

    private static string UtcTimestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
