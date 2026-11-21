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
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The tenant aggregate against real managed publication and deletion. The read holds the tenant gate in shared
/// mode, so a managed write that arrives during it waits: it commits after the read when the read ends within the
/// write's gate wait, and otherwise fails and rolls back. Either way the read returns exactly the state before the
/// write. A management record never links to a data store that does not exist.
/// </summary>
public partial class Given_data_store_namespace_reservations
{
    private const string SnapshotReadCategory = "EducationOrganizationSnapshotRead";

    private static readonly EducationOrganizationProjectionItem SeededOrganization = new(
        255901001,
        "Seeded School",
        null,
        "edfi.School",
        null
    );

    private EducationOrganizationSnapshotRepository SnapshotReads(JobDatabaseSessionHooks? hooks = null) =>
        new(
            PublicationOptions,
            Options.Create(new JobOptions()),
            new TenantContextProvider { Context = new TenantContext.NotMultitenant() }
        )
        {
            SessionHooks = hooks,
        };

    private async Task<IReadOnlyList<TenantAggregateDataStore>> AggregateAsync() =>
        PausedAggregateRead.DataStoresOf(
            await SnapshotReads().GetTenantAggregateAsync(CancellationToken.None)
        );

    private Task<PausedAggregateRead> PausedSnapshotReadAsync(string pausePoint) =>
        PausedAggregateRead.StartAsync(SnapshotReads, pausePoint);

    private Task WaitUntilGateWaiterAsync(Task writer) =>
        PausedAggregateRead.WaitUntilGateWaiterAsync(() => Open(), writer);

    /// <summary>A published snapshot of one organization for the data store, as a refresh leaves it.</summary>
    private async Task SeedSnapshot(int dataStoreId)
    {
        await using SqlConnection connection = await Open();
        await connection.ExecuteAsync(
            """
            INSERT INTO dmscs.EducationOrganizationSnapshotTarget (DataStoreId, LastGeneration, ActiveGeneration)
            VALUES (@Id, 1, 1);
            INSERT INTO dmscs.EducationOrganizationSnapshot (DataStoreId, Generation, EducationOrganizationId,
                NameOfInstitution, Discriminator, RefreshedAt, SourceContractVersion)
            VALUES (@Id, 1, @OrganizationId, @Name, @Discriminator, SYSUTCDATETIME(),
                N'educationOrganizationProjection.v1');
            """,
            new
            {
                Id = dataStoreId,
                OrganizationId = SeededOrganization.EducationOrganizationId,
                Name = SeededOrganization.NameOfInstitution,
                SeededOrganization.Discriminator,
            }
        );
    }

    [Test, Category(SnapshotReadCategory)]
    public async Task It_reads_the_aggregate_before_a_managed_delete_that_waits_for_the_read(
        [Values(PausedAggregateRead.BeforeOrganizations, PausedAggregateRead.BeforeUnlinkedManages)]
            string pausePoint
    )
    {
        var seed = await SeedDeletion();
        int dataStoreId = seed.Command.HistoricalDataStoreId;
        await SeedSnapshot(dataStoreId);
        IReadOnlyList<TenantAggregateDataStore> before = await AggregateAsync();
        TenantAggregateDataStore linked = before.Should().ContainSingle().Subject;
        (int?, int?, string) linkedFields = (dataStoreId, seed.Command.Id, "DeleteInProgress");
        (linked.Id, linked.DataStoreManageId, linked.Status).Should().Be(linkedFields);
        linked.EducationOrganizations.Should().Equal(SeededOrganization);

        PausedAggregateRead read = await PausedSnapshotReadAsync(pausePoint);
        Task deleting;
        try
        {
            deleting = PublicationRepository().CompleteDeleteAsync(seed.Command, seed.Execution, default);
            await WaitUntilGateWaiterAsync(deleting);
        }
        finally
        {
            read.Release();
        }

        (await read.FinishAsync()).ShouldMatch(before, "the managed delete waited for the read");
        await deleting;
        TenantAggregateDataStore unlinked = (await AggregateAsync()).Should().ContainSingle().Subject;
        (int?, int?, string) unlinkedFields = (null, seed.Command.Id, "Deleted");
        (unlinked.Id, unlinked.DataStoreManageId, unlinked.Status).Should().Be(unlinkedFields);
        unlinked.EducationOrganizations.Should().BeEmpty();
    }

    [Test, Category(SnapshotReadCategory)]
    public async Task It_reads_the_aggregate_before_a_managed_publication_that_waits_for_the_read(
        [Values(PausedAggregateRead.BeforeOrganizations, PausedAggregateRead.BeforeUnlinkedManages)]
            string pausePoint
    )
    {
        var seed = await SeedPublication();
        IReadOnlyList<TenantAggregateDataStore> before = await AggregateAsync();
        TenantAggregateDataStore pending = before.Should().ContainSingle().Subject;
        (int?, int?, string?) pendingFields = (null, seed.Command.Id, null);
        (pending.Id, pending.DataStoreManageId, pending.DatabaseName).Should().Be(pendingFields);

        PausedAggregateRead read = await PausedSnapshotReadAsync(pausePoint);
        Task<int> publishing;
        try
        {
            publishing = PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
            await WaitUntilGateWaiterAsync(publishing);
        }
        finally
        {
            read.Release();
        }

        (await read.FinishAsync()).ShouldMatch(before, "the publication waited for the read");
        int published = await publishing;
        TenantAggregateDataStore linked = (await AggregateAsync()).Should().ContainSingle().Subject;
        (int?, int?) linkedFields = (published, seed.Command.Id);
        (linked.Id, linked.DataStoreManageId).Should().Be(linkedFields);
        linked.DatabaseName.Should().NotBeNull("a published record reports its database name");
    }

    [Test, Category(SnapshotReadCategory)]
    public async Task It_rolls_back_a_managed_publication_that_waits_past_its_gate_bound()
    {
        var seed = await SeedPublication();
        IReadOnlyList<TenantAggregateDataStore> before = await AggregateAsync();
        int dataStoresBefore = await DataStoreCountAsync();

        PausedAggregateRead read = await PausedSnapshotReadAsync(PausedAggregateRead.BeforeCommit);
        Func<Task> publish = () =>
            PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
        TimeSpan waited;
        try
        {
            long started = Stopwatch.GetTimestamp();
            await publish.Should().ThrowAsync<TimeoutException>();
            waited = Stopwatch.GetElapsedTime(started);
            read.Result.IsCompleted.Should().BeFalse("the read still holds the gate");
        }
        finally
        {
            read.Release();
        }

        waited
            .Should()
            .BeGreaterThan(TimeSpan.FromSeconds(4.5))
            .And.BeLessThan(TimeSpan.FromSeconds(15), "the writer gives up after its 5-second gate bound");
        (await read.FinishAsync()).ShouldMatch(before);
        (await AggregateAsync()).ShouldMatch(before, "the publication rolled back");
        (await DataStoreCountAsync()).Should().Be(dataStoresBefore, "no ordinary data store was created");
    }

    [Test, Category(SnapshotReadCategory)]
    public async Task It_refuses_a_management_link_to_a_missing_data_store()
    {
        var seed = await SeedDeletion();
        await using SqlConnection connection = await Open();
        int missing = await connection.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(Id), 0) + 1000 FROM dmscs.DataStore;"
        );
        var unlinkedSeed = await SeedPublication(name: "Unlinked");

        Func<Task> dangle = () =>
            connection.ExecuteAsync(
                "UPDATE dmscs.DataStoreManage SET DataStoreId = @Missing WHERE Id = @Id;",
                new { Missing = missing, unlinkedSeed.Command.Id }
            );
        Func<Task> orphan = () =>
            connection.ExecuteAsync(
                "DELETE FROM dmscs.DataStore WHERE Id = @Id;",
                new { Id = seed.Command.HistoricalDataStoreId }
            );

        (await dangle.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
        (await orphan.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
        (int?, int?) linkedEntry = (seed.Command.HistoricalDataStoreId, seed.Command.Id);
        (int?, int?) unlinkedEntry = (null, unlinkedSeed.Command.Id);
        (await AggregateAsync())
            .Select(entry => (entry.Id, entry.DataStoreManageId))
            .Should()
            .Equal(linkedEntry, unlinkedEntry);
    }

    private async Task<int> DataStoreCountAsync()
    {
        await using SqlConnection connection = await Open();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dmscs.DataStore;");
    }
}
