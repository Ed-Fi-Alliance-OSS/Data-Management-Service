// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The education organization refresh routes through the production HTTP pipeline, against a database deployed with
/// every migration: the real tenant middleware, existence check, enqueuer and job status route. In multi-tenant mode
/// the selected tenant is <c>tenant-a</c>; in single-tenant mode the partition is the data stores without a tenant.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
[NonParallelizable]
[Category("EducationOrganizationHttp")]
public class Given_education_organization_refresh_over_HTTP(bool multitenant)
{
    private const string Admin = "edfi_admin_api/full_access";
    private const string AllRoute = "/v3/dataStores/edOrgs/refresh";
    private const string AllMessage = "Education organizations refresh has been queued for all instances";
    private const string OneMessage =
        "Education organizations refresh has been queued for the specified instance";
    private const string Unset = "\0";

    private JobUpgradeTestDatabase _database = null!;
    private WebApplicationFactory<Program> _host = null!;
    private long _selectedTenant;
    private long _otherTenant;

    private sealed record JobRow(
        string JobId,
        long? TenantId,
        string JobType,
        short PayloadVersion,
        string Payload,
        string Status,
        int AttemptCount
    );

    private long? Partition => multitenant ? _selectedTenant : null;

    private static string OneRoute(object dataStoreId) => $"/v3/dataStores/{dataStoreId}/edOrgs/refresh";

    [OneTimeSetUp]
    public async Task Deploy()
    {
        _database = new();
        _database.DeployThrough(42);
        await using var connection = await Open();
        _selectedTenant = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO dmscs.Tenant (Name) OUTPUT inserted.Id VALUES (N'tenant-a');"
        );
        _otherTenant = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO dmscs.Tenant (Name) OUTPUT inserted.Id VALUES (N'tenant-b');"
        );
        _host = ManagedDataStoreApiTestHost.Create(
            _ => { },
            provider: "mssql",
            connectionString: _database.ConnectionString,
            multitenant: multitenant
        );
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DropAsync();
        }
    }

    [SetUp]
    public async Task Clear()
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """
            DROP TRIGGER IF EXISTS dmscs.RefuseJobInsert;
            DELETE FROM dmscs.Job;
            DELETE FROM dmscs.DataStore;
            """
        );
    }

    private async Task<SqlConnection> Open()
    {
        SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>A client of the selected partition, unless another tenant header (or none) is given.</summary>
    private HttpClient Client(string? scope = Admin, string? tenant = Unset, string? pathBase = null)
    {
        var client = _host.CreateClient();
        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }
        string? header = tenant;
        if (tenant == Unset)
        {
            header = multitenant ? "tenant-a" : null;
        }
        if (header is not null)
        {
            client.DefaultRequestHeaders.Add("Tenant", header);
        }
        if (pathBase is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-PathBase", pathBase);
        }
        return client;
    }

    private async Task<int> InsertDataStoreAsync(string name, long? tenantId)
    {
        await using var connection = await Open();
        return await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStore (Name, DataStoreType, Provider, TenantId)
            OUTPUT inserted.Id
            VALUES (@Name, N'Shared', N'sqlserver', @TenantId);
            """,
            new { Name = name, TenantId = tenantId }
        );
    }

    private async Task<JobRow[]> JobsAsync()
    {
        await using var connection = await Open();
        return
        [
            .. await connection.QueryAsync<JobRow>(
                """
                SELECT JobId, TenantId, JobType, PayloadVersion, Payload, Status, AttemptCount
                FROM dmscs.Job ORDER BY Id;
                """
            ),
        ];
    }

    private async Task<long> TargetCountAsync()
    {
        await using var connection = await Open();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM dmscs.EducationOrganizationSnapshotTarget;"
        );
    }

    /// <summary>
    /// The 202 contract: exactly the two jobQueuedResult properties, a Location relative to
    /// <paramref name="pathBase"/> that names the same job, and that job stored Pending in the selected partition.
    /// </summary>
    private async Task<string> AssertQueuedAsync(
        HttpResponseMessage response,
        string message,
        string payload,
        string pathBase = ""
    )
    {
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = JsonNode.Parse(text)!.AsObject();
        body.Select(property => property.Key).Should().Equal("jobId", "message");
        body["message"]!.GetValue<string>().Should().Be(message);
        string jobId = body["jobId"]!.GetValue<string>();
        Guid.TryParse(jobId, out _).Should().BeTrue(jobId);
        response.Headers.Location!.IsAbsoluteUri.Should().BeFalse();
        response.Headers.Location.OriginalString.Should().Be($"{pathBase}/v3/jobs/{jobId}");
        (await JobsAsync())
            .Should()
            .Equal(new JobRow(jobId, Partition, "EducationOrganization.Refresh", 1, payload, "Pending", 0));
        return jobId;
    }

    /// <summary>An immediate poll of the returned Location reads the job Pending, in the selected tenant only.</summary>
    private async Task AssertPendingAsync(HttpClient client, HttpResponseMessage accepted, string jobId)
    {
        using var poll = await client.GetAsync(accepted.Headers.Location);
        string text = await poll.Content.ReadAsStringAsync();
        poll.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var status = JsonNode.Parse(text)!.AsObject();
        status["jobId"]!.GetValue<string>().Should().Be(jobId);
        status["status"]!.GetValue<string>().Should().Be("Pending");
        status["finishedAt"].Should().BeNull();
        status["errorMessage"].Should().BeNull();
        if (multitenant)
        {
            using var other = Client(tenant: "tenant-b");
            using var absent = await other.GetAsync($"/v3/jobs/{jobId}");
            absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    private static async Task<JsonObject> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status
    )
    {
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.Location.Should().BeNull();
        return JsonNode.Parse(text)!.AsObject();
    }

    private async Task AssertNothingQueuedAsync()
    {
        (await JobsAsync()).Should().BeEmpty();
        (await TargetCountAsync()).Should().Be(0);
    }

    [Test]
    public async Task It_queues_a_refresh_of_every_data_store_and_polls_it_pending()
    {
        await InsertDataStoreAsync("first", Partition);
        using var client = Client();
        using var response = await client.PostAsync(AllRoute, null);
        string jobId = await AssertQueuedAsync(response, AllMessage, """{"scope":1,"dataStoreId":0}""");
        await AssertPendingAsync(client, response, jobId);
        (await TargetCountAsync()).Should().Be(0);
    }

    [Test]
    public async Task It_queues_a_refresh_of_one_data_store_of_the_partition_and_polls_it_pending()
    {
        int dataStoreId = await InsertDataStoreAsync("one", Partition);
        using var client = Client();
        using var response = await client.PostAsync(OneRoute(dataStoreId), null);
        string jobId = await AssertQueuedAsync(
            response,
            OneMessage,
            $$"""{"scope":2,"dataStoreId":{{dataStoreId}}}"""
        );
        await AssertPendingAsync(client, response, jobId);
        (await TargetCountAsync()).Should().Be(0);
    }

    [TestCase("/cms"), TestCase("/cms/")]
    public async Task It_returns_a_Location_that_resolves_under_the_path_base(string pathBase)
    {
        int dataStoreId = await InsertDataStoreAsync("based", Partition);
        using var client = Client(pathBase: pathBase);
        using (var all = await client.PostAsync("/cms" + AllRoute, null))
        {
            string jobId = await AssertQueuedAsync(
                all,
                AllMessage,
                """{"scope":1,"dataStoreId":0}""",
                "/cms"
            );
            await AssertPendingAsync(client, all, jobId);
        }
        await using (var connection = await Open())
        {
            await connection.ExecuteAsync("DELETE FROM dmscs.Job;");
        }
        using var one = await client.PostAsync("/cms" + OneRoute(dataStoreId), null);
        string oneJobId = await AssertQueuedAsync(
            one,
            OneMessage,
            $$"""{"scope":2,"dataStoreId":{{dataStoreId}}}""",
            "/cms"
        );
        await AssertPendingAsync(client, one, oneJobId);
    }

    [TestCase("absent")]
    [TestCase("other tenant")]
    [TestCase("other partition")]
    public async Task It_returns_404_and_queues_nothing_for_a_data_store_outside_the_partition(string where)
    {
        int ownId = await InsertDataStoreAsync("own", Partition);
        int dataStoreId = where switch
        {
            "absent" => ownId + 100,
            "other tenant" => await InsertDataStoreAsync("foreign", _otherTenant),
            // The data stores of no tenant in multi-tenant mode; another tenant's in single-tenant mode.
            _ => await InsertDataStoreAsync("unpartitioned", multitenant ? null : _selectedTenant),
        };
        using var client = Client();
        using var response = await client.PostAsync(OneRoute(dataStoreId), null);
        var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound);
        problem["detail"]!.GetValue<string>().Should().Be("Data store not found.");
        await AssertNothingQueuedAsync();
    }

    [TestCase("0", HttpStatusCode.NotFound)]
    [TestCase("-1", HttpStatusCode.NotFound)]
    [TestCase("abc", HttpStatusCode.BadRequest)]
    [TestCase("2147483648", HttpStatusCode.BadRequest)]
    public async Task It_answers_a_data_store_id_that_names_no_data_store_without_a_job(
        string dataStoreId,
        HttpStatusCode status
    )
    {
        await InsertDataStoreAsync("present", Partition);
        using var client = Client();
        using var response = await client.PostAsync(OneRoute(dataStoreId), null);
        await AssertProblemAsync(response, status);
        await AssertNothingQueuedAsync();
    }

    [Test]
    public async Task It_applies_the_tenant_header_rules_of_the_tenancy_mode()
    {
        int dataStoreId = await InsertDataStoreAsync("headers", Partition);
        if (multitenant)
        {
            foreach (string route in new[] { AllRoute, OneRoute(dataStoreId) })
            {
                using var missing = Client(tenant: null);
                using var withoutHeader = await missing.PostAsync(route, null);
                (await AssertProblemAsync(withoutHeader, HttpStatusCode.BadRequest))["detail"]!
                    .GetValue<string>()
                    .Should()
                    .Be("The 'Tenant' header is required when multi-tenancy is enabled");
                using var unknown = Client(tenant: "tenant-z");
                using var unknownTenant = await unknown.PostAsync(route, null);
                (await AssertProblemAsync(unknownTenant, HttpStatusCode.BadRequest))["detail"]!
                    .GetValue<string>()
                    .Should()
                    .Be("Invalid tenant: tenant-z");
            }
            using var foreign = Client(tenant: "tenant-b");
            using var otherTenant = await foreign.PostAsync(OneRoute(dataStoreId), null);
            await AssertProblemAsync(otherTenant, HttpStatusCode.NotFound);
            await AssertNothingQueuedAsync();
        }
        else
        {
            // Single-tenant mode ignores the header: the job belongs to the data stores without a tenant.
            using var client = Client(tenant: "tenant-b");
            using var response = await client.PostAsync(OneRoute(dataStoreId), null);
            await AssertQueuedAsync(response, OneMessage, $$"""{"scope":2,"dataStoreId":{{dataStoreId}}}""");
        }
    }

    [TestCase(AllRoute)]
    [TestCase("one")]
    public async Task It_returns_500_without_Location_when_the_job_cannot_be_stored(string route)
    {
        int dataStoreId = await InsertDataStoreAsync("refused", Partition);
        await using (var connection = await Open())
        {
            await connection.ExecuteAsync(
                """
                CREATE TRIGGER dmscs.RefuseJobInsert ON dmscs.Job INSTEAD OF INSERT AS
                BEGIN
                    THROW 50001, N'refused-by-test-sentinel', 1;
                END;
                """
            );
        }
        using var client = Client();
        using var response = await client.PostAsync(
            route == AllRoute ? AllRoute : OneRoute(dataStoreId),
            null
        );
        var problem = await AssertProblemAsync(response, HttpStatusCode.InternalServerError);
        problem.ToJsonString().Should().NotContain("refused-by-test-sentinel");
        await AssertNothingQueuedAsync();
    }

    [TestCase("edfi_admin_api/readonly_access", HttpStatusCode.Forbidden)]
    [TestCase(null, HttpStatusCode.Unauthorized)]
    public async Task It_refuses_a_caller_without_the_admin_scope_without_a_job(
        string? scope,
        HttpStatusCode status
    )
    {
        int dataStoreId = await InsertDataStoreAsync("guarded", Partition);
        using var client = Client(scope);
        foreach (string route in new[] { AllRoute, OneRoute(dataStoreId) })
        {
            using var response = await client.PostAsync(route, null);
            await AssertProblemAsync(response, status);
        }
        await AssertNothingQueuedAsync();
    }
}
