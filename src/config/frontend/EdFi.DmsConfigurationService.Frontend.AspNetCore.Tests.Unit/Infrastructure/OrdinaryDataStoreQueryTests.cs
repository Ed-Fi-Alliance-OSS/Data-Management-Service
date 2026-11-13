// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>The same repository and HTTP assertions run against each provider's actual migrated catalog.</summary>
public abstract class OrdinaryDataStoreQueryTests(bool multitenant, bool canonicalUniqueness)
{
    private WebApplicationFactory<Program> _factory = null!;
    private long _tenantA;
    private long _tenantB;
    private long? CurrentTenantId => multitenant ? _tenantA : null;
    protected bool CanonicalUniqueness => canonicalUniqueness;

    protected abstract string Provider { get; }
    protected abstract string ConnectionString { get; }
    protected abstract Task DeployAsync();
    protected abstract Task DropAsync();
    protected abstract Task<long> InsertTenantAsync(string name);
    protected abstract Task<int> InsertAsync(OrdinaryDataStoreQueryCases.Row row, long? tenantId);
    protected abstract Task ClearStoresAsync();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        await DeployAsync();
        _tenantA = await InsertTenantAsync("query-tenant-a");
        _tenantB = await InsertTenantAsync("query-tenant-b");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:Datastore", Provider);
            builder.UseSetting("AppSettings:MultiTenancy", multitenant.ToString());
            builder.UseSetting("DatabaseSettings:DatabaseConnection", ConnectionString);
            builder.UseSetting("DmsDataStoreProvisioning:Enabled", "false");
            builder.UseSetting("JobSettings:WorkerEnabled", "false");
            builder.UseSetting("JobSettings:SchedulerEnabled", "false");
            builder.UseSetting("JobSettings:RetentionEnabled", "false");
            builder.ConfigureServices(services => services.AddTestAuthentication());
        });
    }

    [SetUp]
    public Task ClearStores() => ClearStoresAsync();

    [OneTimeTearDown]
    public async Task StopHost()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await DropAsync();
    }

    private async Task<int[]> SeedAsync(OrdinaryDataStoreQueryCases.Case test)
    {
        // Lower IDs and earlier sort values in other partitions catch paging before tenant filtering.
        await InsertAsync(new("!"), _tenantB);
        await InsertAsync(test.Rows[0], _tenantB);
        if (multitenant)
        {
            await InsertAsync(new("!"), null);
            await InsertAsync(test.Rows[0], null);
        }
        List<int> ids = [];
        foreach (var row in test.Rows)
        {
            ids.Add(await InsertAsync(row, CurrentTenantId));
        }
        return ids.ToArray();
    }

    private HttpClient Client(string? scope = "edfi_admin_api/full_access", string? tenant = "query-tenant-a")
    {
        HttpClient client = _factory.CreateClient();
        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }
        if (multitenant && tenant is not null)
        {
            client.DefaultRequestHeaders.Add("Tenant", tenant);
        }
        return client;
    }

    [TestCaseSource(typeof(OrdinaryDataStoreQueryCases), nameof(OrdinaryDataStoreQueryCases.Cases))]
    public async Task It_queries_the_real_repository(OrdinaryDataStoreQueryCases.Case test)
    {
        test = canonicalUniqueness ? OrdinaryDataStoreQueryCases.WithCanonicalUniqueness(test) : test;
        int[] ids = await SeedAsync(test);
        using IServiceScope scope = _factory.Services.CreateScope();
        if (multitenant)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextProvider>().Context =
                new TenantContext.Multitenant(_tenantA, "query-tenant-a");
        }
        IDataStoreRepository repository = scope.ServiceProvider.GetRequiredService<IDataStoreRepository>();
        var result = await repository.QueryDataStore(test.Bind(ids));
        var rows = result
            .Should()
            .BeOfType<DataStoreQueryResult.Success>()
            .Subject.DataStoreResponses.ToArray();
        rows.Select(row => row.Id).Should().Equal(test.ExpectedRows.Select(index => ids[index]));
        rows.Select(row => row.Name).Should().Equal(test.ExpectedRows.Select(index => test.Rows[index].Name));
        rows.Select(row => row.DataStoreType)
            .Should()
            .Equal(test.ExpectedRows.Select(index => test.Rows[index].Type));
    }

    [TestCaseSource(typeof(OrdinaryDataStoreQueryCases), nameof(OrdinaryDataStoreQueryCases.Cases))]
    public async Task It_queries_through_HTTP_with_the_real_repository(OrdinaryDataStoreQueryCases.Case test)
    {
        test = canonicalUniqueness ? OrdinaryDataStoreQueryCases.WithCanonicalUniqueness(test) : test;
        int[] ids = await SeedAsync(test);
        using HttpClient client = Client();
        using HttpResponseMessage response = await client.GetAsync(
            OrdinaryDataStoreQueryCases.Url(test.Bind(ids))
        );
        string content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        JsonArray rows = JsonNode.Parse(content)!.AsArray();
        rows.Select(row => row!["id"]!.GetValue<int>())
            .Should()
            .Equal(test.ExpectedRows.Select(index => ids[index]));
        rows.Select(row => row!["name"]!.GetValue<string>())
            .Should()
            .Equal(test.ExpectedRows.Select(index => test.Rows[index].Name));
        rows.Select(row => row!["dataStoreType"]!.GetValue<string>())
            .Should()
            .Equal(test.ExpectedRows.Select(index => test.Rows[index].Type));
        foreach (JsonNode? row in rows)
        {
            row!.AsObject().Should().NotContainKey("nameComparisonKey");
            row["dataStoreContexts"]!.AsArray().Should().BeEmpty();
            row["dataStoreDerivatives"]!.AsArray().Should().BeEmpty();
        }
    }

    [TestCase(null, HttpStatusCode.Unauthorized)]
    [TestCase("unrelated-scope", HttpStatusCode.Forbidden)]
    [TestCase("edfi_admin_api/full_access", HttpStatusCode.OK)]
    [TestCase("edfi_admin_api/readonly_access", HttpStatusCode.OK)]
    [TestCase("edfi_admin_api/authMetadata_readonly_access", HttpStatusCode.OK)]
    public async Task It_preserves_read_authorization(string? scope, HttpStatusCode expected)
    {
        int id = await InsertAsync(new("café"), CurrentTenantId);
        using HttpClient client = Client(scope);
        using HttpResponseMessage response = await client.GetAsync("/v3/dataStores/?name=CAF%C3%A9");
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
        {
            JsonArray rows = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
            rows.Select(row => row!["id"]!.GetValue<int>()).Should().Equal(id);
        }
    }

    [Test]
    public async Task It_keeps_the_selected_rows_display_connections_and_dependents()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        if (multitenant)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextProvider>().Context =
                new TenantContext.Multitenant(_tenantA, "query-tenant-a");
        }
        var repository = scope.ServiceProvider.GetRequiredService<IDataStoreRepository>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDataStoreContextRepository>();
        var derivatives = scope.ServiceProvider.GetRequiredService<IDataStoreDerivativeRepository>();
        var encryption = scope.ServiceProvider.GetRequiredService<IConnectionStringEncryptionService>();
        int selectedId = 0;
        int selectedContextId = 0;
        int selectedDerivativeId = 0;
        string provider = Provider == "mssql" ? "sqlserver" : "postgresql";
        foreach (string name in new[] { "other", " CAFé " })
        {
            var inserted = await repository.InsertDataStore(
                new()
                {
                    Name = name,
                    DataStoreType = "SQL",
                    Provider = provider,
                    ConnectionString = $"Server=host;Database={name};",
                }
            );
            int id = inserted.Should().BeOfType<DataStoreInsertResult.Success>().Subject.Id;
            var context = await contexts.InsertDataStoreContext(
                new()
                {
                    DataStoreId = id,
                    ContextKey = "schoolYear",
                    ContextValue = name,
                }
            );
            var derivative = await derivatives.InsertDataStoreDerivative(
                new()
                {
                    DataStoreId = id,
                    DerivativeType = "ReadReplica",
                    ConnectionString = $"Server=replica;Database={name};",
                }
            );
            selectedId = id;
            selectedContextId = context.Should().BeOfType<DataStoreContextInsertResult.Success>().Subject.Id;
            selectedDerivativeId = derivative
                .Should()
                .BeOfType<DataStoreDerivativeInsertResult.Success>()
                .Subject.Id;
        }

        using HttpClient client = Client();
        foreach (string displayName in new[] { " CAFé ", " Renamed CAFé " })
        {
            if (displayName == " Renamed CAFé ")
            {
                using HttpResponseMessage renamed = await client.PutAsJsonAsync(
                    $"/v3/dataStores/{selectedId}",
                    new
                    {
                        Id = selectedId,
                        Name = displayName,
                        DataStoreType = "SQL",
                        Provider = provider,
                        ConnectionString = "Server=host;Database= CAFé ;",
                    }
                );
                renamed.StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
            foreach (
                string url in new[]
                {
                    "/v3/dataStores/?name=" + Uri.EscapeDataString(displayName) + "&orderBy=name&limit=1",
                    $"/v3/dataStores/{selectedId}",
                }
            )
            {
                using HttpResponseMessage response = await client.GetAsync(url);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                JsonNode row = body is JsonArray array ? array.Should().ContainSingle().Which! : body;
                row["id"]!.GetValue<int>().Should().Be(selectedId);
                row["name"]!.GetValue<string>().Should().Be(displayName);
                row["provider"]!.GetValue<string>().Should().Be(provider);
                encryption
                    .Decrypt(Convert.FromBase64String(row["connectionString"]!.GetValue<string>()))
                    .Should()
                    .Be("Server=host;Database= CAFé ;");
                JsonNode context = row["dataStoreContexts"]!.AsArray().Should().ContainSingle().Which!;
                context["id"]!.GetValue<int>().Should().Be(selectedContextId);
                context["dataStoreId"]!.GetValue<int>().Should().Be(selectedId);
                context["contextKey"]!.GetValue<string>().Should().Be("schoolYear");
                context["contextValue"]!.GetValue<string>().Should().Be(" CAFé ");
                JsonNode derivative = row["dataStoreDerivatives"]!.AsArray().Should().ContainSingle().Which!;
                derivative["id"]!.GetValue<int>().Should().Be(selectedDerivativeId);
                derivative["dataStoreId"]!.GetValue<int>().Should().Be(selectedId);
                derivative["derivativeType"]!.GetValue<string>().Should().Be("ReadReplica");
                encryption
                    .Decrypt(Convert.FromBase64String(derivative["connectionString"]!.GetValue<string>()))
                    .Should()
                    .Be("Server=replica;Database= CAFé ;");
            }
        }
    }

    [Test]
    public async Task It_creates_reads_and_renames_distinct_names_and_maps_duplicates_to_validation_400()
    {
        using HttpClient client = Client();
        // Legacy runs retain their original eligibility; 0040 must admit these previously
        // linguistic equivalents together even with the SQL Server CI_AI display column.
        string[] names = canonicalUniqueness
            ? ["café", "cafe", "CAFÉ", "cafe\u0301", "Store"]
            : ["café", "other", "Store"];
        List<int> ids = [];
        foreach (string name in names)
        {
            using HttpResponseMessage created = await client.PostAsJsonAsync(
                "/v3/dataStores/",
                new { Name = name, DataStoreType = "SQL" }
            );
            string content = await created.Content.ReadAsStringAsync();
            created.StatusCode.Should().Be(HttpStatusCode.Created, content);
            ids.Add(JsonNode.Parse(content)!["id"]!.GetValue<int>());
        }
        for (int i = 0; i < names.Length; i++)
        {
            await AssertNameReadsAsync(client, ids[i], names[i]);
        }

        string duplicate = canonicalUniqueness ? "\u3000sTORE\u00a0" : "Store";
        using HttpResponseMessage duplicatePost = await client.PostAsJsonAsync(
            "/v3/dataStores/",
            new { Name = duplicate, DataStoreType = "SQL" }
        );
        await AssertDuplicateAsync(duplicatePost);
        using HttpResponseMessage duplicatePut = await client.PutAsJsonAsync(
            $"/v3/dataStores/{ids[0]}",
            new
            {
                Id = ids[0],
                Name = duplicate,
                DataStoreType = "SQL",
            }
        );
        await AssertDuplicateAsync(duplicatePut);
        await AssertNameReadsAsync(client, ids[0], names[0]);

        string renamed = canonicalUniqueness ? " café-renamed " : "renamed";
        using HttpResponseMessage renamedResponse = await client.PutAsJsonAsync(
            $"/v3/dataStores/{ids[0]}",
            new
            {
                Id = ids[0],
                Name = renamed,
                DataStoreType = "SQL",
            }
        );
        renamedResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertNameReadsAsync(client, ids[0], renamed);
        using HttpResponseMessage oldName = await client.GetAsync(
            "/v3/dataStores/?name=" + Uri.EscapeDataString(names[0])
        );
        oldName.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await oldName.Content.ReadAsStringAsync())!.AsArray().Should().BeEmpty();
        await AssertNameReadsAsync(client, ids[1], names[1]);
    }

    private static async Task AssertDuplicateAsync(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Headers.Location.Should().BeNull();
        JsonNode.Parse(content)!["validationErrors"]!["Name"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .Should()
            .Equal("A data store with this name already exists.");
    }

    private static async Task AssertNameReadsAsync(HttpClient client, int id, string name)
    {
        foreach (
            string url in new[]
            {
                "/v3/dataStores/?name=" + Uri.EscapeDataString(name),
                $"/v3/dataStores/{id}",
            }
        )
        {
            using HttpResponseMessage response = await client.GetAsync(url);
            string content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            JsonNode body = JsonNode.Parse(content)!;
            JsonNode row = body is JsonArray array ? array.Should().ContainSingle().Which! : body;
            row["id"]!.GetValue<int>().Should().Be(id);
            row["name"]!.GetValue<string>().Should().Be(name);
            row.AsObject().Should().NotContainKey("nameComparisonKey");
        }
    }

    [TestCase("offset=-1")]
    [TestCase("limit=0")]
    [TestCase("limit=-1")]
    [TestCase("id=not-an-integer")]
    [TestCase("id=2147483648")]
    [TestCase("offset=2147483648")]
    [TestCase("limit=2147483648")]
    [TestCase("orderBy=")]
    [TestCase("orderBy=status")]
    [TestCase("orderBy=Name%3BDROP%20TABLE%20dmscs.DataStore")]
    [TestCase("direction=")]
    [TestCase("direction=sideways")]
    public async Task It_preserves_invalid_query_status(string query)
    {
        using HttpClient client = Client();
        using HttpResponseMessage response = await client.GetAsync("/v3/dataStores/?" + query);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task It_partitions_by_the_selected_tenant_without_treating_it_as_authorization()
    {
        if (!multitenant)
        {
            return;
        }
        int first = await InsertAsync(new("café"), _tenantA);
        int second = await InsertAsync(new("café"), _tenantB);
        foreach (var (tenant, id) in new[] { ("query-tenant-a", first), ("query-tenant-b", second) })
        {
            using HttpClient client = Client(AuthorizationScopes.ReadOnlyScope.Name, tenant);
            using HttpResponseMessage response = await client.GetAsync(
                "/v3/dataStores/?name=CAF%C3%A9&limit=1"
            );
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonArray rows = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
            rows.Select(row => row!["id"]!.GetValue<int>()).Should().Equal(id);
        }
        foreach (string? tenant in new string?[] { null, "nonexistent-tenant" })
        {
            using HttpClient client = Client(tenant: tenant);
            using HttpResponseMessage response = await client.GetAsync("/v3/dataStores/");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
}
