// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

/// <summary>
/// DMS-1437 step 2.11 operational re-verification (spec §6.2). Each fixture deploys the CMS schema with its real
/// migrations into its own database, seeds rows with raw SQL, and then measures the step 0.2 probes through the
/// implemented repositories and <see cref="IJobFence"/>: every measured operation is a repository or fence call, with
/// its deadlines, sessions, and result classification. The database is dropped afterwards.
/// </summary>
/// <remarks>
/// As for step 0.2, <c>[Explicit]</c> on every concrete fixture keeps these probes off CI. Run them with
/// <c>dotnet test … --filter "Category=RepositoryProbe"</c>. The server comes
/// from <c>ConnectionStrings__JobProbePostgresql</c> when set, otherwise from <c>DatabaseSettings:DatabaseConnection</c>.
/// Set <c>CMS_JOB_PROBE_RESULTS</c> to a file path to append every measurement as a tab-separated line. The assertions
/// are the spec §6.2 acceptance thresholds and each probe's correctness invariants.
/// </remarks>
[Category("OperationalProbe")]
[Category("RepositoryProbe")]
[NonParallelizable]
public abstract class JobRepositoryProbeBase
{
    protected const string ProbeDatabaseName = "edfi_cms_job_repository_probe";
    protected const int MaxAttempts = 5;
    protected const int LeaseSeconds = 300;
    protected const string LockTimeout = "55P03";

    /// <summary>The §6.1 candidates: RenewalTimeout = RenewalInterval (60 s) / 2, and FenceTimeout 10 s.</summary>
    protected static readonly JobLeaseTimings Timings = new(
        RenewalTimeout: TimeSpan.FromSeconds(30),
        FenceTimeout: TimeSpan.FromSeconds(10)
    );

    private static readonly object _resultsFileLock = new();
    private string? _connectionString;

    protected string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("The probe database has not been deployed.");

    protected IOptions<DatabaseOptions> DatabaseOptions =>
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = ConnectionString,
                EncryptionKey = Configuration.DatabaseOptions.Value.EncryptionKey,
            }
        );

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__JobProbePostgresql")
            is { Length: > 0 } configured
            ? configured
            : Configuration.DatabaseOptions.Value.DatabaseConnection;

    [OneTimeSetUp]
    public async Task DeployProbeDatabase()
    {
        await DropProbeDatabaseAsync();

        NpgsqlConnectionStringBuilder deploy = new(ServerConnectionString)
        {
            Database = ProbeDatabaseName,
            Pooling = false,
        };
        if (
            new Deploy.DatabaseDeploy().DeployDatabase(deploy.ConnectionString)
            is DatabaseDeployResult.DatabaseDeployFailure failure
        )
        {
            Assert.Fail($"Deploying the probe database failed: {failure.Error}");
        }

        _connectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString)
        {
            Database = ProbeDatabaseName,
            ApplicationName = "EdFi.DmsConfigurationService.JobRepositoryProbe",
            MaxPoolSize = 60,
        }.ConnectionString;
    }

    [OneTimeTearDown]
    public async Task DropProbeDatabase()
    {
        NpgsqlConnection.ClearAllPools();
        await DropProbeDatabaseAsync();
    }

    private static async Task DropProbeDatabaseAsync()
    {
        NpgsqlConnectionStringBuilder maintenance = new(ServerConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using NpgsqlConnection connection = new(maintenance.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"""DROP DATABASE IF EXISTS "{ProbeDatabaseName}" WITH (FORCE);""");
    }

    protected JobLeaseRepository LeaseRepository(Action<int>? afterExhaustBatch = null) =>
        new(DatabaseOptions, Timings) { AfterExhaustBatch = afterExhaustBatch };

    protected PostgresqlJobFenceFactory FenceFactory(JobDatabaseSessionHooks? hooks = null) =>
        new(DatabaseOptions, Timings) { SessionHooks = hooks };

    protected JobScheduleRepository ScheduleRepository() =>
        new(
            DatabaseOptions,
            new TestAuditContext("probe"),
            new TenantContextProvider { Context = new TenantContext.NotMultitenant() }
        );

    protected async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandTimeout: 300));
    }

    protected async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<T>(
                new CommandDefinition(sql, parameters, commandTimeout: 300)
            ) ?? throw new InvalidOperationException("The scalar query returned no value.");
    }

    protected Task ResetAsync() =>
        ExecuteAsync("""TRUNCATE "dmscs"."Job", "dmscs"."JobSchedule" RESTART IDENTITY;""");

    protected Task AnalyzeAsync() =>
        ExecuteAsync("""ANALYZE "dmscs"."Job"; ANALYZE "dmscs"."JobSchedule";""");

    /// <summary>
    /// Seeds <paramref name="count"/> jobs in one statement, created one millisecond apart and ending
    /// <paramref name="createdAgeSeconds"/> before now, so claim order is insertion order. Active rows get
    /// <c>NextAttemptAt = CreatedAt</c>, as every enqueue path sets it (A1); the fencing token equals the attempt count.
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
            """
            INSERT INTO "dmscs"."Job" (
                "JobId", "JobType", "PayloadVersion", "Payload", "Status", "CreatedAt", "NextAttemptAt", "FinishedAt",
                "LeaseExpiresAt", "ErrorMessage", "AttemptCount", "LeaseOwner", "FencingToken", "CreatedBy")
            SELECT
                replace(gen_random_uuid()::text, '-', ''),
                'DataStore.RefreshEducationOrganizations',
                1,
                '{"dataStoreId":' || g || '}',
                @Status,
                (now() AT TIME ZONE 'UTC') - (@Count - g) * interval '1 millisecond' - @CreatedAgeSeconds * interval '1 second',
                CASE WHEN @Status IN ('Pending', 'InProgress')
                    THEN (now() AT TIME ZONE 'UTC') - (@Count - g) * interval '1 millisecond' - @CreatedAgeSeconds * interval '1 second'
                END,
                CASE WHEN @Status IN ('Completed', 'Error') THEN (now() AT TIME ZONE 'UTC') - @FinishedAgeSeconds * interval '1 second' END,
                CASE WHEN @Status = 'InProgress' THEN (clock_timestamp() AT TIME ZONE 'UTC') + @LeaseRemainingSeconds * interval '1 second' END,
                CASE WHEN @Status = 'Error' THEN 'The job handler failed.' END,
                @AttemptCount,
                CASE WHEN @Status = 'InProgress' THEN @LeaseOwner END,
                @AttemptCount,
                'probe'
            FROM generate_series(1, @Count) AS g;
            """,
            new
            {
                Status = status,
                Count = count,
                AttemptCount = attemptCount,
                CreatedAgeSeconds = createdAgeSeconds,
                FinishedAgeSeconds = finishedAgeSeconds,
                LeaseRemainingSeconds = leaseRemainingSeconds,
                LeaseOwner = leaseOwner,
            }
        );

    /// <summary>Opens a session that holds the row lock of job <paramref name="id"/> until it commits.</summary>
    protected async Task<RowLockHolder> HoldRowLockAsync(long id, string? afterLockSql = null)
    {
        NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            """SELECT "Id" FROM "dmscs"."Job" WHERE "Id" = @Id FOR UPDATE;""",
            new { Id = id },
            transaction
        );
        if (afterLockSql is not null)
        {
            await connection.ExecuteAsync(afterLockSql, new { Id = id }, transaction);
        }
        return new RowLockHolder(connection, transaction);
    }

    protected static double ElapsedMilliseconds(long start) =>
        Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    protected static double Milliseconds(long from, long to) =>
        Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    protected static void Report(string probe, string metric, object value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        TestContext.Progress.WriteLine($"PROBE repository-postgresql {probe} {metric} {text}");

        if (Environment.GetEnvironmentVariable("CMS_JOB_PROBE_RESULTS") is { Length: > 0 } path)
        {
            lock (_resultsFileLock)
            {
                File.AppendAllText(
                    path,
                    $"repository-postgresql\t{probe}\t{metric}\t{text}{Environment.NewLine}"
                );
            }
        }
    }

    protected static ClaimedJob ClaimedOf(JobClaimResult result) =>
        result.Should().BeOfType<JobClaimResult.Claimed>().Subject.Job;

    protected static string Outcome(JobWriteResult result) =>
        result switch
        {
            JobWriteResult.Success => "Success",
            JobWriteResult.OwnershipLost => "OwnershipLost",
            JobWriteResult.FailureUnknown failure => $"FailureUnknown:{failure.Diagnostic.ProviderErrorCode}",
            JobWriteResult.ResultUnknown unknown => $"ResultUnknown:{unknown.Diagnostic.ProviderErrorCode}",
            _ => result.GetType().Name,
        };

    protected static string Tally(IEnumerable<string> outcomes) =>
        string.Join(
            " ",
            outcomes
                .GroupBy(outcome => outcome)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}")
        );

    /// <summary>A session holding a job's row lock; <see cref="CommitAsync"/> releases it.</summary>
    public sealed class RowLockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IAsyncDisposable
    {
        /// <summary>Commits and returns the timestamp taken just before the commit was sent.</summary>
        public async Task<long> CommitAsync()
        {
            long requested = Stopwatch.GetTimestamp();
            await transaction.CommitAsync();
            return requested;
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

[TestFixture]
[Explicit("DMS-1437 step 2.11 operational probe")]
public class Given_three_claimers_draining_a_backlog_through_the_lease_repository : JobRepositoryProbeBase
{
    private const int PendingJobs = 10_000;
    private const int Claimers = 3;
    private const int ClaimsPerClaimer = 1_000;
    private const int IdlePollsPerClaimer = 100;

    private LatencySummary _backlog = null!;
    private LatencySummary _idle = null!;
    private int _empty;
    private int _failures;
    private int _duplicates;
    private int _idleClaims;

    [OneTimeSetUp]
    public async Task RunProbe()
    {
        await ResetAsync();
        await SeedJobsAsync(JobStatuses.Pending, PendingJobs, attemptCount: 0, createdAgeSeconds: 60);
        await AnalyzeAsync();

        JobLeaseRepository repository = LeaseRepository();
        ConcurrentBag<double> latencies = [];
        ConcurrentBag<long> claimed = [];
        long started = Stopwatch.GetTimestamp();
        await Task.WhenAll(
            Enumerable
                .Range(0, Claimers)
                .Select(claimer =>
                    Task.Run(async () =>
                    {
                        for (int claim = 0; claim < ClaimsPerClaimer; claim++)
                        {
                            long start = Stopwatch.GetTimestamp();
                            JobClaimResult result = await repository.ClaimNext(
                                $"probe-claimer-{claimer}",
                                LeaseSeconds,
                                MaxAttempts,
                                CancellationToken.None
                            );
                            latencies.Add(ElapsedMilliseconds(start));
                            switch (result)
                            {
                                case JobClaimResult.Claimed hit:
                                    claimed.Add(hit.Job.Id);
                                    break;
                                case JobClaimResult.NoneAvailable:
                                    Interlocked.Increment(ref _empty);
                                    break;
                                default:
                                    Interlocked.Increment(ref _failures);
                                    break;
                            }
                        }
                    })
                )
        );
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        _backlog = LatencySummary.From(latencies);
        _duplicates = claimed.Count - claimed.Distinct().Count();
        Report("claim", "backlog_claims", _backlog);
        Report("claim", "backlog_throughput_claims_per_s", Math.Round(claimed.Count / seconds));
        Report("claim", "empty_claims_with_backlog", _empty);
        Report("claim", "claim_failures", _failures);
        Report("claim", "duplicate_claims", _duplicates);

        await ResetAsync();
        await SeedJobsAsync(JobStatuses.InProgress, PendingJobs, attemptCount: 1, createdAgeSeconds: 60);
        await AnalyzeAsync();
        ConcurrentBag<double> idle = [];
        await Task.WhenAll(
            Enumerable
                .Range(0, Claimers)
                .Select(claimer =>
                    Task.Run(async () =>
                    {
                        for (int poll = 0; poll < IdlePollsPerClaimer; poll++)
                        {
                            long start = Stopwatch.GetTimestamp();
                            JobClaimResult result = await repository.ClaimNext(
                                $"probe-idle-{claimer}",
                                LeaseSeconds,
                                MaxAttempts,
                                CancellationToken.None
                            );
                            idle.Add(ElapsedMilliseconds(start));
                            if (result is not JobClaimResult.NoneAvailable)
                            {
                                Interlocked.Increment(ref _idleClaims);
                            }
                        }
                    })
                )
        );
        _idle = LatencySummary.From(idle);
        Report("claim", "idle_polls_10000_leased", _idle);
    }

    [Test]
    public void It_claims_within_250_ms_at_p99() => _backlog.P99.Should().BeLessThanOrEqualTo(250);

    [Test]
    public void It_polls_an_idle_queue_within_250_ms_at_p99()
    {
        _idle.P99.Should().BeLessThanOrEqualTo(250);
        _idleClaims.Should().Be(0);
    }

    [Test]
    public void It_never_comes_back_empty_or_fails_while_work_waits()
    {
        _empty.Should().Be(0);
        _failures.Should().Be(0);
    }

    [Test]
    public void It_never_claims_a_job_twice() => _duplicates.Should().Be(0);
}

[TestFixture]
[Explicit("DMS-1437 step 2.11 operational probe")]
public class Given_renewals_waiting_on_a_row_lock_through_the_lease_repository : JobRepositoryProbeBase
{
    private const string Owner = "probe-renewer";
    private const int Rows = 10;
    private const int Rounds = 10;
    private const int FenceRounds = 3;
    private const int ExpiringRows = 20;

    private readonly List<string> _shortHoldOutcomes = [];
    private readonly List<string> _longHoldOutcomes = [];
    private readonly List<string> _expiringOutcomes = [];
    private readonly List<string> _fenceHoldOutcomes = [];
    private readonly List<string> _fenceLongHoldOutcomes = [];
    private readonly List<string> _gateOutcomes = [];
    private LatencySummary _afterRelease = null!;
    private LatencySummary _afterFenceCommit = null!;
    private LatencySummary _afterFenceGate = null!;
    private LatencySummary _timeToLockTimeout = null!;
    private LatencySummary _fenceTimeToLockTimeout = null!;
    private int _expiringLeasesExtended;
    private int _fencesCommitted;
    private int _gateEnteredBeforeFenceCommit;

    [OneTimeSetUp]
    public async Task RunProbe()
    {
        await ResetAsync();
        await SeedJobsAsync(JobStatuses.InProgress, Rows, attemptCount: 1, leaseOwner: Owner);
        long[] ids = [.. await IdsAsync()];
        JobLeaseRepository repository = LeaseRepository();

        // 4 s hold, released 100 ms apart: the renewal waits on the lock and succeeds after the release. The latency is
        // measured from just before the holder's COMMIT is sent.
        List<double> afterRelease = [];
        for (int round = 0; round < Rounds; round++)
        {
            (string Outcome, double AfterRelease)[] samples = await Task.WhenAll(
                ids.Select(
                    (id, index) =>
                        ContendAsync(repository, id, TimeSpan.FromMilliseconds(4_000 + (100 * index)))
                )
            );
            _shortHoldOutcomes.AddRange(samples.Select(sample => sample.Outcome));
            afterRelease.AddRange(samples.Select(sample => sample.AfterRelease));
        }
        _afterRelease = LatencySummary.From(afterRelease);
        Report("renewal", "after_release_4s_hold", _afterRelease);
        Report("renewal", "outcomes_4s_hold", Tally(_shortHoldOutcomes));

        // 6 s hold: the renewal reports the provider's lock timeout at WriteLockWait.
        List<double> timeToLockTimeout = [];
        foreach (
            (string outcome, double elapsed) in await Task.WhenAll(
                ids.Select(id => LockTimeoutAsync(repository, id))
            )
        )
        {
            _longHoldOutcomes.Add(outcome);
            timeToLockTimeout.Add(elapsed);
        }
        _timeToLockTimeout = LatencySummary.From(timeToLockTimeout);
        Report("renewal", "outcomes_6s_hold", Tally(_longHoldOutcomes));
        Report("renewal", "time_to_lock_timeout_6s_hold", _timeToLockTimeout);

        // 3 s hold over a lease the holder sets to expire 2 s after it takes the lock: once the renewal holds the lock,
        // its fresh time rejects the lease.
        await ResetAsync();
        await SeedJobsAsync(JobStatuses.InProgress, ExpiringRows, attemptCount: 1, leaseOwner: Owner);
        long[] expiring = [.. await IdsAsync()];
        _expiringOutcomes.AddRange(await Task.WhenAll(expiring.Select(id => ExpiringAsync(repository, id))));
        _expiringLeasesExtended = await ScalarAsync<int>(
            """
            SELECT count(*)::int FROM "dmscs"."Job"
            WHERE "LeaseExpiresAt" > (clock_timestamp() AT TIME ZONE 'UTC');
            """
        );
        Report("renewal", "outcomes_expired_during_3s_hold", Tally(_expiringOutcomes));
        Report("renewal", "expiring_leases_extended", _expiringLeasesExtended);

        // Fence-held: the row lock is held by a fence's transaction, whose work lasts 4 s (or 6 s), while a renewal
        // issued at the database waits on it.
        await ResetAsync();
        await SeedJobsAsync(JobStatuses.Pending, Rows, attemptCount: 0);
        ClaimedJob[] claimed = await ClaimAllAsync(repository, Rows);
        List<double> afterFenceCommit = [];
        for (int round = 0; round < FenceRounds; round++)
        {
            (string Outcome, double AfterCommit, bool Committed)[] samples = await Task.WhenAll(
                claimed.Select(
                    (job, index) =>
                        ContendWithFenceAsync(
                            repository,
                            job,
                            TimeSpan.FromMilliseconds(4_000 + (100 * index))
                        )
                )
            );
            _fenceHoldOutcomes.AddRange(samples.Select(sample => sample.Outcome));
            afterFenceCommit.AddRange(samples.Select(sample => sample.AfterCommit));
            _fencesCommitted += samples.Count(sample => sample.Committed);
        }
        _afterFenceCommit = LatencySummary.From(afterFenceCommit);
        Report("renewal", "after_fence_commit_4s_fence", _afterFenceCommit);
        Report("renewal", "outcomes_4s_fence", Tally(_fenceHoldOutcomes));

        List<double> fenceTimeToLockTimeout = [];
        foreach (
            (string outcome, double elapsed, bool committed) in await Task.WhenAll(
                claimed.Select(job => LockTimeoutWithFenceAsync(repository, job))
            )
        )
        {
            _fenceLongHoldOutcomes.Add(outcome);
            fenceTimeToLockTimeout.Add(elapsed);
            _fencesCommitted += committed ? 1 : 0;
        }
        _fenceTimeToLockTimeout = LatencySummary.From(fenceTimeToLockTimeout);
        Report("renewal", "outcomes_6s_fence", Tally(_fenceLongHoldOutcomes));
        Report("renewal", "time_to_lock_timeout_6s_fence", _fenceTimeToLockTimeout);
        Report("renewal", "fences_committed", _fencesCommitted);

        // Gate-mediated, as the runtime runs them: a renewal of the same execution waits at the execution gate for a
        // 1 s fence and runs once the fence has committed and released the gate.
        List<double> afterFenceGate = [];
        foreach (
            (string outcome, double afterFence, bool enteredBeforeCommit) in await Task.WhenAll(
                claimed.Select(job => RenewBehindFenceAtTheGateAsync(repository, job))
            )
        )
        {
            _gateOutcomes.Add(outcome);
            afterFenceGate.Add(afterFence);
            _gateEnteredBeforeFenceCommit += enteredBeforeCommit ? 1 : 0;
        }
        _afterFenceGate = LatencySummary.From(afterFenceGate);
        Report("renewal", "after_fence_release_at_gate", _afterFenceGate);
        Report("renewal", "outcomes_behind_fence_at_gate", Tally(_gateOutcomes));
    }

    private async Task<IEnumerable<long>> IdsAsync()
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return await connection.QueryAsync<long>("""SELECT "Id" FROM "dmscs"."Job" ORDER BY "Id";""");
    }

    private static async Task<ClaimedJob[]> ClaimAllAsync(JobLeaseRepository repository, int count)
    {
        List<ClaimedJob> claimed = [];
        for (int claim = 0; claim < count; claim++)
        {
            claimed.Add(
                ClaimedOf(
                    await repository.ClaimNext(Owner, LeaseSeconds, MaxAttempts, CancellationToken.None)
                )
            );
        }
        return [.. claimed];
    }

    private async Task<(string Outcome, double AfterRelease)> ContendAsync(
        JobLeaseRepository repository,
        long id,
        TimeSpan hold
    )
    {
        await using RowLockHolder holder = await HoldRowLockAsync(id);
        Task<JobWriteResult> renewal = repository.Renew(id, Owner, 1, LeaseSeconds, CancellationToken.None);
        await Task.Delay(hold);
        long released = await holder.CommitAsync();
        JobWriteResult result = await renewal;
        return (Outcome(result), ElapsedMilliseconds(released));
    }

    private async Task<(string Outcome, double Elapsed)> LockTimeoutAsync(
        JobLeaseRepository repository,
        long id
    )
    {
        await using RowLockHolder holder = await HoldRowLockAsync(id);
        long start = Stopwatch.GetTimestamp();
        Task<JobWriteResult> renewal = repository.Renew(id, Owner, 1, LeaseSeconds, CancellationToken.None);
        JobWriteResult result = await renewal;
        double elapsed = ElapsedMilliseconds(start);
        TimeSpan rest = TimeSpan.FromSeconds(6) - Stopwatch.GetElapsedTime(start);
        if (rest > TimeSpan.Zero)
        {
            await Task.Delay(rest);
        }
        await holder.CommitAsync();
        return (Outcome(result), elapsed);
    }

    private async Task<string> ExpiringAsync(JobLeaseRepository repository, long id)
    {
        await using RowLockHolder holder = await HoldRowLockAsync(
            id,
            """
            UPDATE "dmscs"."Job"
            SET "LeaseExpiresAt" = (clock_timestamp() AT TIME ZONE 'UTC') + interval '2 seconds'
            WHERE "Id" = @Id;
            """
        );
        Task<JobWriteResult> renewal = repository.Renew(id, Owner, 1, LeaseSeconds, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await holder.CommitAsync();
        return Outcome(await renewal);
    }

    private async Task<(string Outcome, double AfterCommit, bool Committed)> ContendWithFenceAsync(
        JobLeaseRepository repository,
        ClaimedJob job,
        TimeSpan work
    )
    {
        long commitRequested = 0;
        TaskCompletionSource working = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IJobFence fence = FenceFactory(
                new JobDatabaseSessionHooks
                {
                    BeforeOperation = (operation, _) =>
                    {
                        if (operation == "Commit")
                        {
                            commitRequested = Stopwatch.GetTimestamp();
                        }
                        return Task.CompletedTask;
                    },
                }
            )
            .Create(job, new JobExecutionOwnership());

        Task fenced = fence.ExecuteAsync(
            async (_, token) =>
            {
                working.TrySetResult();
                await Task.Delay(work, token);
            },
            CancellationToken.None
        );
        await working.Task;
        Task<JobWriteResult> renewal = repository.Renew(
            job.Id,
            job.LeaseOwner,
            job.FencingToken,
            LeaseSeconds,
            CancellationToken.None
        );
        bool committed = await Succeeds(fenced);
        JobWriteResult result = await renewal;
        long renewed = Stopwatch.GetTimestamp();
        return (Outcome(result), Milliseconds(commitRequested, renewed), committed);
    }

    private async Task<(string Outcome, double Elapsed, bool Committed)> LockTimeoutWithFenceAsync(
        JobLeaseRepository repository,
        ClaimedJob job
    )
    {
        TaskCompletionSource working = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IJobFence fence = FenceFactory().Create(job, new JobExecutionOwnership());
        Task fenced = fence.ExecuteAsync(
            async (_, token) =>
            {
                working.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(6), token);
            },
            CancellationToken.None
        );
        await working.Task;
        long start = Stopwatch.GetTimestamp();
        JobWriteResult result = await repository.Renew(
            job.Id,
            job.LeaseOwner,
            job.FencingToken,
            LeaseSeconds,
            CancellationToken.None
        );
        double elapsed = ElapsedMilliseconds(start);
        return (Outcome(result), elapsed, await Succeeds(fenced));
    }

    private async Task<(
        string Outcome,
        double AfterFence,
        bool EnteredBeforeCommit
    )> RenewBehindFenceAtTheGateAsync(JobLeaseRepository repository, ClaimedJob job)
    {
        JobExecutionOwnership ownership = new();
        long commitRequested = 0;
        TaskCompletionSource working = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IJobFence fence = FenceFactory(
                new JobDatabaseSessionHooks
                {
                    BeforeOperation = (operation, _) =>
                    {
                        if (operation == "Commit")
                        {
                            commitRequested = Stopwatch.GetTimestamp();
                        }
                        return Task.CompletedTask;
                    },
                }
            )
            .Create(job, ownership);

        Task fenced = fence.ExecuteAsync(
            async (_, token) =>
            {
                working.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            },
            CancellationToken.None
        );
        await working.Task;
        long entered = 0;
        Task<JobWriteResult> renewal = Task.Run(async () =>
        {
            using IDisposable gate = await ownership.EnterForRenewalAsync(CancellationToken.None);
            entered = Stopwatch.GetTimestamp();
            return await repository.Renew(
                job.Id,
                job.LeaseOwner,
                job.FencingToken,
                LeaseSeconds,
                CancellationToken.None
            );
        });
        await fenced;
        long fenceReturned = Stopwatch.GetTimestamp();
        JobWriteResult result = await renewal;
        return (Outcome(result), ElapsedMilliseconds(fenceReturned), entered < commitRequested);
    }

    private static async Task<bool> Succeeds(Task task)
    {
        try
        {
            await task;
            return true;
        }
        catch (Exception exception) when (exception is JobLeaseLostException or JobFenceUnavailableException)
        {
            return false;
        }
    }

    [Test]
    public void It_renews_within_100_ms_of_the_release_at_p99()
    {
        _shortHoldOutcomes.Should().HaveCount(Rows * Rounds).And.OnlyContain(outcome => outcome == "Success");
        _afterRelease.P99.Should().BeLessThanOrEqualTo(100);
    }

    [Test]
    public void It_reports_the_lock_timeout_at_the_lock_wait()
    {
        _longHoldOutcomes.Should().OnlyContain(outcome => outcome == $"FailureUnknown:{LockTimeout}");
        _timeToLockTimeout.Max.Should().BeLessThan(6_000);
    }

    [Test]
    public void It_rejects_a_lease_that_expired_while_it_waited()
    {
        _expiringOutcomes
            .Should()
            .HaveCount(ExpiringRows)
            .And.OnlyContain(outcome => outcome == "OwnershipLost");
        _expiringLeasesExtended.Should().Be(0);
    }

    [Test]
    public void It_renews_within_100_ms_of_a_fence_commit_at_p99()
    {
        _fenceHoldOutcomes
            .Should()
            .HaveCount(Rows * FenceRounds)
            .And.OnlyContain(outcome => outcome == "Success");
        _afterFenceCommit.P99.Should().BeLessThanOrEqualTo(100);
    }

    [Test]
    public void It_reports_the_lock_timeout_behind_a_longer_fence_without_breaking_it()
    {
        _fenceLongHoldOutcomes.Should().OnlyContain(outcome => outcome == $"FailureUnknown:{LockTimeout}");
        _fenceTimeToLockTimeout.Max.Should().BeLessThan(6_000);
        _fencesCommitted.Should().Be(Rows * (FenceRounds + 1));
    }

    [Test]
    public void It_runs_a_gated_renewal_only_after_the_fence_and_within_100_ms_of_it()
    {
        _gateOutcomes.Should().HaveCount(Rows).And.OnlyContain(outcome => outcome == "Success");
        _gateEnteredBeforeFenceCommit.Should().Be(0);
        _afterFenceGate.P99.Should().BeLessThanOrEqualTo(100);
    }
}

[TestFixture]
[Explicit("DMS-1437 step 2.11 operational probe")]
public class Given_bounded_exhaust_sweeps_through_the_lease_repository : JobRepositoryProbeBase
{
    private const int SteadyStateSweeps = 50;
    private const int LoweredLimitRows = 10_000;
    private const int LiveLeases = 9_900;

    private JobExhaustResult _first = null!;
    private double _firstMilliseconds;
    private LatencySummary _steady = null!;
    private int _steadyRows = -1;
    private JobExhaustResult _cancelled = null!;
    private readonly List<int> _cancelledBatches = [];
    private JobExhaustResult _resumed = null!;
    private readonly List<int> _resumedBatches = [];
    private LatencySummary _batches = null!;
    private int _liveLeasesLeft;

    [OneTimeSetUp]
    public async Task RunProbe()
    {
        await ResetAsync();
        await SeedJobsAsync(
            JobStatuses.Completed,
            80_000,
            attemptCount: 1,
            createdAgeSeconds: 86_400,
            finishedAgeSeconds: 3_600
        );
        await SeedJobsAsync(JobStatuses.Pending, LoweredLimitRows, attemptCount: 1, createdAgeSeconds: 600);
        await SeedJobsAsync(JobStatuses.InProgress, LiveLeases, attemptCount: 1, createdAgeSeconds: 600);
        await SeedJobsAsync(JobStatuses.Pending, 50, attemptCount: MaxAttempts, createdAgeSeconds: 300);
        await SeedJobsAsync(
            JobStatuses.InProgress,
            50,
            attemptCount: MaxAttempts,
            createdAgeSeconds: 300,
            leaseRemainingSeconds: -60
        );
        await AnalyzeAsync();

        long start = Stopwatch.GetTimestamp();
        _first = await LeaseRepository()
            .Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, CancellationToken.None);
        _firstMilliseconds = ElapsedMilliseconds(start);
        Report("exhaust", "first_sweep", $"{Rows(_first)} rows in {_firstMilliseconds:F2}ms");

        List<double> steady = [];
        int steadyRows = 0;
        for (int sweep = 0; sweep < SteadyStateSweeps; sweep++)
        {
            start = Stopwatch.GetTimestamp();
            steadyRows += Rows(
                await LeaseRepository()
                    .Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, CancellationToken.None)
            );
            steady.Add(ElapsedMilliseconds(start));
        }
        _steadyRows = steadyRows;
        _steady = LatencySummary.From(steady);
        Report("exhaust", "steady_state_sweeps", _steady);

        // MaxAttempts lowered to 1: 10 000 rows, stopped after the first committed batch and then resumed.
        List<double> batchMilliseconds = [];
        using (CancellationTokenSource stop = new())
        {
            long batchStart = Stopwatch.GetTimestamp();
            _cancelled = await LeaseRepository(rows =>
                {
                    batchMilliseconds.Add(ElapsedMilliseconds(batchStart));
                    batchStart = Stopwatch.GetTimestamp();
                    _cancelledBatches.Add(rows);
                    stop.Cancel();
                })
                .Exhaust(1, JobErrorCode.AttemptsExhausted, stop.Token);
        }

        long resumedStart = Stopwatch.GetTimestamp();
        _resumed = await LeaseRepository(rows =>
            {
                batchMilliseconds.Add(ElapsedMilliseconds(resumedStart));
                resumedStart = Stopwatch.GetTimestamp();
                _resumedBatches.Add(rows);
            })
            .Exhaust(1, JobErrorCode.AttemptsExhausted, CancellationToken.None);
        _batches = LatencySummary.From(batchMilliseconds);
        Report(
            "exhaust",
            "lowered_limit_batches",
            $"{string.Join(",", _cancelledBatches)} | {string.Join(",", _resumedBatches)}"
        );
        Report("exhaust", "lowered_limit_batch", _batches);

        _liveLeasesLeft = await ScalarAsync<int>(
            """
            SELECT count(*)::int FROM "dmscs"."Job"
            WHERE "Status" = 'InProgress' AND "LeaseExpiresAt" > (clock_timestamp() AT TIME ZONE 'UTC');
            """
        );
        Report("exhaust", "live_leases_untouched", _liveLeasesLeft);
    }

    private static int Rows(JobExhaustResult result) =>
        result.Should().BeOfType<JobExhaustResult.Success>().Subject.ExhaustedCount;

    [Test]
    public void It_exhausts_exactly_the_rows_at_the_limit_within_500_ms()
    {
        Rows(_first).Should().Be(100);
        _firstMilliseconds.Should().BeLessThanOrEqualTo(500);
    }

    [Test]
    public void It_sweeps_a_quiet_table_within_500_ms_at_p99()
    {
        _steadyRows.Should().Be(0);
        _steady.P99.Should().BeLessThanOrEqualTo(500);
    }

    [Test]
    public void It_commits_bounded_batches_and_stops_between_them()
    {
        Rows(_cancelled).Should().Be(1_000);
        _cancelledBatches.Should().Equal(1_000);
        Rows(_resumed).Should().Be(9_000);
        _resumedBatches.Should().Equal(1_000, 1_000, 1_000, 1_000, 1_000, 1_000, 1_000, 1_000, 1_000, 0);
        _batches.P99.Should().BeLessThanOrEqualTo(500);
    }

    [Test]
    public void It_leaves_live_leases_untouched() => _liveLeasesLeft.Should().Be(LiveLeases);
}

[TestFixture]
[Explicit("DMS-1437 step 2.11 operational probe")]
public class Given_retention_batches_through_the_retention_repository : JobRepositoryProbeBase
{
    private const int BatchSize = 500;
    private const int Batches = 20;
    private const int RetentionSeconds = 7 * 86_400;

    private readonly List<int> _deleted = [];
    private LatencySummary _batches = null!;
    private int _expiredLeft;
    private int _recentLeft;
    private int _activeLeft;

    [OneTimeSetUp]
    public async Task RunProbe()
    {
        const int thirtyDays = 30 * 86_400;
        await ResetAsync();
        await SeedJobsAsync(
            JobStatuses.Completed,
            40_000,
            attemptCount: 1,
            createdAgeSeconds: thirtyDays,
            finishedAgeSeconds: thirtyDays
        );
        await SeedJobsAsync(
            JobStatuses.Error,
            20_000,
            attemptCount: 5,
            createdAgeSeconds: thirtyDays,
            finishedAgeSeconds: thirtyDays
        );
        await SeedJobsAsync(
            JobStatuses.Completed,
            40_000,
            attemptCount: 1,
            createdAgeSeconds: 86_400,
            finishedAgeSeconds: 86_400
        );
        await SeedJobsAsync(JobStatuses.Pending, 2_000, attemptCount: 0, createdAgeSeconds: thirtyDays);
        await AnalyzeAsync();

        JobRetentionRepository repository = new(DatabaseOptions);
        List<double> batches = [];
        for (int batch = 0; batch < Batches; batch++)
        {
            long start = Stopwatch.GetTimestamp();
            JobRetentionResult result = await repository.DeleteFinishedOlderThan(
                RetentionSeconds,
                BatchSize,
                CancellationToken.None
            );
            batches.Add(ElapsedMilliseconds(start));
            _deleted.Add(result.Should().BeOfType<JobRetentionResult.Success>().Subject.DeletedCount);
        }
        _batches = LatencySummary.From(batches);
        Report("retention", "batch_500", _batches);

        _expiredLeft = await ScalarAsync<int>(
            """
            SELECT count(*)::int FROM "dmscs"."Job"
            WHERE "Status" IN ('Completed', 'Error')
              AND "FinishedAt" <= (now() AT TIME ZONE 'UTC') - interval '7 days';
            """
        );
        _recentLeft = await ScalarAsync<int>(
            """
            SELECT count(*)::int FROM "dmscs"."Job"
            WHERE "Status" IN ('Completed', 'Error')
              AND "FinishedAt" > (now() AT TIME ZONE 'UTC') - interval '7 days';
            """
        );
        _activeLeft = await ScalarAsync<int>(
            """SELECT count(*)::int FROM "dmscs"."Job" WHERE "Status" = 'Pending';"""
        );
        Report("retention", "rows_left", $"expired={_expiredLeft} recent={_recentLeft} active={_activeLeft}");
    }

    [Test]
    public void It_deletes_full_batches_within_1_s()
    {
        _deleted.Should().HaveCount(Batches).And.OnlyContain(rows => rows == BatchSize);
        _batches.Max.Should().BeLessThanOrEqualTo(1_000);
    }

    [Test]
    public void It_deletes_only_expired_finished_rows()
    {
        _expiredLeft.Should().Be(60_000 - (Batches * BatchSize));
        _recentLeft.Should().Be(40_000);
        _activeLeft.Should().Be(2_000);
    }
}

[TestFixture]
[Explicit("DMS-1437 step 2.11 operational probe")]
public class Given_three_sessions_running_mixed_operations_through_the_lease_repository
    : JobRepositoryProbeBase
{
    private const int Sessions = 3;
    private const int SeededJobs = 500;
    private const int TargetOperations = 1_000;

    private readonly ConcurrentBag<string> _outcomes = [];
    private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _latencies = new();
    private int _operations;
    private int _emptyClaims;
    private int _claimedTwice;
    private int _claims;
    private int _completed;

    [OneTimeSetUp]
    public async Task RunProbe()
    {
        await ResetAsync();
        await SeedJobsAsync(JobStatuses.Pending, SeededJobs, attemptCount: 0, createdAgeSeconds: 60);
        await AnalyzeAsync();
        JobLeaseRepository repository = LeaseRepository();

        await Task.WhenAll(
            Enumerable
                .Range(0, Sessions)
                .Select(session =>
                    Task.Run(async () =>
                    {
                        string owner = $"probe-session-{session}";
                        int iteration = 0;
                        while (Volatile.Read(ref _operations) < TargetOperations)
                        {
                            iteration++;
                            if (iteration % 10 == 0)
                            {
                                JobExhaustResult exhausted = await Timed(
                                    "exhaust",
                                    () =>
                                        repository.Exhaust(
                                            MaxAttempts,
                                            JobErrorCode.AttemptsExhausted,
                                            CancellationToken.None
                                        )
                                );
                                _outcomes.Add(
                                    exhausted is JobExhaustResult.Success
                                        ? "exhaust:Success"
                                        : $"exhaust:{exhausted}"
                                );
                            }

                            JobClaimResult claim = await Timed(
                                "claim",
                                () =>
                                    repository.ClaimNext(
                                        owner,
                                        LeaseSeconds,
                                        MaxAttempts,
                                        CancellationToken.None
                                    )
                            );
                            Interlocked.Increment(ref _operations);
                            if (claim is not JobClaimResult.Claimed { Job: var job })
                            {
                                _outcomes.Add($"claim:{claim.GetType().Name}");
                                Interlocked.Increment(ref _emptyClaims);
                                continue;
                            }

                            Interlocked.Increment(ref _claims);
                            JobWriteResult renewed = await Timed(
                                "renew",
                                () =>
                                    repository.Renew(
                                        job.Id,
                                        owner,
                                        job.FencingToken,
                                        LeaseSeconds,
                                        CancellationToken.None
                                    )
                            );
                            Interlocked.Increment(ref _operations);
                            _outcomes.Add($"renew:{Outcome(renewed)}");

                            JobWriteResult completed = await Timed(
                                "complete",
                                () =>
                                    repository.Complete(
                                        job.Id,
                                        owner,
                                        job.FencingToken,
                                        CancellationToken.None
                                    )
                            );
                            Interlocked.Increment(ref _operations);
                            _outcomes.Add($"complete:{Outcome(completed)}");
                        }
                    })
                )
        );

        foreach ((string operation, ConcurrentBag<double> samples) in _latencies.OrderBy(entry => entry.Key))
        {
            Report("mixed", operation, LatencySummary.From(samples));
        }
        Report("mixed", "operations", _operations);
        Report("mixed", "outcomes", Tally(_outcomes));

        _claimedTwice = await ScalarAsync<int>(
            """SELECT count(*)::int FROM "dmscs"."Job" WHERE "AttemptCount" > 1 OR "FencingToken" > 1;"""
        );
        _completed = await ScalarAsync<int>(
            """SELECT count(*)::int FROM "dmscs"."Job" WHERE "Status" = 'Completed';"""
        );
        Report("mixed", "claimed_twice", _claimedTwice);
        Report("mixed", "completed", $"{_completed} of {_claims} claimed");
    }

    private async Task<T> Timed<T>(string operation, Func<Task<T>> call)
    {
        long start = Stopwatch.GetTimestamp();
        T result = await call();
        _latencies.GetOrAdd(operation, _ => []).Add(ElapsedMilliseconds(start));
        return result;
    }

    [Test]
    public void It_runs_at_least_1000_operations() =>
        _operations.Should().BeGreaterThanOrEqualTo(TargetOperations);

    [Test]
    public void It_has_no_deadlock_timeout_or_lost_ownership() =>
        _outcomes
            .Should()
            .OnlyContain(outcome =>
                outcome == "exhaust:Success" || outcome == "renew:Success" || outcome == "complete:Success"
            );

    [Test]
    public void It_never_comes_back_empty_while_jobs_remain() => _emptyClaims.Should().Be(0);

    [Test]
    public void It_claims_each_job_once_and_completes_every_claim()
    {
        _claimedTwice.Should().Be(0);
        _completed.Should().Be(_claims);
    }
}

[TestFixture]
[Explicit("DMS-1437 step 2.11 operational probe")]
public class Given_live_schedule_materializations_through_the_schedule_repository : JobRepositoryProbeBase
{
    private static readonly (int IntervalMinutes, double OverdueSeconds)[] _schedules =
    [
        (1, 0.1),
        (1, 59.9),
        (1, 60.0),
        (1, 60.1),
        (5, 300.05),
        (5, 5 * 300 + 30),
        (60, 3_600 - 0.2),
        (60, 26 * 3_600 + 0.9),
        (1_440, 86_400 * 3.5),
        (1_440, 86_400 * 800),
        (10_080, 86_400 * 30),
        (527_040, 86_400 * 400),
    ];

    private readonly List<JobScheduleMaterializeResult> _results = [];
    private readonly List<string> _mismatches = [];
    private LatencySummary _transactions = null!;
    private JobScheduleMaterializeResult _afterAll = null!;
    private int _eligibleFromCreation;
    private readonly List<string> _materializedJobIds = [];
    private readonly List<string> _claimedJobIds = [];

    [OneTimeSetUp]
    public async Task RunProbe()
    {
        await ResetAsync();
        for (int index = 0; index < _schedules.Length; index++)
        {
            (int interval, double overdue) = _schedules[index];
            await ExecuteAsync(
                """
                INSERT INTO "dmscs"."JobSchedule" (
                    "ScheduleType", "JobType", "PayloadVersion", "Payload", "IntervalMinutes", "Enabled", "NextRunAt")
                VALUES (@ScheduleType, 'DataStore.RefreshEducationOrganizations', 1, '{"dataStoreId":1}',
                    @IntervalMinutes, TRUE, (clock_timestamp() AT TIME ZONE 'UTC') - @Overdue * interval '1 second');
                """,
                new
                {
                    ScheduleType = $"Probe.Schedule{index}",
                    IntervalMinutes = interval,
                    Overdue = overdue,
                }
            );
        }

        JobScheduleRepository repository = ScheduleRepository();
        Dictionary<long, int> intervals = await IntervalsAsync();
        List<double> transactions = [];
        for (int materialization = 0; materialization < _schedules.Length; materialization++)
        {
            long start = Stopwatch.GetTimestamp();
            JobScheduleMaterializeResult result = await repository.MaterializeNextDue(
                "probe-scheduler",
                LeaseSeconds,
                Guid.NewGuid().ToString("N"),
                CancellationToken.None
            );
            transactions.Add(ElapsedMilliseconds(start));
            _results.Add(result);
            if (result is JobScheduleMaterializeResult.Materialized materialized)
            {
                _materializedJobIds.Add(materialized.JobId);
                TimeSpan interval = TimeSpan.FromMinutes(intervals[materialized.ScheduleId]);
                DateTime expected = ScheduleOccurrenceMath.Advance(
                    materialized.Occurrence,
                    interval,
                    materialized.DatabaseUtcNow
                );
                if (
                    materialized.NewNextRunAt != expected
                    || materialized.NewNextRunAt <= materialized.DatabaseUtcNow
                    || materialized.NewNextRunAt - interval > materialized.DatabaseUtcNow
                )
                {
                    _mismatches.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{materialized.ScheduleId}: {materialized.NewNextRunAt:O} vs {expected:O} at {materialized.DatabaseUtcNow:O}"
                        )
                    );
                }
            }
        }
        _transactions = LatencySummary.From(transactions);
        _afterAll = await repository.MaterializeNextDue(
            "probe-scheduler",
            LeaseSeconds,
            Guid.NewGuid().ToString("N"),
            CancellationToken.None
        );
        Report(
            "coalescing",
            "live_materializations",
            _results.Count(result => result is JobScheduleMaterializeResult.Materialized)
        );
        Report("coalescing", "live_mismatches", _mismatches.Count);
        Report("coalescing", "materialization_transaction", _transactions);

        _eligibleFromCreation = await ScalarAsync<int>(
            """
            SELECT count(*)::int FROM "dmscs"."Job"
            WHERE "SourceScheduleId" IS NOT NULL AND "NextAttemptAt" = "CreatedAt";
            """
        );
        JobLeaseRepository lease = LeaseRepository();
        while (
            await lease.ClaimNext("probe-worker", LeaseSeconds, MaxAttempts, CancellationToken.None)
                is JobClaimResult.Claimed claimed
        )
        {
            _claimedJobIds.Add(claimed.Job.JobId);
        }
        Report(
            "coalescing",
            "materialized_jobs_claimed_in_order",
            _claimedJobIds.SequenceEqual(_materializedJobIds)
        );
    }

    private async Task<Dictionary<long, int>> IntervalsAsync()
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return (
            await connection.QueryAsync<(long Id, int IntervalMinutes)>(
                """SELECT "Id", "IntervalMinutes" FROM "dmscs"."JobSchedule";"""
            )
        ).ToDictionary(row => row.Id, row => row.IntervalMinutes);
    }

    [Test]
    public void It_materializes_every_due_schedule_once()
    {
        _results.Should().OnlyContain(result => result is JobScheduleMaterializeResult.Materialized);
        _afterAll.Should().BeOfType<JobScheduleMaterializeResult.NoneDue>();
    }

    [Test]
    public void It_advances_each_schedule_to_the_reference_boundary() => _mismatches.Should().BeEmpty();

    [Test]
    public void It_makes_every_occurrence_claimable_in_enqueue_order()
    {
        _eligibleFromCreation.Should().Be(_schedules.Length);
        _claimedJobIds.Should().Equal(_materializedJobIds);
    }
}

[TestFixture]
[Explicit("DMS-1437 step 2.11 operational probe")]
public class Given_claims_over_fresh_retried_released_and_reclaimable_jobs_through_the_repository
    : JobRepositoryProbeBase
{
    private readonly List<string> _claimOrder = [];
    private readonly Dictionary<string, (int AttemptCount, long FencingToken)> _claimed = [];
    private int _untouched;

    [OneTimeSetUp]
    public async Task RunProbe()
    {
        await ResetAsync();
        await InsertAsync(
            "live_lease",
            JobStatuses.InProgress,
            nextAttemptOffset: -40,
            attemptCount: 1,
            leaseOffset: 300
        );
        await InsertAsync(
            "reclaimable",
            JobStatuses.InProgress,
            nextAttemptOffset: -30,
            attemptCount: 1,
            leaseOffset: -1
        );
        await InsertAsync("released", JobStatuses.Pending, nextAttemptOffset: -20, attemptCount: 1);
        await InsertAsync("fresh_a", JobStatuses.Pending, nextAttemptOffset: -10, attemptCount: 0);
        await InsertAsync("fresh_b", JobStatuses.Pending, nextAttemptOffset: -5, attemptCount: 0);
        await InsertAsync("retry_in_backoff", JobStatuses.Pending, nextAttemptOffset: 600, attemptCount: 1);
        await InsertAsync("at_limit", JobStatuses.Pending, nextAttemptOffset: -50, attemptCount: MaxAttempts);

        JobLeaseRepository repository = LeaseRepository();
        while (
            await repository.ClaimNext("probe-claimer", LeaseSeconds, MaxAttempts, CancellationToken.None)
                is JobClaimResult.Claimed claimed
        )
        {
            _claimOrder.Add(claimed.Job.JobId);
            _claimed[claimed.Job.JobId] = (claimed.Job.AttemptCount, claimed.Job.FencingToken);
        }
        Report("order", "claims", string.Join(",", _claimOrder));

        _untouched = await ScalarAsync<int>(
            """
            SELECT count(*)::int FROM "dmscs"."Job"
            WHERE ("JobId" = 'live_lease' AND "LeaseOwner" = 'probe-seed')
               OR ("JobId" = 'retry_in_backoff' AND "Status" = 'Pending')
               OR ("JobId" = 'at_limit' AND "Status" = 'Pending');
            """
        );
    }

    private Task InsertAsync(
        string jobId,
        string status,
        double nextAttemptOffset,
        int attemptCount,
        double? leaseOffset = null
    ) =>
        ExecuteAsync(
            """
            INSERT INTO "dmscs"."Job" (
                "JobId", "JobType", "PayloadVersion", "Payload", "Status", "NextAttemptAt", "AttemptCount",
                "FencingToken", "LeaseOwner", "LeaseExpiresAt")
            VALUES (@JobId, 'DataStore.RefreshEducationOrganizations', 1, '{"dataStoreId":1}', @Status,
                (clock_timestamp() AT TIME ZONE 'UTC') + @NextAttemptOffset * interval '1 second',
                @AttemptCount, @AttemptCount,
                CASE WHEN @Status = 'InProgress' THEN 'probe-seed' END,
                (clock_timestamp() AT TIME ZONE 'UTC') + @LeaseOffset * interval '1 second');
            """,
            new
            {
                JobId = jobId,
                Status = status,
                NextAttemptOffset = nextAttemptOffset,
                AttemptCount = attemptCount,
                LeaseOffset = leaseOffset,
            }
        );

    [Test]
    public void It_claims_in_eligibility_order() =>
        _claimOrder.Should().Equal("reclaimable", "released", "fresh_a", "fresh_b");

    [Test]
    public void It_counts_the_reclaim_as_a_new_attempt_with_a_new_token() =>
        _claimed["reclaimable"].Should().Be((2, 2L));

    [Test]
    public void It_leaves_the_live_lease_the_backoff_and_the_exhausted_job_alone() =>
        _untouched.Should().Be(3);
}
