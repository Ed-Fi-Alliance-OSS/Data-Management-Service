// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Pauses a tenant aggregate read before one of its statements, lets a write commit there, and checks the result.
/// </summary>
internal static class TenantAggregateReads
{
    /// <summary>The statement names after which a read can be paused: before (b), and before (c).</summary>
    public const string BeforeOrganizations = "ReadOrganizations";
    public const string BeforeUnlinkedManages = "ReadUnlinkedManages";

    /// <summary>
    /// Runs an aggregate read through <paramref name="repository"/>, holds it before the statement
    /// <paramref name="pausePoint"/>, runs <paramref name="write"/> to completion, then lets the read finish.
    /// </summary>
    public static async Task<IReadOnlyList<TenantAggregateDataStore>> ReadAroundAsync(
        Func<JobDatabaseSessionHooks, IEducationOrganizationSnapshotRepository> repository,
        string pausePoint,
        Func<Task> write
    )
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        JobDatabaseSessionHooks hooks = new()
        {
            BeforeOperation = async (operation, _) =>
            {
                if (operation == pausePoint)
                {
                    reached.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
            },
        };
        Task<SnapshotTenantAggregateResult> reading = repository(hooks)
            .GetTenantAggregateAsync(CancellationToken.None);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await write();
        }
        finally
        {
            release.TrySetResult();
        }

        return DataStoresOf(await reading);
    }

    public static IReadOnlyList<TenantAggregateDataStore> DataStoresOf(
        SnapshotTenantAggregateResult result
    ) => result.Should().BeOfType<SnapshotTenantAggregateResult.Success>().Subject.DataStores;

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
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("""DELETE FROM dmscs."DataStoreManage";""");
    }

    protected async Task<int> InsertDataStoreAsync(
        int id,
        string name,
        long? tenantId,
        string type = "Shared"
    ) =>
        await Connection!.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs."DataStore" ("Id", "Name", "DataStoreType", "Provider", "TenantId")
            OVERRIDING SYSTEM VALUE
            VALUES (@Id, @Name, @Type, 'postgresql', @TenantId)
            RETURNING "Id";
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
            INSERT INTO dmscs."DataStoreContext" ("DataStoreId", "ContextKey", "ContextValue")
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
            INSERT INTO dmscs."DataStoreManage" ("TenantId", "Name", "Template", "Status", "DataStoreId",
                "HistoricalDataStoreId", "TargetDatabaseName", "ExpectedSourceIdentity", "EndpointDescriptor",
                "CreateJobId", "CreatedAt", "UpdatedAt", "LastRefreshed", "LastModifiedDate")
            VALUES (@TenantId, @Name, @Template, @Status, @DataStoreId, @HistoricalDataStoreId, @Target,
                gen_random_uuid(), '[1,"postgresql"]', @Target, now(), now(), now(), now())
            RETURNING "Id";
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

    protected static DataStoreRepository DataStores(TenantContext? tenant = null) =>
        new(
            Configuration.DatabaseOptions,
            NullLogger<DataStoreRepository>.Instance,
            new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
            A.Fake<IConnectionStringReader>(),
            A.Fake<IDataStoreContextRepository>(),
            A.Fake<IDataStoreDerivativeRepository>(),
            new TestAuditContext(),
            new TenantContextProvider { Context = tenant ?? new TenantContext.NotMultitenant() }
        );

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
            await InsertContextAsync(Nine, "schoolYear", "2024");
            await InsertContextAsync(Nine, "SchoolYear", "x");
            await InsertContextAsync(_foreign, "schoolYear", "2023");
            // Bytes no encryption key can decrypt.
            await Connection!.ExecuteAsync(
                """UPDATE dmscs."DataStore" SET "ConnectionString" = '\x00deadbeef'::bytea WHERE "Id" = @Id;""",
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
                            new Dictionary<string, string> { ["schoolYear"] = "2024", ["SchoolYear"] = "x" }
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

            TenantAggregateReads
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

            TenantAggregateReads
                .DataStoresOf(await Repository().GetTenantAggregateAsync(CancellationToken.None))
                .ShouldMatch([
                    Unmanaged(single, "Single tenant store", Item(6)),
                    Unlinked(pending, "Single pending", "PendingCreate", published: false),
                ]);
        }

        [Test]
        public async Task It_is_empty_for_a_tenant_without_data_stores_or_records()
        {
            long tenant = await InsertTenantAsync();

            TenantAggregateReads
                .DataStoresOf(
                    await Repository(tenant: new TenantContext.Multitenant(tenant, "empty"))
                        .GetTenantAggregateAsync(CancellationToken.None)
                )
                .Should()
                .BeEmpty();
        }
    }

    /// <summary>
    /// A write that commits while an aggregate read is paused between its statements is wholly absent from that
    /// read, which returns exactly what a read before the write returned.
    /// </summary>
    [TestFixture]
    public class Given_a_write_that_commits_during_an_aggregate_read
        : EducationOrganizationSnapshotReadTestBase
    {
        [Test]
        public async Task It_returns_the_state_before_an_ordinary_delete(
            [Values(TenantAggregateReads.BeforeOrganizations, TenantAggregateReads.BeforeUnlinkedManages)]
                string pausePoint
        )
        {
            int deleted = await InsertDataStoreAsync(900000002, "Deleted store", null);
            int kept = await InsertDataStoreAsync(900000005, "Kept store", null);
            await PublishAsync(deleted, [Item(1), Item(2)]);
            await PublishAsync(kept, [Item(3)]);
            IReadOnlyList<TenantAggregateDataStore> before = TenantAggregateReads.DataStoresOf(
                await Repository().GetTenantAggregateAsync(CancellationToken.None)
            );
            before.ShouldMatch([
                Unmanaged(deleted, "Deleted store", Item(1), Item(2)),
                Unmanaged(kept, "Kept store", Item(3)),
            ]);

            IReadOnlyList<TenantAggregateDataStore> during = await TenantAggregateReads.ReadAroundAsync(
                hooks => Repository(hooks: hooks),
                pausePoint,
                async () =>
                    (await DataStores().DeleteDataStore(deleted))
                        .Should()
                        .BeOfType<DataStoreDeleteResult.Success>()
            );

            during.ShouldMatch(before, "the delete committed after the read's snapshot");
            (await OrganizationCountAsync(deleted)).Should().Be(0, "the delete cascaded");
            TenantAggregateReads
                .DataStoresOf(await Repository().GetTenantAggregateAsync(CancellationToken.None))
                .ShouldMatch([Unmanaged(kept, "Kept store", Item(3))]);
        }

        [Test]
        public async Task It_returns_the_state_before_an_activation_and_its_prune(
            [Values(TenantAggregateReads.BeforeOrganizations, TenantAggregateReads.BeforeUnlinkedManages)]
                string pausePoint
        )
        {
            int id = await InsertDataStoreAsync(900000002, "Refreshed store", null);
            await PublishAsync(id, [Item(1), Item(2)]);
            IReadOnlyList<TenantAggregateDataStore> before = TenantAggregateReads.DataStoresOf(
                await Repository().GetTenantAggregateAsync(CancellationToken.None)
            );
            before.ShouldMatch([Unmanaged(id, "Refreshed store", Item(1), Item(2))]);

            IReadOnlyList<TenantAggregateDataStore> during = await TenantAggregateReads.ReadAroundAsync(
                hooks => Repository(hooks: hooks),
                pausePoint,
                () => PublishAsync(id, [Item(3)])
            );

            during.ShouldMatch(before, "the activation and prune committed after the read's snapshot");
            (await GenerationsAsync(id)).Should().Equal([2L], "the prune removed the read generation");
            TenantAggregateReads
                .DataStoresOf(await Repository().GetTenantAggregateAsync(CancellationToken.None))
                .ShouldMatch([Unmanaged(id, "Refreshed store", Item(3))]);
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
            await using NpgsqlConnection first = await DataSource!.OpenConnectionAsync();
            await using NpgsqlTransaction firstHold = await first.BeginTransactionAsync();
            await DataStoreCatalog.AcquireSharedTenantGateAsync(firstHold, tenant, CancellationToken.None);
            await using NpgsqlConnection second = await DataSource!.OpenConnectionAsync();
            await using NpgsqlTransaction secondHold = await second.BeginTransactionAsync();

            Task sharing = DataStoreCatalog.AcquireSharedTenantGateAsync(
                secondHold,
                tenant,
                CancellationToken.None
            );
            await sharing.WaitAsync(TimeSpan.FromSeconds(4));

            string applicationName = NewApplicationName();
            Task<DataStoreDeleteResult> deleting = new DataStoreRepository(
                NamedOptions(applicationName),
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                A.Fake<IConnectionStringReader>(),
                A.Fake<IDataStoreContextRepository>(),
                A.Fake<IDataStoreDerivativeRepository>(),
                new TestAuditContext(),
                new TenantContextProvider { Context = new TenantContext.Multitenant(tenant, "gated") }
            ).DeleteDataStore(id);
            await WaitUntilBlockedAsync(applicationName, deleting);
            await firstHold.RollbackAsync();
            deleting.IsCompleted.Should().BeFalse("the other shared holder still holds off the writer");
            await secondHold.RollbackAsync();

            (await deleting).Should().BeOfType<DataStoreDeleteResult.Success>();
        }
    }
}
