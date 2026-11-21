// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
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

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[NonParallelizable]
public class Given_education_organization_refresh_requests
{
    private const string AllRoute = "/v3/dataStores/edOrgs/refresh";
    private const string QueuedJobId = "6b1f0c2e-4d7a-4c55-9a51-0d6f3b8e2a17";

    private readonly IEducationOrganizationSnapshotRepository _snapshots =
        A.Fake<IEducationOrganizationSnapshotRepository>();
    private readonly IJobEnqueuer _enqueuer = A.Fake<IJobEnqueuer>();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<(JobEnqueueCommand Command, DbTransaction? Transaction)> _enqueued = [];
    private WebApplicationFactory<Program> _factory = null!;
    private SnapshotDataStoreExistsResult _exists = null!;
    private JobEnqueueResult _enqueue = null!;
    private int _logsBefore;

    private static string OneRoute(object dataStoreId) => $"/v3/dataStores/{dataStoreId}/edOrgs/refresh";

    [OneTimeSetUp]
    public void Start() =>
        _factory = ManagedDataStoreApiTestHost.Create(services =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);
            services.AddSingleton(_snapshots);
            services.AddSingleton(_enqueuer);
        });

    [OneTimeTearDown]
    public async Task Stop() => await _factory.DisposeAsync();

    [SetUp]
    public void Reset()
    {
        Fake.ClearRecordedCalls(_snapshots);
        Fake.ClearRecordedCalls(_enqueuer);
        _enqueued.Clear();
        _logsBefore = _logs.Entries.Count;
        _exists = new SnapshotDataStoreExistsResult.Found();
        _enqueue = new JobEnqueueResult.Success(QueuedJobId);
        A.CallTo(() => _snapshots.DataStoreExistsAsync(A<int>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(_exists));
        A.CallTo(() =>
                _enqueuer.EnqueueAsync(A<JobEnqueueCommand>._, A<DbTransaction?>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (JobEnqueueCommand command, DbTransaction? transaction, CancellationToken _) =>
                {
                    _enqueued.Add((command, transaction));
                    return Task.FromResult(_enqueue);
                }
            );
    }

    private HttpClient Client(string? scope = "edfi_admin_api/full_access", string? pathBase = null)
    {
        var client = _factory.CreateClient();
        client.BaseAddress = new Uri("https://cms.example.test:7443");
        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }
        if (pathBase is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-PathBase", pathBase);
        }
        return client;
    }

    private IReadOnlyList<CapturedLog> NewLogs() => [.. _logs.Entries.Skip(_logsBefore)];

    /// <summary>Exactly the two jobQueuedResult properties, carrying the enqueued job's identifier.</summary>
    private static async Task AssertQueued(HttpResponseMessage response, string message)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Select(property => property.Key).Should().Equal("jobId", "message");
        body["jobId"]!.GetValue<string>().Should().Be(QueuedJobId);
        body["message"]!.GetValue<string>().Should().Be(message);
    }

    private static async Task<JsonObject> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.Location.Should().BeNull();
        return JsonNode.Parse(text)!.AsObject();
    }

    private void AssertNothingQueued() =>
        A.CallTo(() =>
                _enqueuer.EnqueueAsync(A<JobEnqueueCommand>._, A<DbTransaction?>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();

    [TestCase(null, "/v3/jobs/" + QueuedJobId)]
    [TestCase("/cms", "/cms/v3/jobs/" + QueuedJobId)]
    [TestCase("/cms/", "/cms/v3/jobs/" + QueuedJobId)]
    public async Task It_queues_a_refresh_of_every_data_store_with_the_job_location(
        string? pathBase,
        string location
    )
    {
        using var client = Client(pathBase: pathBase);
        using var response = await client.PostAsync(AllRoute, null);
        await AssertQueued(response, EducationOrganizationModule.AllQueuedMessage);
        response.Headers.Location!.IsAbsoluteUri.Should().BeFalse();
        response.Headers.Location.OriginalString.Should().Be(location);
        _enqueued.Should().ContainSingle();
        _enqueued[0]
            .Command.Should()
            .Be(new JobEnqueueCommand("EducationOrganization.Refresh", 1, """{"scope":1,"dataStoreId":0}"""));
        _enqueued[0].Transaction.Should().BeNull();
        Fake.GetCalls(_snapshots).Should().BeEmpty();
    }

    [TestCase(null, "/v3/jobs/" + QueuedJobId)]
    [TestCase("/cms", "/cms/v3/jobs/" + QueuedJobId)]
    public async Task It_queues_a_refresh_of_one_existing_data_store_with_the_job_location(
        string? pathBase,
        string location
    )
    {
        using var client = Client(pathBase: pathBase);
        using var response = await client.PostAsync(OneRoute(7), null);
        await AssertQueued(response, EducationOrganizationModule.OneQueuedMessage);
        response.Headers.Location!.OriginalString.Should().Be(location);
        A.CallTo(() => _snapshots.DataStoreExistsAsync(7, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        _enqueued.Should().ContainSingle();
        _enqueued[0]
            .Command.Should()
            .Be(new JobEnqueueCommand("EducationOrganization.Refresh", 1, """{"scope":2,"dataStoreId":7}"""));
        _enqueued[0].Transaction.Should().BeNull();
    }

    [Test]
    public void It_gives_the_two_routes_distinct_messages()
    {
        EducationOrganizationModule.AllQueuedMessage.Should().NotBeNullOrWhiteSpace();
        EducationOrganizationModule.OneQueuedMessage.Should().NotBeNullOrWhiteSpace();
        EducationOrganizationModule
            .AllQueuedMessage.Should()
            .NotBe(EducationOrganizationModule.OneQueuedMessage);
    }

    [TestCase(7), TestCase(0), TestCase(-3)]
    public async Task It_returns_404_and_queues_nothing_for_a_data_store_absent_from_the_tenant(
        int dataStoreId
    )
    {
        _exists = new SnapshotDataStoreExistsResult.NotFound();
        using var client = Client();
        using var response = await client.PostAsync(OneRoute(dataStoreId), null);
        var problem = await AssertProblem(response, HttpStatusCode.NotFound);
        problem["detail"]!.GetValue<string>().Should().Be("Data store not found.");
        A.CallTo(() => _snapshots.DataStoreExistsAsync(dataStoreId, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        AssertNothingQueued();
    }

    [TestCase("abc"), TestCase("2147483648"), TestCase("1.5"), TestCase("edOrgs")]
    public async Task It_returns_the_existing_parameter_400_for_a_data_store_id_that_is_not_an_int32(
        string value
    )
    {
        using var client = Client();
        using var response = await client.PostAsync(OneRoute(value), null);
        var problem = await AssertProblem(response, HttpStatusCode.BadRequest);
        // The body of every other int32 route parameter that cannot bind.
        using var existing = await client.GetAsync($"/v3/dataStores/{value}");
        var expected = await AssertProblem(existing, HttpStatusCode.BadRequest);
        problem.Remove("correlationId");
        expected.Remove("correlationId");
        problem.ToJsonString().Should().Be(expected.ToJsonString());
        Fake.GetCalls(_snapshots).Should().BeEmpty();
        AssertNothingQueued();
    }

    [Test]
    public async Task It_returns_500_without_Location_when_the_existence_check_fails()
    {
        _exists = new SnapshotDataStoreExistsResult.DbFailure(
            new JobFailureDiagnostic("Npgsql.NpgsqlException", "08006", "DataStoreExists")
        );
        using var client = Client();
        using var response = await client.PostAsync(OneRoute(7), null);
        await AssertProblem(response, HttpStatusCode.InternalServerError);
        AssertNothingQueued();
        var failure = NewLogs().Single(log => log.EventId.Name == "RefreshRequestFailed");
        failure.EventId.Id.Should().Be(1441_40);
        failure.Level.Should().Be(LogLevel.Error);
        failure.State["Request"].Should().Be("RefreshOne");
        failure.State["ExceptionTypeChain"].Should().Be("Npgsql.NpgsqlException");
        failure.State["ProviderErrorCode"].Should().Be("08006");
        failure.State["Operation"].Should().Be("DataStoreExists");
    }

    private static IEnumerable<TestCaseData> EnqueueFailures()
    {
        string[] routes = [AllRoute, OneRoute(7)];
        foreach (string route in routes)
        {
            string request = route == AllRoute ? "RefreshAll" : "RefreshOne";
            yield return new TestCaseData(
                route,
                request,
                new JobEnqueueResult.FailureUnknown(
                    new JobFailureDiagnostic("Npgsql.PostgresException", "57014", "EnqueueJob")
                ),
                "EnqueueJob"
            );
            yield return new TestCaseData(
                route,
                request,
                new JobEnqueueResult.FailureUnsupportedType(),
                "EnqueueFailureUnsupportedType"
            );
            yield return new TestCaseData(
                route,
                request,
                new JobEnqueueResult.FailureUnsupportedVersion(),
                "EnqueueFailureUnsupportedVersion"
            );
            yield return new TestCaseData(
                route,
                request,
                new JobEnqueueResult.FailurePayloadInvalid("InvalidDataStoreId"),
                "EnqueueFailurePayloadInvalid"
            );
            yield return new TestCaseData(
                route,
                request,
                new JobEnqueueResult.ManagementDisabled(),
                "EnqueueManagementDisabled"
            );
        }
    }

    [TestCaseSource(nameof(EnqueueFailures))]
    public async Task It_returns_500_without_Location_when_the_enqueue_fails(
        string route,
        string request,
        JobEnqueueResult failure,
        string operation
    )
    {
        _enqueue = failure;
        using var client = Client();
        using var response = await client.PostAsync(route, null);
        var problem = await AssertProblem(response, HttpStatusCode.InternalServerError);
        problem.ToJsonString().Should().NotContain("InvalidDataStoreId");
        _enqueued.Should().ContainSingle();
        var logged = NewLogs().Single(log => log.EventId.Name == "RefreshRequestFailed");
        logged.State["Request"].Should().Be(request);
        logged.State["Operation"].Should().Be(operation);
    }

    [TestCase("edfi_admin_api/readonly_access", HttpStatusCode.Forbidden)]
    [TestCase(null, HttpStatusCode.Unauthorized)]
    public async Task It_refuses_a_caller_without_the_admin_scope_before_any_lookup(
        string? scope,
        HttpStatusCode status
    )
    {
        using var client = Client(scope);
        foreach (string route in new[] { AllRoute, OneRoute(7) })
        {
            using var response = await client.PostAsync(route, null);
            await AssertProblem(response, status);
        }
        Fake.GetCalls(_snapshots).Should().BeEmpty();
        AssertNothingQueued();
    }

    [Test]
    public void It_maps_both_routes_for_admin_clients_only()
    {
        var routes = _factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText?.EndsWith("/edOrgs/refresh", StringComparison.Ordinal) == true
            )
            .ToDictionary(endpoint =>
                $"{endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods.Single()} {endpoint.RoutePattern.RawText}"
            );
        routes
            .Keys.Should()
            .BeEquivalentTo(
                "POST /v3/dataStores/edOrgs/refresh",
                "POST /v3/dataStores/{dataStoreId}/edOrgs/refresh"
            );
        foreach ((string route, RouteEndpoint endpoint) in routes)
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull(route);
            endpoint
                .Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(data => data.Policy)
                .Should()
                .Equal([SecurityConstants.ServicePolicy, AuthorizationScopePolicies.AdminScopePolicy], route);
        }
    }
}

[NonParallelizable]
public class Given_the_served_openapi_document_for_education_organization_refresh
{
    private JsonNode _document = null!;

    [OneTimeSetUp]
    public async Task Load()
    {
        await using var factory = ManagedDataStoreApiTestHost.Create(_ => { });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        _document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private JsonNode Operation(string path) => _document["paths"]![path]!["post"]!;

    private JsonObject Resolve(JsonNode schema) =>
        (
            schema["$ref"] is JsonNode reference
                ? _document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!
                : schema
        ).AsObject();

    [TestCase("/v3/dataStores/edOrgs/refresh", new[] { "202", "400", "401", "403", "500" })]
    [TestCase(
        "/v3/dataStores/{dataStoreId}/edOrgs/refresh",
        new[] { "202", "400", "401", "403", "404", "500" }
    )]
    public void It_declares_the_queued_job_contract(string path, string[] statuses)
    {
        var operation = Operation(path);
        operation["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()).Should().Equal("DataStores");
        var responses = operation["responses"]!.AsObject();
        responses.Select(response => response.Key).Should().BeEquivalentTo(statuses);
        var accepted = responses["202"]!;
        var location = accepted["headers"]!["Location"]!;
        location["required"]!.GetValue<bool>().Should().BeTrue();
        location["schema"]!["type"]!.GetValue<string>().Should().Be("string");
        location["schema"]!["format"]!.GetValue<string>().Should().Be("uri");
        Resolve(accepted["content"]!["application/json"]!["schema"]!)["properties"]!
            .AsObject()
            .Select(property => property.Key)
            .Should()
            .Equal("jobId", "message");
        foreach (string status in statuses.Where(status => status != "202"))
        {
            responses[status]!["content"]!
                .AsObject()
                .Select(content => content.Key)
                .Should()
                .Equal("application/problem+json");
        }
    }

    [Test]
    public void It_declares_the_data_store_id_as_an_int32_path_parameter()
    {
        var parameter = Operation("/v3/dataStores/{dataStoreId}/edOrgs/refresh")["parameters"]!
            .AsArray()
            .Single()!;
        parameter["name"]!.GetValue<string>().Should().Be("dataStoreId");
        parameter["in"]!.GetValue<string>().Should().Be("path");
        parameter["required"]!.GetValue<bool>().Should().BeTrue();
        parameter["schema"]!["type"]!.GetValue<string>().Should().Be("integer");
        parameter["schema"]!["format"]!.GetValue<string>().Should().Be("int32");
    }
}
