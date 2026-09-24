// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

public class JobLeaseRepositoryTests
{
    private const string OwnerA = "worker-a";
    private const string OwnerB = "worker-b";
    private const int MaxAttempts = 5;

    private static readonly JobLeaseTimings _timings = new(
        RenewalTimeout: TimeSpan.FromSeconds(30),
        FenceTimeout: TimeSpan.FromSeconds(10)
    );

    /// <summary>The ownership columns of a stored job, with the database time they were read at.</summary>
    public sealed class LeaseRow
    {
        public string Status { get; set; } = "";
        public int AttemptCount { get; set; }
        public long FencingToken { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ModifiedBy { get; set; }
    }

    /// <summary>The coarse (non-intent table, partition, or page) and key locks a session holds.</summary>
    public sealed class LockFootprint
    {
        public int CoarseLocks { get; set; }
        public int KeyLocks { get; set; }
    }

    /// <summary>
    /// Session hooks that hold the first operation named <c>name</c> pending for <c>hold</c>, ignoring its
    /// cancellation as a provider waiting for an acknowledgement would, optionally fail it afterwards, and collect the
    /// cleanup task of every hand-over.
    /// </summary>
    public sealed class PendingOperation
    {
        private readonly List<Task<Exception?>> _cleanups = [];
        private int _held;

        public PendingOperation(string name, TimeSpan hold, Exception? failure = null)
        {
            Hooks = new JobDatabaseSessionHooks
            {
                BeforeOperation = async (operation, _) =>
                {
                    if (operation == name && Interlocked.Exchange(ref _held, 1) == 0)
                    {
                        await Task.Delay(hold, CancellationToken.None);
                        if (failure is not null)
                        {
                            throw failure;
                        }
                    }
                },
                HandedOver = cleanup =>
                {
                    lock (_cleanups)
                    {
                        _cleanups.Add(cleanup);
                    }
                },
            };
        }

        public JobDatabaseSessionHooks Hooks { get; }

        /// <summary>Waits for the single hand-over's cleanup and returns how the pending operation ended.</summary>
        public async Task<Exception?> CleanupAsync()
        {
            Task<Exception?> cleanup;
            lock (_cleanups)
            {
                _cleanups.Should().ContainSingle("the operation was handed over exactly once");
                cleanup = _cleanups[0];
            }

            return await cleanup.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    public abstract class JobLeaseTestBase : JobSchemaTestBase
    {
        protected static JobLeaseRepository Repository(JobLeaseTimings? timings = null) =>
            new(MssqlTestConfiguration.DatabaseOptions, timings ?? _timings);

        protected static MssqlJobFenceFactory FenceFactory(JobLeaseTimings? timings = null) =>
            new(MssqlTestConfiguration.DatabaseOptions, timings ?? _timings);

        /// <summary>Seeds one job whose times are offsets, in seconds, from database time.</summary>
        protected async Task<long> SeedJobAsync(
            string status = JobStatuses.Pending,
            double nextAttemptOffset = -1,
            int attemptCount = 0,
            string? leaseOwner = null,
            double? leaseOffset = null,
            long fencingToken = 0,
            string jobType = "DataStore.RefreshEducationOrganizations"
        ) =>
            await Connection!.ExecuteScalarAsync<long>(
                """
                INSERT INTO dmscs.Job (
                    JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt, AttemptCount, LeaseOwner,
                    LeaseExpiresAt, FencingToken, FinishedAt)
                OUTPUT inserted.Id
                VALUES (@JobId, @JobType, 1, N'{"dataStoreId":1}', @Status,
                    CASE WHEN @Status IN (N'Pending', N'InProgress')
                        THEN DATEADD(millisecond, CAST(@NextAttemptOffset * 1000 AS INT), SYSUTCDATETIME()) END,
                    @AttemptCount, @LeaseOwner,
                    DATEADD(millisecond, CAST(@LeaseOffset * 1000 AS INT), SYSUTCDATETIME()),
                    @FencingToken,
                    CASE WHEN @Status IN (N'Completed', N'Error') THEN SYSUTCDATETIME() END);
                """,
                new
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    JobType = jobType,
                    Status = status,
                    NextAttemptOffset = nextAttemptOffset,
                    AttemptCount = attemptCount,
                    LeaseOwner = leaseOwner,
                    LeaseOffset = leaseOffset,
                    FencingToken = fencingToken,
                }
            );

        /// <summary>Gives <paramref name="to"/> exactly the <c>NextAttemptAt</c> of <paramref name="from"/>.</summary>
        protected async Task CopyNextAttemptAtAsync(long from, long to) =>
            await Connection!.ExecuteAsync(
                """
                UPDATE dmscs.Job
                SET NextAttemptAt = (SELECT NextAttemptAt FROM dmscs.Job WHERE Id = @From)
                WHERE Id = @To;
                """,
                new { From = from, To = to }
            );

        protected async Task SeedOverLimitJobsAsync(int count) =>
            await Connection!.ExecuteAsync(
                """
                WITH numbers AS (
                    SELECT TOP (@Count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                    FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
                )
                INSERT INTO dmscs.Job (JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt, AttemptCount)
                SELECT LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')),
                    N'DataStore.RefreshEducationOrganizations', 1, N'{"dataStoreId":1}', N'Pending', SYSUTCDATETIME(),
                    @MaxAttempts
                FROM numbers;
                """,
                new { Count = count, MaxAttempts }
            );

        /// <summary>Seeds <paramref name="count"/> jobs that are eligible to be claimed now.</summary>
        protected async Task SeedEligibleJobsAsync(int count) =>
            await Connection!.ExecuteAsync(
                """
                WITH numbers AS (
                    SELECT TOP (@Count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                    FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
                )
                INSERT INTO dmscs.Job (JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt, AttemptCount)
                SELECT LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')),
                    N'DataStore.RefreshEducationOrganizations', 1, N'{"dataStoreId":1}', N'Pending',
                    DATEADD(millisecond, -(@Count - n), SYSUTCDATETIME()), 0
                FROM numbers;
                """,
                new { Count = count }
            );

        protected async Task<LeaseRow> RowAsync(long id) =>
            await Connection!.QuerySingleAsync<LeaseRow>(
                """
                SELECT Status, AttemptCount, FencingToken, LeaseOwner, LeaseExpiresAt, NextAttemptAt, FinishedAt,
                       ErrorMessage, ModifiedBy
                FROM dmscs.Job WHERE Id = @Id;
                """,
                new { Id = id }
            );

        /// <summary>Moves a lease into the past by database time, as if it had expired unrenewed.</summary>
        protected async Task ExpireLeaseAsync(long id) =>
            await Connection!.ExecuteAsync(
                "UPDATE dmscs.Job SET LeaseExpiresAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE Id = @Id;",
                new { Id = id }
            );

        protected async Task<long> CountAsync(string where) =>
            await Connection!.ExecuteScalarAsync<long>($"SELECT COUNT_BIG(*) FROM dmscs.Job WHERE {where};");

        protected async Task<long> FencedWriteCountAsync() =>
            await Connection!.ExecuteScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM dmscs.OwnershipToken WHERE Description = N'fenced-write';"
            );

        /// <summary>
        /// The locks the given session holds right now: table, partition, or page locks other than intent locks,
        /// which would mean escalation, and key (row) locks.
        /// </summary>
        protected static async Task<LockFootprint> HeldLocksAsync(
            SqlConnection connection,
            DbTransaction transaction
        ) =>
            await connection.QuerySingleAsync<LockFootprint>(
                """
                SELECT
                    COUNT(CASE WHEN resource_type IN (N'OBJECT', N'HOBT', N'PAGE') AND request_mode NOT LIKE N'I%'
                        THEN 1 END) AS CoarseLocks,
                    COUNT(CASE WHEN resource_type = N'KEY' THEN 1 END) AS KeyLocks
                FROM sys.dm_tran_locks
                WHERE request_session_id = @@SPID AND resource_database_id = DB_ID();
                """,
                transaction: transaction
            );

        /// <summary>Options for a private pool of one connection, so a test sees the session the operation used.</summary>
        protected static IOptions<DatabaseOptions> SingleConnectionPool(string name) =>
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = new SqlConnectionStringBuilder(
                        MssqlTestConfiguration.DatabaseConnectionString
                    )
                    {
                        MinPoolSize = 0,
                        MaxPoolSize = 1,
                        ApplicationName = $"{name}-{Guid.NewGuid():N}",
                    }.ConnectionString,
                    EncryptionKey = MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey,
                }
            );

        protected static ClaimedJob Claimed(JobClaimResult result) =>
            result.Should().BeOfType<JobClaimResult.Claimed>().Subject.Job;

        protected static async Task<ClaimedJob> ClaimAsync(string owner = OwnerA, int leaseSeconds = 300) =>
            Claimed(await Repository().ClaimNext(owner, leaseSeconds, MaxAttempts, CancellationToken.None));

        /// <summary>A consumer write inside the fence's transaction.</summary>
        protected static async Task FencedWriteAsync(
            DbTransaction transaction,
            CancellationToken cancellationToken
        ) =>
            await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO dmscs.OwnershipToken (Description) VALUES (N'fenced-write');",
                    transaction: transaction,
                    cancellationToken: cancellationToken
                )
            );

        /// <summary>Runs <paramref name="operation"/> and returns what it threw, or null.</summary>
        protected static async Task<Exception?> ThrownByAsync(Func<Task> operation)
        {
            try
            {
                await operation();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }

    /// <summary>Another transaction holding an update lock (<c>UPDLOCK</c>) on one job row until it is released.</summary>
    public sealed class RowLock : IAsyncDisposable
    {
        private readonly SqlConnection _connection;
        private readonly DbTransaction _transaction;
        private int _released;

        private RowLock(SqlConnection connection, DbTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public static async Task<RowLock> AcquireAsync(long id)
        {
            SqlConnection connection = new(MssqlTestConfiguration.DatabaseConnectionString);
            await connection.OpenAsync();
            DbTransaction transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(
                "SELECT Id FROM dmscs.Job WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id;",
                new { Id = id },
                transaction
            );
            return new RowLock(connection, transaction);
        }

        /// <summary>Releases the lock, without changing the row, after <paramref name="delay"/>.</summary>
        public Task ReleaseAfterAsync(TimeSpan delay) =>
            Task.Run(async () =>
            {
                await Task.Delay(delay);
                await DisposeAsync();
            });

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return;
            }

            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    [TestFixture]
    public class Given_one_eligible_job_and_concurrent_claimers : JobLeaseTestBase
    {
        private long _id;
        private JobClaimResult[] _results = [];
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            _id = await SeedJobAsync();
            _results = await Task.WhenAll(
                Enumerable
                    .Range(1, 8)
                    .Select(worker =>
                        Repository().ClaimNext($"worker-{worker}", 300, MaxAttempts, CancellationToken.None)
                    )
            );
            _row = await RowAsync(_id);
        }

        [Test]
        public void It_permits_at_most_one_unexpired_lease_under_concurrent_claims()
        {
            _results.OfType<JobClaimResult.Claimed>().Should().ContainSingle();
            _results.OfType<JobClaimResult.NoneAvailable>().Should().HaveCount(7);
        }

        [Test]
        public void It_records_the_single_winner_as_the_lease_owner() =>
            _row.LeaseOwner.Should().Be(_results.OfType<JobClaimResult.Claimed>().Single().Job.LeaseOwner);
    }

    [TestFixture]
    public class Given_jobs_in_every_claim_state : JobLeaseTestBase
    {
        private long _expiredLease;
        private long _firstTied;
        private long _secondTied;
        private long _inBackoff;
        private long _liveLease;
        private long _atLimit;
        private readonly List<JobClaimResult> _claims = [];
        private readonly Dictionary<long, LeaseRow> _skipped = [];

        [SetUp]
        public async Task Setup()
        {
            _claims.Clear();
            _firstTied = await SeedJobAsync(nextAttemptOffset: -5);
            _secondTied = await SeedJobAsync();
            await CopyNextAttemptAtAsync(_firstTied, _secondTied);
            _inBackoff = await SeedJobAsync(nextAttemptOffset: 60, attemptCount: 1);
            _liveLease = await SeedJobAsync(
                JobStatuses.InProgress,
                nextAttemptOffset: -20,
                attemptCount: 1,
                leaseOwner: "live-owner",
                leaseOffset: 300,
                fencingToken: 1
            );
            _atLimit = await SeedJobAsync(nextAttemptOffset: -30, attemptCount: MaxAttempts);
            _expiredLease = await SeedJobAsync(
                JobStatuses.InProgress,
                nextAttemptOffset: -10,
                attemptCount: 1,
                leaseOwner: "crashed-owner",
                leaseOffset: -1,
                fencingToken: 1
            );

            for (int claim = 0; claim < 4; claim++)
            {
                _claims.Add(await Repository().ClaimNext(OwnerA, 300, MaxAttempts, CancellationToken.None));
            }

            foreach (long id in new[] { _inBackoff, _liveLease, _atLimit })
            {
                _skipped[id] = await RowAsync(id);
            }
        }

        [Test]
        public void It_claims_in_next_attempt_order_with_ties_broken_by_id() =>
            _claims
                .Take(3)
                .Select(claim => Claimed(claim).Id)
                .Should()
                .Equal(_expiredLease, _firstTied, _secondTied);

        [Test]
        public void It_reclaims_the_expired_lease_with_a_new_attempt_and_token() =>
            Claimed(_claims[0])
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        AttemptCount = 2,
                        FencingToken = 2L,
                        LeaseOwner = OwnerA,
                    }
                );

        [Test]
        public void It_skips_a_retry_in_backoff_a_live_lease_and_a_row_at_the_limit()
        {
            _claims[3].Should().BeOfType<JobClaimResult.NoneAvailable>();
            _skipped[_inBackoff].Status.Should().Be(JobStatuses.Pending);
            _skipped[_liveLease].LeaseOwner.Should().Be("live-owner");
            _skipped[_atLimit].Status.Should().Be(JobStatuses.Pending);
        }

        [Test]
        public void It_uses_database_time_not_process_time()
        {
            ClaimedJob claimed = Claimed(_claims[1]);
            (claimed.LeaseExpiresAt - claimed.DatabaseUtcNow).Should().Be(TimeSpan.FromSeconds(300));
            claimed.DatabaseUtcNow.Kind.Should().Be(DateTimeKind.Utc);
        }
    }

    [TestFixture]
    public class Given_a_persisted_job_of_a_type_no_handler_supports : JobLeaseTestBase
    {
        private JobClaimResult _claim = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync(jobType: "Retired.JobType");
            _claim = await Repository().ClaimNext(OwnerA, 300, MaxAttempts, CancellationToken.None);
        }

        [Test]
        public void It_claims_persisted_unknown_type_for_executor_to_terminate() =>
            Claimed(_claim)
                .Should()
                .BeEquivalentTo(new { JobType = "Retired.JobType", PayloadJson = """{"dataStoreId":1}""" });
    }

    [TestFixture]
    public class Given_a_claimed_job_that_expires_and_is_reclaimed : JobLeaseTestBase
    {
        private ClaimedJob _first = null!;
        private ClaimedJob _second = null!;
        private readonly Dictionary<string, JobWriteResult> _staleWrites = [];
        private JobWriteResult _currentOwnerCompletes = null!;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            _first = await ClaimAsync(OwnerA);
            await ExpireLeaseAsync(_first.Id);
            _second = await ClaimAsync(OwnerB);

            foreach ((string name, JobWriteResult result) in await AllWritesAsync(_first))
            {
                _staleWrites[name] = result;
            }

            _currentOwnerCompletes = await Repository()
                .Complete(_second.Id, OwnerB, _second.FencingToken, CancellationToken.None);
            _row = await RowAsync(_first.Id);
        }

        [Test]
        public void It_increments_fencing_token_on_claim_and_reclaim()
        {
            _first.Should().BeEquivalentTo(new { FencingToken = 1L, AttemptCount = 1 });
            _second
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        _first.Id,
                        FencingToken = 2L,
                        AttemptCount = 2,
                        LeaseOwner = OwnerB,
                    }
                );
        }

        [Test]
        public void It_keeps_the_claim_position_on_reclaim() =>
            _second.NextAttemptAt.Should().Be(_first.NextAttemptAt);

        [Test]
        public void It_rejects_stale_token_after_reclaim()
        {
            foreach ((string name, JobWriteResult result) in _staleWrites)
            {
                result.Should().BeOfType<JobWriteResult.OwnershipLost>(name);
            }
        }

        [Test]
        public void It_lets_the_current_owner_complete()
        {
            _currentOwnerCompletes.Should().BeOfType<JobWriteResult.Success>();
            _row.Status.Should().Be(JobStatuses.Completed);
        }
    }

    [TestFixture]
    public class Given_a_job_reclaimed_by_the_same_owner : JobLeaseTestBase
    {
        private ClaimedJob _first = null!;
        private ClaimedJob _second = null!;
        private readonly Dictionary<string, JobWriteResult> _staleWrites = [];

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            _first = await ClaimAsync(OwnerA);
            await ExpireLeaseAsync(_first.Id);
            _second = await ClaimAsync(OwnerA);

            foreach ((string name, JobWriteResult result) in await AllWritesAsync(_first))
            {
                _staleWrites[name] = result;
            }
        }

        [Test]
        public void It_rejects_the_earlier_token_even_though_the_owner_matches()
        {
            _second.Should().BeEquivalentTo(new { LeaseOwner = OwnerA, FencingToken = 2L });
            foreach ((string name, JobWriteResult result) in _staleWrites)
            {
                result.Should().BeOfType<JobWriteResult.OwnershipLost>(name);
            }
        }
    }

    [TestFixture]
    public class Given_a_claimed_job_whose_lease_expired_without_reclaim : JobLeaseTestBase
    {
        private ClaimedJob _claimed = null!;
        private readonly Dictionary<string, JobWriteResult> _writes = [];
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            _claimed = await ClaimAsync();
            await ExpireLeaseAsync(_claimed.Id);

            foreach ((string name, JobWriteResult result) in await AllWritesAsync(_claimed))
            {
                _writes[name] = result;
            }

            _row = await RowAsync(_claimed.Id);
        }

        [Test]
        public void It_rejects_every_ownership_write_when_expired_without_reclaim()
        {
            _writes.Should().HaveCount(5);
            foreach ((string name, JobWriteResult result) in _writes)
            {
                result.Should().BeOfType<JobWriteResult.OwnershipLost>(name);
            }
        }

        [Test]
        public void It_leaves_the_row_in_progress_with_the_same_owner_and_token() =>
            _row.Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = JobStatuses.InProgress,
                        LeaseOwner = OwnerA,
                        FencingToken = 1L,
                    }
                );
    }

    [TestFixture]
    public class Given_writes_by_the_current_owner : JobLeaseTestBase
    {
        private readonly Dictionary<
            string,
            (ClaimedJob Claim, JobWriteResult Result, LeaseRow Row)
        > _writes = [];

        [SetUp]
        public async Task Setup()
        {
            for (int job = 0; job < 5; job++)
            {
                await SeedJobAsync();
            }

            JobLeaseRepository repository = Repository();
            await Record(
                "renew",
                claim => repository.Renew(claim.Id, OwnerA, claim.FencingToken, 120, default)
            );
            await Record(
                "complete",
                claim => repository.Complete(claim.Id, OwnerA, claim.FencingToken, default)
            );
            await Record(
                "fail terminal",
                claim =>
                    repository.FailTerminal(
                        claim.Id,
                        OwnerA,
                        claim.FencingToken,
                        JobErrorCode.HandlerFailed,
                        default
                    )
            );
            await Record(
                "fail transient",
                claim => repository.FailTransient(claim.Id, OwnerA, claim.FencingToken, 90, default)
            );
            await Record(
                "release",
                claim => repository.ReleaseToPending(claim.Id, OwnerA, claim.FencingToken, default)
            );

            async Task Record(string name, Func<ClaimedJob, Task<JobWriteResult>> write)
            {
                ClaimedJob claim = await ClaimAsync(OwnerA, leaseSeconds: 60);
                JobWriteResult result = await write(claim);
                _writes[name] = (claim, result, await RowAsync(claim.Id));
            }
        }

        [Test]
        public void It_extends_lease_on_renewal_using_database_time()
        {
            (_, JobWriteResult result, LeaseRow row) = _writes["renew"];
            JobWriteResult.Success success = result.Should().BeOfType<JobWriteResult.Success>().Subject;
            (success.NewLeaseExpiresAt - success.DatabaseUtcNow).Should().Be(TimeSpan.FromSeconds(120));
            row.LeaseExpiresAt.Should().Be(success.NewLeaseExpiresAt);
            row.Status.Should().Be(JobStatuses.InProgress);
        }

        [Test]
        public void It_sets_completed_and_finished_at()
        {
            (ClaimedJob claim, JobWriteResult result, LeaseRow row) = _writes["complete"];
            JobWriteResult.Success success = result.Should().BeOfType<JobWriteResult.Success>().Subject;
            success.NewLeaseExpiresAt.Should().BeNull();
            row.Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = JobStatuses.Completed,
                        FinishedAt = (DateTime?)success.DatabaseUtcNow,
                        LeaseOwner = (string?)null,
                        LeaseExpiresAt = (DateTime?)null,
                        ErrorMessage = (string?)null,
                        NextAttemptAt = (DateTime?)claim.NextAttemptAt,
                        ModifiedBy = OwnerA,
                    }
                );
        }

        [Test]
        public void It_sets_error_finished_at_and_registered_message()
        {
            (_, JobWriteResult result, LeaseRow row) = _writes["fail terminal"];
            JobWriteResult.Success success = result.Should().BeOfType<JobWriteResult.Success>().Subject;
            row.Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = JobStatuses.Error,
                        FinishedAt = (DateTime?)success.DatabaseUtcNow,
                        ErrorMessage = JobErrorCode.HandlerFailed.Message,
                        LeaseOwner = (string?)null,
                        LeaseExpiresAt = (DateTime?)null,
                    }
                );
        }

        [Test]
        public void It_returns_to_pending_with_backoff_cleared_lease_null_finished_at()
        {
            (ClaimedJob claim, JobWriteResult result, LeaseRow row) = _writes["fail transient"];
            JobWriteResult.Success success = result.Should().BeOfType<JobWriteResult.Success>().Subject;
            row.Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = JobStatuses.Pending,
                        NextAttemptAt = (DateTime?)success.DatabaseUtcNow.AddSeconds(90),
                        FinishedAt = (DateTime?)null,
                        LeaseOwner = (string?)null,
                        LeaseExpiresAt = (DateTime?)null,
                        claim.AttemptCount,
                        claim.FencingToken,
                    }
                );
        }

        [Test]
        public void It_releases_to_pending_now_keeping_the_attempt()
        {
            (ClaimedJob claim, JobWriteResult result, LeaseRow row) = _writes["release"];
            JobWriteResult.Success success = result.Should().BeOfType<JobWriteResult.Success>().Subject;
            row.Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = JobStatuses.Pending,
                        NextAttemptAt = (DateTime?)success.DatabaseUtcNow,
                        LeaseOwner = (string?)null,
                        LeaseExpiresAt = (DateTime?)null,
                        claim.AttemptCount,
                    }
                );
        }
    }

    [TestFixture]
    public class Given_a_renewal_that_waits_on_a_lock_past_expiry : JobLeaseTestBase
    {
        private JobWriteResult _result = null!;
        private TimeSpan _elapsed;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync(leaseSeconds: 2);
            await using RowLock rowLock = await RowLock.AcquireAsync(claimed.Id);
            Task released = rowLock.ReleaseAfterAsync(TimeSpan.FromSeconds(3));

            long started = Stopwatch.GetTimestamp();
            _result = await Repository()
                .Renew(claimed.Id, OwnerA, claimed.FencingToken, 300, CancellationToken.None);
            _elapsed = Stopwatch.GetElapsedTime(started);
            await released;
            _row = await RowAsync(claimed.Id);
        }

        [Test]
        public void It_rejects_renewal_that_waited_on_a_lock_past_expiry()
        {
            _elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2.5), "the renewal waited for the lock");
            _result.Should().BeOfType<JobWriteResult.OwnershipLost>();
        }

        [Test]
        public void It_leaves_the_expired_lease_unextended() =>
            _row.LeaseExpiresAt.Should().BeBefore(DateTime.UtcNow);
    }

    [TestFixture]
    public class Given_a_completion_that_waits_on_a_lock_past_expiry : JobLeaseTestBase
    {
        private JobWriteResult _result = null!;
        private TimeSpan _elapsed;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync(leaseSeconds: 2);
            await using RowLock rowLock = await RowLock.AcquireAsync(claimed.Id);
            Task released = rowLock.ReleaseAfterAsync(TimeSpan.FromSeconds(3));

            long started = Stopwatch.GetTimestamp();
            _result = await Repository()
                .Complete(claimed.Id, OwnerA, claimed.FencingToken, CancellationToken.None);
            _elapsed = Stopwatch.GetElapsedTime(started);
            await released;
            _row = await RowAsync(claimed.Id);
        }

        [Test]
        public void It_rejects_completion_that_waited_on_a_lock_past_expiry()
        {
            _elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2.5), "the completion waited for the lock");
            _result.Should().BeOfType<JobWriteResult.OwnershipLost>();
        }

        [Test]
        public void It_leaves_the_job_in_progress() => _row.Status.Should().Be(JobStatuses.InProgress);
    }

    [TestFixture]
    public class Given_an_ownership_write_whose_deadline_ends_during_the_lock_wait : JobLeaseTestBase
    {
        private static readonly JobLeaseTimings _shortDeadline = _timings with
        {
            RenewalTimeout = TimeSpan.FromSeconds(2),
        };

        private JobWriteResult _result = null!;
        private TimeSpan _elapsed;
        private ClaimedJob _claimed = null!;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            _claimed = await ClaimAsync();
            await using RowLock rowLock = await RowLock.AcquireAsync(_claimed.Id);
            Task released = rowLock.ReleaseAfterAsync(TimeSpan.FromSeconds(4));

            long started = Stopwatch.GetTimestamp();
            _result = await Repository(_shortDeadline)
                .Renew(_claimed.Id, OwnerA, _claimed.FencingToken, 600, CancellationToken.None);
            _elapsed = Stopwatch.GetElapsedTime(started);
            await released;
            _row = await RowAsync(_claimed.Id);
        }

        [Test]
        public void It_ends_at_the_deadline_rather_than_the_longer_lock_wait() =>
            _elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3.5));

        [Test]
        public void It_reports_that_nothing_was_written()
        {
            JobWriteResult.FailureUnknown failure = _result
                .Should()
                .BeOfType<JobWriteResult.FailureUnknown>()
                .Subject;
            failure.Diagnostic.Operation.Should().Be("Renew");
            failure.Diagnostic.ProviderErrorCode.Should().NotBe("1222");
            _row.LeaseExpiresAt.Should().Be(_claimed.LeaseExpiresAt);
        }
    }

    [TestFixture]
    public class Given_an_ownership_write_that_outwaits_the_write_lock_wait : JobLeaseTestBase
    {
        private JobWriteResult _result = null!;
        private TimeSpan _elapsed;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            await using RowLock rowLock = await RowLock.AcquireAsync(claimed.Id);
            Task released = rowLock.ReleaseAfterAsync(TimeSpan.FromSeconds(7));

            long started = Stopwatch.GetTimestamp();
            _result = await Repository()
                .Complete(claimed.Id, OwnerA, claimed.FencingToken, CancellationToken.None);
            _elapsed = Stopwatch.GetElapsedTime(started);
            await released;
        }

        [Test]
        public void It_reports_a_lock_timeout_after_the_write_lock_wait()
        {
            _elapsed
                .Should()
                .BeGreaterThan(TimeSpan.FromSeconds(4.5))
                .And.BeLessThan(TimeSpan.FromSeconds(6.5));
            _result
                .Should()
                .BeOfType<JobWriteResult.FailureUnknown>()
                .Which.Diagnostic.ProviderErrorCode.Should()
                .Be("1222");
        }
    }

    [TestFixture]
    public class Given_an_ownership_write_whose_deadline_ends_after_the_guarded_update : JobLeaseTestBase
    {
        private static readonly JobLeaseTimings _shortDeadline = _timings with
        {
            RenewalTimeout = TimeSpan.FromSeconds(2),
        };

        private JobWriteResult _result = null!;
        private int _guardedWrites;

        [SetUp]
        public async Task Setup()
        {
            _guardedWrites = 0;
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _shortDeadline)
            {
                BeforeOwnershipCommit = async _ =>
                {
                    Interlocked.Increment(ref _guardedWrites);
                    await Task.Delay(TimeSpan.FromSeconds(2.5), CancellationToken.None);
                },
            };

            _result = await repository.Complete(
                claimed.Id,
                OwnerA,
                claimed.FencingToken,
                CancellationToken.None
            );
        }

        [Test]
        public void It_reports_the_result_as_unknown() =>
            _result
                .Should()
                .BeOfType<JobWriteResult.ResultUnknown>()
                .Which.Diagnostic.Operation.Should()
                .Be("Complete");

        [Test]
        public void It_issues_the_guarded_write_once_without_retrying() => _guardedWrites.Should().Be(1);
    }

    [TestFixture]
    public class Given_a_fence_for_the_current_owner : JobLeaseTestBase
    {
        private Exception? _thrown;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            IJobFence fence = FenceFactory().Create(claimed, new JobExecutionOwnership());

            _thrown = await ThrownByAsync(() => fence.ExecuteAsync(FencedWriteAsync, CancellationToken.None));
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_commits_the_fenced_write()
        {
            _thrown.Should().BeNull();
            _fencedWrites.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_a_fence_after_the_job_was_reclaimed : JobLeaseTestBase
    {
        private Exception? _thrown;
        private bool _workRan;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob first = await ClaimAsync(OwnerA);
            await ExpireLeaseAsync(first.Id);
            await ClaimAsync(OwnerB);
            IJobFence fence = FenceFactory().Create(first, new JobExecutionOwnership());

            _thrown = await ThrownByAsync(() =>
                fence.ExecuteAsync(
                    async (transaction, token) =>
                    {
                        _workRan = true;
                        await FencedWriteAsync(transaction, token);
                    },
                    CancellationToken.None
                )
            );
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_rolls_back_fenced_write_when_reclaimed_before_fence()
        {
            _thrown.Should().BeOfType<JobLeaseLostException>();
            _fencedWrites.Should().Be(0);
        }

        [Test]
        public void It_never_runs_the_work() => _workRan.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_fence_whose_lease_expires_during_work : JobLeaseTestBase
    {
        private Exception? _thrown;
        private bool _workWrote;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync(leaseSeconds: 4);
            IJobFence fence = FenceFactory().Create(claimed, new JobExecutionOwnership());

            _thrown = await ThrownByAsync(() =>
                fence.ExecuteAsync(
                    async (transaction, token) =>
                    {
                        await FencedWriteAsync(transaction, token);
                        _workWrote = true;

                        // Work that ignores its token and outlives the lease.
                        await Task.Delay(TimeSpan.FromSeconds(4.5), CancellationToken.None);
                    },
                    CancellationToken.None
                )
            );
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_rolls_back_fenced_write_when_lease_expires_during_work()
        {
            _workWrote.Should().BeTrue();
            _thrown.Should().BeOfType<JobLeaseLostException>();
            _fencedWrites.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_fenced_work_that_honors_its_token_past_the_fence_deadline : JobLeaseTestBase
    {
        private Exception? _thrown;
        private TimeSpan _elapsed;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync(leaseSeconds: 4);
            IJobFence fence = FenceFactory().Create(claimed, new JobExecutionOwnership());

            long started = Stopwatch.GetTimestamp();
            _thrown = await ThrownByAsync(() =>
                fence.ExecuteAsync(
                    async (transaction, token) =>
                    {
                        await FencedWriteAsync(transaction, token);
                        await Task.Delay(TimeSpan.FromSeconds(8), token);
                    },
                    CancellationToken.None
                )
            );
            _elapsed = Stopwatch.GetElapsedTime(started);
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_cancels_the_work_at_the_deadline_taken_from_the_remaining_lease()
        {
            _elapsed
                .Should()
                .BeLessThan(TimeSpan.FromSeconds(4), "the deadline is at most the remaining lease less 1 s");
            _thrown.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_rolls_back_the_cancelled_work() => _fencedWrites.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_fence_whose_lock_wait_outlasts_the_lease : JobLeaseTestBase
    {
        private Exception? _thrown;
        private bool _workRan;
        private TimeSpan _elapsed;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync(leaseSeconds: 3);
            IJobFence fence = FenceFactory().Create(claimed, new JobExecutionOwnership());
            await using RowLock rowLock = await RowLock.AcquireAsync(claimed.Id);
            Task released = rowLock.ReleaseAfterAsync(TimeSpan.FromSeconds(4));

            long started = Stopwatch.GetTimestamp();
            _thrown = await ThrownByAsync(() =>
                fence.ExecuteAsync(
                    (_, _) =>
                    {
                        _workRan = true;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None
                )
            );
            _elapsed = Stopwatch.GetElapsedTime(started);
            await released;
        }

        [Test]
        public void It_fails_fence_when_lock_wait_outlasts_lease()
        {
            _elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(3.5), "the fence waited for the lock");
            _thrown.Should().BeOfType<JobLeaseLostException>();
        }

        [Test]
        public void It_validates_with_time_read_after_the_lock_so_the_work_never_runs() =>
            _workRan.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_fence_that_cannot_get_the_row_lock : JobLeaseTestBase
    {
        private Exception? _thrown;
        private bool _workRan;
        private TimeSpan _elapsed;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            IJobFence fence = FenceFactory().Create(claimed, new JobExecutionOwnership());
            await using RowLock rowLock = await RowLock.AcquireAsync(claimed.Id);
            Task released = rowLock.ReleaseAfterAsync(TimeSpan.FromSeconds(7));

            long started = Stopwatch.GetTimestamp();
            _thrown = await ThrownByAsync(() =>
                fence.ExecuteAsync(
                    (_, _) =>
                    {
                        _workRan = true;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None
                )
            );
            _elapsed = Stopwatch.GetElapsedTime(started);
            await released;
        }

        [Test]
        public void It_reports_the_fence_unavailable_after_the_fence_lock_wait()
        {
            _elapsed
                .Should()
                .BeGreaterThan(TimeSpan.FromSeconds(4.5))
                .And.BeLessThan(TimeSpan.FromSeconds(6.5));
            _thrown.Should().BeOfType<JobFenceUnavailableException>();
            _workRan.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_fences_that_must_not_start : JobLeaseTestBase
    {
        private Exception? _uncertain;
        private Exception? _nearlyExpired;
        private bool _workRan;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            await SeedJobAsync();
            ClaimedJob owned = await ClaimAsync();
            ClaimedJob nearlyExpired = await ClaimAsync(leaseSeconds: 1);

            JobExecutionOwnership uncertainOwnership = new();
            uncertainOwnership.TryMarkUncertain("RenewalFailed");
            _uncertain = await ThrownByAsync(() =>
                FenceFactory().Create(owned, uncertainOwnership).ExecuteAsync(Work, CancellationToken.None)
            );
            _nearlyExpired = await ThrownByAsync(() =>
                FenceFactory()
                    .Create(nearlyExpired, new JobExecutionOwnership())
                    .ExecuteAsync(Work, CancellationToken.None)
            );

            Task Work(DbTransaction transaction, CancellationToken token)
            {
                _workRan = true;
                return Task.CompletedTask;
            }
        }

        [Test]
        public void It_refuses_an_execution_whose_ownership_is_uncertain() =>
            _uncertain.Should().BeOfType<JobLeaseLostException>();

        [Test]
        public void It_refuses_a_lease_with_less_than_the_minimum_remaining() =>
            _nearlyExpired.Should().BeOfType<JobLeaseLostException>();

        [Test]
        public void It_never_runs_the_work() => _workRan.Should().BeFalse();
    }

    [TestFixture]
    public class Given_jobs_at_and_under_the_attempt_limit : JobLeaseTestBase
    {
        private readonly Dictionary<string, long> _ids = [];
        private readonly Dictionary<string, LeaseRow> _rows = [];
        private JobExhaustResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _ids["pending at limit"] = await SeedJobAsync(attemptCount: MaxAttempts);
            _ids["pending over limit"] = await SeedJobAsync(attemptCount: MaxAttempts + 2);
            _ids["expired at limit"] = await SeedJobAsync(
                JobStatuses.InProgress,
                attemptCount: MaxAttempts,
                leaseOwner: "crashed-owner",
                leaseOffset: -1,
                fencingToken: 5
            );
            _ids["live at limit"] = await SeedJobAsync(
                JobStatuses.InProgress,
                attemptCount: MaxAttempts,
                leaseOwner: "live-owner",
                leaseOffset: 300,
                fencingToken: 5
            );
            _ids["pending under limit"] = await SeedJobAsync(attemptCount: MaxAttempts - 1);
            _ids["expired under limit"] = await SeedJobAsync(
                JobStatuses.InProgress,
                attemptCount: MaxAttempts - 1,
                leaseOwner: "crashed-owner",
                leaseOffset: -1,
                fencingToken: 4
            );
            _ids["completed at limit"] = await SeedJobAsync(JobStatuses.Completed, attemptCount: MaxAttempts);

            _result = await Repository()
                .Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, CancellationToken.None);

            foreach ((string name, long id) in _ids)
            {
                _rows[name] = await RowAsync(id);
            }
        }

        [Test]
        public void It_reports_the_number_exhausted() =>
            _result.Should().BeOfType<JobExhaustResult.Success>().Which.ExhaustedCount.Should().Be(3);

        [Test]
        public void It_exhausts_pending_rows_at_or_over_the_limit()
        {
            foreach (string name in new[] { "pending at limit", "pending over limit" })
            {
                _rows[name]
                    .Should()
                    .BeEquivalentTo(
                        new
                        {
                            Status = JobStatuses.Error,
                            ErrorMessage = JobErrorCode.AttemptsExhausted.Message,
                            FencingToken = 1L,
                            LeaseOwner = (string?)null,
                        },
                        name
                    );
                _rows[name].FinishedAt.Should().NotBeNull(name);
            }
        }

        [Test]
        public void It_exhausts_an_expired_in_progress_row_at_the_limit_and_bumps_its_token() =>
            _rows["expired at limit"]
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = JobStatuses.Error,
                        FencingToken = 6L,
                        LeaseOwner = (string?)null,
                        LeaseExpiresAt = (DateTime?)null,
                    }
                );

        [Test]
        public void It_leaves_live_leases_rows_under_the_limit_and_finished_rows_untouched()
        {
            _rows["live at limit"]
                .Should()
                .BeEquivalentTo(new { Status = JobStatuses.InProgress, FencingToken = 5L });
            _rows["pending under limit"].Status.Should().Be(JobStatuses.Pending);
            _rows["expired under limit"].Status.Should().Be(JobStatuses.InProgress);
            _rows["completed at limit"].Status.Should().Be(JobStatuses.Completed);
        }
    }

    [TestFixture]
    public class Given_more_over_limit_jobs_than_one_batch : JobLeaseTestBase
    {
        private readonly List<int> _batches = [];
        private JobExhaustResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _batches.Clear();
            await SeedOverLimitJobsAsync(2_500);
            JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _timings)
            {
                AfterExhaustBatch = _batches.Add,
            };
            _result = await repository.Exhaust(
                MaxAttempts,
                JobErrorCode.AttemptsExhausted,
                CancellationToken.None
            );
        }

        [Test]
        public void It_exhausts_in_batches_of_at_most_1000()
        {
            _batches.Should().Equal(1_000, 1_000, 500);
            _result.Should().BeOfType<JobExhaustResult.Success>().Which.ExhaustedCount.Should().Be(2_500);
        }
    }

    [TestFixture]
    public class Given_an_exhaust_sweep_stopped_after_its_first_batch : JobLeaseTestBase
    {
        private readonly List<int> _batches = [];
        private JobExhaustResult _result = null!;
        private long _exhaustedRows;
        private long _pendingRows;

        [SetUp]
        public async Task Setup()
        {
            _batches.Clear();
            await SeedOverLimitJobsAsync(2_500);
            using CancellationTokenSource stopping = new();
            JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _timings)
            {
                AfterExhaustBatch = rows =>
                {
                    _batches.Add(rows);
                    stopping.Cancel();
                },
            };

            _result = await repository.Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, stopping.Token);
            _exhaustedRows = await CountAsync("Status = N'Error'");
            _pendingRows = await CountAsync("Status = N'Pending'");
        }

        [Test]
        public void It_exhausts_in_batches_of_at_most_1000_with_cancellation_between_batches()
        {
            _batches.Should().Equal(1_000);
            _result.Should().BeOfType<JobExhaustResult.Success>().Which.ExhaustedCount.Should().Be(1_000);
        }

        [Test]
        public void It_keeps_the_committed_batch_committed()
        {
            _exhaustedRows.Should().Be(1_000);
            _pendingRows.Should().Be(1_500);
        }
    }

    [TestFixture]
    public class Given_an_exhaust_sweep_cancelled_while_waiting_for_a_connection : JobLeaseTestBase
    {
        private readonly List<int> _batches = [];
        private bool _sweepWaitedForTheConnection;
        private bool _sweepEndedWhileTheConnectionWasHeld;
        private JobExhaustResult _result = null!;
        private long _pendingRows;

        [SetUp]
        public async Task Setup()
        {
            _batches.Clear();
            await SeedOverLimitJobsAsync(10);

            // A private pool of one connection, held here, so the sweep waits in connection acquisition.
            SqlConnectionStringBuilder singleConnectionPool = new(
                MssqlTestConfiguration.DatabaseConnectionString
            )
            {
                MinPoolSize = 0,
                MaxPoolSize = 1,
                ApplicationName = $"exhaust-cancel-{Guid.NewGuid():N}",
            };
            JobLeaseRepository repository = new(
                Options.Create(
                    new DatabaseOptions
                    {
                        DatabaseConnection = singleConnectionPool.ConnectionString,
                        EncryptionKey = MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey,
                    }
                ),
                _timings
            )
            {
                AfterExhaustBatch = _batches.Add,
            };

            using CancellationTokenSource stopping = new();
            Task<JobExhaustResult> sweep;
            await using (SqlConnection held = new(singleConnectionPool.ConnectionString))
            {
                await held.OpenAsync();
                sweep = repository.Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, stopping.Token);

                await Task.Delay(TimeSpan.FromMilliseconds(500));
                _sweepWaitedForTheConnection = !sweep.IsCompleted;
                await stopping.CancelAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                _sweepEndedWhileTheConnectionWasHeld = sweep.IsCompleted;
            }

            _result = await sweep;
            _pendingRows = await CountAsync("Status = N'Pending'");
        }

        [Test]
        public void It_was_waiting_for_the_connection_when_cancelled() =>
            _sweepWaitedForTheConnection.Should().BeTrue();

        [Test]
        public void It_stops_waiting_for_the_connection_as_soon_as_it_is_cancelled() =>
            _sweepEndedWhileTheConnectionWasHeld.Should().BeTrue();

        [Test]
        public void It_reports_success_with_nothing_exhausted() =>
            _result.Should().BeOfType<JobExhaustResult.Success>().Which.ExhaustedCount.Should().Be(0);

        [Test]
        public void It_runs_no_batch_after_the_connection_is_released()
        {
            _batches.Should().BeEmpty();
            _pendingRows.Should().Be(10);
        }
    }

    [TestFixture]
    public class Given_an_over_limit_row_locked_by_another_transaction : JobLeaseTestBase
    {
        private JobExhaustResult _result = null!;
        private LeaseRow _lockedRow = null!;

        [SetUp]
        public async Task Setup()
        {
            long locked = await SeedJobAsync(attemptCount: MaxAttempts);
            await SeedJobAsync(attemptCount: MaxAttempts);

            await using (RowLock rowLock = await RowLock.AcquireAsync(locked))
            {
                _result = await Repository()
                    .Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, CancellationToken.None);
            }

            _lockedRow = await RowAsync(locked);
        }

        [Test]
        public void It_skips_the_locked_row_without_waiting()
        {
            _result.Should().BeOfType<JobExhaustResult.Success>().Which.ExhaustedCount.Should().Be(1);
            _lockedRow.Status.Should().Be(JobStatuses.Pending);
        }
    }

    [TestFixture]
    public class Given_a_claim_at_a_10000_row_backlog : JobLeaseTestBase
    {
        private LockFootprint? _footprint;
        private JobClaimResult _claim = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedEligibleJobsAsync(10_000);
            JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _timings)
            {
                BeforeClaimCommit = async (connection, transaction) =>
                    _footprint = await HeldLocksAsync(connection, transaction),
            };
            _claim = await repository.ClaimNext(OwnerA, 300, MaxAttempts, CancellationToken.None);
        }

        [Test]
        public void It_claims_one_job() => _claim.Should().BeOfType<JobClaimResult.Claimed>();

        [Test]
        public void It_holds_row_level_locks_only()
        {
            TestContext.Out.WriteLine(
                $"claim at a 10000-row backlog: coarse locks {_footprint!.CoarseLocks}, key locks {_footprint.KeyLocks}"
            );
            _footprint!
                .CoarseLocks.Should()
                .Be(0, "no table, partition, or page lock other than intent locks");
            _footprint.KeyLocks.Should().BeInRange(1, 10);
        }
    }

    [TestFixture]
    public class Given_full_exhaust_batches_at_a_10000_row_backlog : JobLeaseTestBase
    {
        private readonly List<LockFootprint> _footprints = [];
        private readonly List<int> _batches = [];
        private JobExhaustResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _footprints.Clear();
            _batches.Clear();
            await SeedOverLimitJobsAsync(10_000);
            JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _timings)
            {
                BeforeExhaustBatchCommit = async (connection, transaction) =>
                    _footprints.Add(await HeldLocksAsync(connection, transaction)),
                AfterExhaustBatch = _batches.Add,
            };
            _result = await repository.Exhaust(
                MaxAttempts,
                JobErrorCode.AttemptsExhausted,
                CancellationToken.None
            );
        }

        [Test]
        public void It_exhausts_the_backlog_in_full_batches()
        {
            _batches.Should().Equal([.. Enumerable.Repeat(1_000, 10), 0]);
            _result.Should().BeOfType<JobExhaustResult.Success>().Which.ExhaustedCount.Should().Be(10_000);
        }

        [Test]
        public void It_holds_row_level_locks_only_in_each_full_batch()
        {
            IEnumerable<LockFootprint> full = _footprints.Take(10);
            foreach (LockFootprint footprint in full)
            {
                TestContext.Out.WriteLine(
                    $"full exhaust batch: coarse locks {footprint.CoarseLocks}, key locks {footprint.KeyLocks}"
                );
                footprint
                    .CoarseLocks.Should()
                    .Be(0, "no table, partition, or page lock other than intent locks");
                footprint
                    .KeyLocks.Should()
                    .BeGreaterThanOrEqualTo(1_000, "each changed row is locked by key");
            }
        }
    }

    /// <summary>A commit statement that the server holds for 4 s after the commit has started.</summary>
    private const string StalledCommit = "WAITFOR DELAY '00:00:04'; COMMIT TRANSACTION;";

    /// <summary>A session cleanup statement that the server holds for 5 s before it runs.</summary>
    private const string StalledEndSession =
        "WAITFOR DELAY '00:00:05'; IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; SET LOCK_TIMEOUT -1;";

    private static readonly JobLeaseTimings _twoSecondDeadlines = new(
        RenewalTimeout: TimeSpan.FromSeconds(2),
        FenceTimeout: TimeSpan.FromSeconds(2)
    );

    [TestFixture]
    public class Given_an_ownership_write_whose_commit_stalls_past_the_deadline : JobLeaseTestBase
    {
        private JobWriteResult _result = null!;
        private TimeSpan _elapsed;
        private int _guardedWrites;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            _guardedWrites = 0;
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _twoSecondDeadlines)
            {
                CommitStatement = StalledCommit,
                BeforeOwnershipCommit = _ =>
                {
                    Interlocked.Increment(ref _guardedWrites);
                    return Task.CompletedTask;
                },
            };

            long started = Stopwatch.GetTimestamp();
            _result = await repository.Complete(
                claimed.Id,
                OwnerA,
                claimed.FencingToken,
                CancellationToken.None
            );
            _elapsed = Stopwatch.GetElapsedTime(started);
            _row = await RowAsync(claimed.Id);
        }

        [Test]
        public void It_ends_the_commit_at_the_deadline() =>
            _elapsed
                .Should()
                .BeLessThan(TimeSpan.FromSeconds(3), "the commit started before the 2 s deadline");

        [Test]
        public void It_reports_the_result_as_unknown_without_retrying()
        {
            _result
                .Should()
                .BeOfType<JobWriteResult.ResultUnknown>()
                .Which.Diagnostic.Operation.Should()
                .Be("Complete");
            _guardedWrites.Should().Be(1);
        }

        [Test]
        public void It_leaves_the_interrupted_commit_rolled_back() =>
            _row.Status.Should().Be(JobStatuses.InProgress);
    }

    [TestFixture]
    public class Given_a_fence_whose_commit_stalls_past_the_deadline : JobLeaseTestBase
    {
        private JobExecutionOwnership _ownership = null!;
        private Exception? _thrown;
        private TimeSpan _elapsed;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            _ownership = new JobExecutionOwnership();
            ClaimedJob claimed = await ClaimAsync();
            IJobFence fence = new MssqlJobFenceFactory(
                MssqlTestConfiguration.DatabaseOptions,
                _twoSecondDeadlines
            )
            {
                CommitStatement = StalledCommit,
            }.Create(claimed, _ownership);

            long started = Stopwatch.GetTimestamp();
            _thrown = await ThrownByAsync(() => fence.ExecuteAsync(FencedWriteAsync, CancellationToken.None));
            _elapsed = Stopwatch.GetElapsedTime(started);
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_ends_the_commit_at_the_fence_deadline() =>
            _elapsed
                .Should()
                .BeLessThan(TimeSpan.FromSeconds(3), "the commit started before the 2 s fence deadline");

        [Test]
        public void It_marks_the_execution_uncertain_and_reports_the_lease_lost()
        {
            _thrown.Should().BeOfType<JobLeaseLostException>();
            _ownership.State.Should().Be(JobOwnershipState.Uncertain);
            _ownership.Reason.Should().Be("FenceCommitUnknown");
        }

        [Test]
        public void It_leaves_the_interrupted_commit_rolled_back() => _fencedWrites.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_claim_and_an_exhaust_batch_whose_commits_stall : JobLeaseTestBase
    {
        private JobClaimResult _claim = null!;
        private TimeSpan _claimElapsed;
        private JobExhaustResult _exhaust = null!;
        private TimeSpan _exhaustElapsed;
        private long _claimable;
        private long _overLimit;

        [SetUp]
        public async Task Setup()
        {
            long claimable = await SeedJobAsync();
            long overLimit = await SeedJobAsync(attemptCount: MaxAttempts);
            JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _timings)
            {
                CommitStatement = "WAITFOR DELAY '00:00:07'; COMMIT TRANSACTION;",
            };

            long started = Stopwatch.GetTimestamp();
            _claim = await repository.ClaimNext(OwnerA, 300, MaxAttempts, CancellationToken.None);
            _claimElapsed = Stopwatch.GetElapsedTime(started);

            started = Stopwatch.GetTimestamp();
            _exhaust = await repository.Exhaust(
                MaxAttempts,
                JobErrorCode.AttemptsExhausted,
                CancellationToken.None
            );
            _exhaustElapsed = Stopwatch.GetElapsedTime(started);

            _claimable = await CountAsync($"Id = {claimable} AND Status = N'Pending' AND AttemptCount = 0");
            _overLimit = await CountAsync($"Id = {overLimit} AND Status = N'Pending'");
        }

        [Test]
        public void It_ends_each_commit_within_the_5_second_claim_bound()
        {
            _claimElapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
            _exhaustElapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
        }

        [Test]
        public void It_reports_both_as_failures()
        {
            _claim.Should().BeOfType<JobClaimResult.FailureUnknown>();
            _exhaust.Should().BeOfType<JobExhaustResult.FailureUnknown>();
        }

        [Test]
        public void It_leaves_both_interrupted_commits_rolled_back()
        {
            _claimable.Should().Be(1);
            _overLimit.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_an_ownership_write_whose_session_cleanup_stalls : JobLeaseTestBase
    {
        private JobWriteResult _result = null!;
        private TimeSpan _elapsed;
        private LeaseRow _row = null!;
        private (int TranCount, int LockTimeout) _nextSession;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            IOptions<DatabaseOptions> pool = SingleConnectionPool("stalled-cleanup");
            JobLeaseRepository repository = new(pool, _twoSecondDeadlines)
            {
                EndSessionStatement = StalledEndSession,
            };

            long started = Stopwatch.GetTimestamp();
            _result = await repository.Complete(
                claimed.Id,
                OwnerA,
                claimed.FencingToken,
                CancellationToken.None
            );
            _elapsed = Stopwatch.GetElapsedTime(started);
            _row = await RowAsync(claimed.Id);

            // The pool's only connection is the one the write used, once its background release completes.
            await using SqlConnection next = new(pool.Value.DatabaseConnection);
            await next.OpenAsync();
            _nextSession = await next.QuerySingleAsync<(int, int)>("SELECT @@TRANCOUNT, @@LOCK_TIMEOUT;");
        }

        [Test]
        public void It_returns_within_the_write_deadline() =>
            _elapsed
                .Should()
                .BeLessThan(TimeSpan.FromSeconds(3), "cleanup gets only what is left of the 2 s deadline");

        [Test]
        public void It_keeps_the_outcome_already_decided()
        {
            _result.Should().BeOfType<JobWriteResult.Success>();
            _row.Status.Should().Be(JobStatuses.Completed);
        }

        [Test]
        public void It_leaves_no_transaction_or_lock_timeout_on_the_released_session() =>
            _nextSession.Should().Be((0, -1));
    }

    [TestFixture]
    public class Given_a_fence_whose_session_cleanup_stalls : JobLeaseTestBase
    {
        private Exception? _thrown;
        private TimeSpan _renewalAdmittedAfter;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            JobExecutionOwnership ownership = new();
            IJobFence fence = new MssqlJobFenceFactory(
                MssqlTestConfiguration.DatabaseOptions,
                _twoSecondDeadlines
            )
            {
                EndSessionStatement = StalledEndSession,
            }.Create(claimed, ownership);

            long started = Stopwatch.GetTimestamp();
            Task<Exception?> fenced = ThrownByAsync(() =>
                fence.ExecuteAsync(FencedWriteAsync, CancellationToken.None)
            );

            // A renewal that arrives while the fence holds the gate.
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            using (IDisposable renewal = await ownership.EnterForRenewalAsync(CancellationToken.None))
            {
                _renewalAdmittedAfter = Stopwatch.GetElapsedTime(started);
            }

            _thrown = await fenced;
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_commits_the_fenced_write()
        {
            _thrown.Should().BeNull();
            _fencedWrites.Should().Be(1);
        }

        [Test]
        public void It_releases_the_gate_to_a_waiting_renewal_within_the_fence_deadline() =>
            _renewalAdmittedAfter
                .Should()
                .BeLessThan(
                    TimeSpan.FromSeconds(3),
                    "cleanup gets only what is left of the 2 s fence deadline"
                );
    }

    [TestFixture]
    public class Given_an_ownership_write_whose_commit_stays_pending_after_cancellation : JobLeaseTestBase
    {
        private JobWriteResult _result = null!;
        private TimeSpan _elapsed;
        private Exception? _pendingOutcome;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            PendingOperation pending = new("Commit", TimeSpan.FromSeconds(3));
            JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _twoSecondDeadlines)
            {
                SessionHooks = pending.Hooks,
            };

            long started = Stopwatch.GetTimestamp();
            _result = await repository.Complete(
                claimed.Id,
                OwnerA,
                claimed.FencingToken,
                CancellationToken.None
            );
            _elapsed = Stopwatch.GetElapsedTime(started);

            _pendingOutcome = await pending.CleanupAsync();
            _row = await RowAsync(claimed.Id);
        }

        [Test]
        public void It_returns_at_the_deadline_while_the_commit_is_still_pending() =>
            _elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.7), "the commit stays pending for 3 s");

        [Test]
        public void It_reports_the_result_as_unknown() =>
            _result.Should().BeOfType<JobWriteResult.ResultUnknown>();

        [Test]
        public void It_releases_the_session_only_after_the_pending_commit_ends()
        {
            _pendingOutcome.Should().NotBeNull("the commit ran with its token already cancelled");
            _row.Status.Should()
                .Be(JobStatuses.InProgress, "closing the connection rolled the transaction back");
        }
    }

    [TestFixture]
    public class Given_a_fence_whose_commit_stays_pending_after_cancellation : JobLeaseTestBase
    {
        private JobExecutionOwnership _ownership = null!;
        private Exception? _thrown;
        private TimeSpan _elapsed;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            _ownership = new JobExecutionOwnership();
            PendingOperation pending = new("Commit", TimeSpan.FromSeconds(3));
            IJobFence fence = new MssqlJobFenceFactory(
                MssqlTestConfiguration.DatabaseOptions,
                _twoSecondDeadlines
            )
            {
                SessionHooks = pending.Hooks,
            }.Create(claimed, _ownership);

            long started = Stopwatch.GetTimestamp();
            _thrown = await ThrownByAsync(() => fence.ExecuteAsync(FencedWriteAsync, CancellationToken.None));
            _elapsed = Stopwatch.GetElapsedTime(started);

            await pending.CleanupAsync();
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_returns_at_the_fence_deadline_while_the_commit_is_still_pending() =>
            _elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.7), "the commit stays pending for 3 s");

        [Test]
        public void It_marks_the_execution_uncertain_and_reports_the_lease_lost()
        {
            _thrown.Should().BeOfType<JobLeaseLostException>();
            _ownership.State.Should().Be(JobOwnershipState.Uncertain);
            _ownership.Reason.Should().Be("FenceCommitUnknown");
        }

        [Test]
        public void It_leaves_the_fenced_write_rolled_back_once_cleanup_releases_the_session() =>
            _fencedWrites.Should().Be(0);
    }

    [TestFixture]
    public class Given_ownership_writes_whose_begin_is_handed_over : JobLeaseTestBase
    {
        private readonly InvalidOperationException _beginFailure = new("injected begin failure");
        private JobWriteResult _succeeds = null!;
        private TimeSpan _succeedsElapsed;
        private Exception? _succeedsOutcome;
        private (int TranCount, int LockTimeout) _succeedsNextSession;
        private JobWriteResult _fails = null!;
        private TimeSpan _failsElapsed;
        private Exception? _failsOutcome;
        private (int TranCount, int LockTimeout) _failsNextSession;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();

            (_succeeds, _succeedsElapsed, _succeedsOutcome, _succeedsNextSession) =
                await HandedOverBeginAsync(claimed, failure: null);
            (_fails, _failsElapsed, _failsOutcome, _failsNextSession) = await HandedOverBeginAsync(
                claimed,
                _beginFailure
            );
        }

        [Test]
        public void It_returns_at_the_deadline_while_the_begin_is_still_pending()
        {
            _succeedsElapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.7));
            _failsElapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.7));
        }

        [Test]
        public void It_reports_that_nothing_was_written()
        {
            _succeeds.Should().BeOfType<JobWriteResult.FailureUnknown>();
            _fails.Should().BeOfType<JobWriteResult.FailureUnknown>();
        }

        [Test]
        public void It_observes_a_begin_that_eventually_succeeds_and_releases_a_clean_session()
        {
            _succeedsOutcome.Should().BeNull();
            _succeedsNextSession.Should().Be((0, -1));
        }

        [Test]
        public void It_observes_the_exception_of_a_begin_that_eventually_fails_and_releases_the_session()
        {
            _failsOutcome.Should().BeSameAs(_beginFailure);
            _failsNextSession.Should().Be((0, -1));
        }

        private static async Task<(JobWriteResult, TimeSpan, Exception?, (int, int))> HandedOverBeginAsync(
            ClaimedJob claimed,
            Exception? failure
        )
        {
            IOptions<DatabaseOptions> pool = SingleConnectionPool("begin-hand-over");
            PendingOperation pending = new("Begin", TimeSpan.FromSeconds(3), failure);
            JobLeaseRepository repository = new(pool, _twoSecondDeadlines) { SessionHooks = pending.Hooks };

            long started = Stopwatch.GetTimestamp();
            JobWriteResult result = await repository.Renew(
                claimed.Id,
                OwnerA,
                claimed.FencingToken,
                300,
                CancellationToken.None
            );
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            Exception? outcome = await pending.CleanupAsync();

            // The pool's only connection is free again once cleanup has released it.
            await using SqlConnection next = new(pool.Value.DatabaseConnection);
            await next.OpenAsync();
            (int, int) nextSession = await next.QuerySingleAsync<(int, int)>(
                "SELECT @@TRANCOUNT, @@LOCK_TIMEOUT;"
            );
            return (result, elapsed, outcome, nextSession);
        }
    }

    [TestFixture]
    public class Given_an_ownership_write_whose_session_cleanup_stays_pending : JobLeaseTestBase
    {
        private JobWriteResult _result = null!;
        private TimeSpan _elapsed;
        private LeaseRow _row = null!;
        private (int TranCount, int LockTimeout) _nextSession;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            IOptions<DatabaseOptions> pool = SingleConnectionPool("pending-cleanup");
            PendingOperation pending = new("EndSession", TimeSpan.FromSeconds(4));
            JobLeaseRepository repository = new(pool, _twoSecondDeadlines) { SessionHooks = pending.Hooks };

            long started = Stopwatch.GetTimestamp();
            _result = await repository.Complete(
                claimed.Id,
                OwnerA,
                claimed.FencingToken,
                CancellationToken.None
            );
            _elapsed = Stopwatch.GetElapsedTime(started);

            await pending.CleanupAsync();
            _row = await RowAsync(claimed.Id);
            await using SqlConnection next = new(pool.Value.DatabaseConnection);
            await next.OpenAsync();
            _nextSession = await next.QuerySingleAsync<(int, int)>("SELECT @@TRANCOUNT, @@LOCK_TIMEOUT;");
        }

        [Test]
        public void It_returns_within_the_write_deadline() =>
            _elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.7), "cleanup stays pending for 4 s");

        [Test]
        public void It_keeps_the_outcome_already_decided()
        {
            _result.Should().BeOfType<JobWriteResult.Success>();
            _row.Status.Should().Be(JobStatuses.Completed);
        }

        [Test]
        public void It_releases_a_clean_session_once_the_pending_cleanup_ends() =>
            _nextSession.Should().Be((0, -1));
    }

    [TestFixture]
    public class Given_a_fence_whose_session_cleanup_stays_pending : JobLeaseTestBase
    {
        private Exception? _thrown;
        private TimeSpan _renewalAdmittedAfter;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedJobAsync();
            ClaimedJob claimed = await ClaimAsync();
            JobExecutionOwnership ownership = new();
            PendingOperation pending = new("EndSession", TimeSpan.FromSeconds(4));
            IJobFence fence = new MssqlJobFenceFactory(
                MssqlTestConfiguration.DatabaseOptions,
                _twoSecondDeadlines
            )
            {
                SessionHooks = pending.Hooks,
            }.Create(claimed, ownership);

            long started = Stopwatch.GetTimestamp();
            Task<Exception?> fenced = ThrownByAsync(() =>
                fence.ExecuteAsync(FencedWriteAsync, CancellationToken.None)
            );
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            using (IDisposable renewal = await ownership.EnterForRenewalAsync(CancellationToken.None))
            {
                _renewalAdmittedAfter = Stopwatch.GetElapsedTime(started);
            }

            _thrown = await fenced;
            await pending.CleanupAsync();
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_commits_the_fenced_write()
        {
            _thrown.Should().BeNull();
            _fencedWrites.Should().Be(1);
        }

        [Test]
        public void It_releases_the_gate_to_a_waiting_renewal_within_the_fence_deadline() =>
            _renewalAdmittedAfter
                .Should()
                .BeLessThan(TimeSpan.FromSeconds(2.7), "cleanup stays pending for 4 s");
    }

    /// <summary>Every ownership-dependent write, by the claim's owner and token, in declaration order.</summary>
    private static async Task<List<(string Name, JobWriteResult Result)>> AllWritesAsync(ClaimedJob claim)
    {
        JobLeaseRepository repository = new(MssqlTestConfiguration.DatabaseOptions, _timings);
        string owner = claim.LeaseOwner;
        long token = claim.FencingToken;
        return
        [
            ("renew", await repository.Renew(claim.Id, owner, token, 300, CancellationToken.None)),
            ("complete", await repository.Complete(claim.Id, owner, token, CancellationToken.None)),
            (
                "fail transient",
                await repository.FailTransient(claim.Id, owner, token, 30, CancellationToken.None)
            ),
            (
                "fail terminal",
                await repository.FailTerminal(
                    claim.Id,
                    owner,
                    token,
                    JobErrorCode.HandlerFailed,
                    CancellationToken.None
                )
            ),
            ("release", await repository.ReleaseToPending(claim.Id, owner, token, CancellationToken.None)),
        ];
    }
}
