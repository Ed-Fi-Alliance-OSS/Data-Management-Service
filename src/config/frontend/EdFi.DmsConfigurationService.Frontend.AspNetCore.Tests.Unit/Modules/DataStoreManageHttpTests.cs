// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using ExceptionTypeOnlyLoggingMetadata = EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.ExceptionTypeOnlyLoggingMetadata;
using ManagedDataStoreRouteMetadata = EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.ManagedDataStoreRouteMetadata;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[NonParallelizable]
public class Given_managed_data_store_HTTP
{
    private readonly IDataStoreManageRepository _repository = A.Fake<IDataStoreManageRepository>();
    private readonly IDmsAdminConnectionSource _admin = A.Fake<IDmsAdminConnectionSource>();
    private readonly IDmsDataStoreProvisionerReadiness _readiness =
        A.Fake<IDmsDataStoreProvisionerReadiness>();
    private readonly CapturingLoggerProvider _logs = new();
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
                services.AddSingleton<ILoggerProvider>(_logs);
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
        _create = new DataStoreManageCreateResult.Success(17, "accepted-create-job");
        _delete = new DataStoreManageDeleteResult.Success("accepted-delete-job");
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

    /// <summary>Exactly the two jobQueuedResult properties, carrying the repository's committed job ID.</summary>
    private static async Task AssertQueued(HttpResponseMessage response, string jobId, string message)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Select(property => property.Key).Should().Equal("jobId", "message");
        body["jobId"]!.GetValue<string>().Should().Be(jobId);
        body["message"]!.GetValue<string>().Should().Be(message);
    }

    private static HttpRequestMessage UnsupportedCharsetPost(string path) =>
        new(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(
                Encoding.UTF8.GetBytes("""{"name":"Store","databaseTemplate":"Minimal"}""")
            )
            {
                Headers = { { "Content-Type", "application/json; charset=not-an-encoding" } },
            },
        };

    [Test]
    public async Task It_returns_the_disabled_400_before_charset_validation_and_keeps_other_outcomes()
    {
        await using var disabled = Host(enabled: false);
        foreach (
            var (host, scope, path, expected) in new (
                WebApplicationFactory<Program>,
                string?,
                string,
                HttpStatusCode
            )[]
            {
                (disabled, "edfi_admin_api/full_access", "/v3/dataStores/manage", HttpStatusCode.BadRequest),
                (disabled, null, "/v3/dataStores/manage", HttpStatusCode.Unauthorized),
                (
                    disabled,
                    "edfi_admin_api/readonly_access",
                    "/v3/dataStores/manage",
                    HttpStatusCode.Forbidden
                ),
                (
                    disabled,
                    "edfi_admin_api/full_access",
                    "/v3/dataStores",
                    HttpStatusCode.UnsupportedMediaType
                ),
                (
                    _factory,
                    "edfi_admin_api/full_access",
                    "/v3/dataStores/manage",
                    HttpStatusCode.UnsupportedMediaType
                ),
            }
        )
        {
            using var client = Client(host, scope);
            using var request = UnsupportedCharsetPost(path);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(expected, $"{path} {scope}");
            if (expected == HttpStatusCode.BadRequest)
            {
                JsonNode.Parse(await response.Content.ReadAsStringAsync())!["validationErrors"]![
                    "dataStore"
                ]![0]!
                    .GetValue<string>()
                    .Should()
                    .Be("Data store management is disabled by AppSettings:EnableDataStoreManagement.");
            }
        }
        Fake.GetCalls(_repository).Should().BeEmpty();
        Fake.GetCalls(_admin).Should().BeEmpty();
    }

    [TestCase(""), TestCase("/cms"), TestCase("/cms/")]
    public async Task It_returns_the_queued_job_and_absolute_location(string pathBase)
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-PathBase", pathBase);
        using var response = await Post(client, suffix: "/");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        await AssertQueued(response, "accepted-create-job", "The Data Store has been queued to be created.");
        response
            .Headers.Location!.AbsoluteUri.Should()
            .Be($"https://cms.example.test:7443{pathBase.TrimEnd('/')}/v3/dataStores/manage/17");
        _receivedCreate!.Name.Should().Be("Store");
        using var deleted = await client.DeleteAsync("/v3/dataStores/manage/17");
        deleted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await AssertQueued(deleted, "accepted-delete-job", "The Data Store has been queued to be deleted.");
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
    public async Task It_logs_safe_failure_diagnostics_correlated_with_the_unchanged_500()
    {
        using var client = Client();
        A.CallTo(() => _admin.ResolveAsync(A<CancellationToken>._))
            .Returns(
                new DmsAdminConnectionResolution.Failed(
                    DmsProvisioningDiagnostic.From(
                        new InvalidOperationException("private-test-value"),
                        "ResolveAdminConnection"
                    )
                )
            );
        await AssertLogged(
            await Post(client),
            "CreateManagedDataStore",
            "System.InvalidOperationException",
            null,
            "ResolveAdminConnection"
        );
        A.CallTo(() => _admin.ResolveAsync(A<CancellationToken>._))
            .Returns(
                new DmsAdminConnectionResolution.Resolved(
                    new DmsAdminConnectionString("Host=db.example;Username=admin;Password=private-test-value")
                )
            );
        var unknown = JobDiagnostics.From(new IOException("private-test-value"), "Read", "40001");
        _create = new DataStoreManageCreateResult.FailureUnknown(unknown);
        await AssertLogged(
            await Post(client),
            "CreateManagedDataStore",
            "System.IO.IOException",
            "40001",
            "Read"
        );
        _query = new DataStoreManageQueryResult.FailureUnknown(unknown);
        await AssertLogged(
            await client.GetAsync("/v3/dataStores/manage"),
            "QueryManagedDataStores",
            "System.IO.IOException",
            "40001",
            "Read"
        );
        _get = new DataStoreManageGetResult.FailureUnknown(unknown);
        await AssertLogged(
            await client.GetAsync("/v3/dataStores/manage/17"),
            "GetManagedDataStore",
            "System.IO.IOException",
            "40001",
            "Read"
        );
        _delete = new DataStoreManageDeleteResult.EnqueueFailed(new JobEnqueueResult.FailureUnknown(unknown));
        await AssertLogged(
            await client.DeleteAsync("/v3/dataStores/manage/17"),
            "DeleteManagedDataStore",
            "System.IO.IOException",
            "40001",
            "Read"
        );
    }

    private async Task AssertLogged(
        HttpResponseMessage response,
        string request,
        string exceptionTypeChain,
        string? providerErrorCode,
        string operation
    )
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            string body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("private-test-value");
            string correlationId = JsonNode.Parse(body)!["correlationId"]!.GetValue<string>();
            var entry = _logs
                .Entries.Where(log => log.EventId.Name == "ManagedRequestFailed")
                .Should()
                .ContainSingle(log => (string?)log.State["TraceId"] == correlationId)
                .Subject;
            entry.Level.Should().Be(LogLevel.Error);
            entry.Exception.Should().BeNull();
            entry.State["Request"].Should().Be(request);
            entry.State["ExceptionTypeChain"].Should().Be(exceptionTypeChain);
            entry.State["ProviderErrorCode"].Should().Be(providerErrorCode);
            entry.State["Operation"].Should().Be(operation);
            entry
                .Message.Should()
                .NotContain("private-test-value")
                .And.NotContain("Password")
                .And.NotContain("db.example");
        }
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
        AssertQueuedSchema(document, post["responses"]!["202"]!);
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
        var deletion = document["paths"]!["/v3/dataStores/manage/{id}"]!["delete"]!["responses"]!.AsObject();
        deletion.Should().NotContainKey("204");
        AssertQueuedSchema(document, deletion["202"]!);
        deletion["202"]!["headers"]?["Location"].Should().BeNull();
    }

    private static void AssertQueuedSchema(JsonNode document, JsonNode response)
    {
        var schema = response["content"]!["application/json"]!["schema"]!;
        string reference = schema["$ref"]!.GetValue<string>();
        var resolved = document["components"]!["schemas"]![reference.Split('/')[^1]]!;
        resolved["properties"]!
            .AsObject()
            .Select(property => property.Key)
            .Should()
            .BeEquivalentTo("jobId", "message");
    }
}

/// <summary>
/// An exception that escapes a managed handler, from resolving a managed dependency at request time or from a
/// repository call, is answered with the fixed 500 and logged as one failed request naming exception types only:
/// its message, inner exception and <c>Data</c> reach neither the response nor any captured log message,
/// structured value, scope or attached exception, in application or framework categories.
/// </summary>
[NonParallelizable]
public class Given_a_managed_data_store_request_whose_dependency_throws
{
    private const string MessageCanary = "Password=managed-message-canary";
    private const string InnerCanary = "managed-inner-exception-canary";
    private const string DataKeyCanary = "managed-data-key-canary";
    private const string DataValueCanary = "managed-data-value-canary";
    private const string FailureTypeChain = "System.TimeoutException -> System.IO.IOException";
    private const string Admin = "edfi_admin_api/full_access";
    private const string ReadOnly = "edfi_admin_api/readonly_access";

    private readonly IDataStoreManageRepository _repository = A.Fake<IDataStoreManageRepository>();
    private readonly TokenRevocationStartupTests.StartupLogCapture _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private bool _failRepositoryResolution;
    private int _acceptanceResolutions;
    private int _repositoryResolutions;
    private int _recordsBefore;

    private static TimeoutException DependencyFailure()
    {
        var failure = new TimeoutException(MessageCanary, new IOException(InnerCanary));
        failure.Data[DataKeyCanary] = DataValueCanary;
        return failure;
    }

    [OneTimeSetUp]
    public void Start()
    {
        _factory = ManagedDataStoreApiTestHost.Create(services =>
        {
            services.AddSingleton(JobClaimPolicy.All);
            services.AddSingleton<ILoggerProvider>(_logs);
            // Every category at every level, the framework's included: a provider rule outranks category levels.
            services.AddLogging(logging =>
                logging.AddFilter<TokenRevocationStartupTests.StartupLogCapture>(
                    category: null,
                    LogLevel.Trace
                )
            );
            services.AddTransient<IDataStoreManageRepository>(_ =>
            {
                Interlocked.Increment(ref _repositoryResolutions);
                return _failRepositoryResolution ? throw DependencyFailure() : _repository;
            });
            services.AddTransient<ManagedDataStoreCreateAcceptance>(_ =>
            {
                Interlocked.Increment(ref _acceptanceResolutions);
                throw DependencyFailure();
            });
        });
        // Starts the host; a startup resolution of either dependency would fail here.
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public async Task Stop() => await _factory.DisposeAsync();

    [SetUp]
    public void Reset()
    {
        Fake.Reset(_repository);
        _failRepositoryResolution = false;
        Interlocked.Exchange(ref _acceptanceResolutions, 0);
        Interlocked.Exchange(ref _repositoryResolutions, 0);
        _recordsBefore = _logs.Records.Count;
    }

    private HttpClient Client(string? scope)
    {
        var client = _factory.CreateClient();
        client.BaseAddress = new Uri("https://cms.example.test:7443");
        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Test]
    public void It_marks_every_managed_route_for_type_only_exception_logging_and_keeps_its_gate_and_policies()
    {
        var routes = _factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith("/v3/dataStores/manage", StringComparison.Ordinal)
                == true
            )
            .ToDictionary(endpoint =>
                $"{endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods.Single()} {endpoint.RoutePattern.RawText}"
            );
        routes
            .Keys.Should()
            .BeEquivalentTo(
                "GET /v3/dataStores/manage/",
                "GET /v3/dataStores/manage/{id}",
                "POST /v3/dataStores/manage/",
                "DELETE /v3/dataStores/manage/{id}"
            );
        foreach ((string route, RouteEndpoint endpoint) in routes)
        {
            endpoint
                .Metadata.GetMetadata<ExceptionTypeOnlyLoggingMetadata>()
                .Should()
                .BeSameAs(ExceptionTypeOnlyLoggingMetadata.Instance, route);
            endpoint
                .Metadata.GetMetadata<ManagedDataStoreRouteMetadata>()
                .Should()
                .BeSameAs(ManagedDataStoreRouteMetadata.Instance, route);
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull(route);
            endpoint
                .Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(data => data.Policy)
                .Should()
                .Equal(
                    [
                        SecurityConstants.ServicePolicy,
                        route.StartsWith("GET ", StringComparison.Ordinal)
                            ? AuthorizationScopePolicies.ReadOnlyOrAdminScopePolicy
                            : AuthorizationScopePolicies.AdminScopePolicy,
                    ],
                    route
                );
        }
    }

    [Test]
    public async Task It_contains_a_create_acceptance_factory_exception()
    {
        using var client = Client(Admin);
        await AssertContained(
            await client.PostAsync(
                "/v3/dataStores/manage",
                Json("""{"name":"Store","databaseTemplate":"Minimal"}""")
            ),
            "POST",
            "/v3/dataStores/manage"
        );
        _acceptanceResolutions.Should().Be(1);
        Fake.GetCalls(_repository).Should().BeEmpty();
    }

    [Test]
    public async Task It_contains_a_repository_factory_exception()
    {
        _failRepositoryResolution = true;
        using var client = Client(ReadOnly);
        await AssertContained(await client.GetAsync("/v3/dataStores/manage"), "GET", "/v3/dataStores/manage");
        _repositoryResolutions.Should().Be(1);
    }

    [Test]
    public async Task It_contains_an_exception_thrown_by_a_repository_call()
    {
        A.CallTo(() => _repository.GetByIdAsync(A<int>._, A<CancellationToken>._)).Throws(DependencyFailure);
        using var client = Client(ReadOnly);
        await AssertContained(
            await client.GetAsync("/v3/dataStores/manage/17"),
            "GET",
            "/v3/dataStores/manage/17"
        );
        A.CallTo(() => _repository.GetByIdAsync(17, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_contains_a_faulted_repository_call()
    {
        A.CallTo(() => _repository.AcceptDeleteAsync(A<int>._, A<CancellationToken>._))
            .ThrowsAsync(DependencyFailure);
        using var client = Client(Admin);
        await AssertContained(
            await client.DeleteAsync("/v3/dataStores/manage/17"),
            "DELETE",
            "/v3/dataStores/manage/17"
        );
        A.CallTo(() => _repository.AcceptDeleteAsync(17, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_resolves_no_managed_dependency_for_a_request_answered_before_the_handler_needs_one()
    {
        _failRepositoryResolution = true;
        const string Valid = """{"name":"Store","databaseTemplate":"Minimal"}""";
        foreach (
            var (method, path, scope, json, expected) in new (
                string,
                string,
                string?,
                string?,
                HttpStatusCode
            )[]
            {
                ("POST", "/v3/dataStores/manage", null, Valid, HttpStatusCode.Unauthorized),
                ("POST", "/v3/dataStores/manage", ReadOnly, Valid, HttpStatusCode.Forbidden),
                ("DELETE", "/v3/dataStores/manage/17", ReadOnly, null, HttpStatusCode.Forbidden),
                ("GET", "/v3/dataStores/manage", null, null, HttpStatusCode.Unauthorized),
                ("POST", "/v3/dataStores/manage", Admin, "{", HttpStatusCode.BadRequest),
                (
                    "POST",
                    "/v3/dataStores/manage",
                    Admin,
                    """{"name":"Store","databaseTemplate":"minimal"}""",
                    HttpStatusCode.BadRequest
                ),
                ("GET", "/v3/dataStores/manage?limit=0", ReadOnly, null, HttpStatusCode.BadRequest),
                ("GET", "/v3/dataStores/manage/oops", ReadOnly, null, HttpStatusCode.BadRequest),
                ("DELETE", "/v3/dataStores/manage/oops", Admin, null, HttpStatusCode.BadRequest),
            }
        )
        {
            using var client = Client(scope);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (json is not null)
            {
                request.Content = Json(json);
            }
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(expected, $"{method} {path} {scope}");
        }
        using (var charset = Client(Admin))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v3/dataStores/manage")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Valid))
                {
                    Headers = { { "Content-Type", "application/json; charset=not-an-encoding" } },
                },
            };
            using var response = await charset.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        }
        _acceptanceResolutions.Should().Be(0);
        _repositoryResolutions.Should().Be(0);
        Fake.GetCalls(_repository).Should().BeEmpty();
    }

    private async Task AssertContained(HttpResponseMessage response, string method, string path)
    {
        using (response)
        {
            // Reading the body to its end waits for the pipeline, and so its request log, to finish.
            string content = await response.Content.ReadAsStringAsync();
            var records = _logs.Records.Skip(_recordsBefore).ToList();
            AssertNoCanary(content, "the response body");
            AssertNoCanary(CapturedText(records), "the captured logs");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            string traceId = response.Headers.GetValues("TraceId").Single();
            JsonNode
                .DeepEquals(JsonNode.Parse(content), FailureResponse.ForUnknown(traceId))
                .Should()
                .BeTrue(content);

            records
                .Should()
                .Contain(
                    record => record.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
                    "framework categories are captured"
                );
            var failed = records
                .Should()
                .ContainSingle(record => record.EventId.Name == "HttpRequestFailed")
                .Subject;
            failed.Level.Should().Be(LogLevel.Error);
            failed.Exception.Should().BeNull();
            failed
                .State.Should()
                .Contain(
                    new KeyValuePair<string, object?>("ExceptionTypes", FailureTypeChain),
                    new KeyValuePair<string, object?>("TraceId", traceId),
                    new KeyValuePair<string, object?>("StatusCode", 500),
                    new KeyValuePair<string, object?>("Method", method),
                    new KeyValuePair<string, object?>("Path", path)
                );
            failed
                .Scopes.OfType<IEnumerable<KeyValuePair<string, object>>>()
                .SelectMany(scope => scope)
                .Where(pair => pair.Key == "TraceId")
                .Select(pair => pair.Value)
                .Should()
                .Contain(traceId, "the request logging scope carries the same TraceId");
        }
    }

    private static void AssertNoCanary(string text, string where) =>
        text.Should()
            .NotContain("managed-message-canary", where)
            .And.NotContain(InnerCanary, where)
            .And.NotContain(DataKeyCanary, where)
            .And.NotContain(DataValueCanary, where);

    /// <summary>
    /// Each record's category, event, rendered message, every structured value and scope, and the attached
    /// exception with its inner exceptions and <c>Data</c>, walked recursively.
    /// </summary>
    private static string CapturedText(IEnumerable<TokenRevocationStartupTests.CapturedStartupLog> records)
    {
        StringBuilder text = new();
        foreach (var record in records)
        {
            text.AppendLine($"{record.Category} {record.EventId} {record.Message}");
            foreach (var pair in record.State)
            {
                AppendValue(text, pair);
            }
            foreach (object? scope in record.Scopes)
            {
                AppendValue(text, scope);
            }
            AppendValue(text, record.Exception);
        }
        return text.ToString();
    }

    private static void AppendValue(StringBuilder text, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string literal:
                text.AppendLine(literal);
                return;
            case KeyValuePair<string, object> pair:
                text.AppendLine(pair.Key);
                AppendValue(text, pair.Value);
                return;
            case Exception exception:
                text.AppendLine(exception.ToString());
                foreach (DictionaryEntry entry in exception.Data)
                {
                    AppendValue(text, entry.Key);
                    AppendValue(text, entry.Value);
                }
                if (exception is AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        AppendValue(text, inner);
                    }
                }
                else
                {
                    AppendValue(text, exception.InnerException);
                }
                return;
            case IEnumerable values:
                text.AppendLine(value.ToString());
                foreach (object? item in values)
                {
                    AppendValue(text, item);
                }
                return;
            default:
                text.AppendLine(value.ToString());
                return;
        }
    }
}
