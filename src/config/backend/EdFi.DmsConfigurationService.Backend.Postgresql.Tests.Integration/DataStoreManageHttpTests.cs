// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private WebApplicationFactory<Program> ManagedHttpHost(
        Func<long?, IJobEnqueuer> enqueuer,
        bool multitenant = false,
        bool ready = true,
        bool enabled = true
    )
    {
        var readiness = CreateReadiness(ready ? "Ready" : "NotReady");
        var admin = A.Fake<IDmsAdminConnectionSource>();
        A.CallTo(() => admin.ResolveAsync(A<CancellationToken>._))
            .Returns(
                new DmsAdminConnectionResolution.Resolved(
                    new DmsAdminConnectionString("Host=db.example;Username=admin;Password=test-only")
                )
            );
        var policy = enabled ? JobClaimPolicy.All : JobClaimPolicy.ExcludeManagedDataStores;
        return ManagedDataStoreApiTestHost.Create(
            services =>
            {
                services.AddSingleton(policy);
                services.AddScoped<IDataStoreManageRepository>(sp =>
                {
                    long? tenant = sp.GetRequiredService<ITenantContextProvider>().Context
                        is TenantContext.Multitenant selected
                        ? selected.TenantId
                        : null;
                    return ManagedRepository(enqueuer(tenant), tenant, readiness);
                });
                services.AddScoped(sp => new ManagedDataStoreCreateAcceptance(
                    readiness,
                    admin,
                    PostgresqlManagedEndpoint.TryParse,
                    ProtectedDatabaseNames.Create("cms", []),
                    sp.GetRequiredService<IDataStoreManageRepository>(),
                    policy
                ));
            },
            provider: "postgresql",
            connectionString: _database.ConnectionString,
            multitenant: multitenant
        );
    }

    private static HttpClient ManagedClient(WebApplicationFactory<Program> factory, string? tenant = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", "edfi_admin_api/full_access");
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add("Tenant", tenant);
        }
        return client;
    }

    private static Task<HttpResponseMessage> ManagedPost(HttpClient client, string name) =>
        client.PostAsJsonAsync("/v3/dataStores/manage", new { name, databaseTemplate = "Minimal" });

    [TestCase(false), TestCase(true), Category("DataStoreManageHttp")]
    public async Task It_accepts_HTTP_create_atomically_and_reads_only_the_selected_partition(
        bool multitenant
    )
    {
        long? tenant = multitenant ? await Tenant("api-a") : null;
        long otherTenant = await Tenant("api-b");
        using var current = AcceptanceServices(tenant);
        using var other = AcceptanceServices(otherTenant);
        await using var host = ManagedHttpHost(
            id => (id == otherTenant ? other : current).GetRequiredService<IJobEnqueuer>(),
            multitenant
        );
        using var client = ManagedClient(host, multitenant ? "api-a" : null);
        using var response = await ManagedPost(client, " Store ");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        int id = int.Parse(
            response.Headers.Location!.Segments[^1],
            System.Globalization.CultureInfo.InvariantCulture
        );
        await AssertAcceptanceCounts(1, 1);
        using var duplicate = await ManagedPost(client, "store");
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertAcceptanceCounts(1, 1);
        using var get = await client.GetAsync($"/v3/dataStores/manage/{id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await get.Content.ReadAsStringAsync())!.AsObject();
        body.Should().HaveCount(9);
        body["status"]!.GetValue<string>().Should().Be("PendingCreate");
        body["dataStoreId"].Should().BeNull();
        body["dataStoreName"].Should().BeNull();
        body["databaseName"].Should().BeNull();
        if (multitenant)
        {
            using var selectedOther = ManagedClient(host, "api-b");
            using var absent = await selectedOther.GetAsync($"/v3/dataStores/manage/{id}");
            absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var list = await selectedOther.GetAsync("/v3/dataStores/manage");
            JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray().Should().BeEmpty();
            using var missingTenant = ManagedClient(host);
            using var invalid = await missingTenant.GetAsync("/v3/dataStores/manage");
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Test, Category("DataStoreManageHttp")]
    public async Task It_applies_HTTP_filters_ordering_and_paging_before_returning_real_rows()
    {
        using var services = AcceptanceServices();
        await using var host = ManagedHttpHost(_ => services.GetRequiredService<IJobEnqueuer>());
        using var client = ManagedClient(host);
        foreach (string name in new[] { "Beta", "alpha", "Gamma" })
        {
            using var accepted = await ManagedPost(client, name);
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        using var page = await client.GetAsync(
            "/v3/dataStores/manage?orderBy=name&direction=Ascending&offset=1&limit=1"
        );
        var rows = JsonNode.Parse(await page.Content.ReadAsStringAsync())!.AsArray();
        rows.Select(x => x!["name"]!.GetValue<string>()).Should().Equal("Beta");
        using var filtered = await client.GetAsync("/v3/dataStores/manage?name=%20ALPHA%20");
        JsonNode
            .Parse(await filtered.Content.ReadAsStringAsync())!
            .AsArray()
            .Select(x => x!["name"]!.GetValue<string>())
            .Should()
            .Equal("alpha");
        using var empty = await client.GetAsync("/v3/dataStores/manage?name=");
        JsonNode.Parse(await empty.Content.ReadAsStringAsync())!.AsArray().Should().BeEmpty();
        using var first = await ManagedPost(client, "EdFi_Ods");
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var collision = await ManagedPost(client, "_");
        collision.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertAcceptanceCounts(4, 4);
    }

    [TestCase("Created", true, HttpStatusCode.NoContent)]
    [TestCase("Created", false, HttpStatusCode.ServiceUnavailable)]
    [TestCase("PendingDelete", false, HttpStatusCode.BadRequest)]
    [TestCase("Deleted", false, HttpStatusCode.NotFound)]
    [Category("DataStoreManageHttp")]
    public async Task It_preserves_HTTP_delete_state_precedence_and_atomic_acceptance(
        string status,
        bool ready,
        HttpStatusCode expected
    )
    {
        var seed = await SeedDeleteAcceptance();
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."DataStoreManage" SET "Status" = @Status, "DataStoreId" = CASE WHEN @Status = 'Deleted' THEN NULL ELSE "DataStoreId" END WHERE "Id" = @Id;""",
            new { seed.Id, Status = status }
        );
        using var services = AcceptanceServices();
        await using var host = ManagedHttpHost(
            _ => services.GetRequiredService<IJobEnqueuer>(),
            ready: ready
        );
        using var client = ManagedClient(host);
        string before = await DeleteAcceptanceSnapshot();
        using var response = await client.DeleteAsync($"/v3/dataStores/manage/{seed.Id}");
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        response.Headers.Location.Should().BeNull();
        if (expected == HttpStatusCode.NoContent)
        {
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
            (await DeleteRow(connection, seed.Id))["Status"].Should().Be("PendingDelete");
            (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."Job";"""))
                .Should()
                .Be(1);
            (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                .Should()
                .Be(1);
        }
        else
        {
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
        }
        using var get = await client.GetAsync($"/v3/dataStores/manage/{seed.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test, Category("DataStoreManageHttp")]
    public async Task It_refuses_HTTP_create_when_not_ready_without_aggregate_or_job()
    {
        using var services = AcceptanceServices();
        await using var host = ManagedHttpHost(
            _ => services.GetRequiredService<IJobEnqueuer>(),
            ready: false
        );
        using var client = ManagedClient(host);
        using var response = await ManagedPost(client, "Store");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await AssertAcceptanceCounts(0, 0);
    }

    [Test, Category("DataStoreManageHttp")]
    public async Task It_keeps_all_managed_routes_mapped_but_disabled_without_writes()
    {
        using var services = AcceptanceServices();
        await using var host = ManagedHttpHost(
            _ => services.GetRequiredService<IJobEnqueuer>(),
            enabled: false
        );
        using var client = ManagedClient(host);
        using var list = await client.GetAsync("/v3/dataStores/manage");
        using var get = await client.GetAsync("/v3/dataStores/manage/bad");
        using var create = await ManagedPost(client, "Store");
        using var delete = await client.DeleteAsync("/v3/dataStores/manage/bad");
        foreach (var response in new[] { list, get, create, delete })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("EnableDataStoreManagement");
        }
        await AssertAcceptanceCounts(0, 0);
    }
}
