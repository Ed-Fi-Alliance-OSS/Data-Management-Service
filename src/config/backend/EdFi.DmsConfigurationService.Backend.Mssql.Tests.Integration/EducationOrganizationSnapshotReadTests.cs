// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
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
/// A tenant aggregate read held before one of its operations, with the gate and target locks it has taken so far,
/// until the test releases it.
/// </summary>
public sealed class PausedAggregateRead
{
    /// <summary>The operations before which a read can be held: (b), (c), and the commit after (c).</summary>
    public const string BeforeOrganizations = "ReadOrganizations";
    public const string BeforeUnlinkedManages = "ReadUnlinkedManages";
    public const string BeforeCommit = "Commit";

    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PausedAggregateRead(
        Func<JobDatabaseSessionHooks, IEducationOrganizationSnapshotRepository> repository,
        string pausePoint
    )
    {
        JobDatabaseSessionHooks hooks = new()
        {
            BeforeOperation = async (operation, _) =>
            {
                if (operation == pausePoint)
                {
                    _reached.TrySetResult();
                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
                }
            },
        };
        Result = repository(hooks).GetTenantAggregateAsync(CancellationToken.None);
    }

    public Task<SnapshotTenantAggregateResult> Result { get; }

    /// <summary>Starts a read and returns once it is held before <paramref name="pausePoint"/>.</summary>
    public static async Task<PausedAggregateRead> StartAsync(
        Func<JobDatabaseSessionHooks, IEducationOrganizationSnapshotRepository> repository,
        string pausePoint
    )
    {
        PausedAggregateRead read = new(repository, pausePoint);
        await read._reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return read;
    }

    public void Release() => _release.TrySetResult();

    /// <summary>Releases the read and returns its data stores.</summary>
    public async Task<IReadOnlyList<TenantAggregateDataStore>> FinishAsync()
    {
        Release();
        return DataStoresOf(await Result);
    }

    public static IReadOnlyList<TenantAggregateDataStore> DataStoresOf(
        SnapshotTenantAggregateResult result
    ) => result.Should().BeOfType<SnapshotTenantAggregateResult.Success>().Subject.DataStores;

    /// <summary>
    /// Waits until a session of the database that <paramref name="open"/> connects to waits for an application
    /// lock, such as the tenant gate, failing when <paramref name="writer"/> ends first or 10 seconds pass.
    /// </summary>
    public static async Task WaitUntilGateWaiterAsync(Func<Task<SqlConnection>> open, Task writer)
    {
        long started = Stopwatch.GetTimestamp();
        await using SqlConnection observer = await open();
        while (
            await observer.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = N'APPLICATION' AND request_status = N'WAIT'
                  AND resource_database_id = DB_ID();
                """
            ) == 0
        )
        {
            writer.IsCompleted.Should().BeFalse("the writer must wait for the read's tenant gate");
            Stopwatch
                .GetElapsedTime(started)
                .Should()
                .BeLessThan(TimeSpan.FromSeconds(10), "the writer must reach the tenant gate");
            await Task.Yield();
        }
    }
}

internal static class TenantAggregateAssertions
{
    /// <summary>Every entry, field and organization equal, in the same order.</summary>
    public static void ShouldMatch(
        this IReadOnlyList<TenantAggregateDataStore> actual,
        IReadOnlyList<TenantAggregateDataStore> expected,
        string because = ""
    ) =>
        actual
            .Should()
            .BeEquivalentTo(
                expected,
                options => options.WithStrictOrdering().ComparingRecordsByMembers(),
                because
            );
}

/// <summary>
/// Shared setup for the snapshot read fixtures: data stores with chosen identifiers, management records, and an
/// ordinary data store repository.
/// </summary>
public abstract class EducationOrganizationSnapshotReadTestBase : EducationOrganizationSnapshotWriteTestBase
{
    protected sealed record ManageRecord(int Id, string TargetDatabaseName);

    // The management table is outside the Respawn set, and a linked record keeps its data store from being deleted.
    [TearDown]
    public async Task DeleteManagementRecords()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM dmscs.DataStoreManage;");
    }

    protected async Task<int> InsertDataStoreAsync(
        int id,
        string name,
        long? tenantId,
        string type = "Shared"
    ) =>
        await Connection!.ExecuteScalarAsync<int>(
            """
            SET IDENTITY_INSERT dmscs.DataStore ON;
            INSERT INTO dmscs.DataStore (Id, Name, DataStoreType, Provider, TenantId)
            OUTPUT inserted.Id
            VALUES (@Id, @Name, @Type, N'sqlserver', @TenantId);
            SET IDENTITY_INSERT dmscs.DataStore OFF;
            """,
            new
            {
                Id = id,
                Name = name,
                Type = type,
                TenantId = tenantId,
            }
        );

    protected Task InsertContextAsync(int dataStoreId, string key, string value) =>
        Connection!.ExecuteAsync(
            """
            INSERT INTO dmscs.DataStoreContext (DataStoreId, ContextKey, ContextValue)
            VALUES (@DataStoreId, @Key, @Value);
            """,
            new
            {
                DataStoreId = dataStoreId,
                Key = key,
                Value = value,
            }
        );

    protected async Task<ManageRecord> InsertManageAsync(
        string name,
        string status,
        int? dataStoreId,
        int? historicalDataStoreId,
        long? tenantId,
        string template = "Minimal"
    )
    {
        string target = "edfi_" + Guid.NewGuid().ToString("N");
        int id = await Connection!.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStoreManage (TenantId, Name, Template, Status, DataStoreId, HistoricalDataStoreId,
                TargetDatabaseName, ExpectedSourceIdentity, EndpointDescriptor, CreateJobId, CreatedAt, UpdatedAt,
                LastRefreshed, LastModifiedDate)
            OUTPUT inserted.Id
            VALUES (@TenantId, @Name, @Template, @Status, @DataStoreId, @HistoricalDataStoreId, @Target, NEWID(),
                N'[1,"mssql"]', @Target, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            new
            {
                TenantId = tenantId,
                Name = name,
                Template = template,
                Status = status,
                DataStoreId = dataStoreId,
                HistoricalDataStoreId = historicalDataStoreId,
                Target = target,
            }
        );
        return new(id, target);
    }

    protected static DataStoreRepository DataStores(
        TenantContext? tenant = null,
        IOptions<DatabaseOptions>? databaseOptions = null
    )
    {
        databaseOptions ??= MssqlTestConfiguration.DatabaseOptions;
        return new(
            databaseOptions,
            NullLogger<DataStoreRepository>.Instance,
            new ConnectionStringEncryptionService(databaseOptions),
            A.Fake<IConnectionStringReader>(),
            A.Fake<IDataStoreContextRepository>(),
            A.Fake<IDataStoreDerivativeRepository>(),
            new TestAuditContext(),
            new TenantContextProvider { Context = tenant ?? new TenantContext.NotMultitenant() }
        );
    }

    protected static EducationOrganizationProjectionItem Organization(
        long id,
        string name,
        string? shortName,
        string discriminator,
        long? parentId
    ) => new(id, name, shortName, discriminator, parentId);

    protected static TenantAggregateDataStore Unmanaged(
        int id,
        string name,
        params EducationOrganizationProjectionItem[] organizations
    ) => new(id, null, name, "Shared", "Created", null, null, organizations);

    /// <summary>A management record linked to no data store; a published one reports its database name.</summary>
    protected static TenantAggregateDataStore Unlinked(
        ManageRecord record,
        string name,
        string status,
        bool published
    ) =>
        new(null, record.Id, name, null, status, "Minimal", published ? record.TargetDatabaseName : null, []);

    protected static Task<IReadOnlyList<TenantAggregateDataStore>> AggregateAsync(
        TenantContext? tenant = null
    ) =>
        Repository(tenant: tenant)
            .GetTenantAggregateAsync(CancellationToken.None)
            .ContinueWith(read => PausedAggregateRead.DataStoresOf(read.Result), TaskScheduler.Default);

    protected static Task<PausedAggregateRead> PausedReadAsync(string pausePoint) =>
        PausedAggregateRead.StartAsync(hooks => Repository(hooks: hooks), pausePoint);

    protected static Task WaitUntilGateWaiterAsync(Task writer) =>
        PausedAggregateRead.WaitUntilGateWaiterAsync(OpenConnectionAsync, writer);
}

public class EducationOrganizationSnapshotReadTests
{
    [TestFixture]
    public class Given_the_refresh_targets_of_a_tenant : EducationOrganizationSnapshotReadTestBase
    {
        // Numeric order differs from both text order and insertion order.
        private const int Ten = 900000010;
        private const int Two = 90000002;
        private const int Nine = 900000009;

        private long _tenant;
        private int _foreign;

        [SetUp]
        public async Task Seed()
        {
            _tenant = await InsertTenantAsync();
            long other = await InsertTenantAsync();
            await InsertDataStoreAsync(Ten, "targets-ten", _tenant);
            await InsertDataStoreAsync(Two, "targets-two", _tenant);
            await InsertDataStoreAsync(Nine, "targets-nine", _tenant);
            _foreign = await InsertDataStoreAsync(900000001, "targets-foreign", other);
            await InsertDataStoreAsync(900000003, "targets-single-tenant", null);
            await InsertContextAsync(Ten, "schoolYear", "2025");
            await InsertContextAsync(Ten, "District", "255901");
            // Context keys are unique per data store under the database collation, which ignores case.
            await InsertContextAsync(Nine, "schoolYear", "2024");
            await InsertContextAsync(Nine, "Region", "x");
            await InsertContextAsync(_foreign, "schoolYear", "2023");
            // Bytes no encryption key can decrypt.
            await Connection!.ExecuteAsync(
                "UPDATE dmscs.DataStore SET ConnectionString = 0x00DEADBEEF WHERE Id = @Id;",
                new { Id = Ten }
            );
        }

        private EducationOrganizationSnapshotRepository TenantRepository() =>
            Repository(tenant: new TenantContext.Multitenant(_tenant, "targets"));

        [Test]
        public async Task It_lists_every_data_store_of_the_tenant_in_numeric_order_with_its_exact_contexts()
        {
            SnapshotRefreshTargetsResult result = await TenantRepository()
                .GetRefreshTargetsAsync(null, CancellationToken.None);

            IReadOnlyList<EducationOrganizationRefreshTarget> targets = result
                .Should()
                .BeOfType<SnapshotRefreshTargetsResult.Success>()
                .Subject.Targets;
            targets.Select(target => target.DataStoreId).Should().Equal(Two, Nine, Ten);
            // Contexts are compared by key; a dictionary's enumeration order carries no meaning.
            targets
                .Should()
                .BeEquivalentTo(
                    new EducationOrganizationRefreshTarget[]
                    {
                        new(Two, new Dictionary<string, string>()),
                        new(
                            Nine,
                            new Dictionary<string, string> { ["schoolYear"] = "2024", ["Region"] = "x" }
                        ),
                        new(
                            Ten,
                            new Dictionary<string, string>
                            {
                                ["District"] = "255901",
                                ["schoolYear"] = "2025",
                            }
                        ),
                    },
                    options => options.ComparingRecordsByMembers()
                );
        }

        [Test]
        public async Task It_lists_only_the_requested_data_store_of_the_tenant()
        {
            EducationOrganizationSnapshotRepository repository = TenantRepository();

            (await repository.GetRefreshTargetsAsync(Nine, CancellationToken.None))
                .Should()
                .BeOfType<SnapshotRefreshTargetsResult.Success>()
                .Subject.Targets.Select(target => target.DataStoreId)
                .Should()
                .Equal(Nine);
            (await repository.GetRefreshTargetsAsync(_foreign, CancellationToken.None))
                .Should()
                .BeOfType<SnapshotRefreshTargetsResult.Success>()
                .Subject.Targets.Should()
                .BeEmpty("another tenant's data store is absent");
            (await repository.GetRefreshTargetsAsync(900000099, CancellationToken.None))
                .Should()
                .BeOfType<SnapshotRefreshTargetsResult.Success>()
                .Subject.Targets.Should()
                .BeEmpty();
        }

        [Test]
        public async Task It_lists_only_the_single_tenant_partition_to_a_single_tenant_caller()
        {
            (await Repository().GetRefreshTargetsAsync(null, CancellationToken.None))
                .Should()
                .BeOfType<SnapshotRefreshTargetsResult.Success>()
                .Subject.Targets.Select(target => target.DataStoreId)
                .Should()
                .Equal(900000003);
        }

        [Test]
        public async Task It_finds_a_data_store_only_in_its_own_tenant()
        {
            EducationOrganizationSnapshotRepository repository = TenantRepository();

            (await repository.DataStoreExistsAsync(Two, CancellationToken.None))
                .Should()
                .Be(new SnapshotDataStoreExistsResult.Found());
            (await repository.DataStoreExistsAsync(_foreign, CancellationToken.None))
                .Should()
                .Be(new SnapshotDataStoreExistsResult.NotFound());
            (await repository.DataStoreExistsAsync(900000003, CancellationToken.None))
                .Should()
                .Be(new SnapshotDataStoreExistsResult.NotFound());
            (await repository.DataStoreExistsAsync(900000099, CancellationToken.None))
                .Should()
                .Be(new SnapshotDataStoreExistsResult.NotFound());
        }
    }

    [TestFixture]
    public class Given_a_tenant_aggregate : EducationOrganizationSnapshotReadTestBase
    {
        private const int Ten = 900000010;
        private const int Two = 90000002;
        private const int Nine = 900000009;

        [Test]
        public async Task It_lists_the_data_stores_then_the_unlinked_management_records_in_numeric_order()
        {
            long tenant = await InsertTenantAsync();
            long other = await InsertTenantAsync();
            TenantContext context = new TenantContext.Multitenant(tenant, "aggregate");
            EducationOrganizationSnapshotRepository repository = Repository(tenant: context);

            await InsertDataStoreAsync(Ten, "Store ten", tenant);
            await InsertDataStoreAsync(Two, "Store two", tenant, "Dedicated");
            await InsertDataStoreAsync(Nine, "Store nine", tenant);
            ManageRecord linked = await InsertManageAsync(
                "Store two",
                "DeleteFailed",
                Two,
                Two,
                tenant,
                "Sample"
            );
            // Inserted before the pending record, so its identifier is lower although its name sorts after.
            ManageRecord deleted = await InsertManageAsync("Zeta retired", "Deleted", null, 12345, tenant);
            ManageRecord pending = await InsertManageAsync(
                "Alpha pending",
                "PendingCreate",
                null,
                null,
                tenant
            );

            // Store ten: generation 1 superseded, generation 2 active and unpruned, generation 3 staged only.
            await PublishAsync(Ten, [Item(1), Item(2)], prune: false, repository: repository);
            await PublishAsync(Ten, [Item(3)], prune: false, repository: repository);
            EducationOrganizationSnapshotLease staged = await AcquiredAsync(repository, Ten);
            (await repository.StageAsync(staged, [Item(4)], ContractVersion, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(1));
            EducationOrganizationProjectionItem[] storeTwo =
            [
                Organization(10, "Ten Elementary", "TE", "edfi.School", 2),
                Organization(2147483649, "Beyond Int32", null, "edfi.School", null),
                Organization(2, "District Two", "D2", "edfi.LocalEducationAgency", 9),
                Organization(9007199254740993, "Beyond Double", "BD", "edfi.School", long.MaxValue - 1),
                Organization(9, "Service Center", null, "edfi.EducationServiceCenter", null),
                Organization(long.MaxValue - 1, "Near Int64 Max", null, "edfi.StateEducationAgency", null),
            ];
            await PublishAsync(Two, storeTwo, repository: repository);

            // Absent from the tenant's aggregate: another tenant's store, snapshot and record, and the single-tenant ones.
            TenantContext otherContext = new TenantContext.Multitenant(other, "other");
            int foreign = await InsertDataStoreAsync(900000001, "Foreign store", other);
            await PublishAsync(foreign, [Item(5)], repository: Repository(tenant: otherContext));
            await InsertManageAsync("Foreign pending", "PendingCreate", null, null, other);
            await InsertDataStoreAsync(900000003, "Single tenant store", null);
            await InsertManageAsync("Single pending", "PendingCreate", null, null, null);

            PausedAggregateRead
                .DataStoresOf(await repository.GetTenantAggregateAsync(CancellationToken.None))
                .ShouldMatch([
                    new(
                        Two,
                        linked.Id,
                        "Store two",
                        "Dedicated",
                        "DeleteFailed",
                        "Sample",
                        linked.TargetDatabaseName,
                        [storeTwo[2], storeTwo[4], storeTwo[0], storeTwo[1], storeTwo[3], storeTwo[5]]
                    ),
                    Unmanaged(Nine, "Store nine"),
                    Unmanaged(Ten, "Store ten", Item(3)),
                    Unlinked(deleted, "Zeta retired", "Deleted", published: true),
                    Unlinked(pending, "Alpha pending", "PendingCreate", published: false),
                ]);
        }

        [Test]
        public async Task It_lists_only_the_single_tenant_partition_to_a_single_tenant_caller()
        {
            long tenant = await InsertTenantAsync();
            int foreign = await InsertDataStoreAsync(900000001, "Tenant store", tenant);
            await PublishAsync(
                foreign,
                [Item(5)],
                repository: Repository(tenant: new TenantContext.Multitenant(tenant, "tenant"))
            );
            await InsertManageAsync("Tenant pending", "PendingCreate", null, null, tenant);
            int single = await InsertDataStoreAsync(900000003, "Single tenant store", null);
            await PublishAsync(single, [Item(6)]);
            ManageRecord pending = await InsertManageAsync(
                "Single pending",
                "PendingCreate",
                null,
                null,
                null
            );

            (await AggregateAsync()).ShouldMatch([
                Unmanaged(single, "Single tenant store", Item(6)),
                Unlinked(pending, "Single pending", "PendingCreate", published: false),
            ]);
        }

        [Test]
        public async Task It_is_empty_for_a_tenant_without_data_stores_or_records()
        {
            long tenant = await InsertTenantAsync();

            (await AggregateAsync(new TenantContext.Multitenant(tenant, "empty"))).Should().BeEmpty();
        }
    }

    /// <summary>
    /// The read holds the tenant gate in shared mode, so an ordinary data store writer that arrives during it waits:
    /// it completes after the read when the read ends within the writer's 5-second gate wait, and otherwise fails and
    /// rolls back. Either way the read returns exactly the state before the write.
    /// </summary>
    [TestFixture]
    public class Given_an_ordinary_delete_during_an_aggregate_read : EducationOrganizationSnapshotReadTestBase
    {
        private int _deleted;
        private int _kept;
        private IReadOnlyList<TenantAggregateDataStore> _before = [];

        [SetUp]
        public async Task Seed()
        {
            _deleted = await InsertDataStoreAsync(900000002, "Deleted store", null);
            _kept = await InsertDataStoreAsync(900000005, "Kept store", null);
            await PublishAsync(_deleted, [Item(1), Item(2)]);
            await PublishAsync(_kept, [Item(3)]);
            _before = await AggregateAsync();
            _before.ShouldMatch([
                Unmanaged(_deleted, "Deleted store", Item(1), Item(2)),
                Unmanaged(_kept, "Kept store", Item(3)),
            ]);
        }

        [Test]
        public async Task It_completes_the_delete_after_the_read_and_returns_the_state_before_it(
            [Values(PausedAggregateRead.BeforeOrganizations, PausedAggregateRead.BeforeUnlinkedManages)]
                string pausePoint
        )
        {
            PausedAggregateRead read = await PausedReadAsync(pausePoint);
            Task<DataStoreDeleteResult> deleting;
            try
            {
                deleting = DataStores().DeleteDataStore(_deleted);
                await WaitUntilGateWaiterAsync(deleting);
            }
            finally
            {
                read.Release();
            }

            (await read.FinishAsync()).ShouldMatch(_before, "the delete waited for the read");
            (await deleting).Should().BeOfType<DataStoreDeleteResult.Success>();
            (await OrganizationCountAsync(_deleted)).Should().Be(0, "the delete cascaded");
            (await AggregateAsync()).ShouldMatch([Unmanaged(_kept, "Kept store", Item(3))]);
        }

        [Test]
        public async Task It_rolls_back_a_delete_that_waits_past_its_gate_bound()
        {
            var stateBefore = await StateAsync(_deleted);
            PausedAggregateRead read = await PausedReadAsync(PausedAggregateRead.BeforeCommit);
            DataStoreDeleteResult deleted;
            TimeSpan waited;
            try
            {
                long started = Stopwatch.GetTimestamp();
                deleted = await DataStores().DeleteDataStore(_deleted);
                waited = Stopwatch.GetElapsedTime(started);
                read.Result.IsCompleted.Should().BeFalse("the read still holds the gate");
            }
            finally
            {
                read.Release();
            }

            deleted.Should().BeOfType<DataStoreDeleteResult.FailureUnknown>();
            waited
                .Should()
                .BeGreaterThan(TimeSpan.FromSeconds(4.5))
                .And.BeLessThan(
                    TimeSpan.FromSeconds(15),
                    "the writer gives up after its 5-second gate bound"
                );
            (await read.FinishAsync()).ShouldMatch(_before);
            (await StateAsync(_deleted)).Should().BeEquivalentTo(stateBefore, "the delete rolled back");
            (await AggregateAsync()).ShouldMatch(_before, "the data store and its snapshot remain");
        }
    }

    /// <summary>
    /// The read keeps a shared lock on each target row it read until it ends. An activation's update lock is
    /// compatible with it, but its update waits, so the read returns the generation that was active when it started.
    /// </summary>
    [TestFixture]
    public class Given_an_activation_during_an_aggregate_read : EducationOrganizationSnapshotReadTestBase
    {
        [Test]
        public async Task It_publishes_after_the_read_and_returns_the_generation_before_it(
            [Values(PausedAggregateRead.BeforeOrganizations, PausedAggregateRead.BeforeUnlinkedManages)]
                string pausePoint
        )
        {
            int id = await InsertDataStoreAsync(900000002, "Refreshed store", null);
            await PublishAsync(id, [Item(1), Item(2)]);
            IReadOnlyList<TenantAggregateDataStore> before = await AggregateAsync();
            before.ShouldMatch([Unmanaged(id, "Refreshed store", Item(1), Item(2))]);
            EducationOrganizationSnapshotRepository repository = Repository();
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, id);
            (await repository.StageAsync(lease, [Item(3)], ContractVersion, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(1));
            string applicationName = NewApplicationName();
            IJobFence fence = await FenceAsync(NamedOptions(applicationName));

            PausedAggregateRead read = await PausedReadAsync(pausePoint);
            Task<SnapshotTargetResult> activating;
            try
            {
                activating = ActivateAsync(repository, fence, lease);
                await WaitUntilBlockedAsync(applicationName, activating);
            }
            finally
            {
                read.Release();
            }

            (await read.FinishAsync()).ShouldMatch(before, "the activation waited for the read");
            (await activating).Should().Be(new SnapshotTargetResult.Success(1));
            (await repository.PruneAsync(id, CancellationToken.None))
                .Should()
                .Be(new SnapshotTargetResult.Success(2));
            (await AggregateAsync()).ShouldMatch([Unmanaged(id, "Refreshed store", Item(3))]);
        }
    }

    [TestFixture]
    public class Given_the_shared_tenant_gate : EducationOrganizationSnapshotReadTestBase
    {
        [Test]
        public async Task It_admits_other_shared_holders_and_holds_off_a_catalog_writer()
        {
            long tenant = await InsertTenantAsync();
            int id = await InsertDataStoreAsync(900000002, "Gated store", tenant);
            await using SqlConnection first = await OpenConnectionAsync();
            await using SqlTransaction firstHold = (SqlTransaction)await first.BeginTransactionAsync();
            short firstSession = await SessionIdAsync(first, firstHold);
            await DataStoreCatalog.AcquireSharedTenantGateAsync(firstHold, tenant, CancellationToken.None);
            await using SqlConnection second = await OpenConnectionAsync();
            await using SqlTransaction secondHold = (SqlTransaction)await second.BeginTransactionAsync();
            short secondSession = await SessionIdAsync(second, secondHold);

            await DataStoreCatalog
                .AcquireSharedTenantGateAsync(secondHold, tenant, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(4));

            string applicationName = NewApplicationName();
            Task<DataStoreDeleteResult> deleting = DataStores(
                    new TenantContext.Multitenant(tenant, "gated"),
                    NamedOptions(applicationName)
                )
                .DeleteDataStore(id);
            await WaitUntilBlockedAsync(applicationName, deleting);
            (await HoldersOfAwaitedGateAsync(applicationName))
                .Should()
                .Equal(
                    new[] { firstSession, secondSession }.Order(),
                    "the writer waits for the gate both readers share"
                );

            await firstHold.RollbackAsync();
            (await HoldersOfAwaitedGateAsync(applicationName))
                .Should()
                .Equal([secondSession], "the other shared holder still holds off the writer");
            deleting.IsCompleted.Should().BeFalse("the writer is still waiting for the gate");
            await secondHold.RollbackAsync();

            (await deleting).Should().BeOfType<DataStoreDeleteResult.Success>();
        }

        private static Task<short> SessionIdAsync(SqlConnection connection, SqlTransaction transaction) =>
            connection.ExecuteScalarAsync<short>(
                "SELECT CAST(@@SPID AS smallint);",
                transaction: transaction
            );

        /// <summary>
        /// The sessions granted the application lock that the session named <paramref name="applicationName"/>
        /// waits for, in ascending order, read from the lock table rather than inferred from elapsed time.
        /// </summary>
        private static async Task<short[]> HoldersOfAwaitedGateAsync(string applicationName)
        {
            await using SqlConnection observer = await OpenConnectionAsync();
            return
            [
                .. await observer.QueryAsync<short>(
                    """
                    SELECT CAST(holder.request_session_id AS smallint)
                    FROM sys.dm_tran_locks AS waiter
                    INNER JOIN sys.dm_exec_sessions AS session ON session.session_id = waiter.request_session_id
                    INNER JOIN sys.dm_tran_locks AS holder
                        ON holder.resource_type = waiter.resource_type
                        AND holder.resource_database_id = waiter.resource_database_id
                        AND holder.resource_description = waiter.resource_description
                        AND holder.request_status = N'GRANT'
                    WHERE session.program_name = @ApplicationName
                      AND waiter.resource_type = N'APPLICATION' AND waiter.request_status = N'WAIT'
                    ORDER BY holder.request_session_id;
                    """,
                    new { ApplicationName = applicationName }
                ),
            ];
        }

        [Test]
        public async Task It_lets_aggregate_reads_of_one_tenant_run_together()
        {
            int id = await InsertDataStoreAsync(900000002, "Shared store", null);
            await PublishAsync(id, [Item(1)]);

            PausedAggregateRead first = await PausedReadAsync(PausedAggregateRead.BeforeCommit);
            PausedAggregateRead second;
            try
            {
                second = await PausedReadAsync(PausedAggregateRead.BeforeCommit);
            }
            finally
            {
                first.Release();
            }

            IReadOnlyList<TenantAggregateDataStore> expected = [Unmanaged(id, "Shared store", Item(1))];
            (await first.FinishAsync()).ShouldMatch(expected);
            (await second.FinishAsync()).ShouldMatch(expected);
        }
    }

    /// <summary>
    /// Large stages, reclaims and prunes hold row locks on many rows of their data store until they commit. An
    /// aggregate read seeks only the active generations, which none of them changes, so it neither waits for them nor
    /// sees their rows.
    /// </summary>
    [TestFixture]
    public class Given_a_large_snapshot_operation_during_an_aggregate_read
        : EducationOrganizationSnapshotReadTestBase
    {
        private const int LargeCount = 50_000;

        [Test]
        public async Task It_reads_the_active_generations_without_waiting(
            [Values("stage", "reclaim", "prune")] string operation
        )
        {
            int large = await InsertDataStoreAsync(900000002, "Large store", null);
            int other = await InsertDataStoreAsync(900000005, "Other store", null);
            await PublishAsync(other, [Item(7), Item(8)]);
            EducationOrganizationProjectionItem[] largeSet =
                EducationOrganizationSnapshotWriteTests.Given_a_target_refreshed_from_one_set_to_another.LargeSet(
                    LargeCount
                );
            CommitPause pause = new(TimeSpan.FromMinutes(2));
            EducationOrganizationSnapshotRepository paused = Repository(hooks: pause.Hooks);
            Task<SnapshotTargetResult> running = operation switch
            {
                "stage" => await StartLargeStageAsync(large, largeSet, paused),
                "reclaim" => await StartLargeReclaimAsync(large, largeSet, paused),
                _ => await StartLargePruneAsync(large, largeSet, paused),
            };

            IReadOnlyList<TenantAggregateDataStore> during;
            TimeSpan elapsed;
            try
            {
                await pause.Reached;
                long started = Stopwatch.GetTimestamp();
                during = await AggregateAsync();
                elapsed = Stopwatch.GetElapsedTime(started);
            }
            finally
            {
                pause.Release();
            }

            await TestContext.Out.WriteLineAsync(
                $"Aggregate read during a paused {LargeCount}-row {operation}: {elapsed.TotalMilliseconds:F0} ms"
            );
            during.ShouldMatch([
                Unmanaged(large, "Large store", Item(1)),
                Unmanaged(other, "Other store", Item(7), Item(8)),
            ]);
            (await running).Should().Be(new SnapshotTargetResult.Success(LargeCount));
        }

        // Generation 1 holds Item(1) and is active; generation 2 stages the large set.
        private async Task<Task<SnapshotTargetResult>> StartLargeStageAsync(
            int large,
            EducationOrganizationProjectionItem[] largeSet,
            EducationOrganizationSnapshotRepository paused
        )
        {
            await PublishAsync(large, [Item(1)]);
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(Repository(), large);
            return paused.StageAsync(lease, largeSet, ContractVersion, CancellationToken.None);
        }

        // Generation 1 holds Item(1) and is active; abandoned generation 2 holds the large set, which generation 3
        // reclaims.
        private async Task<Task<SnapshotTargetResult>> StartLargeReclaimAsync(
            int large,
            EducationOrganizationProjectionItem[] largeSet,
            EducationOrganizationSnapshotRepository paused
        )
        {
            EducationOrganizationSnapshotRepository repository = Repository();
            await PublishAsync(large, [Item(1)]);
            EducationOrganizationSnapshotLease abandoned = await AcquiredAsync(repository, large);
            SnapshotTargetResult staged = await repository.StageAsync(
                abandoned,
                largeSet,
                ContractVersion,
                CancellationToken.None
            );
            staged.Should().Be(new SnapshotTargetResult.Success(LargeCount));
            await SetLeaseExpiryAsync(large, -1);
            EducationOrganizationSnapshotLease lease = await AcquiredAsync(repository, large);
            return paused.ReclaimAsync(lease, CancellationToken.None);
        }

        // Superseded generation 1 holds the large set, which the prune removes; generation 2 holds Item(1) and is active.
        private async Task<Task<SnapshotTargetResult>> StartLargePruneAsync(
            int large,
            EducationOrganizationProjectionItem[] largeSet,
            EducationOrganizationSnapshotRepository paused
        )
        {
            await PublishAsync(large, largeSet, prune: false);
            await PublishAsync(large, [Item(1)], prune: false);
            return paused.PruneAsync(large, CancellationToken.None);
        }
    }

    /// <summary>
    /// Measures how long an aggregate read of 50,000 published organizations holds the shared gate, and how long each
    /// of its operations takes. The gate is held until the client has read every organization, so the hold depends on
    /// the network and client as well as the server; it is reported, not asserted. Run it with
    /// <c>--filter Category=OperationalProbe</c>.
    /// </summary>
    [TestFixture]
    [Category("OperationalProbe")]
    [Explicit("Measurement; run manually with --filter Category=OperationalProbe.")]
    public class Given_a_large_tenant_aggregate : EducationOrganizationSnapshotReadTestBase
    {
        private const int Stores = 10;
        private const int PerStore = 5_000;

        [Test]
        public async Task It_reports_how_long_the_read_holds_the_shared_gate()
        {
            EducationOrganizationProjectionItem[] set =
                EducationOrganizationSnapshotWriteTests.Given_a_target_refreshed_from_one_set_to_another.LargeSet(
                    PerStore
                );
            for (int index = 0; index < Stores; index++)
            {
                int id = await InsertDataStoreAsync(900000100 + index, $"Large store {index}", null);
                await PublishAsync(id, set);
            }

            List<TimeSpan> holds = [];
            for (int run = 0; run < 3; run++)
            {
                Dictionary<string, long> started = [];
                Dictionary<string, long> ended = [];
                JobDatabaseSessionHooks hooks = new()
                {
                    BeforeOperation = (operation, _) =>
                    {
                        started[operation] = Stopwatch.GetTimestamp();
                        return Task.CompletedTask;
                    },
                    AfterOperation = (operation, _) =>
                    {
                        ended[operation] = Stopwatch.GetTimestamp();
                        return Task.CompletedTask;
                    },
                };

                IReadOnlyList<TenantAggregateDataStore> aggregate = PausedAggregateRead.DataStoresOf(
                    await Repository(hooks: hooks).GetTenantAggregateAsync(CancellationToken.None)
                );

                aggregate.Should().HaveCount(Stores);
                aggregate.Sum(entry => entry.EducationOrganizations.Count).Should().Be(Stores * PerStore);
                TimeSpan hold = Stopwatch.GetElapsedTime(ended["AcquireTenantGate"], ended["Commit"]);
                holds.Add(hold);
                await TestContext.Out.WriteLineAsync(
                    $"Run {run + 1}: gate held {hold.TotalMilliseconds:F0} ms; "
                        + string.Join(
                            ", ",
                            started
                                .Keys.Where(ended.ContainsKey)
                                .Select(operation =>
                                    $"{operation} {Stopwatch.GetElapsedTime(started[operation], ended[operation]).TotalMilliseconds:F0} ms"
                                )
                        )
                );
            }

            await TestContext.Out.WriteLineAsync(
                $"Shared gate hold for {Stores * PerStore} organizations in {Stores} data stores: "
                    + string.Join(", ", holds.Select(hold => $"{hold.TotalMilliseconds:F0} ms"))
            );
        }
    }
}
