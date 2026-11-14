// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DataStoreNamespace")]
public partial class Given_data_store_namespace_reservations
{
    private JobUpgradeTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task Deploy()
    {
        _database = new();
        _database.DeployThrough(41);
        await Task.CompletedTask;
    }

    [SetUp]
    public async Task Clear()
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """DELETE FROM dmscs."DataStoreManage"; DELETE FROM dmscs."Job"; DELETE FROM dmscs."DataStore"; DELETE FROM dmscs."Vendor"; DELETE FROM dmscs."Tenant";"""
        );
    }

    [OneTimeTearDown]
    public Task Drop() => _database is null ? Task.CompletedTask : _database.DropAsync();

    private async Task<NpgsqlConnection> Open(string? applicationName = null)
    {
        NpgsqlConnectionStringBuilder builder = new(_database.ConnectionString);
        if (applicationName is not null)
        {
            builder.ApplicationName = applicationName;
        }
        NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private DataStoreRepository Repository(
        long? tenantId = null,
        string? applicationName = null,
        IAuditContext? audit = null,
        IConnectionStringEncryptionService? encryption = null
    )
    {
        NpgsqlConnectionStringBuilder builder = new(_database.ConnectionString);
        if (applicationName is not null)
        {
            builder.ApplicationName = applicationName;
        }
        return new(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = builder.ConnectionString,
                    EncryptionKey = "unused",
                }
            ),
            NullLogger<DataStoreRepository>.Instance,
            encryption ?? A.Fake<IConnectionStringEncryptionService>(),
            A.Fake<IConnectionStringReader>(),
            A.Fake<IDataStoreContextRepository>(),
            A.Fake<IDataStoreDerivativeRepository>(),
            audit ?? new TestAuditContext(),
            new TenantContextProvider
            {
                Context = tenantId is long id
                    ? new TenantContext.Multitenant(id, "namespace-test")
                    : new TenantContext.NotMultitenant(),
            }
        );
    }

    private static DataStoreInsertCommand Insert(string name) =>
        new()
        {
            Name = name,
            DataStoreType = "Production",
            Provider = "postgresql",
        };

    private static DataStoreUpdateCommand Rename(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            DataStoreType = "Production",
        };

    private async Task<long> Tenant(string name)
    {
        await using var connection = await Open();
        return await connection.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = name }
        );
    }

    private static Task<int> Reserve(
        DbConnection connection,
        string name,
        long? tenantId = null,
        string status = "PendingCreate",
        int? dataStoreId = null,
        DbTransaction? transaction = null,
        string? targetName = null
    ) =>
        connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs."DataStoreManage" (
                "Name", "TenantId", "Template", "Status", "DataStoreId", "TargetDatabaseName",
                "ExpectedSourceIdentity", "EndpointDescriptor", "CreateJobId",
                "CreatedAt", "UpdatedAt", "LastRefreshed", "LastModifiedDate")
            VALUES (@Name, @TenantId, 'Minimal', @Status, @DataStoreId, @Target, @Source,
                '[1,"postgresql","tcp","db.example.test",5432,null,"postgres"]', @Job,
                @Timestamp, @Timestamp, @Timestamp, @Timestamp) RETURNING "Id";
            """,
            new
            {
                Name = name,
                TenantId = tenantId,
                Status = status,
                DataStoreId = dataStoreId,
                Target = targetName ?? "target_" + Guid.NewGuid().ToString("N"),
                Source = Guid.NewGuid(),
                Job = Guid.NewGuid().ToString("N"),
                Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
            },
            transaction
        );

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_another_active_reservation_on_insert_and_rename(bool multitenant)
    {
        long? tenantId = multitenant ? await Tenant("selected") : null;
        var repository = Repository(tenantId);
        int id = (await repository.InsertDataStore(Insert("existing")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var connection = await Open();
        await Reserve(connection, " Store ", tenantId);

        (await repository.InsertDataStore(Insert("\u3000STORE\u00a0")))
            .Should()
            .BeOfType<DataStoreInsertResult.FailureDuplicateName>();
        (await repository.UpdateDataStore(Rename(id, "store")))
            .Should()
            .BeOfType<DataStoreUpdateResult.FailureDuplicateName>();
        (
            await connection.QuerySingleAsync<string>(
                """SELECT "Name" FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                new { Id = id }
            )
        )
            .Should()
            .Be("existing");
    }

    [TestCase("PendingCreate")]
    [TestCase("CreateInProgress")]
    [TestCase("CreateFailed")]
    [TestCase("CreateError")]
    [TestCase("Created")]
    [TestCase("PendingDelete")]
    [TestCase("DeleteInProgress")]
    [TestCase("DeleteFailed")]
    [TestCase("DeleteError")]
    public async Task It_reserves_every_non_deleted_status(string status)
    {
        await using var connection = await Open();
        await Reserve(connection, "Store", status: status);
        (await Repository().InsertDataStore(Insert("store")))
            .Should()
            .BeOfType<DataStoreInsertResult.FailureDuplicateName>();
    }

    [Test]
    public async Task It_releases_deleted_reservations_and_preserves_other_tenant_and_null_partitions()
    {
        long tenant = await Tenant("selected");
        long other = await Tenant("other");
        await using var connection = await Open();
        await Reserve(connection, "released", status: "Deleted");
        await Reserve(connection, "Store", tenant);
        (await Repository().InsertDataStore(Insert("released")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>();
        (await Repository().InsertDataStore(Insert("store")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>();
        (await Repository(other).InsertDataStore(Insert("store")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>();
        (await Repository(tenant).InsertDataStore(Insert("store")))
            .Should()
            .BeOfType<DataStoreInsertResult.FailureDuplicateName>();
        await Reserve(connection, "null-only");
        (await Repository(tenant).InsertDataStore(Insert("null-only")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>();
    }

    [Test]
    public async Task It_compares_canonical_keys_without_accent_or_non_ASCII_case_folding()
    {
        await using var connection = await Open();
        await Reserve(connection, "café");
        var repository = Repository();
        (await repository.InsertDataStore(Insert("cafe"))).Should().BeOfType<DataStoreInsertResult.Success>();
        (await repository.InsertDataStore(Insert("CAFÉ"))).Should().BeOfType<DataStoreInsertResult.Success>();
        (await repository.InsertDataStore(Insert("CAFé")))
            .Should()
            .BeOfType<DataStoreInsertResult.FailureDuplicateName>();
        (await repository.InsertDataStore(Insert("CAFE")))
            .Should()
            .BeOfType<DataStoreInsertResult.FailureDuplicateName>();
    }

    [Test]
    public async Task It_rejects_its_own_current_link_before_name_conflicts_and_preserves_not_found_precedence()
    {
        var repository = Repository();
        int id = (await repository.InsertDataStore(Insert("Store")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var connection = await Open();
        int manageId = await Reserve(connection, "Store", status: "Created", dataStoreId: id);
        await Reserve(connection, "other");
        AssertManaged(await repository.UpdateDataStore(Rename(id, "store")), manageId);
        AssertManaged(await repository.UpdateDataStore(Rename(id, "other")), manageId);
        (await repository.UpdateDataStore(Rename(int.MaxValue, "other")))
            .Should()
            .BeOfType<DataStoreUpdateResult.FailureNotExists>();
    }

    [Test]
    public async Task It_propagates_a_borrowed_insert_reservation_conflict_to_rollback_prior_work()
    {
        await using var connection = await Open();
        await Reserve(connection, "Store");
        Func<Task> work = async () =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await Repository().InsertDataStore(Insert("prior"), transaction, default);
            await Repository().InsertDataStore(Insert("store"), transaction, default);
            await transaction.CommitAsync();
        };
        await work.Should().ThrowAsync<Exception>();
        (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
            .Should()
            .Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_checks_both_namespaces_without_truncating_long_Unicode_candidates(bool managed)
    {
        await using var connection = await Open();
        if (managed)
        {
            await Reserve(connection, "café");
        }
        else
        {
            (await Repository().InsertDataStore(Insert("café")))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>();
            (await Repository().InsertDataStore(Insert(new string('x', 256))))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>();
        }
        await using var transaction = await connection.BeginTransactionAsync();
        (await DataStoreCatalog.HasNameReservationAsync(transaction, null, " CAFé ", default))
            .Should()
            .BeTrue();
        (await DataStoreCatalog.HasNameReservationAsync(transaction, null, "cafe", default))
            .Should()
            .BeFalse();
        (await DataStoreCatalog.HasNameReservationAsync(transaction, null, "CAFÉ", default))
            .Should()
            .BeFalse();
        (
            await DataStoreCatalog.HasNameReservationAsync(
                transaction,
                null,
                "café" + new string('界', 1024),
                default
            )
        )
            .Should()
            .BeFalse();
        (
            await DataStoreCatalog.HasNameReservationAsync(
                transaction,
                null,
                new string('x', 256) + "界",
                default
            )
        )
            .Should()
            .BeFalse();
        await transaction.RollbackAsync();
    }

    [TestCase("insert", false, false)]
    [TestCase("rename", false, false)]
    [TestCase("insert", true, false)]
    [TestCase("rename", true, false)]
    [TestCase("insert", false, true)]
    [TestCase("rename", false, true)]
    [TestCase("insert", true, true)]
    [TestCase("rename", true, true)]
    public async Task It_serializes_managed_acceptance_against_both_ordinary_race_orders(
        string operation,
        bool ordinaryFirst,
        bool keyDistinct
    )
    {
        int id = (await Repository().InsertDataStore(Insert("existing")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        string ordinaryName = keyDistinct ? "café" : "Store";
        string managedName = keyDistinct ? "cafe" : "store";
        if (ordinaryFirst)
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim release = new(false);
            var audit = A.Fake<IAuditContext>();
            A.CallTo(() => audit.GetCurrentTimestamp())
                .Returns(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            A.CallTo(() => audit.GetCurrentUser())
                .Invokes(() =>
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The test did not release the ordinary writer.");
                    }
                })
                .Returns("test-user");
            var repository = Repository(audit: audit);
            Task ordinary = Task.Run(async () =>
            {
                if (operation == "insert")
                {
                    (await repository.InsertDataStore(Insert(ordinaryName)))
                        .Should()
                        .BeOfType<DataStoreInsertResult.Success>();
                }
                else
                {
                    (await repository.UpdateDataStore(Rename(id, ordinaryName)))
                        .Should()
                        .BeOfType<DataStoreUpdateResult.Success>();
                }
            });
            Task<bool>? managed = null;
            bool waited = false;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                string applicationName = "namespace-" + Guid.NewGuid().ToString("N");
                managed = AcceptManaged(applicationName, managedName);
                waited = await ObserveGateWait(applicationName, managed);
            }
            finally
            {
                release.Set();
                try
                {
                    await ordinary;
                }
                finally
                {
                    if (managed is not null)
                    {
                        await managed;
                    }
                }
            }
            (await managed!)
                .Should()
                .Be(!keyDistinct, "acceptance must see the ordinary write committed after its wait");
            waited.Should().BeTrue();
        }
        else
        {
            await using var connection = await Open();
            await using var transaction = await connection.BeginTransactionAsync();
            (await DataStoreCatalog.HasNameReservationAsync(transaction, null, managedName, default))
                .Should()
                .BeFalse();
            string applicationName = "namespace-" + Guid.NewGuid().ToString("N");
            var repository = Repository(applicationName: applicationName);
            Task<object> ordinary = Mutate();
            async Task<object> Mutate() =>
                operation == "insert"
                    ? await repository.InsertDataStore(Insert(ordinaryName))
                    : await repository.UpdateDataStore(Rename(id, ordinaryName));
            bool waited = false;
            bool committed = false;
            try
            {
                waited = await ObserveGateWait(applicationName, ordinary);
                await Reserve(connection, managedName, transaction: transaction);
                await transaction.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    await transaction.RollbackAsync();
                }
                await ordinary;
            }
            object result = await ordinary;
            if (operation == "insert")
            {
                result
                    .Should()
                    .BeOfType(
                        keyDistinct
                            ? typeof(DataStoreInsertResult.Success)
                            : typeof(DataStoreInsertResult.FailureDuplicateName)
                    );
            }
            else
            {
                result
                    .Should()
                    .BeOfType(
                        keyDistinct
                            ? typeof(DataStoreUpdateResult.Success)
                            : typeof(DataStoreUpdateResult.FailureDuplicateName)
                    );
            }
            waited.Should().BeTrue();
        }
        if (keyDistinct)
        {
            await using var observer = await Open();
            IEnumerable<string> expectedNames =
                operation == "insert" ? ["existing", ordinaryName] : [ordinaryName];
            (await observer.QueryAsync<string>("""SELECT "Name" FROM dmscs."DataStore" ORDER BY "Id";"""))
                .Should()
                .Equal(expectedNames);
            (await observer.QueryAsync<string>("""SELECT "Name" FROM dmscs."DataStoreManage";"""))
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(managedName);
        }
    }

    private async Task<bool> AcceptManaged(string applicationName, string name)
    {
        await using var connection = await Open(applicationName);
        await using var transaction = await connection.BeginTransactionAsync();
        bool reserved = await DataStoreCatalog.HasNameReservationAsync(transaction, null, name, default);
        if (!reserved)
        {
            await Reserve(connection, name, transaction: transaction);
        }
        await transaction.CommitAsync();
        return reserved;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_releases_the_transaction_gate_on_commit_or_rollback(bool commit)
    {
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        string applicationName = "namespace-" + Guid.NewGuid().ToString("N");
        await using var contender = await Open(applicationName);
        await using var second = await contender.BeginTransactionAsync();
        Task waiting = DataStoreCatalog.AcquireTenantGateAsync(second, null, default);
        bool observed;
        try
        {
            observed = await ObserveGateWait(applicationName, waiting);
        }
        finally
        {
            if (commit)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
            await waiting;
        }
        observed.Should().BeTrue();
        await second.RollbackAsync();
    }

    [Test]
    public async Task It_uses_distinct_gates_for_null_and_different_tenants()
    {
        long tenant = await Tenant("selected");
        long other = await Tenant("other");
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        await using var first = await Open();
        await using var firstTransaction = await first.BeginTransactionAsync();
        await DataStoreCatalog
            .AcquireTenantGateAsync(firstTransaction, tenant, default)
            .WaitAsync(TimeSpan.FromSeconds(3));
        await using var second = await Open();
        await using var secondTransaction = await second.BeginTransactionAsync();
        await DataStoreCatalog
            .AcquireTenantGateAsync(secondTransaction, other, default)
            .WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task It_cancels_a_borrowed_insert_while_waiting_without_inserting_a_row()
    {
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        string applicationName = "namespace-" + Guid.NewGuid().ToString("N");
        await using var contender = await Open(applicationName);
        await using var second = await contender.BeginTransactionAsync();
        using CancellationTokenSource cancellation = new();
        Task waiting = Repository().InsertDataStore(Insert("cancelled"), second, cancellation.Token);
        bool observed = await ObserveGateWait(applicationName, waiting);
        await cancellation.CancelAsync();
        Func<Task> wait = () => waiting;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        await second.RollbackAsync();
        await transaction.RollbackAsync();
        observed.Should().BeTrue();
        (await holder.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";""")).Should().Be(0);
    }

    private static readonly ManagedDataStoreEndpoint Endpoint = new(
        "postgresql",
        "tcp",
        "db.example.test",
        5432,
        null,
        "postgres"
    );

    private static DmsTargetDatabaseName Target(string name)
    {
        DmsTargetDatabaseName
            .TryCreate(name, ProtectedDatabaseNames.Create("cms", []), out var target)
            .Should()
            .Be(DmsTargetNameValidation.Valid);
        return target;
    }

    [Test]
    public async Task It_preserves_cross_tenant_target_reservations_using_native_digest_and_target_keys()
    {
        long tenant = await Tenant("selected");
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        (
            await DataStoreCatalog.HasTargetReservationAsync(
                transaction,
                null,
                Endpoint,
                Target("Target_One"),
                default
            )
        )
            .Should()
            .BeFalse();
        string applicationName = "namespace-" + Guid.NewGuid().ToString("N");
        await using var contender = await Open(applicationName);
        await using var second = await contender.BeginTransactionAsync();
        Task<bool> waiting = DataStoreCatalog.HasTargetReservationAsync(
            second,
            tenant,
            Endpoint,
            Target("TARGET_ONE"),
            default
        );
        bool waited = false;
        bool committed = false;
        try
        {
            waited = await ObserveGateWait(applicationName, waiting);
            await Reserve(holder, "holder", transaction: transaction, targetName: "Target_One");
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await transaction.RollbackAsync();
            }
            await waiting;
        }
        waited.Should().BeTrue("target reservations are shared across tenant gates");
        (await waiting).Should().BeTrue();
        (
            await DataStoreCatalog.HasTargetReservationAsync(
                second,
                tenant,
                Endpoint with
                {
                    Host = "other.example.test",
                },
                Target("Target_One"),
                default
            )
        )
            .Should()
            .BeFalse();
        await second.RollbackAsync();
        await holder.ExecuteAsync("""UPDATE dmscs."DataStoreManage" SET "Status" = 'Deleted';""");
        await using var released = await holder.BeginTransactionAsync();
        (
            await DataStoreCatalog.HasTargetReservationAsync(
                released,
                null,
                Endpoint,
                Target("target_one"),
                default
            )
        )
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task It_takes_multiple_target_locks_in_sorted_resource_order()
    {
        long tenant = await Tenant("selected");
        DmsTargetDatabaseName[] ordered = new[] { Target("Target_One"), Target("Target_Two") }
            .OrderBy(
                target =>
                    Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                Endpoint.CanonicalEncoding + "\n" + target.Value.ToLowerInvariant()
                            )
                        )
                    ),
                StringComparer.Ordinal
            )
            .ToArray();
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTargetReservationsAsync(
            transaction,
            null,
            Endpoint,
            [ordered[0]],
            default
        );
        string applicationName = "namespace-" + Guid.NewGuid().ToString("N");
        await using var contender = await Open(applicationName);
        await using var second = await contender.BeginTransactionAsync();
        Task waiting = DataStoreCatalog.AcquireTargetReservationsAsync(
            second,
            tenant,
            Endpoint,
            ordered.Reverse(),
            default
        );
        bool observed;
        int granted;
        try
        {
            observed = await ObserveGateWait(applicationName, waiting);
            await using var observer = await Open();
            granted = await observer.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE a.application_name = @ApplicationName AND l.locktype = 'advisory' AND l.granted;
                """,
                new { ApplicationName = applicationName }
            );
        }
        finally
        {
            await transaction.RollbackAsync();
            await waiting;
        }
        observed.Should().BeTrue();
        granted.Should().Be(1, "only the tenant gate can precede the first sorted target reservation");
    }

    [TestCase(0)]
    [TestCase(250)]
    [TestCase(12000)]
    public async Task It_restores_the_existing_lock_timeout_after_acquisition(int milliseconds)
    {
        await using var connection = await Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "SELECT set_config('lock_timeout', @Timeout, true);",
            new { Timeout = milliseconds + "ms" },
            transaction
        );
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        (
            await connection.ExecuteScalarAsync<int>(
                "SELECT setting::int FROM pg_settings WHERE name = 'lock_timeout';",
                transaction: transaction
            )
        )
            .Should()
            .Be(milliseconds);
    }

    [Test]
    public async Task It_does_not_lengthen_the_shorter_job_fence_lock_timeout()
    {
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        await using var contender = await Open();
        await using var second = await contender.BeginTransactionAsync();
        await contender.ExecuteAsync("SET LOCAL lock_timeout = '100ms';", transaction: second);
        Task waiting = DataStoreCatalog.AcquireTenantGateAsync(second, null, default);
        try
        {
            Func<Task> acquire = () => waiting.WaitAsync(TimeSpan.FromSeconds(2));
            await acquire
                .Should()
                .ThrowAsync<PostgresException>()
                .Where(exception => exception.SqlState == "55P03");
        }
        finally
        {
            await transaction.RollbackAsync();
            try
            {
                await waiting;
            }
            catch (PostgresException)
            {
                // The failed command leaves rollback to the supplied transaction's owner.
            }
        }
        await second.RollbackAsync();
    }

    // Observe the server's actual advisory/application lock wait before releasing the holder.
    // An early completed mutation fails the test; elapsed time is never accepted as a barrier.
    [TestCase("insert")]
    [TestCase("rename")]
    [TestCase("delete")]
    [TestCase("borrowed-insert")]
    [TestCase("managed-delete")]
    public async Task It_waits_on_the_tenant_gate_before_mutating(string operation)
    {
        var repository = Repository();
        int id = (await repository.InsertDataStore(Insert("existing")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        await HoldGate(holder, transaction, null);
        string applicationName = "namespace-" + Guid.NewGuid().ToString("N");
        var contender = Repository(applicationName: applicationName);
        async Task Mutate()
        {
            switch (operation)
            {
                case "insert":
                    (await contender.InsertDataStore(Insert("new")))
                        .Should()
                        .BeOfType<DataStoreInsertResult.Success>();
                    break;
                case "rename":
                    (await contender.UpdateDataStore(Rename(id, "new")))
                        .Should()
                        .BeOfType<DataStoreUpdateResult.Success>();
                    break;
                case "delete":
                    (await contender.DeleteDataStore(id)).Should().BeOfType<DataStoreDeleteResult.Success>();
                    break;
                default:
                    await BorrowedMutation(contender, applicationName, operation, id);
                    break;
            }
        }
        Task mutation = Mutate();
        bool waited;
        try
        {
            waited = await ObserveGateWait(applicationName, mutation);
        }
        finally
        {
            await transaction.RollbackAsync();
            await mutation;
        }
        waited.Should().BeTrue("the mutation must enter the transaction-scoped catalog gate");
        IEnumerable<string> expectedNames = operation switch
        {
            "insert" or "borrowed-insert" => ["existing", "new"],
            "rename" => ["new"],
            _ => [],
        };
        (await holder.QueryAsync<string>("""SELECT "Name" FROM dmscs."DataStore" ORDER BY "Id";"""))
            .Should()
            .Equal(expectedNames);
    }

    private async Task BorrowedMutation(
        DataStoreRepository repository,
        string applicationName,
        string operation,
        int id
    )
    {
        await using var connection = await Open(applicationName);
        await using var transaction = await connection.BeginTransactionAsync();
        if (operation == "borrowed-insert")
        {
            await repository.InsertDataStore(Insert("new"), transaction, default);
        }
        else
        {
            await repository.DeleteManagedDataStore(id, transaction, default);
        }
        await transaction.CommitAsync();
    }

    private static Task HoldGate(DbConnection connection, DbTransaction transaction, long? tenantId)
    {
        string resource = tenantId is long id
            ? "dmscs:datastore-catalog:tenant:"
                + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "dmscs:datastore-catalog:null";
        long key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(resource)));
        return connection.ExecuteAsync("SELECT pg_advisory_xact_lock(@Key);", new { Key = key }, transaction);
    }

    private async Task<bool> ObserveGateWait(string applicationName, Task contender)
    {
        await using var observer = await Open();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(3));
        try
        {
            while (!contender.IsCompleted)
            {
                if (
                    await observer.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            """
                            SELECT EXISTS (
                                SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                                WHERE a.application_name = @ApplicationName AND l.locktype = 'advisory' AND NOT l.granted);
                            """,
                            new { ApplicationName = applicationName },
                            cancellationToken: deadline.Token
                        )
                    )
                )
                {
                    return true;
                }
                await Task.Yield();
            }
            return false;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return false;
        }
    }
}
