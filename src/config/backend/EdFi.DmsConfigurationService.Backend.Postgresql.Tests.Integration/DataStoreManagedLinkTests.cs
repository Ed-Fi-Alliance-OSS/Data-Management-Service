// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private static void AssertManaged(object result, int manageId)
    {
        if (result is DataStoreUpdateResult)
        {
            result
                .Should()
                .BeOfType<DataStoreUpdateResult.FailureManaged>()
                .Subject.ManageId.Should()
                .Be(manageId);
        }
        else
        {
            result
                .Should()
                .BeOfType<DataStoreDeleteResult.FailureManaged>()
                .Subject.ManageId.Should()
                .Be(manageId);
        }
    }

    [Test]
    public async Task It_refuses_current_links_in_every_status_without_changing_any_ordinary_state(
        [Values(false, true)] bool multitenant,
        [Values(
            "PendingCreate",
            "CreateInProgress",
            "CreateFailed",
            "CreateError",
            "Created",
            "PendingDelete",
            "DeleteInProgress",
            "DeleteFailed",
            "DeleteError",
            "Deleted"
        )]
            string status
    )
    {
        long? tenantId = multitenant ? await Tenant("selected") : null;
        var repository = Repository(tenantId);
        int id = (await repository.InsertDataStore(Insert("linked")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """
            UPDATE dmscs."DataStore" SET "ConnectionString" = @Secret, "ModifiedBy" = 'original' WHERE "Id" = @Id;
            INSERT INTO dmscs."DataStoreContext" ("DataStoreId", "ContextKey", "ContextValue") VALUES (@Id, 'schoolYear', '2026');
            INSERT INTO dmscs."DataStoreDerivative" ("DataStoreId", "DerivativeType", "ConnectionString") VALUES (@Id, 'ReadReplica', @Secret);
            """,
            new { Id = id, Secret = new byte[] { 1, 2, 3, 4 } }
        );
        int manageId = await Reserve(connection, "linked", tenantId, status, id);
        string before = await OrdinarySnapshot(connection, id);
        var command = Rename(id, "changed");
        command.ConnectionString = "Server=changed;Database=changed;";
        AssertManaged(await repository.UpdateDataStore(command), manageId);
        AssertManaged(await repository.DeleteDataStore(id), manageId);
        (await OrdinarySnapshot(connection, id))
            .Should()
            .Be(before, "refusal must preserve secrets, audit fields, and cascading dependents");
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT "DataStoreId" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                new { Id = manageId }
            )
        )
            .Should()
            .Be(id);
    }

    private static async Task<string> OrdinarySnapshot(DbConnection connection, int id)
    {
        List<object> rows = [];
        foreach (string table in new[] { "DataStore", "DataStoreContext", "DataStoreDerivative" })
        {
            string key = table == "DataStore" ? "Id" : "DataStoreId";
            rows.Add(
                (
                    await connection.QueryAsync(
                        $"SELECT * FROM dmscs.\"{table}\" WHERE \"{key}\" = @Id ORDER BY \"Id\";",
                        new { Id = id }
                    )
                )
                    .Select(row => (IDictionary<string, object>)row)
                    .ToArray()
            );
        }
        return JsonSerializer.Serialize(rows);
    }

    [Test]
    public async Task It_scopes_links_to_the_selected_tenant_and_allows_unlinked_and_historical_only_CRUD()
    {
        long tenant = await Tenant("selected");
        long other = await Tenant("other");
        foreach (long? selected in new long?[] { null, tenant, other })
        {
            var repository = Repository(selected);
            int id = (await repository.InsertDataStore(Insert("ordinary")))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>()
                .Subject.Id;
            await using var connection = await Open();
            int manageId = await Reserve(connection, "historical", selected, "Deleted");
            await connection.ExecuteAsync(
                """UPDATE dmscs."DataStoreManage" SET "HistoricalDataStoreId" = @Id WHERE "Id" = @ManageId;""",
                new { Id = id, ManageId = manageId }
            );
            foreach (long? wrong in new long?[] { null, tenant, other }.Where(value => value != selected))
            {
                (await Repository(wrong).UpdateDataStore(Rename(id, "wrong")))
                    .Should()
                    .BeOfType<DataStoreUpdateResult.FailureNotExists>();
                (await Repository(wrong).DeleteDataStore(id))
                    .Should()
                    .BeOfType<DataStoreDeleteResult.FailureNotExists>();
            }
            (await repository.UpdateDataStore(Rename(id, "renamed")))
                .Should()
                .BeOfType<DataStoreUpdateResult.Success>();
            (await repository.DeleteDataStore(id)).Should().BeOfType<DataStoreDeleteResult.Success>();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_does_not_treat_another_tenants_current_link_as_a_selected_tenant_link(
        bool multitenant
    )
    {
        long tenant = await Tenant("selected");
        long? selected = multitenant ? tenant : null;
        long? other = multitenant ? null : tenant;
        var repository = Repository(selected);
        int id = (await repository.InsertDataStore(Insert("ordinary")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var connection = await Open();
        // The FK alone permits this malformed cross-partition link. A guard must still scope
        // its read to the selected tenant; legal publication will enforce link ownership later.
        await Reserve(connection, "foreign", other, "Created", id);
        (await repository.UpdateDataStore(Rename(id, "changed")))
            .Should()
            .BeOfType<DataStoreUpdateResult.Success>();
        (
            await connection.QuerySingleAsync<string>(
                """SELECT "Name" FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                new { Id = id }
            )
        )
            .Should()
            .Be("changed");
    }

    [Test]
    public async Task It_observes_link_commit_or_rollback_before_ordinary_mutation(
        [Values("put", "delete")] string operation,
        [Values(false, true)] bool commit
    )
    {
        int id = (await Repository().InsertDataStore(Insert("ordinary")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        int manageId = await Reserve(
            holder,
            "ordinary",
            status: "Created",
            dataStoreId: id,
            transaction: transaction
        );
        string applicationName = "link-" + Guid.NewGuid().ToString("N");
        Task<object> ordinary = MutateOrdinary(Repository(applicationName: applicationName), id, operation);
        bool waited;
        try
        {
            waited = await ObserveGateWait(applicationName, ordinary);
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
            await ordinary;
        }
        waited.Should().BeTrue();
        if (commit)
        {
            AssertManaged(await ordinary, manageId);
        }
        else
        {
            (await ordinary)
                .Should()
                .BeOfType(
                    operation == "put"
                        ? typeof(DataStoreUpdateResult.Success)
                        : typeof(DataStoreDeleteResult.Success)
                );
        }
    }

    [Test]
    public async Task It_publishes_only_after_the_ordinary_commit_and_never_links_a_deleted_row(
        [Values("put", "delete")] string operation
    )
    {
        int id = (await Repository().InsertDataStore(Insert("ordinary")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var holder = await Open();
        await using var rowLock = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(
            """SELECT "Id" FROM dmscs."DataStore" WHERE "Id" = @Id FOR UPDATE;""",
            new { Id = id },
            rowLock
        );
        string ordinaryName = "ordinary-" + Guid.NewGuid().ToString("N");
        Task<object> ordinary = MutateOrdinary(Repository(applicationName: ordinaryName), id, operation);
        Task<string?>? publisher = null;
        bool rowWait = false;
        bool gateWait = false;
        try
        {
            rowWait = await ObserveOrdinaryRowWait(ordinaryName, ordinary);
            string publisherName = "publisher-" + Guid.NewGuid().ToString("N");
            publisher = PublishCurrentLink(publisherName, id);
            gateWait = await ObserveGateWait(publisherName, publisher);
        }
        finally
        {
            await rowLock.RollbackAsync();
            try
            {
                await ordinary;
            }
            finally
            {
                if (publisher is not null)
                {
                    await publisher;
                }
            }
        }
        rowWait.Should().BeTrue();
        gateWait.Should().BeTrue();
        (await ordinary)
            .Should()
            .BeOfType(
                operation == "put"
                    ? typeof(DataStoreUpdateResult.Success)
                    : typeof(DataStoreDeleteResult.Success)
            );
        (await publisher!).Should().Be(operation == "put" ? "changed" : null);
        (await holder.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStoreManage";"""))
            .Should()
            .Be(operation == "put" ? 1 : 0);
    }

    // Test-only publication stages a current link under the existing gate. It does not exercise
    // the later fenced lifecycle repository or claim to prove its publication protocol.
    private async Task<string?> PublishCurrentLink(string applicationName, int id)
    {
        await using var connection = await Open(applicationName);
        await using var transaction = await connection.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        string? name = await connection.QuerySingleOrDefaultAsync<string>(
            """SELECT "Name" FROM dmscs."DataStore" WHERE "Id" = @Id AND "TenantId" IS NULL;""",
            new { Id = id },
            transaction
        );
        if (name is not null)
        {
            await Reserve(connection, name, status: "Created", dataStoreId: id, transaction: transaction);
        }
        await transaction.CommitAsync();
        return name;
    }

    private static async Task<object> MutateOrdinary(
        DataStoreRepository repository,
        int id,
        string operation
    ) =>
        operation == "put"
            ? await repository.UpdateDataStore(Rename(id, "changed"))
            : await repository.DeleteDataStore(id);

    private async Task<bool> ObserveOrdinaryRowWait(string applicationName, Task contender)
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
                            """SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE a.application_name = @ApplicationName AND l.locktype <> 'advisory' AND NOT l.granted);""",
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
