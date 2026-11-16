// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[NonParallelizable]
public class Given_managed_data_store_HTTP
{
    private readonly IDataStoreManageRepository _repository = A.Fake<IDataStoreManageRepository>();
    private readonly IDmsAdminConnectionSource _admin = A.Fake<IDmsAdminConnectionSource>();
    private readonly IDmsDataStoreProvisionerReadiness _readiness =
        A.Fake<IDmsDataStoreProvisionerReadiness>();
    private WebApplicationFactory<Program> _factory = null!;
    private DataStoreManageCreateResult _create = null!;
    private DataStoreManageDeleteResult _delete = null!;
    private DataStoreManageGetResult _get = null!;
    private DataStoreManageQueryResult _query = null!;
    private DataStoreManageQuery? _receivedQuery;
    private ManagedDataStoreCreateCommand? _receivedCreate;

    private WebApplicationFactory<Program> Host(string identity = "keycloak", bool enabled = true) =>
        ManagedDataStoreApiTestHost.Create(
            services =>
            {
                var policy = enabled ? JobClaimPolicy.All : JobClaimPolicy.ExcludeManagedDataStores;
                services.AddSingleton(policy);
                services.AddSingleton(_repository);
                services.AddSingleton(
                    new ManagedDataStoreCreateAcceptance(
                        _readiness,
                        _admin,
                        PostgresqlManagedEndpoint.TryParse,
                        ProtectedDatabaseNames.Create("cms", []),
                        _repository,
                        policy
                    )
                );
            },
            identity
        );

    [OneTimeSetUp]
    public void Start() => _factory = Host();

    [OneTimeTearDown]
    public async Task Stop() => await _factory.DisposeAsync();

    [SetUp]
    public void Reset()
    {
        Fake.ClearRecordedCalls(_repository);
        Fake.ClearRecordedCalls(_admin);
        _receivedQuery = null;
        _receivedCreate = null;
        _create = new DataStoreManageCreateResult.Success(17, "internal-job");
        _delete = new DataStoreManageDeleteResult.Success("internal-job");
        _get = new DataStoreManageGetResult.Success(Model("PendingCreate"));
        _query = new DataStoreManageQueryResult.Success([Model("PendingCreate")]);
        A.CallTo(() => _readiness.Current)
            .Returns(new DmsProvisionerReadiness.Ready("postgresql", DateTimeOffset.UtcNow));
        A.CallTo(() => _admin.ResolveAsync(A<CancellationToken>._))
            .Returns(
                new DmsAdminConnectionResolution.Resolved(
                    new DmsAdminConnectionString("Host=db.example;Username=admin;Password=private-test-value")
                )
            );
        A.CallTo(() =>
                _repository.AcceptCreateAsync(A<ManagedDataStoreCreateCommand>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (ManagedDataStoreCreateCommand value, CancellationToken _) =>
                {
                    _receivedCreate = value;
                    return Task.FromResult(_create);
                }
            );
        A.CallTo(() => _repository.AcceptDeleteAsync(A<int>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(_delete));
        A.CallTo(() => _repository.GetByIdAsync(A<int>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(_get));
        A.CallTo(() => _repository.QueryAsync(A<DataStoreManageQuery>._, A<CancellationToken>._))
            .ReturnsLazily(
                (DataStoreManageQuery value, CancellationToken _) =>
                {
                    _receivedQuery = value;
                    return Task.FromResult(_query);
                }
            );
    }

    private static DataStoreManageResponse Model(string status) =>
        new(
            17,
            "Store",
            status is "PendingCreate" or "CreateInProgress" or "CreateFailed" or "CreateError" ? null : 22,
            status is "PendingCreate" or "CreateInProgress" or "CreateFailed" or "CreateError"
                ? null
                : "Store",
            status,
            "Minimal",
            status is "PendingCreate" or "CreateInProgress" or "CreateFailed" or "CreateError"
                ? null
                : "EdFi_Ods_Store_Minimal",
            new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero),
            new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero)
        );

    private HttpClient Client(
        WebApplicationFactory<Program>? factory = null,
        string? scope = "edfi_admin_api/full_access"
    )
    {
        var client = (factory ?? _factory).CreateClient();
        client.BaseAddress = new Uri("https://cms.example.test:7443");
        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }
        return client;
    }

    private static Task<HttpResponseMessage> Post(
        HttpClient client,
        string json = """{"name":" Store ","databaseTemplate":"Minimal"}""",
        string suffix = ""
    ) =>
        client.PostAsync(
            "/v3/dataStores/manage" + suffix,
            new StringContent(json, Encoding.UTF8, "application/json")
        );

    [TestCase(""), TestCase("/cms"), TestCase("/cms/")]
    public async Task It_returns_bodyless_acceptance_and_absolute_location(string pathBase)
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-PathBase", pathBase);
        using var response = await Post(client, suffix: "/");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        response
            .Headers.Location!.AbsoluteUri.Should()
            .Be($"https://cms.example.test:7443{pathBase.TrimEnd('/')}/v3/dataStores/manage/17");
        _receivedCreate!.Name.Should().Be("Store");
        using var deleted = await client.DeleteAsync("/v3/dataStores/manage/17");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await deleted.Content.ReadAsStringAsync()).Should().BeEmpty();
        deleted.Headers.Location.Should().BeNull();
    }

    [TestCase("{}"), TestCase("null"), TestCase("{"), TestCase("[]")]
    [TestCase("""{"name":null,"databaseTemplate":"Minimal"}""")]
    [TestCase("""{"name":"Store","databaseTemplate":"minimal"}""")]
    [TestCase("""{"name":"café","databaseTemplate":"Sample"}""")]
    [TestCase("""{"name":"Store","databaseTemplate":"Minimal","provider":"mssql"}""")]
    [TestCase("""{"name":1,"databaseTemplate":"Minimal"}""")]
    public async Task It_rejects_invalid_JSON_or_request_values_before_acceptance(string json)
    {
        using var client = Client();
        using var response = await Post(client, json);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        Fake.GetCalls(_repository).Should().BeEmpty();
        Fake.GetCalls(_admin).Should().BeEmpty();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private-test-value");
    }

    [TestCase("offset="), TestCase("offset=-1"), TestCase("limit=0"), TestCase("limit=2147483648")]
    [TestCase("id=oops"), TestCase("id="), TestCase("orderBy=Name"), TestCase("orderBy=%20")]
    [TestCase("direction="), TestCase("direction=DESC"), TestCase("limit=1&limit=2")]
    public async Task It_rejects_invalid_query_binding_in_the_CMS_format(string query)
    {
        using var client = Client();
        using var response = await client.GetAsync("/v3/dataStores/manage?" + query);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        Fake.GetCalls(_repository).Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_defaults_empty_filters_int32_bounds_and_all_sort_fields()
    {
        using var client = Client();
        using (var response = await client.GetAsync("/v3/dataStores/manage"))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            _receivedQuery.Should().BeEquivalentTo(new DataStoreManageQuery());
        }
        foreach (
            string field in new[]
            {
                "id",
                "name",
                "dataStoreId",
                "dataStoreName",
                "status",
                "databaseTemplate",
                "databaseName",
                "lastRefreshed",
                "lastModifiedDate",
            }
        )
        {
            using var response = await client.GetAsync(
                $"/v3/dataStores/manage?offset=2147483647&limit=2147483647&id=-1&name=&orderBy={field}&direction=Ascending"
            );
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            _receivedQuery!.Name.Should().Be("");
            _receivedQuery.Id.Should().Be(-1);
            _receivedQuery.Offset.Should().Be(int.MaxValue);
            _receivedQuery.Limit.Should().Be(int.MaxValue);
            _receivedQuery.OrderBy.Should().Be(field);
            _receivedQuery.Direction.Should().Be("Ascending");
        }
        using var emptyOrder = await client.GetAsync("/v3/dataStores/manage?orderBy=");
        emptyOrder.StatusCode.Should().Be(HttpStatusCode.OK);
        _receivedQuery!.OrderBy.Should().Be("id");
    }

    [Test]
    public async Task It_emits_exactly_the_nine_fields_including_nulls_and_tombstone_history()
    {
        using var client = Client();
        foreach (string status in Enum.GetNames<ManagedDataStoreStatus>())
        {
            _get = new DataStoreManageGetResult.Success(Model(status));
            using var response = await client.GetAsync("/v3/dataStores/manage/17");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            body.Select(x => x.Key)
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
            body["status"]!.GetValue<string>().Should().Be(status);
            body["dataStoreId"]?.GetValue<int>().Should().Be(Model(status).DataStoreId);
            body["databaseName"]?.GetValue<string>().Should().Be(Model(status).DatabaseName);
            body["lastRefreshed"]!.GetValue<DateTimeOffset>().Offset.Should().Be(TimeSpan.Zero);
        }
    }

    [TestCase("keycloak"), TestCase("self-contained")]
    public async Task It_enforces_service_read_and_write_policies_before_disabled_validation(string identity)
    {
        await using var host = Host(identity, enabled: false);
        foreach (string method in new[] { "GET", "POST", "DELETE" })
        {
            foreach (
                string? scope in new[]
                {
                    null,
                    "unrelated",
                    "edfi_admin_api/authMetadata_readonly_access",
                    "edfi_admin_api/readonly_access",
                    "edfi_admin_api/full_access",
                }
            )
            {
                using var client = Client(host, scope);
                using var request = new HttpRequestMessage(
                    new HttpMethod(method),
                    "/v3/dataStores/manage" + (method == "DELETE" ? "/bad-id" : "")
                );
                if (method == "POST")
                {
                    request.Content = new StringContent("{", Encoding.UTF8, "application/json");
                }
                using var response = await client.SendAsync(request);
                var expected = HttpStatusCode.Forbidden;
                if (scope is null)
                {
                    expected = HttpStatusCode.Unauthorized;
                }
                else if (
                    scope == "edfi_admin_api/full_access"
                    || (method == "GET" && scope == "edfi_admin_api/readonly_access")
                )
                {
                    expected = HttpStatusCode.BadRequest;
                }
                response.StatusCode.Should().Be(expected, $"{identity} {method} {scope}");
                if (expected == HttpStatusCode.BadRequest)
                {
                    var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                    body["validationErrors"]!["dataStore"]![0]!
                        .GetValue<string>()
                        .Should()
                        .Contain("EnableDataStoreManagement");
                }
            }
        }
        Fake.GetCalls(_repository).Should().BeEmpty();
        Fake.GetCalls(_admin).Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_fixed_503_without_leaking_readiness_and_keeps_GET_available()
    {
        A.CallTo(() => _readiness.Current)
            .Returns(
                new DmsProvisionerReadiness.NotReady([
                    new(DmsProvisioningFailureCode.StartupValidationFailed, "sensitive-path"),
                ])
            );
        _delete = new DataStoreManageDeleteResult.ProvisioningUnavailable();
        using var client = Client();
        using var post = await Post(client);
        using var delete = await client.DeleteAsync("/v3/dataStores/manage/17");
        foreach (var response in new[] { post, delete })
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            response.Headers.Location.Should().BeNull();
            response.Headers.RetryAfter.Should().BeNull();
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:service-unavailable");
            body["title"]!.GetValue<string>().Should().Be("Service Unavailable");
            body["detail"]!.GetValue<string>().Should().Be("Managed data store provisioning is unavailable.");
            body["status"]!.GetValue<int>().Should().Be(503);
            body["validationErrors"]!.AsObject().Should().BeEmpty();
            body["errors"]!.AsArray().Should().BeEmpty();
            body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            body.ToJsonString().Should().NotContain("sensitive-path").And.NotContain("private-test-value");
        }
        A.CallTo(() =>
                _repository.AcceptCreateAsync(A<ManagedDataStoreCreateCommand>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
        Fake.GetCalls(_admin).Should().BeEmpty();
        using var get = await client.GetAsync("/v3/dataStores/manage");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_maps_not_found_failure_and_each_non_created_delete_state()
    {
        using var client = Client();
        _get = new DataStoreManageGetResult.NotFound();
        _delete = new DataStoreManageDeleteResult.NotFound();
        using var get = await client.GetAsync("/v3/dataStores/manage/99");
        using var delete = await client.DeleteAsync("/v3/dataStores/manage/99");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (
            var (status, expectedMessage) in new[]
            {
                (ManagedDataStoreStatus.PendingCreate, "The data store is still being created."),
                (ManagedDataStoreStatus.CreateInProgress, "The data store is still being created."),
                (ManagedDataStoreStatus.CreateFailed, "Data store creation has failed."),
                (ManagedDataStoreStatus.CreateError, "Data store creation has failed."),
                (ManagedDataStoreStatus.PendingDelete, "The data store is already being deleted."),
                (ManagedDataStoreStatus.DeleteInProgress, "The data store is already being deleted."),
                (ManagedDataStoreStatus.DeleteFailed, "Data store deletion has failed."),
                (ManagedDataStoreStatus.DeleteError, "Data store deletion has failed."),
            }
        )
        {
            _delete = new DataStoreManageDeleteResult.InvalidState(status);
            using var response = await client.DeleteAsync("/v3/dataStores/manage/17");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            response.Headers.Location.Should().BeNull();
            JsonNode.Parse(await response.Content.ReadAsStringAsync())!["validationErrors"]!["dataStore"]!
                .AsArray()
                .Select(value => value!.GetValue<string>())
                .Should()
                .Equal(expectedMessage);
        }
        _create = new DataStoreManageCreateResult.NameReserved();
        using var duplicate = await Post(client);
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _create = new DataStoreManageCreateResult.TargetReserved();
        using var target = await Post(client);
        target.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _create = new DataStoreManageCreateResult.FailureUnknown(
            JobDiagnostics.From(new IOException("private-test-value"), "Test")
        );
        using var unknown = await Post(client);
        unknown.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await unknown.Content.ReadAsStringAsync()).Should().NotContain("private-test-value");
    }

    [Test]
    public async Task It_describes_the_governing_contract_in_OpenAPI()
    {
        using var client = Client();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var path = document["paths"]!["/v3/dataStores/manage"]!;
        var post = path["post"]!;
        post["responses"]!["202"]!["content"]?.AsObject().Should().BeNullOrEmpty();
        post["responses"]!["202"]!["headers"]!["Location"]!["schema"]!["format"]!
            .GetValue<string>()
            .Should()
            .Be("uri");
        post["responses"]!.AsObject().Should().ContainKey("503");
        var request = post["requestBody"]!["content"]!["application/json"]!["schema"]!;
        request["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        request["required"]!
            .AsArray()
            .Select(x => x!.GetValue<string>())
            .Should()
            .BeEquivalentTo("name", "databaseTemplate");
        var parameters = path["get"]!["parameters"]!.AsArray();
        parameters.Single(x => x!["name"]!.GetValue<string>() == "orderBy")!["schema"]!["enum"]!
            .AsArray()
            .Select(x => x!.GetValue<string>())
            .Should()
            .Contain("");
        parameters
            .Select(x => x!["name"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo("id", "name", "offset", "limit", "orderBy", "direction");
        parameters.Single(x => x!["name"]!.GetValue<string>() == "limit")!["schema"]!["default"]!
            .GetValue<int>()
            .Should()
            .Be(25);
        var deletion = document["paths"]!["/v3/dataStores/manage/{id}"]!["delete"]!["responses"]!["204"]!;
        deletion["content"]?.AsObject().Should().BeNullOrEmpty();
    }
}
