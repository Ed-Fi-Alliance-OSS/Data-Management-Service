// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// <c>GET /v3/tenants/{tenantName}/dataStores/edOrgs</c> and the excluded <c>GET /v3/dataStores/edOrgs</c>, with a
/// tenant repository fake and a snapshot repository probe that records the tenant context of the scope resolving it.
/// In multi-tenant mode the known tenant is <c>Tenant_A</c> (identifier 41). The tests here run in both tenancy
/// modes; each nested fixture adds the rules of its own mode.
/// </summary>
public abstract class TenantAggregateHttpTests(bool multitenant)
{
    private const string Secret = "tenant-lookup-secret-sentinel";

    private readonly ITenantRepository _tenants = A.Fake<ITenantRepository>();
    private readonly IEducationOrganizationSnapshotRepository _snapshots =
        A.Fake<IEducationOrganizationSnapshotRepository>();
    private readonly IDataStoreRepository _dataStores = A.Fake<IDataStoreRepository>();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ConcurrentQueue<TenantContext> _resolvedWith = new();
    private readonly ConcurrentQueue<TenantContext> _requestScopes = new();
    private WebApplicationFactory<Program> _factory = null!;
    private SnapshotTenantAggregateResult _aggregate = null!;
    private TenantGetByNameResult _lookup = null!;
    private int _logsBefore;

    /// <summary>
    /// Data stores in the order the repository returns them: the route keeps it. Organization identifiers include
    /// 2, 9, 10, 2³¹+1, 2⁵³+1 and <see cref="long.MaxValue"/> − 1, which must reach the response as exact digits.
    /// </summary>
    private static SnapshotTenantAggregateResult Aggregate() =>
        new SnapshotTenantAggregateResult.Success([
            new TenantAggregateDataStore(
                3,
                7,
                "Linked",
                "Production",
                "Created",
                "Minimal",
                "edfi_linked",
                [
                    new EducationOrganizationProjectionItem(2, "Two", null, "edfi.School", 255901),
                    new EducationOrganizationProjectionItem(9, "Nine", "N", "edfi.School", null),
                    new EducationOrganizationProjectionItem(
                        10,
                        "Ten",
                        null,
                        "edfi.LocalEducationAgency",
                        null
                    ),
                    new EducationOrganizationProjectionItem(2147483649, "Wide", null, "edfi.School", 10),
                    new EducationOrganizationProjectionItem(
                        9007199254740993,
                        "Beyond",
                        null,
                        "edfi.School",
                        null
                    ),
                    new EducationOrganizationProjectionItem(
                        long.MaxValue - 1,
                        "Largest",
                        null,
                        "edfi.School",
                        9007199254740993
                    ),
                ]
            ),
            new TenantAggregateDataStore(12, null, "Unmanaged", "Shared", "Created", null, null, []),
            new TenantAggregateDataStore(null, 8, "Pending", null, "PendingCreate", "Sample", null, []),
        ]);

    private static string ExpectedBody(string tenantName) =>
        $$"""{"id":"{{tenantName}}","name":"{{tenantName}}","dataStores":[{"id":3,"dataStoreManageId":7,"name":"Linked","dataStoreType":"Production","status":"Created","databaseTemplate":"Minimal","databaseName":"edfi_linked","educationOrganizations":[{"educationOrganizationId":2,"nameOfInstitution":"Two","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":255901},{"educationOrganizationId":9,"nameOfInstitution":"Nine","shortNameOfInstitution":"N","discriminator":"edfi.School","parentId":null},{"educationOrganizationId":10,"nameOfInstitution":"Ten","shortNameOfInstitution":null,"discriminator":"edfi.LocalEducationAgency","parentId":null},{"educationOrganizationId":2147483649,"nameOfInstitution":"Wide","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":10},{"educationOrganizationId":9007199254740993,"nameOfInstitution":"Beyond","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null},{"educationOrganizationId":9223372036854775806,"nameOfInstitution":"Largest","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":9007199254740993}]},{"id":12,"dataStoreManageId":null,"name":"Unmanaged","dataStoreType":"Shared","status":"Created","databaseTemplate":null,"databaseName":null,"educationOrganizations":[]},{"id":null,"dataStoreManageId":8,"name":"Pending","dataStoreType":null,"status":"PendingCreate","databaseTemplate":"Sample","databaseName":null,"educationOrganizations":[]}]}""";

    [OneTimeSetUp]
    public void Start() =>
        _factory = ManagedDataStoreApiTestHost.Create(
            services =>
            {
                services.AddSingleton<ILoggerProvider>(_logs);
                services.AddTransient(_ => _tenants);
                services.AddTransient(_ => _dataStores);
                services.AddTransient(provider =>
                {
                    _resolvedWith.Enqueue(provider.GetRequiredService<ITenantContextProvider>().Context);
                    return _snapshots;
                });
                services.AddSingleton<IStartupFilter>(new RequestScopeProbe(_requestScopes));
            },
            multitenant: multitenant
        );

    [OneTimeTearDown]
    public async Task Stop() => await _factory.DisposeAsync();

    [SetUp]
    public void Reset()
    {
        Fake.ClearRecordedCalls(_tenants);
        Fake.ClearRecordedCalls(_snapshots);
        Fake.ClearRecordedCalls(_dataStores);
        _resolvedWith.Clear();
        _requestScopes.Clear();
        _logsBefore = _logs.Entries.Count;
        _aggregate = Aggregate();
        _lookup = new TenantGetByNameResult.Success(new TenantResponse { Id = 41, Name = "Tenant_A" });
        A.CallTo(() => _tenants.GetTenantByName(A<string>._)).ReturnsLazily(() => Task.FromResult(_lookup));
        A.CallTo(() => _snapshots.GetTenantAggregateAsync(A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(_aggregate));
        A.CallTo(() => _dataStores.GetDataStore(A<int>._)).Returns(new DataStoreGetResult.FailureNotFound());
    }

    /// <summary>Records the request scope's tenant context once the rest of the pipeline has run.</summary>
    private sealed class RequestScopeProbe(ConcurrentQueue<TenantContext> contexts) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, continuation) =>
                    {
                        await continuation();
                        contexts.Enqueue(
                            context.RequestServices.GetRequiredService<ITenantContextProvider>().Context
                        );
                    }
                );
                next(app);
            };
    }

    private HttpClient Client(string? tenant = null, string? scope = "edfi_admin_api/full_access")
    {
        var client = _factory.CreateClient();
        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add("Tenant", tenant);
        }
        return client;
    }

    /// <summary>The Tenant header of an ordinary request in this mode.</summary>
    private string? SelectedHeader => multitenant ? "Tenant_A" : null;

    /// <summary>The route's tenant name that reads the aggregate in this mode.</summary>
    private string SelectedRouteName => multitenant ? "Tenant_A" : "default";

    private static string Route(string tenantName) => $"/v3/tenants/{tenantName}/dataStores/edOrgs";

    private static async Task<JsonObject> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string WithoutCorrelation(JsonObject problem)
    {
        problem.Remove("correlationId");
        return problem.ToJsonString();
    }

    private void AssertNothingResolved()
    {
        _resolvedWith.Should().BeEmpty();
        A.CallTo(() => _snapshots.GetTenantAggregateAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    private void AssertNoLookup() =>
        A.CallTo(() => _tenants.GetTenantByName(A<string>._)).MustNotHaveHappened();

    /// <summary>Each request's own scope ends as it started, whatever the route installed for the read.</summary>
    private void AssertRequestScopesUntouched() =>
        _requestScopes.Should().NotBeEmpty().And.AllBeOfType<TenantContext.NotMultitenant>();

    private IReadOnlyList<CapturedLog> NewLogs() => [.. _logs.Entries.Skip(_logsBefore)];

    [Test]
    public async Task It_returns_500_when_the_aggregate_read_fails()
    {
        _aggregate = new SnapshotTenantAggregateResult.DbFailure(
            new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "1222", "AcquireTenantGate")
        );
        using var client = Client(SelectedHeader);
        using var response = await client.GetAsync(Route(SelectedRouteName));
        await AssertProblem(response, HttpStatusCode.InternalServerError);
        var logged = NewLogs().Single(log => log.EventId.Name == "TenantAggregateReadFailed");
        logged.EventId.Id.Should().Be(1441_41);
        logged.Level.Should().Be(LogLevel.Error);
        logged.State["Request"].Should().Be("GetTenantAggregate");
        logged.State["ProviderErrorCode"].Should().Be("1222");
        logged.State["Operation"].Should().Be("AcquireTenantGate");
    }

    [Test]
    public async Task It_serves_a_read_only_client_and_challenges_an_anonymous_one()
    {
        using (var readOnly = Client(SelectedHeader, "edfi_admin_api/readonly_access"))
        {
            using var response = await readOnly.GetAsync(Route(SelectedRouteName));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        Fake.ClearRecordedCalls(_tenants);
        Fake.ClearRecordedCalls(_snapshots);
        _resolvedWith.Clear();
        using var anonymous = Client(SelectedHeader, scope: null);
        using var challenged = await anonymous.GetAsync(Route(SelectedRouteName));
        await AssertProblem(challenged, HttpStatusCode.Unauthorized);
        AssertNoLookup();
        AssertNothingResolved();
    }

    [TestCase("/v3/dataStores/edOrgs")]
    [TestCase("/v3/dataStores/edOrgs/")]
    [TestCase("/v3/dataStores/1/edOrgs")]
    public async Task It_answers_an_excluded_GET_as_an_unknown_route(string path)
    {
        using var client = Client(SelectedHeader);
        using var response = await client.GetAsync(path);
        var problem = await AssertProblem(response, HttpStatusCode.NotFound);
        problem["detail"]!.GetValue<string>().Should().Be("The requested resource could not be found.");
        using var unknown = await client.GetAsync("/v3/no-such-route");
        var expected = await AssertProblem(unknown, HttpStatusCode.NotFound);
        WithoutCorrelation(problem).Should().Be(WithoutCorrelation(expected));
        using var anonymous = Client(SelectedHeader, scope: null);
        using var challenged = await anonymous.GetAsync(path);
        await AssertProblem(challenged, HttpStatusCode.Unauthorized);
        if (multitenant)
        {
            using var withoutTenant = Client();
            using var missing = await withoutTenant.GetAsync(path);
            (await AssertProblem(missing, HttpStatusCode.BadRequest))["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The 'Tenant' header is required when multi-tenancy is enabled");
        }
        AssertNothingResolved();
        A.CallTo(() => _dataStores.GetDataStore(A<int>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_keeps_the_data_store_identifier_routes_unchanged()
    {
        using var client = Client(SelectedHeader);
        using var absent = await client.GetAsync("/v3/dataStores/1");
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        JsonNode.Parse(await absent.Content.ReadAsStringAsync())!["detail"]!
            .GetValue<string>()
            .Should()
            .Be("DataStore 1 not found. It may have been recently deleted.");
        A.CallTo(() => _dataStores.GetDataStore(1)).MustHaveHappenedOnceExactly();
        using var invalid = await client.GetAsync("/v3/dataStores/edOrgsX");
        await AssertProblem(invalid, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task It_documents_the_aggregate_and_hides_the_excluded_GET()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var paths = document["paths"]!.AsObject();
        paths.Select(path => path.Key).Should().NotContain("/v3/dataStores/edOrgs");
        var operation = paths["/v3/tenants/{tenantName}/dataStores/edOrgs"]!["get"]!;
        operation["responses"]!
            .AsObject()
            .Select(status => status.Key)
            .Should()
            .BeEquivalentTo("200", "400", "401", "403", "404", "500");
        var parameter = operation["parameters"]!.AsArray().Single()!;
        parameter["name"]!.GetValue<string>().Should().Be("tenantName");
        parameter["in"]!.GetValue<string>().Should().Be("path");
        string reference = operation["responses"]!["200"]!["content"]!["application/json"]!["schema"]![
            "$ref"
        ]!.GetValue<string>();
        var schemas = document["components"]!["schemas"]!;
        schemas[reference.Split('/')[^1]]!["properties"]!
            .AsObject()
            .Select(property => property.Key)
            .Should()
            .Equal("id", "name", "dataStores");
        schemas[nameof(TenantDataStoreResponse)]!["properties"]!
            .AsObject()
            .Select(property => property.Key)
            .Should()
            .Equal(
                "id",
                "dataStoreManageId",
                "name",
                "dataStoreType",
                "status",
                "databaseTemplate",
                "databaseName",
                "educationOrganizations"
            );
        schemas[nameof(EducationOrganizationResponse)]!["properties"]!
            .AsObject()
            .Select(property => property.Key)
            .Should()
            .Equal(
                "educationOrganizationId",
                "nameOfInstitution",
                "shortNameOfInstitution",
                "discriminator",
                "parentId"
            );
    }

    [NonParallelizable]
    public class Given_tenant_aggregate_requests_in_multi_tenant_mode() : TenantAggregateHttpTests(true)
    {
        [TestCase("Tenant_A", "Tenant_A")]
        [TestCase("TENANT_A", "tenant_a")]
        public async Task It_reads_the_aggregate_of_the_tenant_in_a_scope_that_carries_it(
            string route,
            string header
        )
        {
            using var client = Client(header);
            using var response = await client.GetAsync(Route(route));
            string text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, text);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            text.Should().Be(ExpectedBody("Tenant_A"));
            A.CallTo(() => _tenants.GetTenantByName(route)).MustHaveHappenedOnceExactly();
            _resolvedWith.Should().Equal(new TenantContext.Multitenant(41, "Tenant_A"));
            AssertRequestScopesUntouched();
        }

        [TestCase(null), TestCase("   ")]
        public async Task It_returns_the_tenant_middleware_400_for_a_missing_header_before_any_lookup(
            string? header
        )
        {
            using var client = Client(header);
            using var response = await client.GetAsync(Route("Tenant_A"));
            var problem = await AssertProblem(response, HttpStatusCode.BadRequest);
            problem["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The 'Tenant' header is required when multi-tenancy is enabled");
            // The body the tenant middleware writes for every other tenant-scoped route.
            using var middleware = await client.GetAsync("/v3/dataStores/1");
            var expected = await AssertProblem(middleware, HttpStatusCode.BadRequest);
            WithoutCorrelation(problem).Should().Be(WithoutCorrelation(expected));
            AssertNoLookup();
            AssertNothingResolved();
        }

        [Test]
        public async Task It_returns_400_for_a_header_that_names_another_tenant_before_any_lookup()
        {
            using var client = Client("Tenant_B");
            using var response = await client.GetAsync(Route("Tenant_A"));
            var problem = await AssertProblem(response, HttpStatusCode.BadRequest);
            problem["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The 'Tenant' header does not match the tenant name in the route.");
            AssertNoLookup();
            AssertNothingResolved();
            AssertRequestScopesUntouched();
        }

        [Test]
        public async Task It_returns_404_for_an_unknown_tenant_without_resolving_the_repository()
        {
            _lookup = new TenantGetByNameResult.FailureNotFound();
            using var client = Client("Tenant_Z");
            using var response = await client.GetAsync(Route("Tenant_Z"));
            var problem = await AssertProblem(response, HttpStatusCode.NotFound);
            problem["detail"]!.GetValue<string>().Should().Be("Tenant not found.");
            A.CallTo(() => _tenants.GetTenantByName("Tenant_Z")).MustHaveHappenedOnceExactly();
            AssertNothingResolved();
            AssertRequestScopesUntouched();
        }

        [Test]
        public async Task It_returns_500_without_the_lookup_message_when_the_tenant_lookup_fails()
        {
            _lookup = new TenantGetByNameResult.FailureUnknown(Secret);
            using var client = Client("Tenant_A");
            using var response = await client.GetAsync(Route("Tenant_A"));
            var problem = await AssertProblem(response, HttpStatusCode.InternalServerError);
            problem.ToJsonString().Should().NotContain(Secret);
            AssertNothingResolved();
            var logged = NewLogs().Single(log => log.EventId.Name == "TenantAggregateReadFailed");
            logged.State["Operation"].Should().Be("GetTenantByName");
            NewLogs().Should().NotContain(log => log.Message.Contains(Secret));
        }
    }

    [NonParallelizable]
    public class Given_tenant_aggregate_requests_in_single_tenant_mode() : TenantAggregateHttpTests(false)
    {
        [TestCase("default"), TestCase("DEFAULT"), TestCase("Default")]
        public async Task It_reads_the_default_tenant_in_any_case_and_ignores_a_header(string route)
        {
            foreach (string? header in new[] { null, "Tenant_B" })
            {
                _resolvedWith.Clear();
                using var client = Client(header);
                using var response = await client.GetAsync(Route(route));
                string text = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, text);
                text.Should().Be(ExpectedBody("default"));
                _resolvedWith
                    .Should()
                    .ContainSingle()
                    .Which.Should()
                    .BeOfType<TenantContext.NotMultitenant>();
            }
            AssertNoLookup();
            AssertRequestScopesUntouched();
        }

        [TestCase("Tenant_A"), TestCase("defaults"), TestCase("default ")]
        public async Task It_returns_404_for_any_other_tenant_name(string route)
        {
            using var client = Client("default");
            using var response = await client.GetAsync(Route(Uri.EscapeDataString(route)));
            var problem = await AssertProblem(response, HttpStatusCode.NotFound);
            problem["detail"]!.GetValue<string>().Should().Be("Tenant not found.");
            AssertNoLookup();
            AssertNothingResolved();
        }
    }
}
