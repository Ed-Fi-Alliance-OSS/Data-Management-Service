// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

/// <summary>
/// DMS-1437 step 0.2 initial operational assessment (spec §6.2). Each fixture creates the scratch schema
/// <c>dmscs_probe</c> with the planned <c>Job</c> and <c>JobSchedule</c> shapes and indexes (spec §3) in its own
/// database, runs the planned SQL (spec D-3, D-4, D-6, D-8, D-9, D-17) through raw SqlClient commands, and drops
/// the schema afterwards. Nothing here touches repositories, options, hosted services, or the <c>dmscs</c> schema.
/// </summary>
/// <remarks>
/// <para>
/// The integration job runs this assembly unfiltered, so <c>[Explicit]</c> on every concrete fixture is what keeps
/// these probes off pull-request CI. Run them with
/// <c>dotnet test … --filter "Category=OperationalProbe"</c>. The server comes from
/// <c>ConnectionStrings__MssqlAdmin</c> (the fixtures are ignored when it is unset); the probe database
/// <c>edfi_cms_job_probe</c> is created on that server when absent, with server defaults (READ COMMITTED, no
/// READ_COMMITTED_SNAPSHOT), as DbUp's <c>EnsureDatabase</c> creates the CMS database. Set
/// <c>CMS_JOB_PROBE_RESULTS</c> to a file path to append every measurement as a tab-separated line.
/// </para>
/// <para>
/// Absolute latencies are hardware-dependent evidence. The assertions are the spec §6.2 acceptance thresholds and
/// the correctness invariants each probe exists to exercise.
/// </para>
/// </remarks>
[Category("OperationalProbe")]
[NonParallelizable]
public abstract class JobOperationalProbeBase
{
    protected const string ProbeDatabaseName = "edfi_cms_job_probe";
    protected const int MaxAttempts = 5;
    protected const int LeaseSeconds = 300;
    protected const int ClaimCommandTimeoutSeconds = 5;

    /// <summary>
    /// The candidate RenewalTimeout (RenewalInterval / 2). It must exceed the 5 s WriteLockWait so that a lock wait
    /// always ends in error 1222 rather than racing a client-side command timeout.
    /// </summary>
    protected const int OwnershipWriteCommandTimeoutSeconds = 30;
    protected const int RetentionCommandTimeoutSeconds = 30;
    protected const int LockTimeoutErrorNumber = 1222;
    protected const int DeadlockErrorNumber = 1205;
    protected const string ExhaustedMessage = "The job exceeded the maximum number of attempts.";

    private static readonly object _resultsFileLock = new();
    private string? _connectionString;

    /// <summary>
    /// The claim shape whose index, seed values, and claim/exhaust statements the fixture uses.
    /// </summary>
    protected virtual ClaimShape Shape => ClaimShape.Planned;

    protected string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("The probe schema has not been created.");

    [OneTimeSetUp]
    public async Task CreateProbeSchema()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server operational probes require the ConnectionStrings__MssqlAdmin environment variable."
        );

        SqlConnectionStringBuilder master = new(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };
        await using (SqlConnection connection = new(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using SqlCommand create = new(
                $"IF DB_ID(N'{ProbeDatabaseName}') IS NULL CREATE DATABASE [{ProbeDatabaseName}];",
                connection
            );
            await create.ExecuteNonQueryAsync();
        }

        _connectionString = new SqlConnectionStringBuilder(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = ProbeDatabaseName,
            ApplicationName = "EdFi.DmsConfigurationService.JobOperationalProbe",
        }.ConnectionString;

        await ExecuteAsync(JobProbeSql.DropSchema);
        await ExecuteAsync(JobProbeSql.CreateSchema);
        await ExecuteAsync(JobProbeSql.CreateTables(Shape));
    }

    [OneTimeTearDown]
    public async Task DropProbeSchema()
    {
        if (_connectionString is null)
        {
            return;
        }

        await ExecuteAsync(JobProbeSql.DropSchema);
        _connectionString = null;
    }

    protected async Task<SqlConnection> OpenConnectionAsync()
    {
        SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    protected async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await using SqlCommand command = new(sql, connection) { CommandTimeout = 300 };
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    protected async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await using SqlCommand command = new(sql, connection) { CommandTimeout = 300 };
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Seeds <paramref name="count"/> jobs in one statement. Creation times are one millisecond apart and end
    /// <paramref name="createdAgeSeconds"/> before now so that FIFO order is the insertion order.
    /// </summary>
    protected Task SeedJobsAsync(
        string status,
        int count,
        int attemptCount,
        int createdAgeSeconds = 0,
        int finishedAgeSeconds = 0,
        int leaseRemainingSeconds = LeaseSeconds,
        string leaseOwner = "probe-seed"
    ) =>
        ExecuteAsync(
            JobProbeSql.SeedJobs,
            ("@Status", status),
            ("@Count", count),
            ("@AttemptCount", attemptCount),
            ("@CreatedAgeSeconds", createdAgeSeconds),
            ("@FinishedAgeSeconds", finishedAgeSeconds),
            ("@LeaseRemainingSeconds", leaseRemainingSeconds),
            ("@LeaseOwner", leaseOwner),
            ("@AvailableAtEnqueue", Shape == ClaimShape.IndexOrdered)
        );

    protected Task UpdateStatisticsAsync() =>
        ExecuteAsync(
            "UPDATE STATISTICS dmscs_probe.Job WITH FULLSCAN; UPDATE STATISTICS dmscs_probe.JobSchedule WITH FULLSCAN;"
        );

    protected static SqlCommand Command(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        int timeoutSeconds = ClaimCommandTimeoutSeconds
    ) => new(sql, connection, transaction) { CommandTimeout = timeoutSeconds };

    protected static SqlParameter DateTime2(string name, DateTime value) =>
        new(name, SqlDbType.DateTime2) { Scale = 7, Value = value };

    /// <summary>
    /// D-3: one claim statement in its own (implicit) transaction.
    /// </summary>
    protected async Task<ClaimedProbeJob?> ClaimAsync(SqlConnection connection, string owner)
    {
        await using SqlCommand claim = Command(connection, null, JobProbeSql.Claim(Shape));
        claim.Parameters.AddWithValue("@Owner", owner);
        claim.Parameters.AddWithValue("@LeaseSeconds", LeaseSeconds);
        claim.Parameters.AddWithValue("@MaxAttempts", MaxAttempts);
        await using SqlDataReader reader = await claim.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }
        return new ClaimedProbeJob(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
    }

    /// <summary>
    /// D-4 lock-then-validate: bounded row lock first, then the guarded write as a separate statement whose
    /// <c>SYSUTCDATETIME()</c> is evaluated when that statement starts, after the lock is held.
    /// </summary>
    protected static async Task<ProbeWriteOutcome> OwnershipWriteAsync(
        SqlConnection connection,
        string guardedUpdateSql,
        long id,
        string owner,
        long token
    )
    {
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using (
                SqlCommand lockWait = Command(
                    connection,
                    transaction,
                    JobProbeSql.SetWriteLockWait,
                    OwnershipWriteCommandTimeoutSeconds
                )
            )
            {
                await lockWait.ExecuteNonQueryAsync();
            }

            await using (
                SqlCommand lockRow = Command(
                    connection,
                    transaction,
                    JobProbeSql.LockJobRow,
                    OwnershipWriteCommandTimeoutSeconds
                )
            )
            {
                lockRow.Parameters.AddWithValue("@Id", id);
                await lockRow.ExecuteScalarAsync();
            }

            object? written;
            await using (
                SqlCommand guarded = Command(
                    connection,
                    transaction,
                    guardedUpdateSql,
                    OwnershipWriteCommandTimeoutSeconds
                )
            )
            {
                guarded.Parameters.AddWithValue("@Id", id);
                guarded.Parameters.AddWithValue("@Owner", owner);
                guarded.Parameters.AddWithValue("@Token", token);
                guarded.Parameters.AddWithValue("@LeaseSeconds", LeaseSeconds);
                written = await guarded.ExecuteScalarAsync();
            }

            await transaction.CommitAsync();
            return written is null ? ProbeWriteOutcome.OwnershipLost : ProbeWriteOutcome.Success;
        }
        catch (SqlException exception) when (exception.Number == LockTimeoutErrorNumber)
        {
            await transaction.RollbackAsync();
            return ProbeWriteOutcome.LockTimeout;
        }
    }

    protected async Task<int> ExhaustAsync(SqlConnection connection, int maxAttempts = MaxAttempts)
    {
        await using SqlCommand exhaust = Command(connection, null, JobProbeSql.Exhaust(Shape));
        exhaust.Parameters.AddWithValue("@MaxAttempts", maxAttempts);
        exhaust.Parameters.AddWithValue("@Message", ExhaustedMessage);
        exhaust.Parameters.AddWithValue("@Owner", "probe-exhaust");
        return await exhaust.ExecuteNonQueryAsync();
    }

    protected static double ElapsedMilliseconds(long start) =>
        Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    protected static void Report(string probe, string metric, object value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        TestContext.Progress.WriteLine($"PROBE mssql {probe} {metric} {text}");

        if (Environment.GetEnvironmentVariable("CMS_JOB_PROBE_RESULTS") is { Length: > 0 } path)
        {
            lock (_resultsFileLock)
            {
                File.AppendAllText(path, $"mssql\t{probe}\t{metric}\t{text}{Environment.NewLine}");
            }
        }
    }

    protected static void Report(string probe, string metric, LatencySummary summary) =>
        Report(probe, metric, summary.ToString());
}

public enum ProbeWriteOutcome
{
    Success,
    OwnershipLost,
    LockTimeout,
}

/// <summary>
/// <see cref="Planned"/> is spec v3.1 as written: <c>IX_Job_Claim (Status, NextAttemptAt, Id)</c>, <c>NextAttemptAt</c>
/// null until a retry or release, and claims ordered by <c>COALESCE(NextAttemptAt, CreatedAt), Id</c>.
/// <see cref="IndexOrdered"/> is the step 0.2 proposal: <c>NextAttemptAt</c> set to the enqueue time so every active
/// row carries its eligibility time, <c>IX_Job_Claim (NextAttemptAt, Id)</c> over the active statuses, claims ordered
/// by <c>NextAttemptAt, Id</c>, and a redundant <c>Status IN (N'Pending', N'InProgress')</c> conjunct on claim and
/// exhaust, without which SQL Server does not match the filtered index.
/// </summary>
public enum ClaimShape
{
    Planned,
    IndexOrdered,
}

public sealed record ClaimedProbeJob(long Id, long FencingToken, int AttemptCount);

public sealed record LatencySummary(int Count, double P50, double P95, double P99, double Max)
{
    /// <summary>
    /// Nearest-rank percentiles.
    /// </summary>
    public static LatencySummary From(IEnumerable<double> samples)
    {
        double[] sorted = [.. samples.Order()];
        if (sorted.Length == 0)
        {
            return new LatencySummary(0, 0, 0, 0, 0);
        }

        double Rank(double percentile) =>
            sorted[Math.Max(0, (int)Math.Ceiling(percentile / 100 * sorted.Length) - 1)];

        return new LatencySummary(sorted.Length, Rank(50), Rank(95), Rank(99), sorted[^1]);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"n={Count} p50={P50:F2}ms p95={P95:F2}ms p99={P99:F2}ms max={Max:F2}ms"
        );
}

/// <summary>
/// The planned statements. Table and index shapes follow spec §3 with the schema renamed to <c>dmscs_probe</c>.
/// </summary>
internal static class JobProbeSql
{
    public const string DropSchema = """
        IF OBJECT_ID(N'dmscs_probe.Job', N'U') IS NOT NULL DROP TABLE dmscs_probe.Job;
        IF OBJECT_ID(N'dmscs_probe.JobSchedule', N'U') IS NOT NULL DROP TABLE dmscs_probe.JobSchedule;
        IF OBJECT_ID(N'dmscs_probe.Tenant', N'U') IS NOT NULL DROP TABLE dmscs_probe.Tenant;
        IF SCHEMA_ID(N'dmscs_probe') IS NOT NULL DROP SCHEMA dmscs_probe;
        """;

    public const string CreateSchema = "CREATE SCHEMA dmscs_probe;";

    private const string PayloadObjectCheck =
        "ISJSON(Payload) = 1 AND SUBSTRING(Payload, PATINDEX(N'%[^ ' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%', Payload), 1) = N'{'";

    private static string ClaimIndex(ClaimShape shape) =>
        shape == ClaimShape.Planned
            ? "CREATE INDEX IX_Job_Claim ON dmscs_probe.Job (Status, NextAttemptAt, Id) INCLUDE (LeaseExpiresAt, AttemptCount) WHERE Status IN (N'Pending', N'InProgress');"
            : "CREATE INDEX IX_Job_Claim ON dmscs_probe.Job (NextAttemptAt, Id) INCLUDE (Status, LeaseExpiresAt, AttemptCount) WHERE Status IN (N'Pending', N'InProgress');";

    public static string CreateTables(ClaimShape shape) =>
        $"""
            CREATE TABLE dmscs_probe.Tenant (
                Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tenant PRIMARY KEY,
                TenantName NVARCHAR(256) NOT NULL
            );

            INSERT INTO dmscs_probe.Tenant (TenantName) VALUES (N'probe-a'), (N'probe-b'), (N'probe-c');

            CREATE TABLE dmscs_probe.JobSchedule (
                Id BIGINT IDENTITY(1,1) NOT NULL,
                TenantId BIGINT NULL,
                ScheduleType NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL,
                JobType NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL,
                PayloadVersion SMALLINT NOT NULL,
                Payload NVARCHAR(4000) NOT NULL,
                IntervalMinutes INT NOT NULL,
                Enabled BIT NOT NULL,
                NextRunAt DATETIME2 NOT NULL,
                LastEnqueuedOccurrence DATETIME2 NULL,
                LeaseOwner NVARCHAR(200) NULL,
                LeaseExpiresAt DATETIME2 NULL,
                FencingToken BIGINT NOT NULL CONSTRAINT DF_JobSchedule_FencingToken DEFAULT 0,
                CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_JobSchedule_CreatedAt DEFAULT SYSUTCDATETIME(),
                CreatedBy NVARCHAR(256),
                LastModifiedAt DATETIME2,
                ModifiedBy NVARCHAR(256),
                CONSTRAINT PK_JobSchedule PRIMARY KEY (Id),
                CONSTRAINT FK_JobSchedule_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs_probe.Tenant (Id) ON DELETE NO ACTION,
                CONSTRAINT CK_JobSchedule_Payload_Object CHECK ({PayloadObjectCheck}),
                CONSTRAINT CK_JobSchedule_IntervalMinutes CHECK (IntervalMinutes BETWEEN 1 AND 527040)
            );

            CREATE UNIQUE INDEX UX_JobSchedule_Tenant_Type ON dmscs_probe.JobSchedule (TenantId, ScheduleType) WHERE TenantId IS NOT NULL;
            CREATE UNIQUE INDEX UX_JobSchedule_SingleTenant_Type ON dmscs_probe.JobSchedule (ScheduleType) WHERE TenantId IS NULL;
            CREATE INDEX IX_JobSchedule_Due ON dmscs_probe.JobSchedule (Enabled, NextRunAt);
            CREATE INDEX IX_JobSchedule_TenantId ON dmscs_probe.JobSchedule (TenantId);

            CREATE TABLE dmscs_probe.Job (
                Id BIGINT IDENTITY(1,1) NOT NULL,
                JobId NVARCHAR(150) COLLATE Latin1_General_BIN2 NOT NULL,
                TenantId BIGINT NULL,
                JobType NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL,
                PayloadVersion SMALLINT NOT NULL,
                Payload NVARCHAR(4000) NOT NULL,
                SourceScheduleId BIGINT NULL,
                ScheduledOccurrence DATETIME2 NULL,
                Status NVARCHAR(20) NOT NULL,
                CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Job_CreatedAt DEFAULT SYSUTCDATETIME(),
                FinishedAt DATETIME2 NULL,
                NextAttemptAt DATETIME2 NULL,
                LeaseExpiresAt DATETIME2 NULL,
                ErrorMessage NVARCHAR(1000) NULL,
                AttemptCount INT NOT NULL CONSTRAINT DF_Job_AttemptCount DEFAULT 0,
                LeaseOwner NVARCHAR(200) NULL,
                FencingToken BIGINT NOT NULL CONSTRAINT DF_Job_FencingToken DEFAULT 0,
                CreatedBy NVARCHAR(256),
                LastModifiedAt DATETIME2,
                ModifiedBy NVARCHAR(256),
                CONSTRAINT PK_Job PRIMARY KEY (Id),
                CONSTRAINT FK_Job_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs_probe.Tenant (Id) ON DELETE NO ACTION,
                CONSTRAINT FK_Job_JobSchedule FOREIGN KEY (SourceScheduleId) REFERENCES dmscs_probe.JobSchedule (Id) ON DELETE NO ACTION,
                CONSTRAINT CK_Job_Payload_Object CHECK ({PayloadObjectCheck}),
                CONSTRAINT CK_Job_Occurrence_Pairing CHECK (
                    (SourceScheduleId IS NULL AND ScheduledOccurrence IS NULL)
                    OR (SourceScheduleId IS NOT NULL AND ScheduledOccurrence IS NOT NULL)),
                CONSTRAINT CK_Job_Status CHECK (Status IN (N'Pending', N'InProgress', N'Completed', N'Error')),
                CONSTRAINT CK_Job_AttemptCount CHECK (AttemptCount >= 0)
            );

            CREATE UNIQUE INDEX UX_Job_JobId ON dmscs_probe.Job (JobId);
            CREATE UNIQUE INDEX UX_Job_SourceScheduleId_ScheduledOccurrence ON dmscs_probe.Job (SourceScheduleId, ScheduledOccurrence)
                WHERE SourceScheduleId IS NOT NULL AND ScheduledOccurrence IS NOT NULL;
            {ClaimIndex(shape)}
            CREATE INDEX IX_Job_Retention ON dmscs_probe.Job (Status, FinishedAt) WHERE Status IN (N'Completed', N'Error');
            CREATE INDEX IX_Job_TenantId ON dmscs_probe.Job (TenantId);
            """;

    public const string SeedJobs = """
        WITH numbers AS (
            SELECT TOP (@Count) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS INT) AS n
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
        )
        INSERT INTO dmscs_probe.Job (
            JobId, TenantId, JobType, PayloadVersion, Payload, Status, CreatedAt, NextAttemptAt, FinishedAt,
            LeaseExpiresAt, ErrorMessage, AttemptCount, LeaseOwner, FencingToken, CreatedBy)
        SELECT
            LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')),
            CASE WHEN n % 4 = 0 THEN NULL ELSE 1 + n % 3 END,
            N'DataStore.RefreshEducationOrganizations',
            1,
            CONCAT(N'{"dataStoreId":', n, N'}'),
            @Status,
            DATEADD(millisecond, -(@Count - n), DATEADD(second, -@CreatedAgeSeconds, SYSUTCDATETIME())),
            CASE WHEN @AvailableAtEnqueue = 1 AND @Status IN (N'Pending', N'InProgress')
                THEN DATEADD(millisecond, -(@Count - n), DATEADD(second, -@CreatedAgeSeconds, SYSUTCDATETIME()))
            END,
            CASE WHEN @Status IN (N'Completed', N'Error') THEN DATEADD(second, -@FinishedAgeSeconds, SYSUTCDATETIME()) END,
            CASE WHEN @Status = N'InProgress' THEN DATEADD(second, @LeaseRemainingSeconds, SYSUTCDATETIME()) END,
            CASE WHEN @Status = N'Error' THEN N'The job handler failed.' END,
            @AttemptCount,
            CASE WHEN @Status = N'InProgress' THEN @LeaseOwner END,
            @AttemptCount,
            N'probe'
        FROM numbers;
        """;

    /// <summary>D-3.</summary>
    public static string Claim(ClaimShape shape) =>
        shape == ClaimShape.Planned
            ? $"""
                WITH candidate AS (
                    SELECT TOP (1) Id, Status, LeaseOwner, LeaseExpiresAt, FencingToken, AttemptCount, LastModifiedAt, ModifiedBy
                    FROM dmscs_probe.Job WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE ((Status = N'Pending' AND (NextAttemptAt IS NULL OR NextAttemptAt <= SYSUTCDATETIME()))
                        OR (Status = N'InProgress' AND LeaseExpiresAt <= SYSUTCDATETIME()))
                      AND AttemptCount < @MaxAttempts
                    ORDER BY COALESCE(NextAttemptAt, CreatedAt), Id
                )
                {ClaimUpdate}
                """
            : $"""
                WITH candidate AS (
                    SELECT TOP (1) Id, Status, LeaseOwner, LeaseExpiresAt, FencingToken, AttemptCount, LastModifiedAt, ModifiedBy
                    FROM dmscs_probe.Job WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE ((Status = N'Pending' AND NextAttemptAt <= SYSUTCDATETIME())
                        OR (Status = N'InProgress' AND LeaseExpiresAt <= SYSUTCDATETIME()))
                      AND Status IN (N'Pending', N'InProgress')
                      AND AttemptCount < @MaxAttempts
                    ORDER BY NextAttemptAt, Id
                )
                {ClaimUpdate}
                """;

    private const string ClaimUpdate = """
        UPDATE candidate
        SET Status = N'InProgress',
            LeaseOwner = @Owner,
            LeaseExpiresAt = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME()),
            FencingToken = FencingToken + 1,
            AttemptCount = AttemptCount + 1,
            LastModifiedAt = SYSUTCDATETIME(),
            ModifiedBy = @Owner
        OUTPUT inserted.Id, inserted.FencingToken, inserted.AttemptCount, inserted.LeaseExpiresAt, SYSUTCDATETIME() AS DbNow;
        """;

    /// <summary>D-4 step 1 (the fixed 5 s WriteLockWait).</summary>
    public const string SetWriteLockWait = "SET LOCK_TIMEOUT 5000;";

    /// <summary>D-4 step 2.</summary>
    public const string LockJobRow = "SELECT Id FROM dmscs_probe.Job WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id;";

    private const string OwnershipPredicate = """
        Id = @Id AND LeaseOwner = @Owner AND FencingToken = @Token AND Status = N'InProgress'
          AND LeaseExpiresAt > SYSUTCDATETIME()
        """;

    /// <summary>D-4 step 3 for Renew.</summary>
    public const string Renew = $"""
        UPDATE dmscs_probe.Job
        SET LeaseExpiresAt = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME()),
            LastModifiedAt = SYSUTCDATETIME(),
            ModifiedBy = @Owner
        OUTPUT inserted.LeaseExpiresAt
        WHERE {OwnershipPredicate};
        """;

    /// <summary>D-4 step 3 for Complete.</summary>
    public const string Complete = $"""
        UPDATE dmscs_probe.Job
        SET Status = N'Completed',
            FinishedAt = SYSUTCDATETIME(),
            NextAttemptAt = NULL,
            LeaseOwner = NULL,
            LeaseExpiresAt = NULL,
            LastModifiedAt = SYSUTCDATETIME(),
            ModifiedBy = @Owner
        OUTPUT inserted.Id
        WHERE {OwnershipPredicate};
        """;

    /// <summary>D-6 Exhaust: single statement, skipping locked rows.</summary>
    public static string Exhaust(ClaimShape shape) =>
        $"""
            UPDATE dmscs_probe.Job WITH (READPAST, ROWLOCK)
            SET Status = N'Error',
                FinishedAt = SYSUTCDATETIME(),
                ErrorMessage = @Message,
                FencingToken = FencingToken + 1,
                LeaseOwner = NULL,
                LeaseExpiresAt = NULL,
                LastModifiedAt = SYSUTCDATETIME(),
                ModifiedBy = @Owner
            WHERE AttemptCount >= @MaxAttempts
              AND (Status = N'Pending' OR (Status = N'InProgress' AND LeaseExpiresAt <= SYSUTCDATETIME()))
              {(shape == ClaimShape.Planned ? "" : "AND Status IN (N'Pending', N'InProgress')")};
            """;

    /// <summary>D-17: one bounded batch.</summary>
    public const string RetentionBatch = """
        DELETE TOP (@BatchSize) FROM dmscs_probe.Job WITH (READPAST, ROWLOCK)
        WHERE Status IN (N'Completed', N'Error')
          AND FinishedAt <= DATEADD(second, -@RetentionSeconds, SYSUTCDATETIME());
        """;

    /// <summary>
    /// D-9 coalescing expression: estimate <c>j0</c> from <c>DATEDIFF_BIG(second, …)</c>, which counts second
    /// boundaries and can overestimate the elapsed time by less than one second, then choose the boundary if it is in
    /// the future, else the next one. The same text serves the live advancing UPDATE and the injected-time vectors.
    /// </summary>
    public static string Advance(string nextRunAt, string intervalMinutes, string now)
    {
        string boundary =
            $"DATEADD(minute, CAST(IIF(DATEDIFF_BIG(second, {nextRunAt}, {now}) < 0, 0, DATEDIFF_BIG(second, {nextRunAt}, {now}) / ({intervalMinutes} * 60)) AS INT) * {intervalMinutes}, {nextRunAt})";
        return $"IIF({boundary} > {now}, {boundary}, DATEADD(minute, {intervalMinutes}, {boundary}))";
    }

    public static readonly string AdvanceWithInjectedNow =
        $"SELECT {Advance("@NextRunAt", "@IntervalMinutes", "@Now")};";

    /// <summary>D-8 step 1.</summary>
    public const string LockDueSchedule = """
        SELECT TOP (1) Id, TenantId, JobType, PayloadVersion, Payload, IntervalMinutes, NextRunAt, FencingToken
        FROM dmscs_probe.JobSchedule WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE Enabled = 1
          AND NextRunAt <= SYSUTCDATETIME()
          AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt <= SYSUTCDATETIME())
        ORDER BY NextRunAt, Id;
        """;

    /// <summary>D-8 step 2.</summary>
    public const string LeaseSchedule = """
        UPDATE dmscs_probe.JobSchedule
        SET LeaseOwner = @Owner,
            LeaseExpiresAt = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME()),
            FencingToken = FencingToken + 1
        OUTPUT inserted.FencingToken
        WHERE Id = @Id;
        """;

    /// <summary>D-8 step 3.</summary>
    public const string InsertOccurrence = """
        INSERT INTO dmscs_probe.Job (
            JobId, TenantId, JobType, PayloadVersion, Payload, SourceScheduleId, ScheduledOccurrence, Status, CreatedBy)
        SELECT @JobId, @TenantId, @JobType, @PayloadVersion, @Payload, @Id, @Occurrence, N'Pending', N'system'
        WHERE NOT EXISTS (
            SELECT 1 FROM dmscs_probe.Job WHERE SourceScheduleId = @Id AND ScheduledOccurrence = @Occurrence);
        """;

    /// <summary>D-8 step 4, the coalescing decision point, with one time sample.</summary>
    public static readonly string AdvanceSchedule = $"""
        UPDATE s
        SET LastEnqueuedOccurrence = s.NextRunAt,
            NextRunAt = {Advance("s.NextRunAt", "s.IntervalMinutes", "t.[now]")},
            LeaseOwner = NULL,
            LeaseExpiresAt = NULL,
            LastModifiedAt = t.[now],
            ModifiedBy = @Owner
        OUTPUT inserted.NextRunAt, t.[now]
        FROM dmscs_probe.JobSchedule AS s
        CROSS APPLY (SELECT SYSUTCDATETIME() AS [now]) AS t
        WHERE s.Id = @Id
          AND s.LeaseOwner = @Owner
          AND s.FencingToken = @Token
          AND s.LeaseExpiresAt > t.[now]
          AND s.Enabled = 1;
        """;
}

/// <summary>
/// Probe 1: claim latency with 10 000 pending rows and three concurrent claimers, then idle-poll latency against a
/// queue whose remaining rows are all leased.
/// </summary>
[TestFixture(ClaimShape.Planned)]
[TestFixture(ClaimShape.IndexOrdered)]
[Explicit("Operational probe (DMS-1437 step 0.2); run manually with --filter Category=OperationalProbe.")]
public class Given_three_claimers_draining_a_backlog_of_10000_pending_jobs(ClaimShape shape)
    : JobOperationalProbeBase
{
    protected override ClaimShape Shape => shape;

    private const int PendingJobs = 10_000;
    private const int Claimers = 3;
    private const int ClaimsPerClaimer = 1_000;
    private const int IdlePollsPerClaimer = 100;

    private readonly ConcurrentBag<double> _claimLatencies = [];
    private readonly ConcurrentBag<double> _idlePollLatencies = [];
    private readonly ConcurrentBag<long> _claimedIds = [];
    private readonly ConcurrentBag<int> _claimErrors = [];
    private int _emptyClaimsWithBacklog;
    private int _idlePollsReturningRows;
    private long _claimedRowsInDatabase;
    private long _claimedRowsWithFirstAttemptTokens;
    private int _tableLocksHeldByOneClaim;
    private int _keyLocksHeldByOneClaim;

    [OneTimeSetUp]
    public async Task Setup()
    {
        await SeedJobsAsync("Pending", PendingJobs, attemptCount: 0, createdAgeSeconds: 60);
        await UpdateStatisticsAsync();
        (_tableLocksHeldByOneClaim, _keyLocksHeldByOneClaim) = await MeasureOneClaimLockFootprintAsync();
        Report(
            $"claim[{shape}]",
            "locks_held_by_one_claim",
            $"object_non_intent={_tableLocksHeldByOneClaim} key={_keyLocksHeldByOneClaim}"
        );

        long started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, Claimers).Select(ClaimBacklogAsync));
        double wallMilliseconds = ElapsedMilliseconds(started);

        _claimedRowsInDatabase = await ScalarAsync(
            "SELECT COUNT_BIG(*) FROM dmscs_probe.Job WHERE Status = N'InProgress' AND LeaseOwner LIKE N'probe-claimer-%'"
        );
        _claimedRowsWithFirstAttemptTokens = await ScalarAsync(
            "SELECT COUNT_BIG(*) FROM dmscs_probe.Job WHERE Status = N'InProgress' AND FencingToken = 1 AND AttemptCount = 1"
        );

        await ExecuteAsync(
            """
            UPDATE dmscs_probe.Job
            SET Status = N'InProgress', LeaseOwner = N'probe-busy', AttemptCount = 1, FencingToken = 1,
                LeaseExpiresAt = DATEADD(hour, 1, SYSUTCDATETIME())
            WHERE Status = N'Pending';
            """
        );
        await UpdateStatisticsAsync();
        await Task.WhenAll(Enumerable.Range(0, Claimers).Select(PollIdleQueueAsync));

        Report($"claim[{shape}]", "backlog_claims", LatencySummary.From(_claimLatencies));
        Report(
            $"claim[{shape}]",
            "backlog_throughput_claims_per_s",
            (_claimedIds.Count / (wallMilliseconds / 1000)).ToString("F0", CultureInfo.InvariantCulture)
        );
        Report($"claim[{shape}]", "empty_claims_with_backlog", _emptyClaimsWithBacklog);
        Report($"claim[{shape}]", "claim_errors", string.Join(",", _claimErrors.Order()));
        Report($"claim[{shape}]", "idle_polls_10000_leased", LatencySummary.From(_idlePollLatencies));
    }

    /// <summary>
    /// Runs one claim inside an explicit transaction and counts the locks it still holds: table-level locks other than
    /// intent locks (an escalation) and key locks. The transaction is rolled back.
    /// </summary>
    private async Task<(int TableLocks, int KeyLocks)> MeasureOneClaimLockFootprintAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using (SqlCommand claim = Command(connection, transaction, JobProbeSql.Claim(Shape)))
        {
            claim.Parameters.AddWithValue("@Owner", "probe-footprint");
            claim.Parameters.AddWithValue("@LeaseSeconds", LeaseSeconds);
            claim.Parameters.AddWithValue("@MaxAttempts", MaxAttempts);
            await using SqlDataReader claimed = await claim.ExecuteReaderAsync();
            while (await claimed.ReadAsync())
            {
                // Drain the OUTPUT row so the statement completes before the lock query runs.
            }
        }

        (int TableLocks, int KeyLocks) footprint;
        await using (
            SqlCommand locks = Command(
                connection,
                transaction,
                """
                SELECT
                    COUNT(CASE WHEN resource_type = N'OBJECT' AND request_mode NOT LIKE N'I%' THEN 1 END),
                    COUNT(CASE WHEN resource_type = N'KEY' THEN 1 END)
                FROM sys.dm_tran_locks
                WHERE request_session_id = @@SPID AND resource_database_id = DB_ID();
                """
            )
        )
        await using (SqlDataReader reader = await locks.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            footprint = (reader.GetInt32(0), reader.GetInt32(1));
        }

        await transaction.RollbackAsync();
        return footprint;
    }

    private async Task ClaimBacklogAsync(int claimer)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        string owner = $"probe-claimer-{claimer}";
        for (int i = 0; i < ClaimsPerClaimer; i++)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                ClaimedProbeJob? claimed = await ClaimAsync(connection, owner);
                _claimLatencies.Add(ElapsedMilliseconds(start));
                if (claimed is null)
                {
                    Interlocked.Increment(ref _emptyClaimsWithBacklog);
                    continue;
                }
                _claimedIds.Add(claimed.Id);
            }
            catch (SqlException exception)
            {
                _claimLatencies.Add(ElapsedMilliseconds(start));
                _claimErrors.Add(exception.Number);
            }
        }
    }

    private async Task PollIdleQueueAsync(int claimer)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        for (int i = 0; i < IdlePollsPerClaimer; i++)
        {
            long start = Stopwatch.GetTimestamp();
            ClaimedProbeJob? claimed = await ClaimAsync(connection, $"probe-idle-{claimer}");
            _idlePollLatencies.Add(ElapsedMilliseconds(start));
            if (claimed is not null)
            {
                Interlocked.Increment(ref _idlePollsReturningRows);
            }
        }
    }

    [Test]
    public void It_claims_one_job_with_row_level_locks_only()
    {
        _tableLocksHeldByOneClaim.Should().Be(0);
        _keyLocksHeldByOneClaim.Should().BeLessThanOrEqualTo(4);
    }

    [Test]
    public void It_claims_the_backlog_with_p99_at_or_below_250_ms() =>
        LatencySummary.From(_claimLatencies).P99.Should().BeLessThanOrEqualTo(250);

    [Test]
    public void It_never_claims_the_same_job_twice()
    {
        _claimErrors.Should().BeEmpty();
        _claimedIds.Should().HaveCount(Claimers * ClaimsPerClaimer);
        _claimedIds.Distinct().Should().HaveCount(Claimers * ClaimsPerClaimer);
        _claimedRowsInDatabase.Should().Be(Claimers * ClaimsPerClaimer);
        _claimedRowsWithFirstAttemptTokens.Should().Be(Claimers * ClaimsPerClaimer);
    }

    [Test]
    public void It_never_returns_an_empty_claim_while_eligible_jobs_remain() =>
        _emptyClaimsWithBacklog.Should().Be(0);

    [Test]
    public void It_polls_a_fully_leased_queue_with_p99_at_or_below_250_ms()
    {
        _idlePollsReturningRows.Should().Be(0);
        LatencySummary.From(_idlePollLatencies).P99.Should().BeLessThanOrEqualTo(250);
    }
}

/// <summary>
/// Probe 2: lock-then-validate renewal while another session holds the job row lock. The spec's literal 5 s hold
/// equals the fixed 5 s WriteLockWait, so the probe brackets it: a 4 s hold (the renewal must wait, then succeed),
/// a 6 s hold (the renewal must report a lock timeout), and a 3 s hold over a lease that expires after 2 s (the
/// renewal must be rejected by the fresh-time predicate once it holds the lock).
/// </summary>
[TestFixture]
[Explicit("Operational probe (DMS-1437 step 0.2); run manually with --filter Category=OperationalProbe.")]
public class Given_renewals_waiting_on_a_job_row_lock_held_by_another_session : JobOperationalProbeBase
{
    private const string Owner = "probe-renewer";
    private const int ConcurrentRows = 10;
    private const int LiveRounds = 10;
    private const int ExpiringRows = 20;
    private static readonly TimeSpan _shortHold = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan _longHold = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan _expiringHold = TimeSpan.FromSeconds(3);

    private readonly List<RenewalSample> _liveSamples = [];
    private RenewalSample[] _expiredSamples = [];
    private RenewalSample[] _timedOutSamples = [];
    private long _expiredRowsRenewed;

    private sealed record RenewalSample(
        ProbeWriteOutcome Outcome,
        bool LeaseLiveWhenContended,
        double TotalMilliseconds,
        double AfterReleaseMilliseconds
    );

    [OneTimeSetUp]
    public async Task Setup()
    {
        await SeedJobsAsync("InProgress", ConcurrentRows, attemptCount: 1, leaseOwner: Owner);
        long[] liveIds = await IdsAsync(Owner);
        for (int round = 0; round < LiveRounds; round++)
        {
            _liveSamples.AddRange(await ContendAsync(liveIds, _shortHold));
        }

        await ExecuteAsync("DELETE FROM dmscs_probe.Job;");
        await SeedJobsAsync("InProgress", ExpiringRows, attemptCount: 1, leaseOwner: Owner);
        long[] expiringIds = await IdsAsync(Owner);
        _expiredSamples = await ContendAsync(expiringIds, _expiringHold, leaseSecondsBeforeLock: 2);
        _expiredRowsRenewed = await ScalarAsync(
            "SELECT COUNT_BIG(*) FROM dmscs_probe.Job WHERE LeaseExpiresAt > SYSUTCDATETIME()"
        );

        await ExecuteAsync("DELETE FROM dmscs_probe.Job;");
        await SeedJobsAsync("InProgress", ConcurrentRows, attemptCount: 1, leaseOwner: Owner);
        _timedOutSamples = await ContendAsync(await IdsAsync(Owner), _longHold);

        Report(
            "renewal",
            "after_release_4s_hold",
            LatencySummary.From(_liveSamples.Select(sample => sample.AfterReleaseMilliseconds))
        );
        Report(
            "renewal",
            "total_4s_hold",
            LatencySummary.From(_liveSamples.Select(sample => sample.TotalMilliseconds))
        );
        Report("renewal", "outcomes_4s_hold", Outcomes(_liveSamples));
        Report("renewal", "outcomes_expired_during_3s_hold", Outcomes(_expiredSamples));
        Report("renewal", "outcomes_6s_hold", Outcomes(_timedOutSamples));
        Report(
            "renewal",
            "time_to_lock_timeout_6s_hold",
            LatencySummary.From(_timedOutSamples.Select(sample => sample.TotalMilliseconds))
        );
    }

    private static string Outcomes(IEnumerable<RenewalSample> samples) =>
        string.Join(
            ",",
            samples
                .GroupBy(sample => sample.Outcome)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}")
        );

    private async Task<long[]> IdsAsync(string owner)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await using SqlCommand command = new(
            "SELECT Id FROM dmscs_probe.Job WHERE LeaseOwner = @Owner ORDER BY Id;",
            connection
        );
        command.Parameters.AddWithValue("@Owner", owner);
        List<long> ids = [];
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return [.. ids];
    }

    /// <summary>
    /// Opens every connection first, optionally resets the leases, then starts one holder/renewer pair per row with
    /// holder starts staggered by 100 ms so that no two releases coincide.
    /// </summary>
    private async Task<RenewalSample[]> ContendAsync(
        IReadOnlyList<long> ids,
        TimeSpan hold,
        int? leaseSecondsBeforeLock = null
    )
    {
        List<(SqlConnection Holder, SqlConnection Renewer)> pairs = [];
        try
        {
            foreach (long _ in ids)
            {
                pairs.Add((await OpenConnectionAsync(), await OpenConnectionAsync()));
            }

            if (leaseSecondsBeforeLock is { } leaseSeconds)
            {
                await ExecuteAsync(
                    """
                    UPDATE dmscs_probe.Job
                    SET LeaseExpiresAt = DATEADD(second, @Seconds, SYSUTCDATETIME())
                    WHERE LeaseOwner = @Owner;
                    """,
                    ("@Seconds", leaseSeconds),
                    ("@Owner", Owner)
                );
            }

            return await Task.WhenAll(
                ids.Select(
                    (id, index) =>
                        ContendOneAsync(
                            id,
                            pairs[index].Holder,
                            pairs[index].Renewer,
                            leaseSecondsBeforeLock is null
                                ? TimeSpan.FromMilliseconds(100 * index)
                                : TimeSpan.Zero,
                            hold
                        )
                )
            );
        }
        finally
        {
            foreach ((SqlConnection holder, SqlConnection renewer) in pairs)
            {
                await holder.DisposeAsync();
                await renewer.DisposeAsync();
            }
        }
    }

    private static async Task<RenewalSample> ContendOneAsync(
        long id,
        SqlConnection holder,
        SqlConnection renewer,
        TimeSpan stagger,
        TimeSpan hold
    )
    {
        await Task.Delay(stagger);
        await using SqlTransaction holderTransaction = (SqlTransaction)await holder.BeginTransactionAsync();

        bool leaseLive;
        await using (
            SqlCommand lockRow = Command(
                holder,
                holderTransaction,
                """
                SELECT IIF(LeaseExpiresAt > SYSUTCDATETIME(), 1, 0)
                FROM dmscs_probe.Job WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id;
                """
            )
        )
        {
            lockRow.Parameters.AddWithValue("@Id", id);
            leaseLive = (int)(await lockRow.ExecuteScalarAsync())! == 1;
        }

        long started = Stopwatch.GetTimestamp();
        Task<(ProbeWriteOutcome Outcome, long Finished)> renewal = Task.Run(async () =>
        {
            ProbeWriteOutcome outcome = await OwnershipWriteAsync(
                renewer,
                JobProbeSql.Renew,
                id,
                Owner,
                token: 1
            );
            return (outcome, Stopwatch.GetTimestamp());
        });

        await Task.Delay(hold);
        long released = Stopwatch.GetTimestamp();
        await holderTransaction.CommitAsync();

        (ProbeWriteOutcome outcome, long finished) = await renewal;
        return new RenewalSample(
            outcome,
            leaseLive,
            Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds,
            Stopwatch.GetElapsedTime(released, finished).TotalMilliseconds
        );
    }

    [Test]
    public void It_contends_only_while_the_lease_is_live() =>
        _liveSamples
            .Concat(_expiredSamples)
            .Concat(_timedOutSamples)
            .Should()
            .OnlyContain(sample => sample.LeaseLiveWhenContended);

    [Test]
    public void It_waits_for_the_holder_before_renewing() =>
        _liveSamples
            .Should()
            .OnlyContain(sample => sample.TotalMilliseconds >= _shortHold.TotalMilliseconds - 50);

    [Test]
    public void It_renews_every_live_lease_after_the_lock_is_released()
    {
        _liveSamples.Should().HaveCount(ConcurrentRows * LiveRounds);
        _liveSamples.Should().OnlyContain(sample => sample.Outcome == ProbeWriteOutcome.Success);
    }

    [Test]
    public void It_renews_within_100_ms_of_the_lock_release_at_p99() =>
        LatencySummary
            .From(_liveSamples.Select(sample => sample.AfterReleaseMilliseconds))
            .P99.Should()
            .BeLessThanOrEqualTo(100);

    [Test]
    public void It_rejects_every_renewal_whose_lease_expired_during_the_wait()
    {
        _expiredSamples.Should().HaveCount(ExpiringRows);
        _expiredSamples.Should().OnlyContain(sample => sample.Outcome == ProbeWriteOutcome.OwnershipLost);
        _expiredRowsRenewed.Should().Be(0);
    }

    [Test]
    public void It_reports_a_lock_timeout_when_the_holder_outlasts_the_write_lock_wait() =>
        _timedOutSamples.Should().OnlyContain(sample => sample.Outcome == ProbeWriteOutcome.LockTimeout);
}

/// <summary>
/// Probe 3: <c>Exhaust</c> over 100 000 rows (80 000 finished, 10 000 pending under the limit, 9 900 leased under
/// the limit, 50 pending and 50 expired-leased at the limit), then the steady-state sweep that finds nothing, then
/// the one-off sweep after <c>MaxAttempts</c> is lowered to 1.
/// </summary>
[TestFixture(ClaimShape.Planned)]
[TestFixture(ClaimShape.IndexOrdered)]
[Explicit("Operational probe (DMS-1437 step 0.2); run manually with --filter Category=OperationalProbe.")]
public class Given_an_exhaust_sweep_over_100000_jobs(ClaimShape shape) : JobOperationalProbeBase
{
    protected override ClaimShape Shape => shape;

    private const int SteadyStateSweeps = 50;

    private double _firstSweepMilliseconds;
    private int _firstSweepRows;
    private readonly List<double> _steadyStateSweeps = [];
    private readonly List<int> _steadyStateRows = [];
    private long _exhaustedRows;
    private long _untouchedActiveRows;
    private double _loweredLimitSweepMilliseconds;
    private int _loweredLimitSweepRows;

    [OneTimeSetUp]
    public async Task Setup()
    {
        await SeedJobsAsync(
            "Completed",
            40_000,
            attemptCount: 1,
            createdAgeSeconds: 86_400,
            finishedAgeSeconds: 86_000
        );
        await SeedJobsAsync(
            "Error",
            40_000,
            attemptCount: 5,
            createdAgeSeconds: 86_400,
            finishedAgeSeconds: 86_000
        );
        await SeedJobsAsync("Pending", 10_000, attemptCount: 2, createdAgeSeconds: 600);
        await SeedJobsAsync("InProgress", 9_900, attemptCount: 2, createdAgeSeconds: 600);
        await SeedJobsAsync("Pending", 50, attemptCount: MaxAttempts, createdAgeSeconds: 600);
        await SeedJobsAsync(
            "InProgress",
            50,
            attemptCount: MaxAttempts,
            createdAgeSeconds: 600,
            leaseRemainingSeconds: -60
        );
        await UpdateStatisticsAsync();

        await using SqlConnection connection = await OpenConnectionAsync();

        long start = Stopwatch.GetTimestamp();
        _firstSweepRows = await ExhaustAsync(connection);
        _firstSweepMilliseconds = ElapsedMilliseconds(start);

        for (int i = 0; i < SteadyStateSweeps; i++)
        {
            start = Stopwatch.GetTimestamp();
            _steadyStateRows.Add(await ExhaustAsync(connection));
            _steadyStateSweeps.Add(ElapsedMilliseconds(start));
        }

        _exhaustedRows = await ScalarAsync(
            """
            SELECT COUNT_BIG(*) FROM dmscs_probe.Job
            WHERE Status = N'Error' AND ErrorMessage = @Message AND LeaseOwner IS NULL
              AND LeaseExpiresAt IS NULL AND FencingToken = @Token
            """,
            ("@Message", ExhaustedMessage),
            ("@Token", (long)MaxAttempts + 1)
        );
        _untouchedActiveRows = await ScalarAsync(
            "SELECT COUNT_BIG(*) FROM dmscs_probe.Job WHERE Status IN (N'Pending', N'InProgress') AND AttemptCount = 2"
        );

        start = Stopwatch.GetTimestamp();
        _loweredLimitSweepRows = await ExhaustAsync(connection, maxAttempts: 1);
        _loweredLimitSweepMilliseconds = ElapsedMilliseconds(start);

        Report(
            $"exhaust[{shape}]",
            "first_sweep_ms",
            _firstSweepMilliseconds.ToString("F2", CultureInfo.InvariantCulture)
        );
        Report($"exhaust[{shape}]", "first_sweep_rows", _firstSweepRows);
        Report($"exhaust[{shape}]", "steady_state_sweeps", LatencySummary.From(_steadyStateSweeps));
        Report(
            $"exhaust[{shape}]",
            "lowered_limit_sweep_ms",
            _loweredLimitSweepMilliseconds.ToString("F2", CultureInfo.InvariantCulture)
        );
        Report($"exhaust[{shape}]", "lowered_limit_sweep_rows", _loweredLimitSweepRows);
    }

    [Test]
    public void It_exhausts_exactly_the_rows_at_the_limit()
    {
        _firstSweepRows.Should().Be(100);
        _exhaustedRows.Should().Be(100);
        _steadyStateRows.Should().OnlyContain(rows => rows == 0);
    }

    [Test]
    public void It_leaves_rows_under_the_limit_untouched() => _untouchedActiveRows.Should().Be(19_900);

    [Test]
    public void It_runs_the_first_sweep_at_or_below_500_ms() =>
        _firstSweepMilliseconds.Should().BeLessThanOrEqualTo(500);

    [Test]
    public void It_runs_steady_state_sweeps_with_p99_at_or_below_500_ms() =>
        LatencySummary.From(_steadyStateSweeps).P99.Should().BeLessThanOrEqualTo(500);

    [Test]
    public void It_terminates_every_pending_row_over_a_lowered_limit_in_one_sweep() =>
        _loweredLimitSweepRows.Should().Be(10_000);
}

/// <summary>
/// Probe 4: retention batches of 500 over 100 000 finished rows (60 000 past the seven-day window, 40 000 inside
/// it) plus 2 000 old active rows that the predicate must never delete.
/// </summary>
[TestFixture]
[Explicit("Operational probe (DMS-1437 step 0.2); run manually with --filter Category=OperationalProbe.")]
public class Given_retention_batches_over_100000_finished_jobs : JobOperationalProbeBase
{
    private const int BatchSize = 500;
    private const int Batches = 20;
    private const int RetentionSeconds = 7 * 86_400;

    private readonly List<double> _batchLatencies = [];
    private readonly List<int> _batchRows = [];
    private long _remainingExpired;
    private long _remainingRecent;
    private long _remainingActive;

    [OneTimeSetUp]
    public async Task Setup()
    {
        const int thirtyDays = 30 * 86_400;
        await SeedJobsAsync(
            "Completed",
            30_000,
            1,
            createdAgeSeconds: thirtyDays,
            finishedAgeSeconds: 8 * 86_400
        );
        await SeedJobsAsync(
            "Error",
            30_000,
            5,
            createdAgeSeconds: thirtyDays,
            finishedAgeSeconds: 8 * 86_400
        );
        await SeedJobsAsync(
            "Completed",
            20_000,
            1,
            createdAgeSeconds: 2 * 86_400,
            finishedAgeSeconds: 86_400
        );
        await SeedJobsAsync("Error", 20_000, 5, createdAgeSeconds: 2 * 86_400, finishedAgeSeconds: 86_400);
        await SeedJobsAsync("Pending", 1_000, 1, createdAgeSeconds: thirtyDays);
        await SeedJobsAsync("InProgress", 1_000, 1, createdAgeSeconds: thirtyDays);
        await UpdateStatisticsAsync();

        await using SqlConnection connection = await OpenConnectionAsync();
        for (int i = 0; i < Batches; i++)
        {
            await using SqlCommand batch = Command(
                connection,
                null,
                JobProbeSql.RetentionBatch,
                RetentionCommandTimeoutSeconds
            );
            batch.Parameters.AddWithValue("@RetentionSeconds", RetentionSeconds);
            batch.Parameters.AddWithValue("@BatchSize", BatchSize);
            long start = Stopwatch.GetTimestamp();
            _batchRows.Add(await batch.ExecuteNonQueryAsync());
            _batchLatencies.Add(ElapsedMilliseconds(start));
        }

        _remainingExpired = await ScalarAsync(
            """
            SELECT COUNT_BIG(*) FROM dmscs_probe.Job
            WHERE Status IN (N'Completed', N'Error') AND FinishedAt <= DATEADD(day, -7, SYSUTCDATETIME())
            """
        );
        _remainingRecent = await ScalarAsync(
            """
            SELECT COUNT_BIG(*) FROM dmscs_probe.Job
            WHERE Status IN (N'Completed', N'Error') AND FinishedAt > DATEADD(day, -7, SYSUTCDATETIME())
            """
        );
        _remainingActive = await ScalarAsync(
            "SELECT COUNT_BIG(*) FROM dmscs_probe.Job WHERE Status IN (N'Pending', N'InProgress')"
        );

        Report("retention", "batch_500", LatencySummary.From(_batchLatencies));
    }

    [Test]
    public void It_deletes_exactly_one_batch_per_statement() =>
        _batchRows.Should().OnlyContain(rows => rows == BatchSize);

    [Test]
    public void It_deletes_only_finished_rows_past_the_window()
    {
        _remainingExpired.Should().Be(60_000 - (BatchSize * Batches));
        _remainingRecent.Should().Be(40_000);
        _remainingActive.Should().Be(2_000);
    }

    [Test]
    public void It_deletes_each_batch_at_or_below_1_s() =>
        _batchLatencies.Should().OnlyContain(milliseconds => milliseconds <= 1_000);
}

/// <summary>
/// Probe 5: three sessions each loop claim → renew → complete over a shared queue, running <c>Exhaust</c> every
/// tenth iteration as a worker poll would, until at least 1 000 claim/renew/complete operations have run.
/// </summary>
[TestFixture(ClaimShape.Planned)]
[TestFixture(ClaimShape.IndexOrdered)]
[Explicit("Operational probe (DMS-1437 step 0.2); run manually with --filter Category=OperationalProbe.")]
public class Given_three_sessions_running_1000_mixed_claim_renew_complete_operations(ClaimShape shape)
    : JobOperationalProbeBase
{
    protected override ClaimShape Shape => shape;

    private const int Sessions = 3;
    private const int SeededJobs = 500;
    private const int TargetOperations = 1_000;

    private long _operations;
    private int _deadlocks;
    private int _lockTimeouts;
    private int _ownershipLost;
    private int _exhaustedRows;
    private int _emptyClaims;
    private readonly ConcurrentBag<string> _unexpectedErrors = [];
    private readonly ConcurrentBag<long> _claimedIds = [];
    private readonly ConcurrentBag<long> _completedIds = [];
    private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _latencies = new();
    private long _completedRowsInDatabase;
    private long _rowsClaimedMoreThanOnce;

    [OneTimeSetUp]
    public async Task Setup()
    {
        await SeedJobsAsync("Pending", SeededJobs, attemptCount: 0, createdAgeSeconds: 60);
        await UpdateStatisticsAsync();

        await Task.WhenAll(Enumerable.Range(0, Sessions).Select(RunSessionAsync));

        _completedRowsInDatabase = await ScalarAsync(
            """
            SELECT COUNT_BIG(*) FROM dmscs_probe.Job
            WHERE Status = N'Completed' AND AttemptCount = 1 AND FencingToken = 1 AND LeaseOwner IS NULL
            """
        );
        _rowsClaimedMoreThanOnce = await ScalarAsync(
            "SELECT COUNT_BIG(*) FROM dmscs_probe.Job WHERE AttemptCount > 1 OR FencingToken > 1"
        );

        foreach ((string operation, ConcurrentBag<double> samples) in _latencies.OrderBy(pair => pair.Key))
        {
            Report($"mixed[{shape}]", operation, LatencySummary.From(samples));
        }
        Report($"mixed[{shape}]", "operations", _operations);
        Report($"mixed[{shape}]", "deadlocks", _deadlocks);
        Report($"mixed[{shape}]", "lock_timeouts", _lockTimeouts);
        Report($"mixed[{shape}]", "ownership_lost", _ownershipLost);
        Report($"mixed[{shape}]", "empty_claims_while_jobs_remain", _emptyClaims);
        Report($"mixed[{shape}]", "unexpected_errors", string.Join(",", _unexpectedErrors));
    }

    private async Task RunSessionAsync(int session)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        string owner = $"probe-session-{session}";
        int iteration = 0;

        while (Interlocked.Read(ref _operations) < TargetOperations)
        {
            try
            {
                if (++iteration % 10 == 0)
                {
                    int exhausted = await TimeAsync("exhaust", () => ExhaustAsync(connection));
                    Interlocked.Add(ref _exhaustedRows, exhausted);
                }

                ClaimedProbeJob? claim = await TimeAsync("claim", () => ClaimAsync(connection, owner));
                Interlocked.Increment(ref _operations);
                if (claim is not { } claimed)
                {
                    Interlocked.Increment(ref _emptyClaims);
                    continue;
                }
                _claimedIds.Add(claimed.Id);

                foreach (
                    (string operation, string sql) in new[]
                    {
                        ("renew", JobProbeSql.Renew),
                        ("complete", JobProbeSql.Complete),
                    }
                )
                {
                    ProbeWriteOutcome outcome = await TimeAsync(
                        operation,
                        () => OwnershipWriteAsync(connection, sql, claimed.Id, owner, claimed.FencingToken)
                    );
                    Interlocked.Increment(ref _operations);
                    if (outcome == ProbeWriteOutcome.OwnershipLost)
                    {
                        Interlocked.Increment(ref _ownershipLost);
                    }
                    else if (outcome == ProbeWriteOutcome.LockTimeout)
                    {
                        Interlocked.Increment(ref _lockTimeouts);
                    }
                    else if (operation == "complete")
                    {
                        _completedIds.Add(claimed.Id);
                    }
                }
            }
            catch (SqlException exception) when (exception.Number == DeadlockErrorNumber)
            {
                Interlocked.Increment(ref _deadlocks);
            }
            catch (SqlException exception)
            {
                _unexpectedErrors.Add(exception.Number.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private async Task<T> TimeAsync<T>(string operation, Func<Task<T>> action)
    {
        long start = Stopwatch.GetTimestamp();
        T result = await action();
        _latencies.GetOrAdd(operation, _ => []).Add(ElapsedMilliseconds(start));
        return result;
    }

    [Test]
    public void It_runs_at_least_1000_operations() =>
        _operations.Should().BeGreaterThanOrEqualTo(TargetOperations);

    [Test]
    public void It_records_zero_deadlocks() => _deadlocks.Should().Be(0);

    [Test]
    public void It_never_returns_an_empty_claim_while_jobs_remain() => _emptyClaims.Should().Be(0);

    [Test]
    public void It_records_zero_lost_updates()
    {
        _ownershipLost.Should().Be(0);
        _lockTimeouts.Should().Be(0);
        _unexpectedErrors.Should().BeEmpty();
        _exhaustedRows.Should().Be(0);
        _rowsClaimedMoreThanOnce.Should().Be(0);
    }

    [Test]
    public void It_completes_every_claimed_job_exactly_once()
    {
        _claimedIds.Distinct().Should().HaveCount(_claimedIds.Count);
        _completedIds.Should().BeEquivalentTo(_claimedIds);
        _completedRowsInDatabase.Should().Be(_claimedIds.Count);
    }
}

/// <summary>
/// Probe 6: the D-9 coalescing expression. Part one evaluates the exact expression text with an injected time over
/// fixed vectors (the review's worked example, boundaries 100 ms before, at, and after, second-count under- and
/// overestimates, sub-millisecond boundaries, multi-interval downtime, the maximum interval, and a not-yet-due row).
/// Part two runs the whole D-8 materialization transaction against due schedules and compares every advance with
/// the reference computed from the database time the statement returned.
/// </summary>
[TestFixture]
[Explicit("Operational probe (DMS-1437 step 0.2); run manually with --filter Category=OperationalProbe.")]
public class Given_schedule_coalescing_vectors_and_live_materializations : JobOperationalProbeBase
{
    private const string Owner = "probe-scheduler";
    private static readonly DateTime _day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public sealed record CoalescingVector(
        string Name,
        int IntervalMinutes,
        DateTime NextRunAt,
        DateTime Now,
        DateTime Expected
    );

    private static DateTime At(string timeOfDay) =>
        _day + TimeSpan.ParseExact(timeOfDay, @"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture);

    private static readonly CoalescingVector[] _vectors =
    [
        new("worked_example", 1, At("12:00:00.900"), At("12:01:00.100"), At("12:01:00.900")),
        new("before_boundary_100ms", 1, At("12:00:00.000"), At("12:00:59.900"), At("12:01:00.000")),
        new("at_boundary", 1, At("12:00:00.000"), At("12:01:00.000"), At("12:02:00.000")),
        new("after_boundary_100ms", 1, At("12:00:00.000"), At("12:01:00.100"), At("12:02:00.000")),
        new(
            "fractional_before_boundary_100ms",
            1,
            At("12:00:00.900"),
            At("12:01:00.800"),
            At("12:01:00.900")
        ),
        new("fractional_at_boundary", 1, At("12:00:00.900"), At("12:01:00.900"), At("12:02:00.900")),
        new("fractional_after_boundary_100ms", 1, At("12:00:00.900"), At("12:01:01.000"), At("12:02:00.900")),
        new("second_count_below_elapsed", 1, At("12:00:00.900"), At("12:00:59.950"), At("12:01:00.900")),
        new("second_count_above_elapsed", 1, At("12:00:00.100"), At("12:01:00.050"), At("12:01:00.100")),
        new(
            "microsecond_before_boundary",
            1,
            At("12:00:00.999999"),
            At("12:01:00.999998"),
            At("12:01:00.999999")
        ),
        new(
            "microsecond_at_boundary",
            1,
            At("12:00:00.999999"),
            At("12:01:00.999999"),
            At("12:02:00.999999")
        ),
        new(
            "tick_before_boundary",
            1,
            At("12:00:00.9999999"),
            At("12:01:00.9999998"),
            At("12:01:00.9999999")
        ),
        new("due_exactly_now", 5, At("12:00:00.250"), At("12:00:00.250"), At("12:05:00.250")),
        new(
            "downtime_663_intervals",
            5,
            At("00:00:00.250"),
            new DateTime(2026, 1, 3, 7, 15, 42, 125, DateTimeKind.Unspecified),
            new DateTime(2026, 1, 3, 7, 20, 0, 250, DateTimeKind.Unspecified)
        ),
        new(
            "daily_three_and_a_half_intervals",
            1440,
            At("06:00:00.000"),
            new DateTime(2026, 1, 4, 18, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 1, 5, 6, 0, 0, DateTimeKind.Unspecified)
        ),
        new(
            "maximum_interval",
            527_040,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2027, 1, 3, 0, 0, 0, DateTimeKind.Unspecified)
        ),
        new("not_yet_due", 1, At("12:00:00.500"), At("12:00:00.400"), At("12:00:00.500")),
    ];

    private static readonly (int IntervalMinutes, TimeSpan Overdue)[] _liveSchedules =
    [
        (1, TimeSpan.FromMilliseconds(100)),
        (1, TimeSpan.FromMilliseconds(59_900)),
        (1, TimeSpan.FromMilliseconds(60_000)),
        (1, TimeSpan.FromMilliseconds(60_100)),
        (1, TimeSpan.FromMinutes(3.5)),
        (5, TimeSpan.FromMilliseconds(100)),
        (5, TimeSpan.FromMinutes(4.999)),
        (5, TimeSpan.FromMinutes(100) + TimeSpan.FromMilliseconds(250)),
        (60, TimeSpan.FromHours(7.25)),
        (1440, TimeSpan.FromDays(3.5)),
        (1440, TimeSpan.FromMilliseconds(1)),
        (527_040, TimeSpan.FromDays(800)),
    ];

    private readonly Dictionary<string, DateTime> _databaseResults = [];
    private readonly List<Materialization> _materializations = [];
    private long _occurrencesMatchingSchedules;
    private long _schedulesWithRecordedOccurrence;

    private sealed record Materialization(
        long ScheduleId,
        int IntervalMinutes,
        DateTime OriginalNextRunAt,
        DateTime AdvancedNextRunAt,
        DateTime DatabaseNow,
        bool Inserted,
        double Milliseconds
    );

    /// <summary>
    /// The C# reference for D-9: the smallest boundary <c>NextRunAt + j·Interval</c> strictly after <c>now</c>, or
    /// <c>NextRunAt</c> unchanged when it is already in the future.
    /// </summary>
    public static DateTime AdvanceReference(DateTime nextRunAt, int intervalMinutes, DateTime now)
    {
        long intervalTicks = TimeSpan.FromMinutes(intervalMinutes).Ticks;
        long elapsedTicks = now.Ticks - nextRunAt.Ticks;
        if (elapsedTicks < 0)
        {
            return nextRunAt;
        }
        return new DateTime(
            nextRunAt.Ticks + ((elapsedTicks / intervalTicks) + 1) * intervalTicks,
            DateTimeKind.Unspecified
        );
    }

    [OneTimeSetUp]
    public async Task Setup()
    {
        await using SqlConnection connection = await OpenConnectionAsync();

        foreach (CoalescingVector vector in _vectors)
        {
            await using SqlCommand advance = Command(connection, null, JobProbeSql.AdvanceWithInjectedNow);
            advance.Parameters.Add(DateTime2("@NextRunAt", vector.NextRunAt));
            advance.Parameters.Add(DateTime2("@Now", vector.Now));
            advance.Parameters.AddWithValue("@IntervalMinutes", vector.IntervalMinutes);
            _databaseResults[vector.Name] = (DateTime)(await advance.ExecuteScalarAsync())!;
        }

        for (int i = 0; i < _liveSchedules.Length; i++)
        {
            (int intervalMinutes, TimeSpan overdue) = _liveSchedules[i];
            await using SqlCommand seed = Command(
                connection,
                null,
                """
                INSERT INTO dmscs_probe.JobSchedule (
                    TenantId, ScheduleType, JobType, PayloadVersion, Payload, IntervalMinutes, Enabled, NextRunAt, CreatedBy)
                VALUES (NULL, @ScheduleType, N'DataStore.RefreshEducationOrganizations', 1, N'{"dataStoreId":1}',
                    @IntervalMinutes, 1,
                    DATEADD(microsecond, -(@OverdueMicroseconds % 1000000),
                        DATEADD(second, -(@OverdueMicroseconds / 1000000), SYSUTCDATETIME())),
                    N'probe');
                """
            );
            seed.Parameters.AddWithValue("@ScheduleType", $"probe.schedule.{i}");
            seed.Parameters.AddWithValue("@IntervalMinutes", intervalMinutes);
            seed.Parameters.AddWithValue("@OverdueMicroseconds", overdue.Ticks / 10);
            await seed.ExecuteNonQueryAsync();
        }

        for (int guard = 0; guard < _liveSchedules.Length * 2; guard++)
        {
            Materialization? materialization = await MaterializeNextAsync(connection);
            if (materialization is null)
            {
                break;
            }
            _materializations.Add(materialization);
        }

        _occurrencesMatchingSchedules = await ScalarAsync(
            """
            SELECT COUNT_BIG(*) FROM dmscs_probe.Job AS j
            JOIN dmscs_probe.JobSchedule AS s ON s.Id = j.SourceScheduleId
            WHERE j.ScheduledOccurrence = s.LastEnqueuedOccurrence AND j.Status = N'Pending' AND j.CreatedBy = N'system'
            """
        );
        _schedulesWithRecordedOccurrence = await ScalarAsync(
            """
            SELECT COUNT_BIG(*) FROM dmscs_probe.JobSchedule
            WHERE LastEnqueuedOccurrence IS NOT NULL AND LeaseOwner IS NULL AND LeaseExpiresAt IS NULL
              AND FencingToken = 1
            """
        );

        Report("coalescing", "vectors", _vectors.Length);
        Report(
            "coalescing",
            "vector_mismatches",
            _vectors.Count(vector => _databaseResults[vector.Name] != vector.Expected)
        );
        Report("coalescing", "live_materializations", _materializations.Count);
        Report(
            "coalescing",
            "live_mismatches",
            _materializations.Count(m =>
                m.AdvancedNextRunAt != AdvanceReference(m.OriginalNextRunAt, m.IntervalMinutes, m.DatabaseNow)
            )
        );
        Report(
            "coalescing",
            "materialization_transaction",
            LatencySummary.From(_materializations.Select(m => m.Milliseconds))
        );
    }

    /// <summary>
    /// D-8 as one bounded transaction: lock a due row, lease it, insert the occurrence, advance with a fresh time
    /// sample, commit.
    /// </summary>
    private static async Task<Materialization?> MaterializeNextAsync(SqlConnection connection)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        long start = Stopwatch.GetTimestamp();
        await using SqlTransaction transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(deadline.Token);

        long id;
        long? tenantId;
        string jobType;
        short payloadVersion;
        string payload;
        int intervalMinutes;
        DateTime nextRunAt;
        await using (SqlCommand lockDue = Command(connection, transaction, JobProbeSql.LockDueSchedule))
        await using (SqlDataReader reader = await lockDue.ExecuteReaderAsync(deadline.Token))
        {
            if (!await reader.ReadAsync(deadline.Token))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(deadline.Token);
                return null;
            }
            id = reader.GetInt64(0);
            tenantId = await reader.IsDBNullAsync(1, deadline.Token) ? null : reader.GetInt64(1);
            jobType = reader.GetString(2);
            payloadVersion = reader.GetInt16(3);
            payload = reader.GetString(4);
            intervalMinutes = reader.GetInt32(5);
            nextRunAt = reader.GetDateTime(6);
        }

        long token;
        await using (SqlCommand lease = Command(connection, transaction, JobProbeSql.LeaseSchedule))
        {
            lease.Parameters.AddWithValue("@Id", id);
            lease.Parameters.AddWithValue("@Owner", Owner);
            lease.Parameters.AddWithValue("@LeaseSeconds", 30);
            token = (long)(await lease.ExecuteScalarAsync(deadline.Token))!;
        }

        int inserted;
        await using (SqlCommand insert = Command(connection, transaction, JobProbeSql.InsertOccurrence))
        {
            insert.Parameters.AddWithValue("@JobId", Guid.NewGuid().ToString("N"));
            insert.Parameters.Add(
                new SqlParameter("@TenantId", SqlDbType.BigInt) { Value = (object?)tenantId ?? DBNull.Value }
            );
            insert.Parameters.AddWithValue("@JobType", jobType);
            insert.Parameters.AddWithValue("@PayloadVersion", payloadVersion);
            insert.Parameters.AddWithValue("@Payload", payload);
            insert.Parameters.AddWithValue("@Id", id);
            insert.Parameters.Add(DateTime2("@Occurrence", nextRunAt));
            inserted = await insert.ExecuteNonQueryAsync(deadline.Token);
        }

        DateTime advanced;
        DateTime databaseNow;
        await using (SqlCommand advance = Command(connection, transaction, JobProbeSql.AdvanceSchedule))
        {
            advance.Parameters.AddWithValue("@Id", id);
            advance.Parameters.AddWithValue("@Owner", Owner);
            advance.Parameters.AddWithValue("@Token", token);
            await using SqlDataReader reader = await advance.ExecuteReaderAsync(deadline.Token);
            if (!await reader.ReadAsync(deadline.Token))
            {
                throw new InvalidOperationException(
                    "The advancing UPDATE lost ownership of a schedule it had just leased."
                );
            }
            advanced = reader.GetDateTime(0);
            databaseNow = reader.GetDateTime(1);
        }

        await transaction.CommitAsync(deadline.Token);
        return new Materialization(
            id,
            intervalMinutes,
            nextRunAt,
            advanced,
            databaseNow,
            inserted == 1,
            ElapsedMilliseconds(start)
        );
    }

    [Test]
    public void It_agrees_with_the_reference_on_every_vector() =>
        _vectors
            .Should()
            .OnlyContain(vector =>
                AdvanceReference(vector.NextRunAt, vector.IntervalMinutes, vector.Now) == vector.Expected
            );

    [Test]
    public void It_matches_every_vector_exactly_in_the_database()
    {
        foreach (CoalescingVector vector in _vectors)
        {
            _databaseResults[vector.Name].Should().Be(vector.Expected, vector.Name);
        }
    }

    [Test]
    public void It_materializes_every_due_schedule_once()
    {
        _materializations.Should().HaveCount(_liveSchedules.Length);
        _materializations.Select(m => m.ScheduleId).Distinct().Should().HaveCount(_liveSchedules.Length);
        _materializations.Should().OnlyContain(m => m.Inserted);
        _occurrencesMatchingSchedules.Should().Be(_liveSchedules.Length);
        _schedulesWithRecordedOccurrence.Should().Be(_liveSchedules.Length);
    }

    [Test]
    public void It_advances_every_live_schedule_to_the_first_boundary_after_the_sampled_time()
    {
        foreach (Materialization m in _materializations)
        {
            m.AdvancedNextRunAt.Should()
                .Be(AdvanceReference(m.OriginalNextRunAt, m.IntervalMinutes, m.DatabaseNow));
            m.AdvancedNextRunAt.Should().BeAfter(m.DatabaseNow);
            (m.AdvancedNextRunAt - TimeSpan.FromMinutes(m.IntervalMinutes))
                .Should()
                .BeOnOrBefore(m.DatabaseNow);
        }
    }
}
