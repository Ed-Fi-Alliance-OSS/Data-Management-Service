// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Shared setup for the snapshot write fixtures: data stores, a repository with fixed lease timings, a real job
/// fence for activation, and readers of the target row and the stored generations.
/// </summary>
public abstract class EducationOrganizationSnapshotWriteTestBase : DatabaseTest
{
    protected const string ContractVersion = "educationOrganizationProjection.v1";
    protected const int ForeignKeyOrCheckViolation = 547;
    protected const int DeadlockVictim = 1205;
    protected const int LockTimeout = 1222;

    /// <summary>A 5-minute lease and a 20-second renewal interval, so a renewal is bounded by 10 seconds.</summary>
    protected static readonly IOptions<JobOptions> LeaseOptions = Options.Create(
        new JobOptions { LeaseDuration = TimeSpan.FromMinutes(5), RenewalInterval = TimeSpan.FromSeconds(20) }
    );

    private static readonly JobLeaseTimings _fenceTimings = new(
        RenewalTimeout: TimeSpan.FromSeconds(10),
        FenceTimeout: TimeSpan.FromSeconds(15)
    );

    /// <summary>The fence of one claimed job, through which every activation in these fixtures commits.</summary>
    protected IJobFence Fence { get; private set; } = null!;

    [SetUp]
    public async Task FenceSetup() => Fence = await FenceAsync();

    protected static EducationOrganizationSnapshotRepository Repository(
        IOptions<DatabaseOptions>? databaseOptions = null,
        TenantContext? tenant = null,
        JobDatabaseSessionHooks? hooks = null,
        IOptions<JobOptions>? jobOptions = null
    ) =>
        new(
            databaseOptions ?? MssqlTestConfiguration.DatabaseOptions,
            jobOptions ?? LeaseOptions,
            new TenantContextProvider { Context = tenant ?? new TenantContext.NotMultitenant() }
        )
        {
            SessionHooks = hooks,
        };

    /// <summary>The test database under an application name, so its sessions can be found in the DMVs.</summary>
    protected static IOptions<DatabaseOptions> NamedOptions(string applicationName) =>
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = new SqlConnectionStringBuilder(
                    MssqlTestConfiguration.DatabaseConnectionString
                )
                {
                    ApplicationName = applicationName,
                }.ConnectionString,
                EncryptionKey = MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey,
            }
        );

    protected static string NewApplicationName() => "snapshot-test-" + Guid.NewGuid().ToString("N");

    protected static string NewOwner() => Guid.NewGuid().ToString("N");

    protected static EducationOrganizationProjectionItem Item(long id, long? parentId = null) =>
        new(id, $"Organization {id}", null, "edfi.School", parentId);

    protected async Task<int> InsertDataStoreAsync(string name, long? tenantId = null) =>
        await Connection!.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStore (Name, DataStoreType, Provider, TenantId)
            OUTPUT inserted.Id
            VALUES (@Name, N'Shared', N'sqlserver', @TenantId);
            """,
            new { Name = name, TenantId = tenantId }
        );

    protected async Task<long> InsertTenantAsync() =>
        await Connection!.ExecuteScalarAsync<long>(
            "INSERT INTO dmscs.Tenant (Name) OUTPUT inserted.Id VALUES (@Name);",
            new { Name = "snapshot-tenant-" + Guid.NewGuid().ToString("N") }
        );

    /// <summary>Claims a fresh job and returns its fence.</summary>
    protected async Task<IJobFence> FenceAsync(
        IOptions<DatabaseOptions>? databaseOptions = null,
        JobExecutionOwnership? ownership = null
    )
    {
        await Connection!.ExecuteAsync(
            """
            INSERT INTO dmscs.Job (JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt, AttemptCount,
                FencingToken)
            VALUES (@JobId, N'EducationOrganization.Refresh', 1, N'{"scope":1,"dataStoreId":0}', N'Pending',
                DATEADD(second, -1, SYSUTCDATETIME()), 0, 0);
            """,
            new { JobId = Guid.NewGuid().ToString("N") }
        );
        ClaimedJob claim = (
            await new JobLeaseRepository(MssqlTestConfiguration.DatabaseOptions, _fenceTimings).ClaimNext(
                NewOwner(),
                300,
                5,
                CancellationToken.None
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        return new MssqlJobFenceFactory(
            databaseOptions ?? MssqlTestConfiguration.DatabaseOptions,
            _fenceTimings
        ).Create(claim, ownership ?? new JobExecutionOwnership());
    }

    protected static async Task<SnapshotTargetResult> ActivateAsync(
        EducationOrganizationSnapshotRepository repository,
        IJobFence fence,
        EducationOrganizationSnapshotLease lease
    )
    {
        SnapshotTargetResult? result = null;
        await fence.ExecuteAsync(
            async (transaction, token) =>
                result = await repository.ActivateAsync(transaction, lease, ContractVersion, token),
            CancellationToken.None
        );
        return result!;
    }

    protected static async Task<EducationOrganizationSnapshotLease> AcquiredAsync(
        EducationOrganizationSnapshotRepository repository,
        int dataStoreId,
        string? owner = null
    ) =>
        (await repository.AcquireAsync(dataStoreId, owner ?? NewOwner(), CancellationToken.None))
            .Should()
            .BeOfType<SnapshotAcquireResult.Acquired>()
            .Subject.Lease;

    /// <summary>A complete refresh: acquire, reclaim, admission renewal, stage, fenced activation, and prune.</summary>
    protected async Task<EducationOrganizationSnapshotLease> PublishAsync(
        int dataStoreId,
        IReadOnlyList<EducationOrganizationProjectionItem> items,
        bool prune = true,
        EducationOrganizationSnapshotRepository? repository = null
    )
    {
        repository ??= Repository();
        EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, dataStoreId);
        SnapshotTargetResult reclaimed = await repository.ReclaimAsync(lease, CancellationToken.None);
        reclaimed.Should().BeOfType<SnapshotTargetResult.Success>("reclaim returned {0}", reclaimed);
        SnapshotTargetResult renewed = await repository.RenewAsync(lease, CancellationToken.None);
        renewed.Should().Be(new SnapshotTargetResult.Success(1), "renewal returned {0}", renewed);
        SnapshotTargetResult staged = await repository.StageAsync(
            lease,
            items,
            ContractVersion,
            CancellationToken.None
        );
        staged.Should().Be(new SnapshotTargetResult.Success(items.Count), "staging returned {0}", staged);
        (await ActivateAsync(repository, Fence, lease)).Should().Be(new SnapshotTargetResult.Success(1));
        if (prune)
        {
            (await repository.PruneAsync(dataStoreId, CancellationToken.None))
                .Should()
                .BeOfType<SnapshotTargetResult.Success>();
        }

        return lease;
    }

    /// <summary>Sets the lease to expire <paramref name="seconds"/> from database time (negative: in the past).</summary>
    protected Task SetLeaseExpiryAsync(int dataStoreId, double seconds) =>
        Connection!.ExecuteAsync(
            """
            UPDATE dmscs.EducationOrganizationSnapshotTarget
            SET LeaseExpiresAt = DATEADD(millisecond, @Milliseconds, SYSUTCDATETIME())
            WHERE DataStoreId = @Id;
            """,
            new { Id = dataStoreId, Milliseconds = (int)(seconds * 1000) }
        );

    protected Task<TargetState> TargetAsync(int dataStoreId) =>
        Connection!.QuerySingleAsync<TargetState>(
            """
            SELECT LastGeneration, ActiveGeneration, LeaseOwner, LeaseGeneration, LeaseExpiresAt, TenantId,
                SourceContractVersion, RefreshedAt
            FROM dmscs.EducationOrganizationSnapshotTarget
            WHERE DataStoreId = @Id;
            """,
            new { Id = dataStoreId }
        );

    protected async Task<int> TargetCountAsync(int dataStoreId) =>
        await Connection!.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.EducationOrganizationSnapshotTarget WHERE DataStoreId = @Id;",
            new { Id = dataStoreId }
        );

    protected async Task<int> OrganizationCountAsync(int dataStoreId) =>
        await Connection!.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.EducationOrganizationSnapshot WHERE DataStoreId = @Id;",
            new { Id = dataStoreId }
        );

    /// <summary>The distinct generations present in the organization rows, ascending.</summary>
    protected async Task<List<long>> GenerationsAsync(int dataStoreId) =>
        (
            await Connection!.QueryAsync<long>(
                """
                SELECT DISTINCT Generation FROM dmscs.EducationOrganizationSnapshot
                WHERE DataStoreId = @Id ORDER BY Generation;
                """,
                new { Id = dataStoreId }
            )
        ).ToList();

    /// <summary>The organization ids of the active generation, ascending.</summary>
    protected async Task<List<long>> ActiveIdsAsync(int dataStoreId) =>
        (
            await Connection!.QueryAsync<long>(
                """
                SELECT s.EducationOrganizationId
                FROM dmscs.EducationOrganizationSnapshot AS s
                INNER JOIN dmscs.EducationOrganizationSnapshotTarget AS t
                    ON t.DataStoreId = s.DataStoreId AND t.ActiveGeneration = s.Generation
                WHERE s.DataStoreId = @Id
                ORDER BY s.EducationOrganizationId;
                """,
                new { Id = dataStoreId }
            )
        ).ToList();

    /// <summary>The target row and every organization row of a data store, for before-and-after comparison.</summary>
    protected async Task<(TargetState Target, List<string> Rows)> StateAsync(int dataStoreId) =>
        (
            await TargetAsync(dataStoreId),
            (
                await Connection!.QueryAsync<string>(
                    """
                    SELECT CONCAT_WS(N'|', Generation, EducationOrganizationId, TenantId, NameOfInstitution,
                        CONVERT(nvarchar(33), RefreshedAt, 126), SourceContractVersion)
                    FROM dmscs.EducationOrganizationSnapshot
                    WHERE DataStoreId = @Id
                    ORDER BY Generation, EducationOrganizationId;
                    """,
                    new { Id = dataStoreId }
                )
            ).ToList()
        );

    /// <summary>
    /// Holds an exclusive lock on the target row in <paramref name="transaction"/> without changing the row. It
    /// blocks the update lock of the lease operations and the shared lock of staging.
    /// </summary>
    protected static Task HoldTargetAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int dataStoreId
    ) =>
        connection.ExecuteAsync(
            """
            SELECT 1 FROM dmscs.EducationOrganizationSnapshotTarget WITH (XLOCK, HOLDLOCK, ROWLOCK)
            WHERE DataStoreId = @Id;
            """,
            new { Id = dataStoreId },
            transaction
        );

    /// <summary>
    /// Waits until a session named <paramref name="applicationName"/> waits for a lock, failing when
    /// <paramref name="operation"/> ends first or 10 seconds pass.
    /// </summary>
    protected static async Task WaitUntilBlockedAsync(string applicationName, Task operation)
    {
        long started = Stopwatch.GetTimestamp();
        await using SqlConnection observer = await OpenConnectionAsync();
        while (
            await observer.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM sys.dm_exec_requests AS r
                INNER JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
                WHERE s.program_name = @ApplicationName AND r.blocking_session_id <> 0
                  AND r.wait_type LIKE N'LCK%';
                """,
                new { ApplicationName = applicationName }
            ) == 0
        )
        {
            operation.IsCompleted.Should().BeFalse("the operation must wait for the held lock");
            Stopwatch
                .GetElapsedTime(started)
                .Should()
                .BeLessThan(TimeSpan.FromSeconds(10), "the operation must reach the held lock");
            await Task.Yield();
        }
    }

    /// <summary>
    /// On the holder's transaction, which can read the row it locks, waits until the database clock has passed the
    /// target's lease expiry. The condition is evaluated by the database; nothing sleeps for a fixed time.
    /// </summary>
    protected static async Task WaitForLeaseExpiryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int dataStoreId
    )
    {
        long started = Stopwatch.GetTimestamp();
        while (!await LeaseExpiredAsync(connection, transaction, dataStoreId))
        {
            Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(10));
            await Task.Delay(20);
        }
    }

    protected static async Task<bool> LeaseExpiredAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int dataStoreId
    ) =>
        await connection.ExecuteScalarAsync<int>(
            """
            SELECT CASE WHEN SYSUTCDATETIME() > LeaseExpiresAt THEN 1 ELSE 0 END
            FROM dmscs.EducationOrganizationSnapshotTarget WHERE DataStoreId = @Id;
            """,
            new { Id = dataStoreId },
            transaction
        ) == 1;

    /// <summary>
    /// Session hooks that hold every commit of a session until released. <c>Reached</c> waits at most
    /// <c>reachedTimeout</c> (default 10 seconds) for the first commit.
    /// </summary>
    protected sealed class CommitPause
    {
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private readonly TimeSpan _reachedTimeout;

        public CommitPause(TimeSpan? reachedTimeout = null)
        {
            _reachedTimeout = reachedTimeout ?? TimeSpan.FromSeconds(10);
            Hooks = new JobDatabaseSessionHooks
            {
                BeforeOperation = async (operation, _) =>
                {
                    if (operation == "Commit")
                    {
                        _reached.TrySetResult();
                        await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
                    }
                },
            };
        }

        public JobDatabaseSessionHooks Hooks { get; }

        public Task Reached => _reached.Task.WaitAsync(_reachedTimeout);

        public void Release() => _release.TrySetResult();
    }

    protected sealed record TargetState(
        long LastGeneration,
        long? ActiveGeneration,
        string? LeaseOwner,
        long? LeaseGeneration,
        DateTime? LeaseExpiresAt,
        long? TenantId,
        string? SourceContractVersion,
        DateTime? RefreshedAt
    );
}

public class EducationOrganizationSnapshotWriteTests
{
    [TestFixture]
    public class Given_a_target_refreshed_from_one_set_to_another : EducationOrganizationSnapshotWriteTestBase
    {
        private const long A = 255901001;
        private const long B = 255901002;
        private const long C = 255901003;
        private const long D = 255901004;

        [Test]
        public async Task It_publishes_exactly_the_second_set()
        {
            int id = await InsertDataStoreAsync("replace");
            await PublishAsync(id, [Item(A), Item(B), Item(C)]);
            (await ActiveIdsAsync(id)).Should().Equal(A, B, C);

            await PublishAsync(id, [Item(B, parentId: 255901), Item(D)]);

            (await ActiveIdsAsync(id)).Should().Equal(B, D);
            (await GenerationsAsync(id)).Should().Equal(2);
            TargetState target = await TargetAsync(id);
            target.LastGeneration.Should().Be(2);
            target.ActiveGeneration.Should().Be(2);
            target.SourceContractVersion.Should().Be(ContractVersion);
            target.RefreshedAt.Should().NotBeNull();
            (target.LeaseOwner, target.LeaseGeneration, target.LeaseExpiresAt)
                .Should()
                .Be((null, null, null), "activation clears the lease");
        }

        [Test]
        public async Task It_publishes_an_empty_set_as_an_active_generation_without_rows()
        {
            int id = await InsertDataStoreAsync("empty");
            await PublishAsync(id, [Item(A), Item(B)]);

            await PublishAsync(id, []);

            (await TargetAsync(id)).ActiveGeneration.Should().Be(2);
            (await ActiveIdsAsync(id)).Should().BeEmpty();
            (await OrganizationCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_stores_every_field_and_int64_identifier_exactly()
        {
            int id = await InsertDataStoreAsync("fields");
            const long BeyondDoublePrecision = 9_007_199_254_740_993;
            long aboveInt32 = (long)int.MaxValue + 2;
            await PublishAsync(
                id,
                [
                    new EducationOrganizationProjectionItem(
                        aboveInt32,
                        "Grand Bend ISD",
                        "GBISD",
                        "edfi.LocalEducationAgency",
                        null
                    ),
                    new EducationOrganizationProjectionItem(
                        BeyondDoublePrecision,
                        "Grand Bend High School",
                        null,
                        "edfi.School",
                        long.MaxValue - 1
                    ),
                ]
            );

            var rows = await Connection!.QueryAsync<(long, string, string?, string, long?, string, long?)>(
                """
                SELECT EducationOrganizationId, NameOfInstitution, ShortNameOfInstitution, Discriminator, ParentId,
                    SourceContractVersion, TenantId
                FROM dmscs.EducationOrganizationSnapshot WHERE DataStoreId = @Id ORDER BY EducationOrganizationId;
                """,
                new { Id = id }
            );
            rows.Should()
                .Equal(
                    (
                        aboveInt32,
                        "Grand Bend ISD",
                        "GBISD",
                        "edfi.LocalEducationAgency",
                        null,
                        ContractVersion,
                        null
                    ),
                    (
                        BeyondDoublePrecision,
                        "Grand Bend High School",
                        null,
                        "edfi.School",
                        long.MaxValue - 1,
                        ContractVersion,
                        null
                    )
                );
        }

        [Test]
        public async Task It_stages_fifty_thousand_organizations()
        {
            int id = await InsertDataStoreAsync("large");

            await PublishAsync(id, LargeSet(50_000));

            (await OrganizationCountAsync(id)).Should().Be(50_000);
            (
                await Connection!.QuerySingleAsync<(string?, long?)>(
                    """
                    SELECT ShortNameOfInstitution, ParentId FROM dmscs.EducationOrganizationSnapshot
                    WHERE DataStoreId = @Id AND EducationOrganizationId = 49999;
                    """,
                    new { Id = id }
                )
            ).Should().Be(("short", 1_049_999L));
            (
                await Connection!.QuerySingleAsync<(string?, long?)>(
                    """
                    SELECT ShortNameOfInstitution, ParentId FROM dmscs.EducationOrganizationSnapshot
                    WHERE DataStoreId = @Id AND EducationOrganizationId = 50000;
                    """,
                    new { Id = id }
                )
            ).Should().Be(((string?)null, 1_050_000L));
        }

        internal static EducationOrganizationProjectionItem[] LargeSet(int count) =>
            [
                .. Enumerable
                    .Range(1, count)
                    .Select(index => new EducationOrganizationProjectionItem(
                        index,
                        new string('n', 75),
                        index % 2 == 0 ? null : "short",
                        "edfi.School",
                        index % 3 == 0 ? null : index + 1_000_000L
                    )),
            ];
    }

    /// <summary>
    /// Staging checks the foreign key and check constraints, so they stay trusted. Large stages, reclaims and prunes
    /// request row locks in batched statements; paused before their commit, they hold only intent locks at page and
    /// table level in these fixtures, and another data store completes a refresh meanwhile. Escalation remains
    /// possible in general (for example under lock-memory pressure), so this is evidence for these operations, not
    /// a guarantee.
    /// </summary>
    [TestFixture]
    public class Given_large_snapshot_writes : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_keeps_the_snapshot_constraints_trusted_after_staging()
        {
            int id = await InsertDataStoreAsync("trusted");
            await PublishAsync(id, [Item(1), Item(2)]);

            (await UntrustedConstraintsAsync()).Should().BeEmpty();
        }

        [Test]
        public async Task It_rejects_an_invalid_parent_or_generation_and_keeps_the_constraints_trusted(
            [Values("missing target", "generation zero")] string invalid
        )
        {
            int id = await InsertDataStoreAsync("invalid-" + invalid);
            await AcquiredAsync(Repository(), id);
            int dataStoreId = invalid == "missing target" ? id + 1_000 : id;
            long generation = invalid == "generation zero" ? 0 : 1;

            await using SqlConnection connection = await OpenConnectionAsync();
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            Func<Task> insert = () =>
                EducationOrganizationSnapshotRepository.InsertOrganizationsAsync(
                    connection,
                    transaction,
                    new EducationOrganizationSnapshotRepository.StagedGeneration(
                        dataStoreId,
                        generation,
                        null,
                        DateTime.UtcNow,
                        ContractVersion
                    ),
                    [Item(1)],
                    JobDeadline.Start(TimeSpan.FromSeconds(30)),
                    CancellationToken.None
                );

            (await insert.Should().ThrowAsync<SqlException>())
                .Which.Number.Should()
                .Be(ForeignKeyOrCheckViolation);
            if (transaction.Connection is not null)
            {
                // The failed insert can already have ended the transaction.
                await transaction.RollbackAsync();
            }
            (await UntrustedConstraintsAsync()).Should().BeEmpty();
            (await OrganizationCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_stages_a_large_set_without_blocking_another_data_stores_snapshot()
        {
            int large = await InsertDataStoreAsync("large-held");
            int other = await InsertDataStoreAsync("other-store");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), large);
            string applicationName = NewApplicationName();
            CommitPause pause = new(TimeSpan.FromMinutes(2));
            Task<SnapshotTargetResult> staging = Repository(NamedOptions(applicationName), hooks: pause.Hooks)
                .StageAsync(
                    lease,
                    Given_a_target_refreshed_from_one_set_to_another.LargeSet(50_000),
                    ContractVersion,
                    CancellationToken.None
                );

            try
            {
                await pause.Reached;
                (await CoarseSnapshotLocksAsync(applicationName))
                    .Should()
                    .Equal(["OBJECT:IX", "PAGE:IX"], "the stage holds only intent locks above row level");

                // The other data store's whole refresh runs while the large stage holds its locks.
                await PublishAsync(other, [Item(1), Item(2)]);
            }
            finally
            {
                pause.Release();
            }

            (await staging).Should().Be(new SnapshotTargetResult.Success(50_000));
            (await ActiveIdsAsync(other)).Should().Equal(1, 2);
        }

        [Test]
        public async Task It_prunes_a_large_generation_without_blocking_another_data_stores_snapshot()
        {
            int large = await InsertDataStoreAsync("large-prune");
            int other = await InsertDataStoreAsync("other-prune");
            EducationOrganizationProjectionItem[] items =
                Given_a_target_refreshed_from_one_set_to_another.LargeSet(50_000);
            await PublishAsync(large, items, prune: false);
            await PublishAsync(large, items, prune: false);
            string applicationName = NewApplicationName();
            CommitPause pause = new(TimeSpan.FromMinutes(2));
            Task<SnapshotTargetResult> pruning = Repository(NamedOptions(applicationName), hooks: pause.Hooks)
                .PruneAsync(large, CancellationToken.None);

            try
            {
                await pause.Reached;
                (await CoarseSnapshotLocksAsync(applicationName))
                    .Should()
                    .Equal(["OBJECT:IX", "PAGE:IX"], "the prune holds only intent locks above row level");

                await PublishAsync(other, [Item(1)]);
            }
            finally
            {
                pause.Release();
            }

            (await pruning).Should().Be(new SnapshotTargetResult.Success(50_000));
            (await GenerationsAsync(large)).Should().Equal(2);
            (await ActiveIdsAsync(other)).Should().Equal(1);
        }

        [Test]
        public async Task It_reclaims_a_large_generation_without_blocking_another_data_stores_snapshot()
        {
            int large = await InsertDataStoreAsync("large-reclaim");
            int other = await InsertDataStoreAsync("other-reclaim");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease abandoned = await AcquiredAsync(repository, large);
            (
                await repository.StageAsync(
                    abandoned,
                    Given_a_target_refreshed_from_one_set_to_another.LargeSet(50_000),
                    ContractVersion,
                    CancellationToken.None
                )
            )
                .Should()
                .Be(new SnapshotTargetResult.Success(50_000));
            await SetLeaseExpiryAsync(large, -1);
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, large);
            string applicationName = NewApplicationName();
            CommitPause pause = new(TimeSpan.FromMinutes(2));
            Task<SnapshotTargetResult> reclaiming = Repository(
                    NamedOptions(applicationName),
                    hooks: pause.Hooks
                )
                .ReclaimAsync(lease, CancellationToken.None);

            try
            {
                await pause.Reached;
                (await CoarseSnapshotLocksAsync(applicationName))
                    .Should()
                    .Equal(["OBJECT:IX", "PAGE:IX"], "the reclaim holds only intent locks above row level");

                await PublishAsync(other, [Item(1)]);
            }
            finally
            {
                pause.Release();
            }

            (await reclaiming).Should().Be(new SnapshotTargetResult.Success(50_000));
            (await GenerationsAsync(large)).Should().BeEmpty();
            (await ActiveIdsAsync(other)).Should().Equal(1);
        }

        /// <summary>
        /// The distinct table- and page-level lock modes (<c>OBJECT:IX</c>, <c>PAGE:X</c>, ...) that the session named
        /// <paramref name="applicationName"/> holds on the snapshot table, in order.
        /// </summary>
        private static async Task<List<string>> CoarseSnapshotLocksAsync(string applicationName)
        {
            await using SqlConnection observer = await OpenConnectionAsync();
            return (
                await observer.QueryAsync<string>(
                    """
                    SELECT DISTINCT CONCAT(l.resource_type, N':', l.request_mode) AS LockMode
                    FROM sys.dm_tran_locks AS l
                    INNER JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id
                    WHERE s.program_name = @ApplicationName
                      AND l.resource_database_id = DB_ID()
                      AND (
                          (l.resource_type = N'OBJECT'
                              AND l.resource_associated_entity_id = OBJECT_ID(N'dmscs.EducationOrganizationSnapshot'))
                          OR (l.resource_type = N'PAGE'
                              AND l.resource_associated_entity_id IN (
                                  SELECT hobt_id FROM sys.partitions
                                  WHERE object_id = OBJECT_ID(N'dmscs.EducationOrganizationSnapshot')))
                      )
                    ORDER BY LockMode;
                    """,
                    new { ApplicationName = applicationName }
                )
            ).ToList();
        }

        private async Task<List<string>> UntrustedConstraintsAsync() =>
            (
                await Connection!.QueryAsync<string>(
                    """
                    SELECT name FROM sys.foreign_keys
                    WHERE parent_object_id IN (OBJECT_ID(N'dmscs.EducationOrganizationSnapshot'),
                        OBJECT_ID(N'dmscs.EducationOrganizationSnapshotTarget'))
                      AND (is_not_trusted = 1 OR is_disabled = 1)
                    UNION ALL
                    SELECT name FROM sys.check_constraints
                    WHERE parent_object_id IN (OBJECT_ID(N'dmscs.EducationOrganizationSnapshot'),
                        OBJECT_ID(N'dmscs.EducationOrganizationSnapshotTarget'))
                      AND (is_not_trusted = 1 OR is_disabled = 1);
                    """
                )
            ).ToList();
    }

    [TestFixture]
    public class Given_an_acquisition_that_cannot_take_the_lease : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_reports_busy_without_writing_while_another_lease_is_live()
        {
            int id = await InsertDataStoreAsync("busy");
            EducationOrganizationSnapshotLease held = await AcquiredAsync(Repository(), id);
            TargetState before = await TargetAsync(id);

            (await Repository().AcquireAsync(id, NewOwner(), CancellationToken.None))
                .Should()
                .Be(new SnapshotAcquireResult.Busy());

            (await TargetAsync(id)).Should().Be(before);
            before.LeaseOwner.Should().Be(held.Owner);
            (await TargetCountAsync(id)).Should().Be(1);
        }

        [Test]
        public async Task It_reports_an_absent_data_store_as_not_found_without_creating_a_target()
        {
            (await Repository().AcquireAsync(987_654, NewOwner(), CancellationToken.None))
                .Should()
                .Be(new SnapshotAcquireResult.DataStoreNotFound());
            (await TargetCountAsync(987_654)).Should().Be(0);
        }

        [Test]
        public async Task It_reports_another_tenants_data_store_as_not_found_without_creating_a_target()
        {
            long owning = await InsertTenantAsync();
            long other = await InsertTenantAsync();
            int id = await InsertDataStoreAsync("tenant-store", owning);

            (
                await Repository(tenant: new TenantContext.Multitenant(other, "other"))
                    .AcquireAsync(id, NewOwner(), CancellationToken.None)
            )
                .Should()
                .Be(new SnapshotAcquireResult.DataStoreNotFound());
            (
                await Repository(tenant: new TenantContext.NotMultitenant())
                    .AcquireAsync(id, NewOwner(), CancellationToken.None)
            )
                .Should()
                .Be(new SnapshotAcquireResult.DataStoreNotFound());
            (await TargetCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_records_the_owning_tenant_on_the_target_and_staged_rows()
        {
            long owning = await InsertTenantAsync();
            int id = await InsertDataStoreAsync("tenant-owned", owning);
            EducationOrganizationSnapshotRepository repository = Repository(
                tenant: new TenantContext.Multitenant(owning, "owning")
            );

            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            (await repository.StageAsync(lease, [Item(1)], ContractVersion, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(1));

            (await TargetAsync(id)).TenantId.Should().Be(owning);
            (
                await Connection!.ExecuteScalarAsync<long?>(
                    "SELECT TenantId FROM dmscs.EducationOrganizationSnapshot WHERE DataStoreId = @Id;",
                    new { Id = id }
                )
            )
                .Should()
                .Be(owning);
        }

        [Test]
        public async Task It_propagates_cancellation_of_the_callers_token()
        {
            int id = await InsertDataStoreAsync("cancelled");
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            Func<Task> acquire = () => Repository().AcquireAsync(id, NewOwner(), cancellation.Token);

            await acquire.Should().ThrowAsync<OperationCanceledException>();
            (await TargetCountAsync(id)).Should().Be(0);
        }
    }

    /// <summary>
    /// Two first acquisitions of a data store with no target yet: the key-range lock on the missing key makes the
    /// second wait for the first, then find its live lease. Neither gets a duplicate-key error.
    /// </summary>
    [TestFixture]
    public class Given_two_first_acquisitions_of_the_same_data_store
        : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_lets_one_acquire_and_reports_the_other_busy()
        {
            int id = await InsertDataStoreAsync("first-acquisitions");
            CommitPause pause = new();
            string firstOwner = NewOwner();
            Task<SnapshotAcquireResult> first = Repository(hooks: pause.Hooks)
                .AcquireAsync(id, firstOwner, CancellationToken.None);
            await pause.Reached;

            string applicationName = NewApplicationName();
            Task<SnapshotAcquireResult> second = Repository(NamedOptions(applicationName))
                .AcquireAsync(id, NewOwner(), CancellationToken.None);
            await WaitUntilBlockedAsync(applicationName, second);
            pause.Release();

            (await first)
                .Should()
                .Be(
                    new SnapshotAcquireResult.Acquired(
                        new EducationOrganizationSnapshotLease(id, firstOwner, 1)
                    )
                );
            (await second).Should().Be(new SnapshotAcquireResult.Busy());
            (await TargetCountAsync(id)).Should().Be(1);
            TargetState target = await TargetAsync(id);
            (target.LastGeneration, target.LeaseOwner, target.LeaseGeneration)
                .Should()
                .Be((1L, firstOwner, 1L));
        }
    }

    [TestFixture]
    public class Given_a_lease_that_another_execution_took_over : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_refuses_every_operation_of_the_previous_owner()
        {
            int id = await InsertDataStoreAsync("takeover");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease stale = await AcquiredAsync(repository, id);
            await SetLeaseExpiryAsync(id, -1);

            EducationOrganizationSnapshotLease current = await AcquiredAsync(repository, id);
            current.Generation.Should().Be(2, "the expired lease is taken over with the next generation");
            TargetState afterTakeover = await TargetAsync(id);

            SnapshotTargetResult taken = new SnapshotTargetResult.LeaseLost(SnapshotLeaseLossReason.Taken);
            (await repository.RenewAsync(stale, CancellationToken.None)).Should().Be(taken);
            (await repository.ReclaimAsync(stale, CancellationToken.None)).Should().Be(taken);
            (await repository.StageAsync(stale, [Item(1)], ContractVersion, CancellationToken.None))
                .Should()
                .Be(taken);
            (await ActivateAsync(repository, Fence, stale)).Should().Be(taken);
            (await repository.ReleaseAsync(stale, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(0), "the stale owner has no lease to clear");

            (await TargetAsync(id)).Should().Be(afterTakeover);
            (await OrganizationCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_lets_the_new_owner_publish()
        {
            int id = await InsertDataStoreAsync("takeover-publish");
            await AcquiredAsync(Repository(), id);
            await SetLeaseExpiryAsync(id, -1);

            await PublishAsync(id, [Item(7)]);

            (await TargetAsync(id)).ActiveGeneration.Should().Be(2);
            (await ActiveIdsAsync(id)).Should().Equal(7);
        }
    }

    [TestFixture]
    public class Given_an_expired_lease_that_no_one_took_over : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_refuses_to_revive_or_use_the_lease()
        {
            int id = await InsertDataStoreAsync("expired");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            await SetLeaseExpiryAsync(id, -1);
            TargetState before = await TargetAsync(id);

            SnapshotTargetResult expired = new SnapshotTargetResult.LeaseLost(
                SnapshotLeaseLossReason.Expired
            );
            (await repository.RenewAsync(lease, CancellationToken.None)).Should().Be(expired);
            (await repository.ReclaimAsync(lease, CancellationToken.None)).Should().Be(expired);
            (await repository.StageAsync(lease, [Item(1)], ContractVersion, CancellationToken.None))
                .Should()
                .Be(expired);
            (await ActivateAsync(repository, Fence, lease)).Should().Be(expired);

            (await TargetAsync(id)).Should().Be(before, "nothing revives or publishes an expired lease");
            (await OrganizationCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_lets_the_holder_release_it()
        {
            int id = await InsertDataStoreAsync("release");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            await SetLeaseExpiryAsync(id, -1);

            (await repository.ReleaseAsync(lease, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(1));

            TargetState target = await TargetAsync(id);
            (target.LeaseOwner, target.LeaseGeneration, target.LeaseExpiresAt)
                .Should()
                .Be((null, null, null));
            target.LastGeneration.Should().Be(1);
        }
    }

    /// <summary>
    /// Another transaction holds an exclusive lock on the target row without changing it while the lease expires,
    /// then releases it within the operation's lock wait. The operation must judge the lease after it gets the
    /// lock: SQL Server reads <c>SYSUTCDATETIME()</c> when a statement starts, so a clock read in the locking
    /// statement itself would predate the wait and is caught here.
    /// </summary>
    [TestFixture]
    public class Given_a_lease_that_expires_while_an_operation_waits_for_the_target_lock
        : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_judges_the_lease_after_the_wait(
            [Values("renew", "acquire", "stage", "activate")] string operation
        )
        {
            int id = await InsertDataStoreAsync("lock-wait-" + operation);
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            string applicationName = NewApplicationName();
            EducationOrganizationSnapshotRepository waiter = Repository(NamedOptions(applicationName));
            IJobFence fence =
                operation == "activate" ? await FenceAsync(NamedOptions(applicationName)) : Fence;
            string newOwner = NewOwner();
            await SetLeaseExpiryAsync(id, 3);
            TargetState before = await TargetAsync(id);

            await using SqlConnection holder = await OpenConnectionAsync();
            await using SqlTransaction hold = (SqlTransaction)await holder.BeginTransactionAsync();
            await HoldTargetAsync(holder, hold, id);

            Task<object> pending = operation switch
            {
                "renew" => Boxed(waiter.RenewAsync(lease, CancellationToken.None)),
                "acquire" => Boxed(waiter.AcquireAsync(id, newOwner, CancellationToken.None)),
                "stage" => Boxed(
                    waiter.StageAsync(lease, [Item(1)], ContractVersion, CancellationToken.None)
                ),
                _ => Boxed(ActivateAsync(waiter, fence, lease)),
            };
            await WaitUntilBlockedAsync(applicationName, pending);
            (await LeaseExpiredAsync(holder, hold, id))
                .Should()
                .BeFalse("the operation must start waiting while the lease is live");

            await WaitForLeaseExpiryAsync(holder, hold, id);
            await hold.RollbackAsync();
            object result = await pending;

            TargetState after = await TargetAsync(id);
            switch (operation)
            {
                case "acquire":
                    result
                        .Should()
                        .Be(
                            new SnapshotAcquireResult.Acquired(
                                new EducationOrganizationSnapshotLease(id, newOwner, 2)
                            ),
                            "a lease that expired during the wait is taken over, not reported busy"
                        );
                    after.LeaseOwner.Should().Be(newOwner);
                    break;
                default:
                    result.Should().Be(new SnapshotTargetResult.LeaseLost(SnapshotLeaseLossReason.Expired));
                    after.Should().Be(before, "the expired lease is neither revived nor published");
                    (await OrganizationCountAsync(id)).Should().Be(0);
                    break;
            }
        }

        private static async Task<object> Boxed<T>(Task<T> task)
            where T : notnull => await task;
    }

    /// <summary>
    /// A successful acquisition or renewal waits for another transaction's lock on the target row, then writes the
    /// lease expiry. The expiry must be one lease duration after a database clock read taken after the wait: no
    /// earlier than the clock just before the lock is released, and no later than the clock after the operation
    /// returns. That rules out a lease left unextended, one extended from its previous expiry, a duration in the
    /// wrong unit, and a clock read before the wait, such as one in the locking statement itself.
    /// </summary>
    [TestFixture]
    public class Given_a_lease_written_after_a_wait_for_the_target_lock
        : EducationOrganizationSnapshotWriteTestBase
    {
        /// <summary><c>DATETIME2</c> has 100-nanosecond precision.</summary>
        private static readonly TimeSpan _precision = TimeSpan.FromTicks(1);

        [Test]
        public async Task It_expires_one_lease_duration_after_the_clock_read_after_the_wait(
            [Values("acquire", "renew")] string operation
        )
        {
            int id = await InsertDataStoreAsync("lease-expiry-" + operation);
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            if (operation == "acquire")
            {
                (await repository.ReleaseAsync(lease, CancellationToken.None))
                    .Should()
                    .BeOfType<SnapshotTargetResult.Success>();
            }

            TimeSpan leaseDuration = LeaseOptions.Value.LeaseDuration;
            string applicationName = NewApplicationName();
            EducationOrganizationSnapshotRepository waiter = Repository(NamedOptions(applicationName));
            string newOwner = NewOwner();

            await using SqlConnection holder = await OpenConnectionAsync();
            await using SqlTransaction hold = (SqlTransaction)await holder.BeginTransactionAsync();
            await HoldTargetAsync(holder, hold, id);

            Task<object> pending =
                operation == "acquire"
                    ? Boxed(waiter.AcquireAsync(id, newOwner, CancellationToken.None))
                    : Boxed(waiter.RenewAsync(lease, CancellationToken.None));
            await WaitUntilBlockedAsync(applicationName, pending);
            DateTime beforeRelease = await holder.ExecuteScalarAsync<DateTime>(
                "SELECT SYSUTCDATETIME();",
                transaction: hold
            );
            await hold.RollbackAsync();
            object result = await pending;
            DateTime afterReturn = await Connection!.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");

            result
                .Should()
                .Be(
                    operation == "acquire"
                        ? (object)
                            new SnapshotAcquireResult.Acquired(
                                new EducationOrganizationSnapshotLease(id, newOwner, 2)
                            )
                        : new SnapshotTargetResult.Success(1)
                );
            DateTime expiresAt = (await TargetAsync(id)).LeaseExpiresAt!.Value;
            expiresAt
                .Should()
                .BeOnOrAfter(
                    beforeRelease + leaseDuration - _precision,
                    "the expiry is computed from a clock read after the lock wait"
                )
                .And.BeOnOrBefore(
                    afterReturn + leaseDuration + _precision,
                    "the expiry is one lease duration after a clock read during the operation"
                );
        }

        private static async Task<object> Boxed<T>(Task<T> task)
            where T : notnull => await task;
    }

    [TestFixture]
    public class Given_repeated_abandonment_before_a_successful_refresh
        : EducationOrganizationSnapshotWriteTestBase
    {
        /// <summary>
        /// Three executions each acquire, reclaim and stage a non-empty generation, then abandon it by letting the
        /// lease expire; a fourth publishes. The target row's allocation is checked separately from the generations
        /// present in the organization rows at every checkpoint.
        /// </summary>
        [Test]
        public async Task It_keeps_at_most_the_active_and_the_current_generation(
            [Values(false, true)] bool published
        )
        {
            int id = await InsertDataStoreAsync("abandon-" + published);
            long? active = null;
            if (published)
            {
                active = (await PublishAsync(id, [Item(100)])).Generation;
            }

            long[] activeRows = active is { } a ? [a] : [];
            EducationOrganizationSnapshotRepository repository = Repository();
            for (int execution = 1; execution <= 4; execution++)
            {
                EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
                long g = lease.Generation;
                g.Should().Be((active ?? 0) + execution);
                (await repository.ReclaimAsync(lease, CancellationToken.None))
                    .Should()
                    .BeOfType<SnapshotTargetResult.Success>();

                await AssertCheckpointAsync(
                    id,
                    g,
                    active,
                    activeRows,
                    $"execution {execution} before staging"
                );

                (
                    await repository.StageAsync(
                        lease,
                        [Item(execution), Item(execution + 10)],
                        ContractVersion,
                        CancellationToken.None
                    )
                )
                    .Should()
                    .Be(new SnapshotTargetResult.Success(2));

                await AssertCheckpointAsync(
                    id,
                    g,
                    active,
                    [.. activeRows, g],
                    $"execution {execution} after staging"
                );

                if (execution < 4)
                {
                    await SetLeaseExpiryAsync(id, -1);
                    continue;
                }

                (await ActivateAsync(repository, Fence, lease))
                    .Should()
                    .Be(new SnapshotTargetResult.Success(1));
                (await repository.PruneAsync(id, CancellationToken.None))
                    .Should()
                    .BeOfType<SnapshotTargetResult.Success>();

                TargetState target = await TargetAsync(id);
                (target.LastGeneration, target.LeaseGeneration, target.ActiveGeneration)
                    .Should()
                    .Be((g, null, g));
                (await GenerationsAsync(id)).Should().Equal([g], "after activation and prune");
                (await ActiveIdsAsync(id)).Should().Equal(4, 14);
            }
        }

        private async Task AssertCheckpointAsync(
            int id,
            long generation,
            long? active,
            long[] expectedRowGenerations,
            string checkpoint
        )
        {
            TargetState target = await TargetAsync(id);
            (target.LastGeneration, target.LeaseGeneration, target.ActiveGeneration)
                .Should()
                .Be((generation, generation, active), checkpoint);
            (await GenerationsAsync(id)).Should().Equal(expectedRowGenerations, checkpoint);
        }
    }

    [TestFixture]
    public class Given_a_reclaim_by_the_lease_holder : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_keeps_the_active_and_its_own_generation()
        {
            int id = await InsertDataStoreAsync("reclaim-own");
            await PublishAsync(id, [Item(1)]);
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            (await repository.StageAsync(lease, [Item(2)], ContractVersion, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(1));

            (await repository.ReclaimAsync(lease, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(0));

            (await GenerationsAsync(id)).Should().Equal(1, 2);
            (await ActiveIdsAsync(id)).Should().Equal(1);
        }
    }

    [TestFixture]
    public class Given_a_prune_after_activation : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_deletes_only_generations_older_than_the_active_one()
        {
            int id = await InsertDataStoreAsync("prune");
            await PublishAsync(id, [Item(1), Item(2), Item(3)], prune: false);
            await PublishAsync(id, [Item(2), Item(4)], prune: false);
            (await GenerationsAsync(id)).Should().Equal(1, 2);

            (await Repository().PruneAsync(id, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(3));

            (await GenerationsAsync(id)).Should().Equal(2);
            (await ActiveIdsAsync(id)).Should().Equal(2, 4);
        }

        /// <summary>
        /// Activation clears the lease, so another execution can acquire, reclaim and stage the next generation
        /// before the publishing execution's prune runs. The prune deletes only generations older than the active
        /// one, never the newer staged one, which then publishes intact.
        /// </summary>
        [Test]
        public async Task It_keeps_a_newer_generation_staged_before_the_prune()
        {
            int id = await InsertDataStoreAsync("prune-newer");
            await PublishAsync(id, [Item(1), Item(2), Item(3)], prune: false);
            await PublishAsync(id, [Item(2), Item(4)], prune: false);
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            lease.Generation.Should().Be(3);
            (await repository.ReclaimAsync(lease, CancellationToken.None))
                .Should()
                .BeOfType<SnapshotTargetResult.Success>();
            (await repository.RenewAsync(lease, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(1));
            (
                await repository.StageAsync(
                    lease,
                    [Item(4), Item(5), Item(6)],
                    ContractVersion,
                    CancellationToken.None
                )
            )
                .Should()
                .Be(new SnapshotTargetResult.Success(3));
            // The reclaim removed generation 1.
            (await GenerationsAsync(id))
                .Should()
                .Equal(2, 3);
            List<string> rowsBeforePrune = (await StateAsync(id)).Rows;

            (await Repository().PruneAsync(id, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(0));

            (await GenerationsAsync(id)).Should().Equal(2, 3);
            (await StateAsync(id))
                .Rows.Should()
                .Equal(rowsBeforePrune, "no row of either generation is deleted");
            (await ActiveIdsAsync(id)).Should().Equal(2, 4);

            (await ActivateAsync(repository, Fence, lease)).Should().Be(new SnapshotTargetResult.Success(1));
            (await ActiveIdsAsync(id)).Should().Equal(4, 5, 6);
        }

        [Test]
        public async Task It_deletes_nothing_before_a_generation_is_published()
        {
            int id = await InsertDataStoreAsync("prune-unpublished");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            await repository.StageAsync(lease, [Item(1)], ContractVersion, CancellationToken.None);

            (await repository.PruneAsync(id, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(0));
            (await GenerationsAsync(id)).Should().Equal(1);
        }
    }

    [TestFixture]
    public class Given_an_activation_inside_a_job_fence : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_rolls_the_activation_back_when_the_fence_refuses_to_commit()
        {
            int id = await InsertDataStoreAsync("fence-refusal");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            await repository.StageAsync(lease, [Item(1)], ContractVersion, CancellationToken.None);
            JobExecutionOwnership ownership = new();
            IJobFence fence = await FenceAsync(ownership: ownership);
            TargetState before = await TargetAsync(id);
            SnapshotTargetResult? activated = null;

            Func<Task> fenced = () =>
                fence.ExecuteAsync(
                    async (transaction, token) =>
                    {
                        activated = await repository.ActivateAsync(
                            transaction,
                            lease,
                            ContractVersion,
                            token
                        );
                        // The execution loses ownership before the fence commits.
                        ownership.TryMarkUncertain("SnapshotTest");
                    },
                    CancellationToken.None
                );

            await fenced.Should().ThrowAsync<JobLeaseLostException>();
            activated.Should().Be(new SnapshotTargetResult.Success(1));
            (await TargetAsync(id)).Should().Be(before, "the activation commits only with the fence");
        }

        [Test]
        public async Task It_reports_a_removed_data_store()
        {
            int id = await InsertDataStoreAsync("fence-removed");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            await Connection!.ExecuteAsync("DELETE FROM dmscs.DataStore WHERE Id = @Id;", new { Id = id });

            (await ActivateAsync(repository, Fence, lease))
                .Should()
                .Be(new SnapshotTargetResult.DataStoreRemoved());
        }
    }

    [TestFixture]
    public class Given_a_data_store_deleted_during_a_refresh : EducationOrganizationSnapshotWriteTestBase
    {
        private static DataStoreRepository DataStores(IOptions<DatabaseOptions> databaseOptions) =>
            new(
                databaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(databaseOptions),
                A.Fake<IConnectionStringReader>(),
                A.Fake<IDataStoreContextRepository>(),
                A.Fake<IDataStoreDerivativeRepository>(),
                new TestAuditContext(),
                new TenantContextProvider { Context = new TenantContext.NotMultitenant() }
            );

        [Test]
        public async Task It_reports_not_found_when_the_delete_commits_before_acquisition()
        {
            int id = await InsertDataStoreAsync("delete-first-acquire");
            string applicationName = NewApplicationName();
            await using SqlConnection deleter = await OpenConnectionAsync();
            await using SqlTransaction delete = (SqlTransaction)await deleter.BeginTransactionAsync();
            await deleter.ExecuteAsync(
                "DELETE FROM dmscs.DataStore WHERE Id = @Id;",
                new { Id = id },
                delete
            );

            Task<SnapshotAcquireResult> acquiring = Repository(NamedOptions(applicationName))
                .AcquireAsync(id, NewOwner(), CancellationToken.None);
            await WaitUntilBlockedAsync(applicationName, acquiring);
            await delete.CommitAsync();

            (await acquiring).Should().Be(new SnapshotAcquireResult.DataStoreNotFound());
            (await TargetCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_cascades_the_new_target_when_acquisition_commits_before_the_delete()
        {
            int id = await InsertDataStoreAsync("refresh-first-acquire");
            CommitPause pause = new();
            Task<SnapshotAcquireResult> acquiring = Repository(hooks: pause.Hooks)
                .AcquireAsync(id, NewOwner(), CancellationToken.None);
            await pause.Reached;

            string applicationName = NewApplicationName();
            Task<DataStoreDeleteResult> deleting = DataStores(NamedOptions(applicationName))
                .DeleteDataStore(id);
            await WaitUntilBlockedAsync(applicationName, deleting);
            pause.Release();

            (await acquiring).Should().BeOfType<SnapshotAcquireResult.Acquired>();
            (await deleting).Should().BeOfType<DataStoreDeleteResult.Success>();
            (await TargetCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_reports_the_target_removed_when_the_delete_commits_before_staging()
        {
            int id = await InsertDataStoreAsync("delete-first-stage");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            string applicationName = NewApplicationName();
            await using SqlConnection deleter = await OpenConnectionAsync();
            await using SqlTransaction delete = (SqlTransaction)await deleter.BeginTransactionAsync();
            await deleter.ExecuteAsync(
                "DELETE FROM dmscs.DataStore WHERE Id = @Id;",
                new { Id = id },
                delete
            );

            Task<SnapshotTargetResult> staging = Repository(NamedOptions(applicationName))
                .StageAsync(lease, [Item(1)], ContractVersion, CancellationToken.None);
            await WaitUntilBlockedAsync(applicationName, staging);
            await delete.CommitAsync();

            (await staging).Should().Be(new SnapshotTargetResult.DataStoreRemoved());
            (await OrganizationCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_cascades_the_staged_rows_when_staging_commits_before_the_delete()
        {
            int id = await InsertDataStoreAsync("refresh-first-stage");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            CommitPause pause = new();
            Task<SnapshotTargetResult> staging = Repository(hooks: pause.Hooks)
                .StageAsync(lease, [Item(1), Item(2)], ContractVersion, CancellationToken.None);
            await pause.Reached;

            string applicationName = NewApplicationName();
            Task<DataStoreDeleteResult> deleting = DataStores(NamedOptions(applicationName))
                .DeleteDataStore(id);
            await WaitUntilBlockedAsync(applicationName, deleting);
            pause.Release();

            (await staging).Should().Be(new SnapshotTargetResult.Success(2));
            (await deleting).Should().BeOfType<DataStoreDeleteResult.Success>();
            (await TargetCountAsync(id)).Should().Be(0);
            (await OrganizationCountAsync(id)).Should().Be(0);
        }
    }

    /// <summary>
    /// A trigger raises an error inside each operation's write. The operations report it as a database failure
    /// carrying only the error number and the operation name, and roll back.
    /// </summary>
    [TestFixture]
    public class Given_a_forced_database_error : EducationOrganizationSnapshotWriteTestBase
    {
        private const string ForcedErrorNumber = "50001";

        private int _id;
        private EducationOrganizationSnapshotLease _lease = null!;

        [SetUp]
        public async Task Setup()
        {
            _id = await InsertDataStoreAsync("forced");
            _lease = await AcquiredAsync(Repository(), _id);
            await Connection!.ExecuteAsync(
                """
                CREATE TRIGGER dmscs.raise_snapshot_test_target ON dmscs.EducationOrganizationSnapshotTarget
                AFTER INSERT, UPDATE AS THROW 50001, N'snapshot-test', 1;
                """
            );
            await Connection!.ExecuteAsync(
                """
                CREATE TRIGGER dmscs.raise_snapshot_test_rows ON dmscs.EducationOrganizationSnapshot
                AFTER INSERT AS THROW 50001, N'snapshot-test', 1;
                """
            );
        }

        [TearDown]
        public async Task DropTriggers() =>
            await Connection!.ExecuteAsync(
                """
                DROP TRIGGER IF EXISTS dmscs.raise_snapshot_test_target;
                DROP TRIGGER IF EXISTS dmscs.raise_snapshot_test_rows;
                """
            );

        [Test]
        public async Task It_classifies_the_first_acquisition_failure()
        {
            int first = await InsertDataStoreAsync("forced-first");

            SnapshotAcquireResult result = await Repository()
                .AcquireAsync(first, NewOwner(), CancellationToken.None);

            JobFailureDiagnostic diagnostic = result
                .Should()
                .BeOfType<SnapshotAcquireResult.DbFailure>()
                .Subject.Diagnostic;
            (diagnostic.ProviderErrorCode, diagnostic.Operation).Should().Be((ForcedErrorNumber, "Acquire"));
            (await TargetCountAsync(first)).Should().Be(0, "the failed target insert rolls back");
        }

        [Test]
        public async Task It_classifies_the_renewal_failure()
        {
            TargetState before = await TargetAsync(_id);

            AssertFailure(await Repository().RenewAsync(_lease, CancellationToken.None), "Renew");
            (await TargetAsync(_id)).Should().Be(before);
        }

        [Test]
        public async Task It_classifies_the_release_failure()
        {
            TargetState before = await TargetAsync(_id);

            AssertFailure(await Repository().ReleaseAsync(_lease, CancellationToken.None), "Release");
            (await TargetAsync(_id)).Should().Be(before);
        }

        [Test]
        public async Task It_classifies_the_staging_failure()
        {
            AssertFailure(
                await Repository().StageAsync(_lease, [Item(1)], ContractVersion, CancellationToken.None),
                "Stage"
            );
            (await OrganizationCountAsync(_id)).Should().Be(0);
        }

        [Test]
        public async Task It_throws_the_activation_failure_so_the_fence_rolls_back()
        {
            TargetState before = await TargetAsync(_id);

            Func<Task> activation = () => ActivateAsync(Repository(), Fence, _lease);

            SnapshotStoreException thrown = (
                await activation.Should().ThrowAsync<SnapshotStoreException>()
            ).Which;
            (thrown.Diagnostic.ProviderErrorCode, thrown.Diagnostic.Operation)
                .Should()
                .Be((ForcedErrorNumber, "Activate"));
            (await TargetAsync(_id)).Should().Be(before);
        }

        private static void AssertFailure(SnapshotTargetResult result, string operation)
        {
            JobFailureDiagnostic diagnostic = result
                .Should()
                .BeOfType<SnapshotTargetResult.DbFailure>()
                .Subject.Diagnostic;
            (diagnostic.ProviderErrorCode, diagnostic.Operation).Should().Be((ForcedErrorNumber, operation));
        }
    }

    /// <summary>
    /// Real deadlocks (<c>1205</c>) and lock timeouts (<c>1222</c>) end refresh operations as database failures
    /// that carry the error number, with nothing written.
    /// </summary>
    [TestFixture]
    public class Given_a_deadlock_or_lock_timeout : EducationOrganizationSnapshotWriteTestBase
    {
        /// <summary>
        /// The holder locks the target row, the acquisition takes its shared lock on the data store row and waits
        /// for the target, and the holder then updates the data store row, closing the cycle. The holder runs at
        /// high deadlock priority, so the engine ends the acquisition.
        /// </summary>
        [Test]
        public async Task It_classifies_an_acquisition_chosen_as_the_deadlock_victim()
        {
            int id = await InsertDataStoreAsync("deadlock");
            await AcquiredAsync(Repository(), id);
            await SetLeaseExpiryAsync(id, -1);
            TargetState before = await TargetAsync(id);
            string applicationName = NewApplicationName();

            await using SqlConnection holder = await OpenConnectionAsync();
            await holder.ExecuteAsync("SET DEADLOCK_PRIORITY HIGH;");
            await using SqlTransaction hold = (SqlTransaction)await holder.BeginTransactionAsync();
            await HoldTargetAsync(holder, hold, id);
            Task<SnapshotAcquireResult> acquiring = Repository(NamedOptions(applicationName))
                .AcquireAsync(id, NewOwner(), CancellationToken.None);
            await WaitUntilBlockedAsync(applicationName, acquiring);
            await holder.ExecuteAsync(
                "UPDATE dmscs.DataStore SET Name = Name WHERE Id = @Id;",
                new { Id = id },
                hold
            );
            SnapshotAcquireResult result = await acquiring;
            await hold.RollbackAsync();

            JobFailureDiagnostic diagnostic = result
                .Should()
                .BeOfType<SnapshotAcquireResult.DbFailure>()
                .Subject.Diagnostic;
            (diagnostic.ProviderErrorCode, diagnostic.Operation)
                .Should()
                .Be((DeadlockVictim.ToString(System.Globalization.CultureInfo.InvariantCulture), "Acquire"));
            (await TargetAsync(id)).Should().Be(before);
        }

        [Test]
        public async Task It_classifies_an_acquisition_lock_timeout()
        {
            int id = await InsertDataStoreAsync("acquire-lock-timeout");
            await AcquiredAsync(Repository(), id);
            await SetLeaseExpiryAsync(id, -1);
            TargetState before = await TargetAsync(id);

            SnapshotAcquireResult result = await WhileTargetIsHeldAsync(
                id,
                () => Repository().AcquireAsync(id, NewOwner(), CancellationToken.None)
            );

            AssertLockTimeout(
                result.Should().BeOfType<SnapshotAcquireResult.DbFailure>().Subject.Diagnostic,
                "Acquire"
            );
            (await TargetAsync(id)).Should().Be(before);
        }

        [Test]
        public async Task It_classifies_a_staging_lock_timeout()
        {
            int id = await InsertDataStoreAsync("stage-lock-timeout");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);

            SnapshotTargetResult result = await WhileTargetIsHeldAsync(
                id,
                () => Repository().StageAsync(lease, [Item(1)], ContractVersion, CancellationToken.None)
            );

            AssertLockTimeout(
                result.Should().BeOfType<SnapshotTargetResult.DbFailure>().Subject.Diagnostic,
                "Stage"
            );
            (await OrganizationCountAsync(id)).Should().Be(0);
        }

        [Test]
        public async Task It_throws_an_activation_lock_timeout_so_the_fence_rolls_back()
        {
            int id = await InsertDataStoreAsync("activate-lock-timeout");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            TargetState before = await TargetAsync(id);

            SnapshotStoreException? thrown = await WhileTargetIsHeldAsync<SnapshotStoreException?>(
                id,
                async () =>
                {
                    try
                    {
                        await ActivateAsync(repository, Fence, lease);
                        return null;
                    }
                    catch (SnapshotStoreException exception)
                    {
                        return exception;
                    }
                }
            );

            thrown.Should().NotBeNull();
            AssertLockTimeout(thrown!.Diagnostic, "Activate");
            (await TargetAsync(id)).Should().Be(before);
        }

        /// <summary>Runs <paramref name="operation"/> while another transaction holds the target row lock.</summary>
        private static async Task<T> WhileTargetIsHeldAsync<T>(int dataStoreId, Func<Task<T>> operation)
        {
            await using SqlConnection holder = await OpenConnectionAsync();
            await using SqlTransaction hold = (SqlTransaction)await holder.BeginTransactionAsync();
            await HoldTargetAsync(holder, hold, dataStoreId);
            try
            {
                return await operation();
            }
            finally
            {
                await hold.RollbackAsync();
            }
        }

        private static void AssertLockTimeout(JobFailureDiagnostic diagnostic, string operation) =>
            (diagnostic.ProviderErrorCode, diagnostic.Operation)
                .Should()
                .Be((LockTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture), operation));
    }

    /// <summary>
    /// A target of another partition, with two published generations and a live lease that has staged rows. A
    /// repository of a different partition uses that real lease. Every operation behaves as if the target were
    /// absent, and the target row and its organization rows stay exactly as they were.
    /// </summary>
    [TestFixture]
    public class Given_a_target_of_another_partition : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_neither_reads_nor_changes_the_foreign_target(
            [Values("another-tenant", "single-tenant-caller", "multitenant-caller")] string mismatch
        )
        {
            long owningTenant = await InsertTenantAsync();
            long callerTenant = await InsertTenantAsync();
            (TenantContext owner, TenantContext caller, long? storeTenant) = mismatch switch
            {
                "another-tenant" => (
                    new TenantContext.Multitenant(owningTenant, "owning"),
                    new TenantContext.Multitenant(callerTenant, "caller"),
                    owningTenant
                ),
                "single-tenant-caller" => (
                    new TenantContext.Multitenant(owningTenant, "owning"),
                    new TenantContext.NotMultitenant(),
                    owningTenant
                ),
                _ => (
                    (TenantContext)new TenantContext.NotMultitenant(),
                    (TenantContext)new TenantContext.Multitenant(callerTenant, "caller"),
                    (long?)null
                ),
            };
            int id = await InsertDataStoreAsync("partition-" + mismatch, storeTenant);
            EducationOrganizationSnapshotRepository owning = Repository(tenant: owner);
            await PublishAsync(id, [Item(1), Item(2)], prune: false, repository: owning);
            await PublishAsync(id, [Item(2), Item(3)], prune: false, repository: owning);
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(owning, id);
            (await owning.StageAsync(lease, [Item(4)], ContractVersion, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(1));
            (TargetState Target, List<string> Rows) before = await StateAsync(id);
            before.Target.ActiveGeneration.Should().Be(2);
            (await GenerationsAsync(id)).Should().Equal(1, 2, 3);

            EducationOrganizationSnapshotRepository foreign = Repository(tenant: caller);
            SnapshotTargetResult absent = new SnapshotTargetResult.DataStoreRemoved();
            (await foreign.AcquireAsync(id, NewOwner(), CancellationToken.None))
                .Should()
                .Be(new SnapshotAcquireResult.DataStoreNotFound());
            (await foreign.ReclaimAsync(lease, CancellationToken.None)).Should().Be(absent);
            (await foreign.RenewAsync(lease, CancellationToken.None)).Should().Be(absent);
            (await foreign.StageAsync(lease, [Item(5)], ContractVersion, CancellationToken.None))
                .Should()
                .Be(absent);
            (await ActivateAsync(foreign, Fence, lease)).Should().Be(absent);
            (await foreign.ReleaseAsync(lease, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(0));
            (await foreign.PruneAsync(id, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(0));

            (TargetState Target, List<string> Rows) after = await StateAsync(id);
            after.Target.Should().Be(before.Target);
            after.Rows.Should().Equal(before.Rows);
            (await owning.RenewAsync(lease, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(1), "the owning partition's lease is intact");
        }
    }

    /// <summary>
    /// A completed or disposed transaction, or one of another provider, must be refused before any statement, so
    /// activation can never publish outside the transaction it was given. The lease is live and staged, so an
    /// activation that ran would publish.
    /// </summary>
    [TestFixture]
    public class Given_an_activation_with_an_unusable_transaction : EducationOrganizationSnapshotWriteTestBase
    {
        [Test]
        public async Task It_rejects_a_completed_or_disposed_transaction_before_any_statement(
            [Values("committed", "rolled-back", "disposed")] string state
        )
        {
            int id = await InsertDataStoreAsync("transaction-" + state);
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            await repository.StageAsync(lease, [Item(1)], ContractVersion, CancellationToken.None);
            (TargetState Target, List<string> Rows) before = await StateAsync(id);

            await using SqlConnection connection = await OpenConnectionAsync();
            SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            switch (state)
            {
                case "committed":
                    await transaction.CommitAsync();
                    break;
                case "rolled-back":
                    await transaction.RollbackAsync();
                    break;
                default:
                    await transaction.DisposeAsync();
                    break;
            }

            Func<Task> activation = () =>
                repository.ActivateAsync(transaction, lease, ContractVersion, CancellationToken.None);

            await activation.Should().ThrowAsync<InvalidOperationException>();
            (TargetState Target, List<string> Rows) after = await StateAsync(id);
            after.Target.Should().Be(before.Target, "nothing was published");
            after.Rows.Should().Equal(before.Rows);
            await transaction.DisposeAsync();
        }

        [Test]
        public async Task It_rejects_a_transaction_of_another_provider()
        {
            int id = await InsertDataStoreAsync("transaction-provider");
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            TargetState before = await TargetAsync(id);

            Func<Task> activation = () =>
                repository.ActivateAsync(
                    A.Fake<DbTransaction>(),
                    lease,
                    ContractVersion,
                    CancellationToken.None
                );

            await activation.Should().ThrowAsync<ArgumentException>();
            (await TargetAsync(id)).Should().Be(before);
        }
    }

    /// <summary>
    /// A renewal is bounded by <c>RenewalInterval / 2</c> (here 2 seconds, with a 1-second lock wait) and a release
    /// by 5 seconds. A commit that stays pending past the bound ends in the deadline; a held target lock ends in the
    /// lock wait or, if it comes first, the deadline. Either way the operation returns within its bound with a
    /// failure diagnostic, its cleanup completes, and the lease and publication state stay unchanged.
    /// </summary>
    [TestFixture]
    public class Given_an_operation_that_outlasts_its_bound : EducationOrganizationSnapshotWriteTestBase
    {
        private static readonly IOptions<JobOptions> _shortRenewal = Options.Create(
            new JobOptions
            {
                LeaseDuration = TimeSpan.FromMinutes(5),
                RenewalInterval = TimeSpan.FromSeconds(4),
            }
        );

        /// <summary>The repository's fixed bound on a release.</summary>
        private static readonly TimeSpan _releaseTimeout = TimeSpan.FromSeconds(5);

        /// <summary>How early a deadline timer may fire against the stopwatch.</summary>
        private static readonly TimeSpan _timerResolution = TimeSpan.FromMilliseconds(50);

        /// <summary>The longest any wait in these tests may take before it fails the test.</summary>
        private static readonly TimeSpan _bound = TimeSpan.FromSeconds(15);

        [Test]
        public async Task It_bounds_a_renewal_whose_commit_stays_pending()
        {
            int id = await InsertDataStoreAsync("renew-deadline");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            TargetState before = await TargetAsync(id);
            PendingCommit commit = new();
            try
            {
                long started = Stopwatch.GetTimestamp();
                Task<SnapshotTargetResult> renewing = Repository(
                        hooks: commit.Hooks,
                        jobOptions: _shortRenewal
                    )
                    .RenewAsync(lease, CancellationToken.None);
                await commit.Reached;
                SnapshotTargetResult result = await renewing.WaitAsync(_bound);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

                commit.CleanupIsPending.Should().BeTrue("the renewal returned while its commit was pending");
                AssertWithinDeadline(elapsed, _shortRenewal.Value.RenewalTimeout);
                AssertDeadline(result, "Renew");
            }
            finally
            {
                commit.Release();
            }

            (await commit.CleanupAsync())
                .Should()
                .NotBeNull("the released commit ran with its token already cancelled");
            (await TargetAsync(id)).Should().Be(before, "the pending commit never committed");
        }

        [Test]
        public async Task It_bounds_a_release_whose_commit_stays_pending()
        {
            int id = await InsertDataStoreAsync("release-deadline");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            TargetState before = await TargetAsync(id);
            PendingCommit commit = new();
            try
            {
                long started = Stopwatch.GetTimestamp();
                Task<SnapshotTargetResult> releasing = Repository(hooks: commit.Hooks)
                    .ReleaseAsync(lease, CancellationToken.None);
                await commit.Reached;
                SnapshotTargetResult result = await releasing.WaitAsync(_bound);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

                commit.CleanupIsPending.Should().BeTrue("the release returned while its commit was pending");
                AssertWithinDeadline(elapsed, _releaseTimeout);
                AssertDeadline(result, "Release");
            }
            finally
            {
                commit.Release();
            }

            (await commit.CleanupAsync())
                .Should()
                .NotBeNull("the released commit ran with its token already cancelled");
            (await TargetAsync(id)).Should().Be(before, "the lease is still held");
        }

        [Test]
        public async Task It_bounds_a_renewal_blocked_by_a_held_target_lock()
        {
            int id = await InsertDataStoreAsync("renew-lock-wait");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            TargetState before = await TargetAsync(id);
            await using SqlConnection holder = await OpenConnectionAsync();
            await using SqlTransaction hold = (SqlTransaction)await holder.BeginTransactionAsync();
            await HoldTargetAsync(holder, hold, id);

            long started = Stopwatch.GetTimestamp();
            SnapshotTargetResult result = await Repository(jobOptions: _shortRenewal)
                .RenewAsync(lease, CancellationToken.None);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            await hold.RollbackAsync();

            elapsed
                .Should()
                .BeLessThan(TimeSpan.FromSeconds(2.7), "the lock is held until the renewal returns");
            JobFailureDiagnostic diagnostic = result
                .Should()
                .BeOfType<SnapshotTargetResult.DbFailure>()
                .Subject.Diagnostic;
            diagnostic.Operation.Should().Be("Renew");
            (
                diagnostic.ProviderErrorCode == "1222"
                || diagnostic.ExceptionTypeChain.StartsWith(
                    "System.TimeoutException",
                    StringComparison.Ordinal
                )
            )
                .Should()
                .BeTrue("either the lock wait or the deadline ends the wait, and both are failures");
            (await TargetAsync(id)).Should().Be(before);
        }

        [Test]
        public async Task It_propagates_cancellation_while_an_acquisition_is_pending()
        {
            int id = await InsertDataStoreAsync("acquire-cancelled");
            TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<Task<JobDatabaseSessionCleanup>> cleanups = [];
            JobDatabaseSessionHooks hooks = new()
            {
                // The commit is held, ignoring its own cancellation, after the target row was inserted.
                BeforeOperation = async (operation, _) =>
                {
                    if (operation == "Commit")
                    {
                        reached.TrySetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                    }
                },
                HandedOver = cleanup =>
                {
                    lock (cleanups)
                    {
                        cleanups.Add(cleanup);
                    }
                },
            };
            using CancellationTokenSource cancellation = new();
            Task<SnapshotAcquireResult> acquiring = Repository(hooks: hooks)
                .AcquireAsync(id, NewOwner(), cancellation.Token);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            long started = Stopwatch.GetTimestamp();
            await cancellation.CancelAsync();
            Func<Task> awaitAcquisition = () => acquiring;
            await awaitAcquisition.Should().ThrowAsync<OperationCanceledException>();
            Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(1));

            release.TrySetResult();
            Task<JobDatabaseSessionCleanup> handedOver;
            lock (cleanups)
            {
                handedOver = cleanups.Should().ContainSingle().Subject;
            }
            await handedOver.WaitAsync(TimeSpan.FromSeconds(15));
            (await TargetCountAsync(id)).Should().Be(0, "the uncommitted target insert is rolled back");
        }

        private static void AssertDeadline(SnapshotTargetResult result, string operation)
        {
            JobFailureDiagnostic diagnostic = result
                .Should()
                .BeOfType<SnapshotTargetResult.DbFailure>()
                .Subject.Diagnostic;
            (diagnostic.Operation, diagnostic.ProviderErrorCode).Should().Be((operation, (string?)null));
            diagnostic.ExceptionTypeChain.Should().StartWith("System.TimeoutException");
        }

        /// <summary>
        /// The operation ended at its deadline: not before it, allowing for timer resolution, and within a second
        /// after it.
        /// </summary>
        private static void AssertWithinDeadline(TimeSpan elapsed, TimeSpan deadline) =>
            elapsed
                .Should()
                .BeGreaterThanOrEqualTo(deadline - _timerResolution, "the commit is pending until released")
                .And.BeLessThan(deadline + TimeSpan.FromSeconds(1), "the deadline ends the wait");

        /// <summary>
        /// Holds the session's first commit until <see cref="Release"/>, ignoring the commit's own cancellation as a
        /// provider call that does not return would, and records the pending session's hand-over to cleanup.
        /// </summary>
        private sealed class PendingCommit
        {
            private readonly TaskCompletionSource _reached = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            private readonly TaskCompletionSource _release = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            private readonly List<Task<JobDatabaseSessionCleanup>> _cleanups = [];
            private int _held;

            public PendingCommit()
            {
                Hooks = new JobDatabaseSessionHooks
                {
                    BeforeOperation = async (operation, _) =>
                    {
                        if (operation == "Commit" && Interlocked.Exchange(ref _held, 1) == 0)
                        {
                            _reached.TrySetResult();
                            await _release.Task.WaitAsync(_bound, CancellationToken.None);
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

            public Task Reached => _reached.Task.WaitAsync(_bound);

            /// <summary>True when the session was handed over exactly once and its cleanup has not finished.</summary>
            public bool CleanupIsPending
            {
                get
                {
                    lock (_cleanups)
                    {
                        return _cleanups.Count == 1 && !_cleanups[0].IsCompleted;
                    }
                }
            }

            public void Release() => _release.TrySetResult();

            /// <summary>Waits for the single hand-over's cleanup and returns how the pending commit ended.</summary>
            public async Task<Exception?> CleanupAsync()
            {
                Task<JobDatabaseSessionCleanup> cleanup;
                lock (_cleanups)
                {
                    _cleanups.Should().ContainSingle("the operation was handed over exactly once");
                    cleanup = _cleanups[0];
                }

                return (await cleanup.WaitAsync(_bound)).Operation;
            }
        }
    }
}
