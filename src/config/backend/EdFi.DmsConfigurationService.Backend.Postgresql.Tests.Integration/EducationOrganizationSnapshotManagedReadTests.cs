// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The tenant aggregate against real managed publication and deletion: a managed write that commits while a read is
/// paused between its statements is wholly absent from that read, and a management record never links to a data
/// store that does not exist.
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
        TenantAggregateReads.DataStoresOf(
            await SnapshotReads().GetTenantAggregateAsync(CancellationToken.None)
        );

    /// <summary>A published snapshot of one organization for the data store, as a refresh leaves it.</summary>
    private async Task SeedSnapshot(int dataStoreId)
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """
            INSERT INTO dmscs."EducationOrganizationSnapshotTarget" ("DataStoreId", "LastGeneration", "ActiveGeneration")
            VALUES (@Id, 1, 1);
            INSERT INTO dmscs."EducationOrganizationSnapshot" ("DataStoreId", "Generation", "EducationOrganizationId",
                "NameOfInstitution", "Discriminator", "RefreshedAt", "SourceContractVersion")
            VALUES (@Id, 1, @OrganizationId, @Name, @Discriminator, now(), 'educationOrganizationProjection.v1');
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
    public async Task It_reads_the_aggregate_before_a_managed_delete_that_commits_mid_read(
        [Values(TenantAggregateReads.BeforeOrganizations, TenantAggregateReads.BeforeUnlinkedManages)]
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

        IReadOnlyList<TenantAggregateDataStore> during = await TenantAggregateReads.ReadAroundAsync(
            SnapshotReads,
            pausePoint,
            () => PublicationRepository().CompleteDeleteAsync(seed.Command, seed.Execution, default)
        );

        during.ShouldMatch(before, "the managed delete committed after the read's snapshot");
        TenantAggregateDataStore unlinked = (await AggregateAsync()).Should().ContainSingle().Subject;
        (int?, int?, string) unlinkedFields = (null, seed.Command.Id, "Deleted");
        (unlinked.Id, unlinked.DataStoreManageId, unlinked.Status).Should().Be(unlinkedFields);
        unlinked.EducationOrganizations.Should().BeEmpty();
    }

    [Test, Category(SnapshotReadCategory)]
    public async Task It_reads_the_aggregate_before_a_managed_publication_that_commits_mid_read(
        [Values(TenantAggregateReads.BeforeOrganizations, TenantAggregateReads.BeforeUnlinkedManages)]
            string pausePoint
    )
    {
        var seed = await SeedPublication();
        IReadOnlyList<TenantAggregateDataStore> before = await AggregateAsync();
        TenantAggregateDataStore pending = before.Should().ContainSingle().Subject;
        (int?, int?, string?) pendingFields = (null, seed.Command.Id, null);
        (pending.Id, pending.DataStoreManageId, pending.DatabaseName).Should().Be(pendingFields);
        int published = 0;

        IReadOnlyList<TenantAggregateDataStore> during = await TenantAggregateReads.ReadAroundAsync(
            SnapshotReads,
            pausePoint,
            async () =>
                published = await PublicationRepository()
                    .PublishCreateAsync(seed.Command, seed.Execution, default)
        );

        during.ShouldMatch(before, "the publication committed after the read's snapshot");
        TenantAggregateDataStore linked = (await AggregateAsync()).Should().ContainSingle().Subject;
        (int?, int?) linkedFields = (published, seed.Command.Id);
        (linked.Id, linked.DataStoreManageId).Should().Be(linkedFields);
        linked.DatabaseName.Should().NotBeNull("a published record reports its database name");
    }

    [Test, Category(SnapshotReadCategory)]
    public async Task It_refuses_a_management_link_to_a_missing_data_store()
    {
        var seed = await SeedDeletion();
        await using var connection = await Open();
        int missing = await connection.ExecuteScalarAsync<int>(
            """SELECT COALESCE(MAX("Id"), 0) + 1000 FROM dmscs."DataStore";"""
        );
        var unlinkedSeed = await SeedPublication(name: "Unlinked");

        Func<Task> dangle = () =>
            connection.ExecuteAsync(
                """UPDATE dmscs."DataStoreManage" SET "DataStoreId" = @Missing WHERE "Id" = @Id;""",
                new { Missing = missing, unlinkedSeed.Command.Id }
            );
        Func<Task> orphan = () =>
            connection.ExecuteAsync(
                """DELETE FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                new { Id = seed.Command.HistoricalDataStoreId }
            );

        (await dangle.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
        (await orphan.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
        (int?, int?) linkedEntry = (seed.Command.HistoricalDataStoreId, seed.Command.Id);
        (int?, int?) unlinkedEntry = (null, unlinkedSeed.Command.Id);
        (await AggregateAsync())
            .Select(entry => (entry.Id, entry.DataStoreManageId))
            .Should()
            .Equal(linkedEntry, unlinkedEntry);
    }
}
