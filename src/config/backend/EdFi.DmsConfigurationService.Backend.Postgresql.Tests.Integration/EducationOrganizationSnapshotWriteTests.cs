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
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Shared setup for the snapshot write fixtures: data stores, a repository with fixed lease timings, a real job
/// fence for activation, and readers of the target row and the stored generations.
/// </summary>
public abstract class EducationOrganizationSnapshotWriteTestBase : DatabaseTest
{
    protected const string ContractVersion = "educationOrganizationProjection.v1";

    /// <summary>A 5-minute lease and a 20-second renewal interval, so a renewal is bounded by 10 seconds.</summary>
    protected static readonly IOptions<JobOptions> LeaseOptions = Options.Create(
        new JobOptions { LeaseDuration = TimeSpan.FromMinutes(5), RenewalInterval = TimeSpan.FromSeconds(20) }
    );

    private static readonly JobLeaseTimings _fenceTimings = new(
        RenewalTimeout: TimeSpan.FromSeconds(10),
        FenceTimeout: TimeSpan.FromSeconds(15)
    );

    private const string LockTargetSql = """
        SELECT 1 FROM dmscs."EducationOrganizationSnapshotTarget" WHERE "DataStoreId" = @Id FOR UPDATE;
        """;

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
            databaseOptions ?? Configuration.DatabaseOptions,
            jobOptions ?? LeaseOptions,
            new TenantContextProvider { Context = tenant ?? new TenantContext.NotMultitenant() }
        )
        {
            SessionHooks = hooks,
        };

    /// <summary>The test database under an application name, so its sessions can be found in <c>pg_locks</c>.</summary>
    protected static IOptions<DatabaseOptions> NamedOptions(string applicationName) =>
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = new NpgsqlConnectionStringBuilder(
                    Configuration.DatabaseOptions.Value.DatabaseConnection
                )
                {
                    ApplicationName = applicationName,
                }.ConnectionString,
                EncryptionKey = Configuration.DatabaseOptions.Value.EncryptionKey,
            }
        );

    protected static string NewApplicationName() => "snapshot-test-" + Guid.NewGuid().ToString("N");

    protected static string NewOwner() => Guid.NewGuid().ToString("N");

    protected static EducationOrganizationProjectionItem Item(long id, long? parentId = null) =>
        new(id, $"Organization {id}", null, "edfi.School", parentId);

    protected async Task<int> InsertDataStoreAsync(string name, long? tenantId = null) =>
        await Connection!.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs."DataStore" ("Name", "DataStoreType", "Provider", "TenantId")
            VALUES (@Name, 'Shared', 'postgresql', @TenantId)
            RETURNING "Id";
            """,
            new { Name = name, TenantId = tenantId }
        );

    protected async Task<long> InsertTenantAsync() =>
        await Connection!.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
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
            INSERT INTO dmscs."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status", "NextAttemptAt",
                "AttemptCount", "FencingToken")
            VALUES (@JobId, 'EducationOrganization.Refresh', 1, '{"scope":1,"dataStoreId":0}', 'Pending',
                (clock_timestamp() AT TIME ZONE 'UTC') - interval '1 second', 0, 0);
            """,
            new { JobId = Guid.NewGuid().ToString("N") }
        );
        ClaimedJob claim = (
            await new JobLeaseRepository(Configuration.DatabaseOptions, _fenceTimings).ClaimNext(
                NewOwner(),
                300,
                5,
                CancellationToken.None
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        return new PostgresqlJobFenceFactory(
            databaseOptions ?? Configuration.DatabaseOptions,
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
        (await repository.ReclaimAsync(lease, CancellationToken.None))
            .Should()
            .BeOfType<SnapshotTargetResult.Success>();
        (await repository.RenewAsync(lease, CancellationToken.None))
            .Should()
            .Be(new SnapshotTargetResult.Success(1));
        (await repository.StageAsync(lease, items, ContractVersion, CancellationToken.None))
            .Should()
            .Be(new SnapshotTargetResult.Success(items.Count));
        (await ActivateAsync(repository, Fence, lease)).Should().Be(new SnapshotTargetResult.Success(1));
        if (prune)
        {
            (await repository.PruneAsync(dataStoreId, CancellationToken.None))
                .Should()
                .BeOfType<SnapshotTargetResult.Success>();
        }

        return lease;
    }

    /// <summary>The target row and every organization row of a data store, for before-and-after comparison.</summary>
    protected async Task<(TargetState Target, List<string> Rows)> StateAsync(int dataStoreId) =>
        (
            await TargetAsync(dataStoreId),
            (
                await Connection!.QueryAsync<string>(
                    """
                    SELECT concat_ws('|', "Generation", "EducationOrganizationId", "TenantId", "NameOfInstitution",
                        "RefreshedAt", "SourceContractVersion")
                    FROM dmscs."EducationOrganizationSnapshot"
                    WHERE "DataStoreId" = @Id
                    ORDER BY "Generation", "EducationOrganizationId";
                    """,
                    new { Id = dataStoreId }
                )
            ).ToList()
        );

    /// <summary>Sets the lease to expire <paramref name="seconds"/> from database time (negative: in the past).</summary>
    protected Task SetLeaseExpiryAsync(int dataStoreId, double seconds) =>
        Connection!.ExecuteAsync(
            """
            UPDATE dmscs."EducationOrganizationSnapshotTarget"
            SET "LeaseExpiresAt" = (clock_timestamp() AT TIME ZONE 'UTC') + @Seconds * interval '1 second'
            WHERE "DataStoreId" = @Id;
            """,
            new { Id = dataStoreId, Seconds = seconds }
        );

    protected Task<TargetState> TargetAsync(int dataStoreId) =>
        Connection!.QuerySingleAsync<TargetState>(
            """
            SELECT "LastGeneration", "ActiveGeneration", "LeaseOwner", "LeaseGeneration", "LeaseExpiresAt",
                "TenantId", "SourceContractVersion", "RefreshedAt"
            FROM dmscs."EducationOrganizationSnapshotTarget"
            WHERE "DataStoreId" = @Id;
            """,
            new { Id = dataStoreId }
        );

    protected async Task<int> TargetCountAsync(int dataStoreId) =>
        await Connection!.ExecuteScalarAsync<int>(
            """SELECT COUNT(*) FROM dmscs."EducationOrganizationSnapshotTarget" WHERE "DataStoreId" = @Id;""",
            new { Id = dataStoreId }
        );

    protected async Task<int> OrganizationCountAsync(int dataStoreId) =>
        await Connection!.ExecuteScalarAsync<int>(
            """SELECT COUNT(*) FROM dmscs."EducationOrganizationSnapshot" WHERE "DataStoreId" = @Id;""",
            new { Id = dataStoreId }
        );

    /// <summary>The distinct generations present in the organization rows, ascending.</summary>
    protected async Task<List<long>> GenerationsAsync(int dataStoreId) =>
        (
            await Connection!.QueryAsync<long>(
                """
                SELECT DISTINCT "Generation" FROM dmscs."EducationOrganizationSnapshot"
                WHERE "DataStoreId" = @Id ORDER BY "Generation";
                """,
                new { Id = dataStoreId }
            )
        ).ToList();

    /// <summary>The organization ids of the active generation, ascending.</summary>
    protected async Task<List<long>> ActiveIdsAsync(int dataStoreId) =>
        (
            await Connection!.QueryAsync<long>(
                """
                SELECT s."EducationOrganizationId"
                FROM dmscs."EducationOrganizationSnapshot" AS s
                JOIN dmscs."EducationOrganizationSnapshotTarget" AS t
                    ON t."DataStoreId" = s."DataStoreId" AND t."ActiveGeneration" = s."Generation"
                WHERE s."DataStoreId" = @Id
                ORDER BY s."EducationOrganizationId";
                """,
                new { Id = dataStoreId }
            )
        ).ToList();

    /// <summary>Locks the target row on <paramref name="connection"/> without changing it.</summary>
    protected static Task HoldTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int dataStoreId
    ) => connection.ExecuteAsync(LockTargetSql, new { Id = dataStoreId }, transaction);

    /// <summary>
    /// Waits until a session named <paramref name="applicationName"/> waits for a lock, failing when
    /// <paramref name="operation"/> ends first or 10 seconds pass.
    /// </summary>
    protected async Task WaitUntilBlockedAsync(string applicationName, Task operation)
    {
        long started = Stopwatch.GetTimestamp();
        await using NpgsqlConnection observer = await DataSource!.OpenConnectionAsync();
        while (
            !await observer.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks AS l JOIN pg_stat_activity AS a ON a.pid = l.pid
                    WHERE a.application_name = @ApplicationName AND NOT l.granted);
                """,
                new { ApplicationName = applicationName }
            )
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
    /// Waits, on <paramref name="connection"/>, until the database clock has passed the target's lease expiry. The
    /// condition is evaluated by the database; nothing sleeps for a fixed time.
    /// </summary>
    protected static async Task WaitForLeaseExpiryAsync(NpgsqlConnection connection, int dataStoreId)
    {
        DateTime expiresAt = await connection.ExecuteScalarAsync<DateTime>(
            """SELECT "LeaseExpiresAt" FROM dmscs."EducationOrganizationSnapshotTarget" WHERE "DataStoreId" = @Id;""",
            new { Id = dataStoreId }
        );
        long started = Stopwatch.GetTimestamp();
        while (
            !await connection.ExecuteScalarAsync<bool>(
                """SELECT (clock_timestamp() AT TIME ZONE 'UTC') > @ExpiresAt;""",
                new { ExpiresAt = expiresAt }
            )
        )
        {
            Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(10));
            await Task.Delay(20);
        }
    }

    protected async Task<bool> LeaseIsLiveAsync(int dataStoreId) =>
        await Connection!.ExecuteScalarAsync<bool>(
            """
            SELECT "LeaseExpiresAt" > (clock_timestamp() AT TIME ZONE 'UTC')
            FROM dmscs."EducationOrganizationSnapshotTarget" WHERE "DataStoreId" = @Id;
            """,
            new { Id = dataStoreId }
        );

    /// <summary>Session hooks that hold every commit of a session until released.</summary>
    protected sealed class CommitPause
    {
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public CommitPause()
        {
            Hooks = new JobDatabaseSessionHooks
            {
                BeforeOperation = async (operation, _) =>
                {
                    if (operation == "Commit")
                    {
                        _reached.TrySetResult();
                        await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                    }
                },
            };
        }

        public JobDatabaseSessionHooks Hooks { get; }

        public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

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
                SELECT "EducationOrganizationId", "NameOfInstitution", "ShortNameOfInstitution", "Discriminator",
                    "ParentId", "SourceContractVersion", "TenantId"
                FROM dmscs."EducationOrganizationSnapshot" WHERE "DataStoreId" = @Id ORDER BY "EducationOrganizationId";
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
            EducationOrganizationProjectionItem[] items =
            [
                .. Enumerable
                    .Range(1, 50_000)
                    .Select(index => new EducationOrganizationProjectionItem(
                        index,
                        new string('n', 75),
                        index % 2 == 0 ? null : "short",
                        "edfi.School",
                        index % 3 == 0 ? null : index + 1_000_000L
                    )),
            ];

            await PublishAsync(id, items);

            (await OrganizationCountAsync(id)).Should().Be(50_000);
            (
                await Connection!.QuerySingleAsync<(string?, long?)>(
                    """
                    SELECT "ShortNameOfInstitution", "ParentId" FROM dmscs."EducationOrganizationSnapshot"
                    WHERE "DataStoreId" = @Id AND "EducationOrganizationId" = 49999;
                    """,
                    new { Id = id }
                )
            ).Should().Be(("short", 1_049_999L));
            (
                await Connection!.QuerySingleAsync<(string?, long?)>(
                    """
                    SELECT "ShortNameOfInstitution", "ParentId" FROM dmscs."EducationOrganizationSnapshot"
                    WHERE "DataStoreId" = @Id AND "EducationOrganizationId" = 50000;
                    """,
                    new { Id = id }
                )
            ).Should().Be(((string?)null, 1_050_000L));
        }
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
                    """SELECT "TenantId" FROM dmscs."EducationOrganizationSnapshot" WHERE "DataStoreId" = @Id;""",
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
    /// Another transaction holds the target row lock without changing the row while the lease expires, then
    /// releases it within the operation's lock wait. The operation must judge the lease after it gets the lock, by
    /// the statement clock. Its own transaction started before the wait, so reading the transaction start time
    /// (<c>now()</c>) instead would also be caught here.
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

            await using NpgsqlConnection holder = await DataSource!.OpenConnectionAsync();
            await using NpgsqlTransaction hold = await holder.BeginTransactionAsync();
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
            (await LeaseIsLiveAsync(id))
                .Should()
                .BeTrue("the operation must start waiting while the lease is live");

            await WaitForLeaseExpiryAsync(holder, id);
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
                        default
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
            await Connection!.ExecuteAsync(
                """DELETE FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                new { Id = id }
            );

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
            await using NpgsqlConnection deleter = await DataSource!.OpenConnectionAsync();
            await using NpgsqlTransaction delete = await deleter.BeginTransactionAsync();
            await deleter.ExecuteAsync(
                """DELETE FROM dmscs."DataStore" WHERE "Id" = @Id;""",
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
            await using NpgsqlConnection deleter = await DataSource!.OpenConnectionAsync();
            await using NpgsqlTransaction delete = await deleter.BeginTransactionAsync();
            await deleter.ExecuteAsync(
                """DELETE FROM dmscs."DataStore" WHERE "Id" = @Id;""",
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
    /// A trigger raises a deadlock (<c>40P01</c>) or lock timeout (<c>55P03</c>) inside each operation's write. The
    /// operations report it as a database failure carrying only the code and the operation name, and roll back.
    /// </summary>
    [TestFixture("40P01")]
    [TestFixture("55P03")]
    public class Given_a_forced_database_error(string sqlState) : EducationOrganizationSnapshotWriteTestBase
    {
        private int _id;
        private EducationOrganizationSnapshotLease _lease = null!;

        [SetUp]
        public async Task Setup()
        {
            _id = await InsertDataStoreAsync("forced-" + sqlState);
            _lease = await AcquiredAsync(Repository(), _id);
            await Connection!.ExecuteAsync(
                $"""
                CREATE FUNCTION dmscs.raise_snapshot_test_error() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'snapshot-test' USING ERRCODE = '{sqlState}'; END $$;
                CREATE TRIGGER raise_snapshot_test_target BEFORE UPDATE ON dmscs."EducationOrganizationSnapshotTarget"
                FOR EACH ROW EXECUTE FUNCTION dmscs.raise_snapshot_test_error();
                CREATE TRIGGER raise_snapshot_test_rows BEFORE INSERT ON dmscs."EducationOrganizationSnapshot"
                FOR EACH ROW EXECUTE FUNCTION dmscs.raise_snapshot_test_error();
                """
            );
        }

        [TearDown]
        public async Task DropTriggers() =>
            await Connection!.ExecuteAsync(
                """
                DROP TRIGGER IF EXISTS raise_snapshot_test_target ON dmscs."EducationOrganizationSnapshotTarget";
                DROP TRIGGER IF EXISTS raise_snapshot_test_rows ON dmscs."EducationOrganizationSnapshot";
                DROP FUNCTION IF EXISTS dmscs.raise_snapshot_test_error();
                """
            );

        [Test]
        public async Task It_classifies_the_acquisition_failure()
        {
            // A data store refreshed for the first time: the target insert succeeds and the lease write fails.
            int first = await InsertDataStoreAsync("forced-first-" + sqlState);

            SnapshotAcquireResult result = await Repository()
                .AcquireAsync(first, NewOwner(), CancellationToken.None);

            JobFailureDiagnostic diagnostic = result
                .Should()
                .BeOfType<SnapshotAcquireResult.DbFailure>()
                .Subject.Diagnostic;
            (diagnostic.ProviderErrorCode, diagnostic.Operation).Should().Be((sqlState, "Acquire"));
            (await TargetCountAsync(first))
                .Should()
                .Be(0, "the target insert rolls back with the failed lease write");
        }

        [Test]
        public async Task It_classifies_the_renewal_failure()
        {
            TargetState before = await TargetAsync(_id);

            AssertFailure(await Repository().RenewAsync(_lease, CancellationToken.None), "Renew");
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
        public async Task It_classifies_the_release_failure() =>
            AssertFailure(await Repository().ReleaseAsync(_lease, CancellationToken.None), "Release");

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
                .Be((sqlState, "Activate"));
            (await TargetAsync(_id)).Should().Be(before);
        }

        private void AssertFailure(SnapshotTargetResult result, string operation)
        {
            JobFailureDiagnostic diagnostic = result
                .Should()
                .BeOfType<SnapshotTargetResult.DbFailure>()
                .Subject.Diagnostic;
            (diagnostic.ProviderErrorCode, diagnostic.Operation).Should().Be((sqlState, operation));
        }
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
    /// Npgsql keeps a completed transaction's connection and would run commands on it in autocommit. Activation must
    /// refuse such a transaction before any statement, so it can never publish outside the transaction it was given.
    /// The lease is live and staged, so an activation that ran would publish.
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

            await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
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

        [Test]
        public async Task It_bounds_a_renewal_whose_commit_stays_pending()
        {
            int id = await InsertDataStoreAsync("renew-deadline");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            (TargetState Target, List<string> Rows) before = await StateAsync(id);
            JobLeaseRepositoryTests.PendingOperation pending = new("Commit", TimeSpan.FromSeconds(3));

            long started = Stopwatch.GetTimestamp();
            SnapshotTargetResult result = await Repository(hooks: pending.Hooks, jobOptions: _shortRenewal)
                .RenewAsync(lease, CancellationToken.None);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.7), "the commit stays pending for 3 s");
            AssertDeadline(result, "Renew");
            (await pending.CleanupAsync())
                .Should()
                .NotBeNull("the commit ran with its token already cancelled");
            (await StateAsync(id)).Target.Should().Be(before.Target, "the pending commit never committed");
        }

        [Test]
        public async Task It_bounds_a_release_whose_commit_stays_pending()
        {
            int id = await InsertDataStoreAsync("release-deadline");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            TargetState before = await TargetAsync(id);
            JobLeaseRepositoryTests.PendingOperation pending = new("Commit", TimeSpan.FromSeconds(6));

            long started = Stopwatch.GetTimestamp();
            SnapshotTargetResult result = await Repository(hooks: pending.Hooks)
                .ReleaseAsync(lease, CancellationToken.None);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5.7), "the commit stays pending for 6 s");
            AssertDeadline(result, "Release");
            (await pending.CleanupAsync())
                .Should()
                .NotBeNull("the commit ran with its token already cancelled");
            (await TargetAsync(id)).Should().Be(before, "the lease is still held");
        }

        [Test]
        public async Task It_bounds_a_renewal_blocked_by_a_held_target_lock()
        {
            int id = await InsertDataStoreAsync("renew-lock-wait");
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), id);
            TargetState before = await TargetAsync(id);
            await using NpgsqlConnection holder = await DataSource!.OpenConnectionAsync();
            await using NpgsqlTransaction hold = await holder.BeginTransactionAsync();
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
                diagnostic.ProviderErrorCode == "55P03"
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
                // The lease write is held, ignoring its own cancellation, after the target row was inserted.
                BeforeOperation = async (operation, _) =>
                {
                    if (operation == "TakeLease")
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
    }
}
