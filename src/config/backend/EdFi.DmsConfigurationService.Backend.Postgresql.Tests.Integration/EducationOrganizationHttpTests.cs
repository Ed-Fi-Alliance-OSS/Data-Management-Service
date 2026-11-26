// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

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
            """INSERT INTO dmscs."Tenant" ("Name") VALUES ('tenant-a') RETURNING "Id";"""
        );
        _otherTenant = await connection.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs."Tenant" ("Name") VALUES ('tenant-b') RETURNING "Id";"""
        );
        _host = ManagedDataStoreApiTestHost.Create(
            _ => { },
            provider: "postgresql",
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
            DROP TRIGGER IF EXISTS refuse_job_insert ON dmscs."Job";
            DROP FUNCTION IF EXISTS dmscs.refuse_job_insert();
            DELETE FROM dmscs."Job";
            DELETE FROM dmscs."DataStore";
            """
        );
    }

    private async Task<NpgsqlConnection> Open()
    {
        NpgsqlConnection connection = new(_database.ConnectionString);
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
            INSERT INTO dmscs."DataStore" ("Name", "DataStoreType", "Provider", "TenantId")
            VALUES (@Name, 'Shared', 'postgresql', @TenantId)
            RETURNING "Id";
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
                SELECT "JobId", "TenantId", "JobType", "PayloadVersion", "Payload", "Status", "AttemptCount"
                FROM dmscs."Job" ORDER BY "Id";
                """
            ),
        ];
    }

    private async Task<long> TargetCountAsync()
    {
        await using var connection = await Open();
        return await connection.ExecuteScalarAsync<long>(
            """SELECT COUNT(*) FROM dmscs."EducationOrganizationSnapshotTarget";"""
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
            await connection.ExecuteAsync("""DELETE FROM dmscs."Job";""");
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
                CREATE FUNCTION dmscs.refuse_job_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'refused-by-test-sentinel'; END $$;
                CREATE TRIGGER refuse_job_insert BEFORE INSERT ON dmscs."Job"
                FOR EACH ROW EXECUTE FUNCTION dmscs.refuse_job_insert();
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

/// <summary>
/// <c>GET /v3/tenants/{tenantName}/dataStores/edOrgs</c> through the production HTTP pipeline, against the shared test
/// database, with snapshots published through the target protocol. The selected partition is a fresh tenant in
/// multi-tenant mode and the data stores without a tenant in single-tenant mode; another tenant's data stores, and
/// the data stores of the other partition, are present in every test and never appear.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
[NonParallelizable]
[Category("EducationOrganizationHttp")]
public class Given_tenant_aggregates_over_HTTP(bool multitenant) : EducationOrganizationSnapshotReadTestBase
{
    // Numeric order differs from both text order and insertion order.
    private const int Ten = 910000010;
    private const int Two = 91000002;
    private const int Nine = 910000009;
    private const int Foreign = 91000001;
    private const int Unpartitioned = 91000003;
    private const string Admin = "edfi_admin_api/full_access";
    private const string ConnectionSecret = "Password=aggregate-connection-sentinel";
    private const string ContextSecret = "aggregate-context-sentinel";

    private WebApplicationFactory<Program> _host = null!;
    private long _tenant;
    private string _tenantName = null!;
    private long _otherTenant;
    private string _otherTenantName = null!;

    [OneTimeSetUp]
    public void StartHost() => _host = Host();

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [SetUp]
    public async Task InsertTenants()
    {
        _tenantName = "Aggregate_" + Guid.NewGuid().ToString("N");
        _otherTenantName = "Aggregate_" + Guid.NewGuid().ToString("N");
        _tenant = await InsertNamedTenantAsync(_tenantName);
        _otherTenant = await InsertNamedTenantAsync(_otherTenantName);
    }

    /// <summary>The production host, or one whose snapshot repository runs <paramref name="hooks"/>.</summary>
    private WebApplicationFactory<Program> Host(JobDatabaseSessionHooks? hooks = null) =>
        ManagedDataStoreApiTestHost.Create(
            services =>
            {
                if (hooks is not null)
                {
                    // Resolved in the route's scope, so the repository reads the tenant the route installed.
                    services.AddTransient<IEducationOrganizationSnapshotRepository>(provider =>
                        Repository(
                            tenant: provider.GetRequiredService<ITenantContextProvider>().Context,
                            hooks: hooks
                        )
                    );
                }
            },
            provider: "postgresql",
            connectionString: Configuration.DatabaseOptions.Value.DatabaseConnection,
            multitenant: multitenant
        );

    private async Task<long> InsertNamedTenantAsync(string name) =>
        await Connection!.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = name }
        );

    private long? Partition => multitenant ? _tenant : null;

    private TenantContext PartitionContext =>
        multitenant
            ? new TenantContext.Multitenant(_tenant, _tenantName)
            : new TenantContext.NotMultitenant();

    /// <summary>The tenant name of the response, and the route's name for it in this mode.</summary>
    private string CanonicalName => multitenant ? _tenantName : "default";

    private HttpClient Client(string? tenant)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", Admin);
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add("Tenant", tenant);
        }
        return client;
    }

    /// <summary>A client of the selected partition, whose route names it in another case than it is stored.</summary>
    private HttpClient PartitionClient(WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? _host).CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", Admin);
        if (multitenant)
        {
            client.DefaultRequestHeaders.Add("Tenant", _tenantName.ToLowerInvariant());
        }
        return client;
    }

    private string PartitionRoute =>
        $"/v3/tenants/{(multitenant ? _tenantName.ToUpperInvariant() : "DEFAULT")}/dataStores/edOrgs";

    private static string Text(string? value) => value is null ? "null" : $"\"{value}\"";

    private static string Number(long? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private static string OrganizationJson(EducationOrganizationProjectionItem item) =>
        $$"""{"educationOrganizationId":{{Number(item.EducationOrganizationId)}},"nameOfInstitution":{{Text(item.NameOfInstitution)}},"shortNameOfInstitution":{{Text(item.ShortNameOfInstitution)}},"discriminator":{{Text(item.Discriminator)}},"parentId":{{Number(item.ParentId)}}}""";

    private static string StoreJson(TenantAggregateDataStore store) =>
        $$"""{"id":{{Number(store.Id)}},"dataStoreManageId":{{Number(store.DataStoreManageId)}},"name":{{Text(store.Name)}},"dataStoreType":{{Text(store.DataStoreType)}},"status":{{Text(store.Status)}},"databaseTemplate":{{Text(store.DatabaseTemplate)}},"databaseName":{{Text(store.DatabaseName)}},"educationOrganizations":[{{string.Join(',', store.EducationOrganizations.Select(OrganizationJson))}}]}""";

    /// <summary>The exact response text of the selected tenant with <paramref name="stores"/>.</summary>
    private string Json(params TenantAggregateDataStore[] stores) =>
        $$"""{"id":{{Text(CanonicalName)}},"name":{{Text(CanonicalName)}},"dataStores":[{{string.Join(',', stores.Select(StoreJson))}}]}""";

    private static async Task<string> ReadOkAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        return text;
    }

    private static async Task<string> ProblemDetailAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        return JsonNode.Parse(text)!["detail"]!.GetValue<string>();
    }

    /// <summary>Data stores outside the selected partition, each with a published snapshot and a management record.</summary>
    private async Task SeedOtherPartitionsAsync()
    {
        await InsertDataStoreAsync(Foreign, "Foreign store", _otherTenant);
        await PublishAsync(
            Foreign,
            [Item(3)],
            repository: Repository(tenant: new TenantContext.Multitenant(_otherTenant, _otherTenantName))
        );
        await InsertManageAsync("Foreign pending", "PendingCreate", null, null, _otherTenant);
        long? unpartitioned = multitenant ? null : _tenant;
        await InsertDataStoreAsync(Unpartitioned, "Unpartitioned store", unpartitioned);
        await PublishAsync(
            Unpartitioned,
            [Item(4)],
            repository: Repository(
                tenant: unpartitioned is long id
                    ? new TenantContext.Multitenant(id, _tenantName)
                    : new TenantContext.NotMultitenant()
            )
        );
        await InsertManageAsync("Unpartitioned pending", "PendingCreate", null, null, unpartitioned);
    }

    [Test]
    public async Task It_returns_the_full_aggregate_of_snapshots_published_through_the_protocol()
    {
        EducationOrganizationSnapshotRepository repository = Repository(tenant: PartitionContext);
        await InsertDataStoreAsync(Ten, "Ten store", Partition, "Production");
        await InsertDataStoreAsync(Two, "Two store", Partition);
        await InsertDataStoreAsync(Nine, "Nine store", Partition);
        await InsertContextAsync(Ten, "secret", ContextSecret);
        await Connection!.ExecuteAsync(
            """UPDATE dmscs."DataStore" SET "ConnectionString" = convert_to(@Value, 'UTF8') WHERE "Id" = @Id;""",
            new { Value = ConnectionSecret, Id = Two }
        );
        EducationOrganizationProjectionItem[] tenOrganizations =
        [
            Organization(2, "Two Elementary", null, "edfi.School", 255901),
            Organization(9, "Nine Middle", "Nine", "edfi.School", null),
            Organization(10, "Ten District", null, "edfi.LocalEducationAgency", null),
            Organization(2147483649, "Wide High", null, "edfi.School", 10),
            Organization(9007199254740993, "Beyond Academy", null, "edfi.School", null),
            Organization(long.MaxValue - 1, "Largest Academy", "Largest", "edfi.School", 9007199254740993),
        ];
        // Staged in another order than the identifiers: the response orders them.
        await PublishAsync(Ten, [.. tenOrganizations.Reverse()], repository: repository);
        ManageRecord linked = await InsertManageAsync("Two store", "Created", Two, Two, Partition);
        await PublishAsync(Two, [Item(1)], prune: false, repository: repository);
        await PublishAsync(Two, [Item(5)], repository: repository);
        ManageRecord pending = await InsertManageAsync(
            "Pending store",
            "PendingCreate",
            null,
            null,
            Partition
        );
        ManageRecord deleted = await InsertManageAsync("Deleted store", "Deleted", null, 77, Partition);
        await SeedOtherPartitionsAsync();

        using var client = PartitionClient();
        using var response = await client.GetAsync(PartitionRoute);
        string text = await ReadOkAsync(response);

        text.Should()
            .Be(
                Json(
                    new TenantAggregateDataStore(
                        Two,
                        linked.Id,
                        "Two store",
                        "Shared",
                        "Created",
                        "Minimal",
                        linked.TargetDatabaseName,
                        [Item(5)]
                    ),
                    Unmanaged(Nine, "Nine store"),
                    new TenantAggregateDataStore(
                        Ten,
                        null,
                        "Ten store",
                        "Production",
                        "Created",
                        null,
                        null,
                        tenOrganizations
                    ),
                    Unlinked(pending, "Pending store", "PendingCreate", published: false),
                    Unlinked(deleted, "Deleted store", "Deleted", published: true)
                )
            );
        text.Should()
            .Contain("\"educationOrganizationId\":9007199254740993,")
            .And.Contain("\"educationOrganizationId\":9223372036854775806,");
        foreach (
            string sentinel in new[]
            {
                ConnectionSecret,
                ContextSecret,
                ContractVersion,
                "Foreign",
                "Unpartitioned",
                "tenantId",
                "expectedSourceIdentity",
                "generation",
            }
        )
        {
            text.Should().NotContainEquivalentOf(sentinel);
        }
    }

    [Test]
    public async Task It_applies_the_tenant_rules_of_the_route_against_the_database()
    {
        await InsertDataStoreAsync(Ten, "Ten store", Partition);
        await PublishAsync(Ten, [Item(6)], repository: Repository(tenant: PartitionContext));
        await SeedOtherPartitionsAsync();
        string own = Json(Unmanaged(Ten, "Ten store", Item(6)));
        if (multitenant)
        {
            string route = $"/v3/tenants/{_tenantName}/dataStores/edOrgs";
            using (var missing = Client(null))
            using (var response = await missing.GetAsync(route))
            {
                (await ProblemDetailAsync(response, HttpStatusCode.BadRequest))
                    .Should()
                    .Be("The 'Tenant' header is required when multi-tenancy is enabled");
            }
            using (var mismatched = Client(_otherTenantName))
            using (var response = await mismatched.GetAsync(route))
            {
                (await ProblemDetailAsync(response, HttpStatusCode.BadRequest))
                    .Should()
                    .Be("The 'Tenant' header does not match the tenant name in the route.");
            }
            string unknownName = "Aggregate_" + Guid.NewGuid().ToString("N");
            using (var unknown = Client(unknownName))
            using (var response = await unknown.GetAsync($"/v3/tenants/{unknownName}/dataStores/edOrgs"))
            {
                (await ProblemDetailAsync(response, HttpStatusCode.NotFound))
                    .Should()
                    .Be("Tenant not found.");
            }
            // The other tenant reads only its own data store and management record.
            using var other = Client(_otherTenantName);
            using var otherResponse = await other.GetAsync(
                $"/v3/tenants/{_otherTenantName}/dataStores/edOrgs"
            );
            var otherBody = JsonNode.Parse(await ReadOkAsync(otherResponse))!;
            otherBody["name"]!.GetValue<string>().Should().Be(_otherTenantName);
            otherBody["dataStores"]!
                .AsArray()
                .Select(store => store!["name"]!.GetValue<string>())
                .Should()
                .Equal("Foreign store", "Foreign pending");
        }
        else
        {
            using (var unknown = Client(null))
            using (var response = await unknown.GetAsync($"/v3/tenants/{_tenantName}/dataStores/edOrgs"))
            {
                (await ProblemDetailAsync(response, HttpStatusCode.NotFound))
                    .Should()
                    .Be("Tenant not found.");
            }
            // A header naming a real tenant is ignored: the response is the partition without a tenant.
            using var withHeader = Client(_otherTenantName);
            using var response2 = await withHeader.GetAsync("/v3/tenants/Default/dataStores/edOrgs");
            (await ReadOkAsync(response2)).Should().Be(own);
        }
        using var client = PartitionClient();
        using var ownResponse = await client.GetAsync(PartitionRoute);
        (await ReadOkAsync(ownResponse)).Should().Be(own);
    }

    [Test]
    public async Task It_leaves_no_snapshot_of_a_data_store_deleted_over_HTTP()
    {
        EducationOrganizationSnapshotRepository repository = Repository(tenant: PartitionContext);
        await InsertDataStoreAsync(Ten, "Ten store", Partition);
        await InsertDataStoreAsync(Two, "Two store", Partition);
        await PublishAsync(Ten, [Item(1), Item(2)], prune: false, repository: repository);
        await PublishAsync(Ten, [Item(3)], prune: false, repository: repository);
        await PublishAsync(Two, [Item(4)], repository: repository);
        using var client = PartitionClient();
        using (var deleted = await client.DeleteAsync($"/v3/dataStores/{Ten}"))
        {
            deleted
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }
        (await TargetCountAsync(Ten)).Should().Be(0);
        (await OrganizationCountAsync(Ten)).Should().Be(0);
        (await ActiveIdsAsync(Two)).Should().Equal(4);
        using var response = await client.GetAsync(PartitionRoute);
        (await ReadOkAsync(response)).Should().Be(Json(Unmanaged(Two, "Two store", Item(4))));
    }

    [Test]
    public async Task It_returns_the_state_before_a_delete_that_commits_during_the_read()
    {
        EducationOrganizationSnapshotRepository repository = Repository(tenant: PartitionContext);
        await InsertDataStoreAsync(Ten, "Ten store", Partition);
        await InsertDataStoreAsync(Two, "Two store", Partition);
        await PublishAsync(Ten, [Item(1), Item(2)], repository: repository);
        await PublishAsync(Two, [Item(4)], repository: repository);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        JobDatabaseSessionHooks hooks = new()
        {
            BeforeOperation = async (operation, _) =>
            {
                if (operation == TenantAggregateReads.BeforeOrganizations)
                {
                    reached.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
            },
        };
        await using var hooked = Host(hooks);
        using var reader = PartitionClient(hooked);
        Task<HttpResponseMessage> reading = reader.GetAsync(PartitionRoute);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var client = PartitionClient();
            using var deleted = await client.DeleteAsync($"/v3/dataStores/{Ten}");
            deleted
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }
        finally
        {
            release.TrySetResult();
        }

        // Read before the delete committed: the deleted store with its whole snapshot, never part of it.
        using (var before = await reading)
        {
            (await ReadOkAsync(before))
                .Should()
                .Be(
                    Json(Unmanaged(Two, "Two store", Item(4)), Unmanaged(Ten, "Ten store", Item(1), Item(2)))
                );
        }
        using var after = await PartitionClient().GetAsync(PartitionRoute);
        (await ReadOkAsync(after)).Should().Be(Json(Unmanaged(Two, "Two store", Item(4))));
    }
}
