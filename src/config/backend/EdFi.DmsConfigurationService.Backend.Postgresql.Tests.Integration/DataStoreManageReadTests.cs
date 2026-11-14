// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private static readonly DateTime ReadTimestamp = new DateTime(
        2031,
        7,
        8,
        9,
        10,
        11,
        DateTimeKind.Unspecified
    ).AddTicks(1234560);

    private DataStoreManageRepository ReadRepository(long? tenantId = null, string readiness = "Ready") =>
        ManagedRepository(A.Fake<IJobEnqueuer>(), tenantId, CreateReadiness(readiness));

    private static async Task<IReadOnlyList<DataStoreManageResponse>> Read(
        IDataStoreManageRepository repository,
        DataStoreManageQuery query
    ) =>
        (await repository.QueryAsync(query, default))
            .Should()
            .BeOfType<DataStoreManageQueryResult.Success>()
            .Subject.DataStores;

    private static Task SetReadFields(
        DbConnection connection,
        int id,
        int? historicalId = null,
        string? historicalName = null,
        string template = "Minimal",
        int refreshedDays = 0,
        int modifiedDays = 0
    ) =>
        connection.ExecuteAsync(
            """
            UPDATE dmscs."DataStoreManage"
            SET "HistoricalDataStoreId" = @HistoricalId, "HistoricalDataStoreName" = @HistoricalName,
                "Template" = @Template, "LastRefreshed" = @Refreshed, "LastModifiedDate" = @Modified
            WHERE "Id" = @Id;
            """,
            new
            {
                Id = id,
                HistoricalId = historicalId,
                HistoricalName = historicalName,
                Template = template,
                Refreshed = ReadTimestamp.AddDays(refreshedDays),
                Modified = ReadTimestamp.AddDays(modifiedDays),
            }
        );

    [
        TestCase("PendingCreate"),
        TestCase("CreateInProgress"),
        TestCase("CreateFailed"),
        TestCase("CreateError")
    ]
    [TestCase("Created"), TestCase("PendingDelete"), TestCase("DeleteInProgress"), TestCase("DeleteFailed")]
    [TestCase("DeleteError"), TestCase("Deleted")]
    [Category("ManagedDataStoreReads")]
    public async Task It_projects_only_public_lifecycle_fields_including_tombstone_history_and_utc(
        string status
    )
    {
        bool linked =
            status
            is "Created"
                or "PendingDelete"
                or "DeleteInProgress"
                or "DeleteFailed"
                or "DeleteError"
                or "Deleted";
        int? historicalId = null;
        if (linked)
        {
            historicalId = (await Repository().InsertDataStore(Insert("Published NAME")))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>()
                .Subject.Id;
        }
        await using var connection = await Open();
        int id = await Reserve(
            connection,
            "Display Name",
            status: status,
            dataStoreId: status == "Deleted" ? null : historicalId,
            targetName: linked ? "PublishedDatabase" : "UnpublishedTargetSensitive"
        );
        await SetReadFields(connection, id, historicalId, linked ? "Published NAME" : null, "Sample", 0, 2);
        await connection.ExecuteAsync(
            """
            UPDATE dmscs."DataStoreManage"
            SET "CreateJobId" = 'internal-job-sensitive', "DeleteJobId" = 'internal-delete-sensitive',
                "EndpointDescriptor" = '[1,"postgresql","tcp","internal-endpoint-sensitive",5432,null,"postgres"]',
                "LastFailureCategory" = 'Permanent', "LastFailureProvisioningCode" = 'internal-failure-sensitive'
            WHERE "Id" = @Id;
            """,
            new { Id = id }
        );
        if (historicalId is not null)
        {
            await connection.ExecuteAsync(
                """UPDATE dmscs."DataStore" SET "ConnectionString" = @Secret WHERE "Id" = @Id;""",
                new { Id = historicalId, Secret = Encoding.UTF8.GetBytes("encrypted-connection-sensitive") }
            );
            if (status == "Deleted")
            {
                await connection.ExecuteAsync(
                    """DELETE FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                    new { Id = historicalId }
                );
            }
        }

        var repository = ReadRepository();
        var result = (await repository.GetByIdAsync(id, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.Success>()
            .Subject.DataStore;
        var expected = new DataStoreManageResponse(
            id,
            "Display Name",
            historicalId,
            linked ? "Published NAME" : null,
            status,
            "Sample",
            linked ? "PublishedDatabase" : null,
            new DateTimeOffset(2031, 7, 8, 9, 10, 11, TimeSpan.Zero).AddTicks(1234560),
            new DateTimeOffset(2031, 7, 10, 9, 10, 11, TimeSpan.Zero).AddTicks(1234560)
        );
        result.Should().Be(expected);
        result.LastRefreshed.Offset.Should().Be(TimeSpan.Zero);
        result.LastModifiedDate.Offset.Should().Be(TimeSpan.Zero);
        (await Read(repository, new())).Should().Equal(expected);

        string json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }
        );
        using var document = JsonDocument.Parse(json);
        document
            .RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(
                "id",
                "name",
                "dataStoreId",
                "dataStoreName",
                "status",
                "databaseTemplate",
                "databaseName",
                "lastRefreshed",
                "lastModifiedDate"
            );
        if (!linked)
        {
            foreach (string field in new[] { "dataStoreId", "dataStoreName", "databaseName" })
            {
                document.RootElement.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null);
            }
        }
        json.Should().NotContain("sensitive").And.NotContain("UnpublishedTargetSensitive");
    }

    [TestCase("id", new[] { 0, 1, 2, 3, 4, 5 }, new[] { 5, 4, 3, 2, 1, 0 })]
    [TestCase("name", new[] { 3, 1, 2, 5, 4, 0 }, new[] { 0, 4, 5, 2, 1, 3 })]
    [TestCase("dataStoreId", new[] { 1, 2, 3, 0, 4, 5 }, new[] { 0, 3, 2, 1, 5, 4 })]
    [TestCase("dataStoreName", new[] { 3, 1, 2, 0, 4, 5 }, new[] { 0, 2, 1, 3, 5, 4 })]
    [TestCase("status", new[] { 0, 1, 2, 3, 4, 5 }, new[] { 5, 4, 3, 2, 1, 0 })]
    [TestCase("databaseTemplate", new[] { 1, 2, 5, 0, 3, 4 }, new[] { 4, 3, 0, 5, 2, 1 })]
    [TestCase("databaseName", new[] { 1, 2, 3, 0, 4, 5 }, new[] { 0, 3, 2, 1, 5, 4 })]
    [TestCase("lastRefreshed", new[] { 3, 4, 1, 2, 0, 5 }, new[] { 5, 0, 2, 1, 4, 3 })]
    [TestCase("lastModifiedDate", new[] { 0, 5, 3, 4, 1, 2 }, new[] { 2, 1, 4, 3, 5, 0 })]
    [Category("ManagedDataStoreReads")]
    public async Task It_orders_all_public_fields_in_both_directions_with_nulls_last_and_stable_pages(
        string field,
        int[] ascending,
        int[] descending
    )
    {
        await using var connection = await Open();
        string[] names = ["Zulu", "Beta", "bETA", "Alpha", "PendingZ", "PendingA"];
        int?[] historicalIds = [20, 3, 3, 11, null, null];
        string?[] historicalNames = ["Zulu", "Beta", "bETA", "Alpha", null, null];
        string[] targets = ["Zulu_2", "Alpha_2", "alpha_2", "Beta_2", "AAA_Private", "ZZZ_Private"];
        string[] templates = ["Sample", "Minimal", "Minimal", "Sample", "Sample", "Minimal"];
        int[] refreshedDays = [2, 1, 1, 0, 0, 2];
        int[] modifiedDays = [0, 2, 2, 1, 1, 0];
        List<int> ids = [];
        for (int i = 0; i < names.Length; i++)
        {
            int id = await Reserve(
                connection,
                names[i],
                status: i < 4 ? "Deleted" : "PendingCreate",
                targetName: targets[i]
            );
            ids.Add(id);
            await SetReadFields(
                connection,
                id,
                historicalIds[i],
                historicalNames[i],
                templates[i],
                refreshedDays[i],
                modifiedDays[i]
            );
        }
        var repository = ReadRepository();
        foreach (var (direction, order) in new[] { ("Ascending", ascending), ("Descending", descending) })
        {
            var query = new DataStoreManageQuery
            {
                OrderBy = field,
                Direction = direction,
                Limit = int.MaxValue,
            };
            (await Read(repository, query))
                .Select(row => row.Id)
                .Should()
                .Equal(order.Select(index => ids[index]));
            query.Offset = 1;
            query.Limit = 3;
            (await Read(repository, query))
                .Select(row => row.Id)
                .Should()
                .Equal(order.Skip(1).Take(3).Select(index => ids[index]));
        }
    }

    [TestCase("name"), TestCase("dataStoreName"), TestCase("databaseName")]
    [Category("ManagedDataStoreReads")]
    public async Task It_uses_ascii_ordinal_text_order_for_prefixes_digits_and_underscores(string field)
    {
        await using var connection = await Open();
        string[] values = ["a", "A_", "a0", "AA", "a_"];
        List<int> ids = [];
        for (int i = 0; i < values.Length; i++)
        {
            int id = await Reserve(connection, values[i], status: "Deleted", targetName: values[i]);
            ids.Add(id);
            await SetReadFields(connection, id, 100 + i, values[i]);
        }
        var repository = ReadRepository();
        (await Read(repository, new() { OrderBy = field, Direction = "Ascending" }))
            .Select(row => row.Id)
            .Should()
            .Equal(new[] { 0, 2, 1, 4, 3 }.Select(index => ids[index]));
        (await Read(repository, new() { OrderBy = field, Direction = "Descending" }))
            .Select(row => row.Id)
            .Should()
            .Equal(new[] { 3, 4, 1, 2, 0 }.Select(index => ids[index]));
    }

    [Test, Category("ManagedDataStoreReads")]
    public async Task It_uses_managed_defaults_and_int32_paging_without_an_artificial_limit()
    {
        await using var connection = await Open();
        List<int> ids = [];
        for (int i = 0; i < 28; i++)
        {
            ids.Add(await Reserve(connection, $"Store{i}"));
        }
        var repository = ReadRepository();
        (await Read(repository, new()))
            .Select(row => row.Id)
            .Should()
            .Equal(ids.AsEnumerable().Reverse().Take(25));
        (await Read(repository, new() { OrderBy = "", Offset = 25 }))
            .Select(row => row.Id)
            .Should()
            .Equal(ids.Take(3).Reverse());
        (await Read(repository, new() { Limit = int.MaxValue })).Should().HaveCount(28);
        (await Read(repository, new() { Offset = int.MaxValue, Limit = int.MaxValue })).Should().BeEmpty();
    }

    [TestCase(false), TestCase(true)]
    [Category("ManagedDataStoreReads")]
    public async Task It_intersects_canonical_name_and_id_with_tenant_before_paging(bool multitenant)
    {
        long? selected = multitenant ? await Tenant("selected-read") : null;
        long other = await Tenant("other-read");
        await using var connection = await Open();
        int selectedId = await Reserve(connection, "Mixed Name", selected);
        int otherId = await Reserve(connection, "Mixed Name", other);
        int unscopedId = multitenant ? await Reserve(connection, "Mixed Name") : selectedId;
        var repository = ReadRepository(selected);
        (
            await Read(
                repository,
                new()
                {
                    Name = "\u3000mIXED nAME\u00A0",
                    Id = selectedId,
                    Limit = 1,
                }
            )
        ).Select(row => row.Id).Should().Equal(selectedId);
        (await Read(repository, new() { Name = "Other Name", Id = selectedId })).Should().BeEmpty();
        (await Read(repository, new() { Name = "Mixed Name", Id = otherId })).Should().BeEmpty();
        (await Read(repository, new() { Name = "Mixed Name", Offset = 1 })).Should().BeEmpty();
        (await Read(repository, new() { Limit = 1 })).Select(row => row.Id).Should().Equal(selectedId);
        (await repository.GetByIdAsync(otherId, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.NotFound>();
        if (multitenant)
        {
            (await repository.GetByIdAsync(unscopedId, default))
                .Should()
                .BeOfType<DataStoreManageGetResult.NotFound>();
            (await Read(ReadRepository(), new())).Select(row => row.Id).Should().Equal(unscopedId);
        }
        foreach (int missing in new[] { int.MinValue, -1, 0, int.MaxValue })
        {
            (await Read(repository, new() { Id = missing })).Should().BeEmpty();
            (await repository.GetByIdAsync(missing, default))
                .Should()
                .BeOfType<DataStoreManageGetResult.NotFound>();
        }
    }

    [Test, Category("ManagedDataStoreReads")]
    public async Task It_does_not_omit_empty_or_truncate_long_unicode_and_sql_like_name_filters()
    {
        await using var connection = await Open();
        string name = new('A', 46);
        int id = await Reserve(connection, name);
        int cafeId = await Reserve(connection, "Cafe");
        var repository = ReadRepository();
        (await Read(repository, new() { Name = "CAFE" })).Select(row => row.Id).Should().Equal(cafeId);
        (await Read(repository, new() { Name = name.ToLowerInvariant() }))
            .Select(row => row.Id)
            .Should()
            .Equal(id);
        foreach (
            string filter in new[]
            {
                "",
                " \t\u3000",
                name + "B",
                name + new string('B', 5000),
                name + "\0",
                name + "é",
                "café",
                name + "😀",
                "' OR 1=1 --",
                "%",
                "_",
            }
        )
        {
            (await Read(repository, new() { Name = filter })).Should().BeEmpty();
        }
    }

    [TestCase("StartupPending"), TestCase("NotReady"), TestCase("Disabled")]
    [Category("ManagedDataStoreReads")]
    public async Task It_reads_previously_accepted_records_when_provisioning_readiness_is_closed(
        string readiness
    )
    {
        await using var connection = await Open();
        int id = await Reserve(connection, "Existing");
        var enqueuer = A.Fake<IJobEnqueuer>(options => options.Strict());
        var repository = ManagedRepository(enqueuer, readiness: CreateReadiness(readiness));
        (await Read(repository, new())).Select(row => row.Id).Should().Equal(id);
        (await repository.GetByIdAsync(id, default)).Should().BeOfType<DataStoreManageGetResult.Success>();
    }

    [TestCase("orderBy", "Id"), TestCase("orderBy", "NAME"), TestCase("orderBy", " ")]
    [
        TestCase("orderBy", "id; DROP TABLE dmscs.DataStoreManage;--"),
        TestCase("orderBy", "name DESC"),
        TestCase("orderBy", null)
    ]
    [TestCase("direction", ""), TestCase("direction", "ASC"), TestCase("direction", "descending")]
    [TestCase("direction", "Descending; SELECT 1"), TestCase("direction", null)]
    [TestCase("offset", "-1"), TestCase("limit", "0"), TestCase("limit", "-1")]
    [Category("ManagedDataStoreReads")]
    public async Task It_rejects_invalid_direct_queries_before_attempting_a_connection(
        string field,
        string? value
    )
    {
        var query = new DataStoreManageQuery();
        switch (field)
        {
            case "orderBy":
                query.OrderBy = value!;
                break;
            case "direction":
                query.Direction = value!;
                break;
            case "offset":
                query.Offset = int.Parse(value!);
                break;
            case "limit":
                query.Limit = int.Parse(value!);
                break;
        }
        var repository = InvalidConnectionReadRepository();
        (await repository.QueryAsync(query, default))
            .Should()
            .BeOfType<DataStoreManageQueryResult.InvalidRequest>();
    }

    private DataStoreManageRepository InvalidConnectionReadRepository() =>
        new(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = "sensitive-connection-sentinel=invalid",
                    EncryptionKey = "unused",
                }
            ),
            CreateTenantContext(null),
            CreateReadiness(),
            A.Fake<IJobEnqueuer>(),
            ProtectedDatabaseNames.Create("cms", []),
            Repository(),
            EdFi.DmsConfigurationService.Backend.Jobs.JobClaimPolicy.All
        );

    [Test, Category("ManagedDataStoreReads")]
    public async Task It_propagates_cancellation_and_returns_only_safe_read_failures()
    {
        var repository = InvalidConnectionReadRepository();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        var query = () => repository.QueryAsync(new(), cancellation.Token);
        var get = () => repository.GetByIdAsync(1, cancellation.Token);
        await query.Should().ThrowAsync<OperationCanceledException>();
        await get.Should().ThrowAsync<OperationCanceledException>();
        var queryFailure = (await repository.QueryAsync(new(), default))
            .Should()
            .BeOfType<DataStoreManageQueryResult.FailureUnknown>()
            .Subject.Diagnostic;
        var getFailure = (await repository.GetByIdAsync(1, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.FailureUnknown>()
            .Subject.Diagnostic;
        getFailure.Should().Be(queryFailure);
        queryFailure.Operation.Should().Be("QueryManagedDataStores");
        JsonSerializer.Serialize(queryFailure).Should().NotContain("sensitive-connection-sentinel");
    }
}
