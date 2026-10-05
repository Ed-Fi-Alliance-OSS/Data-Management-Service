// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Middleware;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// One route mode: whether the deployment is multi-tenant, which route qualifiers it configures, and
/// the concrete prefix a client of tenant <c>Tenant_255901</c>, district 255901 and school year 2025
/// sends.
/// </summary>
public sealed record ProjectionRouteMode(
    string Name,
    bool MultiTenancy,
    string QualifierSegments,
    string ConcretePrefix,
    string? Tenant,
    IReadOnlyDictionary<string, string> Qualifiers
)
{
    public const string RouteSuffix = "/management/education-organizations";

    public static ProjectionRouteMode SingleTenant { get; } =
        new("single-tenant", false, "", "", null, new Dictionary<string, string>());

    public static ProjectionRouteMode SingleTenantWithQualifiers { get; } =
        new(
            "single-tenant with route qualifiers",
            false,
            "districtId,schoolYear",
            "/255901/2025",
            null,
            new Dictionary<string, string> { ["districtId"] = "255901", ["schoolYear"] = "2025" }
        );

    public static ProjectionRouteMode MultiTenant { get; } =
        new("multi-tenant", true, "", "/Tenant_255901", "Tenant_255901", new Dictionary<string, string>());

    public static ProjectionRouteMode MultiTenantWithQualifiers { get; } =
        new(
            "multi-tenant with route qualifiers",
            true,
            "districtId,schoolYear",
            "/Tenant_255901/255901/2025",
            "Tenant_255901",
            new Dictionary<string, string> { ["districtId"] = "255901", ["schoolYear"] = "2025" }
        );

    public static ProjectionRouteMode[] All { get; } =
    [SingleTenant, SingleTenantWithQualifiers, MultiTenant, MultiTenantWithQualifiers];

    /// <summary>The path a client of this mode sends.</summary>
    public string ProjectionPath => $"{ConcretePrefix}{RouteSuffix}";

    /// <summary>The Discovery document carrying this mode's concrete values.</summary>
    public string ConcreteDiscoveryPath => ConcretePrefix.Length == 0 ? "/" : ConcretePrefix;

    /// <summary>The values a client substitutes for each placeholder of a Discovery URL template.</summary>
    public static IReadOnlyDictionary<string, string> PlaceholderValues { get; } =
        new Dictionary<string, string>
        {
            ["{tenant}"] = "Tenant_255901",
            ["{districtId}"] = "255901",
            ["{schoolYear}"] = "2025",
        };

    public override string ToString() => Name;
}

/// <summary>
/// The real host with the education-organization projection facade faked, so each test sees exactly
/// what the frontend hands Core and what it does with Core's answer.
/// </summary>
internal sealed class ProjectionFrontendHost : IAsyncDisposable
{
    public IApiService ApiService { get; } = A.Fake<IApiService>();

    public ConcurrentQueue<(FrontendRequest Request, CancellationToken Token)> Calls { get; } = new();

    public WebApplicationFactory<Program> Factory { get; }

    public IFrontendResponse Response { get; set; } = FakeResponse(200, "application/json");

    public ProjectionFrontendHost(
        bool enableProjection,
        ProjectionRouteMode mode,
        string? pathBase = null,
        Func<FrontendRequest, CancellationToken, Task<IFrontendResponse>>? handle = null,
        Action<IServiceCollection>? configureServices = null
    )
    {
        A.CallTo(() =>
                ApiService.GetEducationOrganizationProjection(A<FrontendRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (FrontendRequest request, CancellationToken token) =>
                {
                    Calls.Enqueue((request, token));
                    return handle?.Invoke(request, token) ?? Task.FromResult(Response);
                }
            );

        Dictionary<string, string?> settings = new()
        {
            ["AppSettings:EnableEducationOrganizationProjection"] = enableProjection ? "true" : "false",
            ["AppSettings:MultiTenancy"] = mode.MultiTenancy ? "true" : "false",
            ["AppSettings:RouteQualifierSegments"] = mode.QualifierSegments,
        };

        if (pathBase is not null)
        {
            settings["AppSettings:PathBase"] = pathBase;
        }

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(settings)
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.AddTransient(_ => ApiService);
                configureServices?.Invoke(services);
            });
        });
    }

    public static IFrontendResponse FakeResponse(int statusCode, string contentType)
    {
        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(statusCode);
        A.CallTo(() => response.Body).Returns(new JsonObject { ["status"] = statusCode });
        A.CallTo(() => response.Headers).Returns([]);
        A.CallTo(() => response.ContentType).Returns(contentType);
        return response;
    }

    public async Task<HttpResponseMessage> Get(
        string pathAndQuery,
        CancellationToken cancellationToken = default
    )
    {
        using HttpClient client = Factory.CreateClient();
        return await client.GetAsync(pathAndQuery, cancellationToken);
    }

    public async Task<JsonObject> Discovery(string path)
    {
        using HttpResponseMessage response = await Get(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// The projection route and its Discovery members in each route mode, with the toggle on and off.
/// </summary>
[TestFixtureSource(typeof(ProjectionRouteMode), nameof(ProjectionRouteMode.All))]
[NonParallelizable]
public class Given_The_Projection_Route_In_A_Route_Mode(ProjectionRouteMode mode)
{
    private const string EndpointMember = "educationOrganizationProjection";

    private ProjectionFrontendHost _enabled = null!;
    private ProjectionFrontendHost _disabled = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _enabled = new ProjectionFrontendHost(enableProjection: true, mode);
        _disabled = new ProjectionFrontendHost(enableProjection: false, mode);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _enabled.DisposeAsync();
        await _disabled.DisposeAsync();
    }

    [SetUp]
    public void ResetCalls()
    {
        _enabled.Calls.Clear();
        _disabled.Calls.Clear();
    }

    private string ConcreteUrl(string suffix) => $"http://localhost{mode.ConcretePrefix}{suffix}";

    private static string Substitute(string template) =>
        ProjectionRouteMode.PlaceholderValues.Aggregate(
            template,
            (url, placeholder) => url.Replace(placeholder.Key, placeholder.Value, StringComparison.Ordinal)
        );

    private void ShouldHaveDispatchedThisModesRequest(string expectedPath)
    {
        _enabled.Calls.Should().ContainSingle();
        FrontendRequest request = _enabled.Calls.Single().Request;

        request.Tenant.Should().Be(mode.Tenant);
        request
            .RouteQualifiers.ToDictionary(pair => pair.Key.Value, pair => pair.Value.Value)
            .Should()
            .BeEquivalentTo(mode.Qualifiers);
        request.Path.Should().Be(expectedPath);
    }

    [Test]
    public async Task It_dispatches_the_route_to_the_projection_facade_with_the_tenant_and_qualifiers()
    {
        using HttpResponseMessage response = await _enabled.Get($"{mode.ProjectionPath}?dataStoreId=3788");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveDispatchedThisModesRequest(mode.ProjectionPath);
        _enabled.Calls.Single().Request.QueryParameters.Should().Contain("dataStoreId", "3788");
    }

    [Test]
    public async Task It_advertises_the_path_it_serves_and_the_contract_versions_it_accepts()
    {
        JsonObject discovery = await _enabled.Discovery(mode.ConcreteDiscoveryPath);

        discovery["urls"]![EndpointMember]!
            .GetValue<string>()
            .Should()
            .Be(ConcreteUrl(ProjectionRouteMode.RouteSuffix));
        discovery[EndpointMember]!["contractVersions"]!
            .AsArray()
            .Select(version => version!.GetValue<string>())
            .Should()
            .Equal(ProjectionContractVersions.Supported)
            .And.Equal("educationOrganizationProjection.v1");
        discovery["urls"]!["oauth"]!.GetValue<string>().Should().Be(ConcreteUrl("/oauth/token"));
        discovery["urls"]!.AsObject().Count.Should().Be(8);
    }

    [Test]
    public async Task It_serves_the_template_discovered_at_the_root_once_its_placeholders_are_filled()
    {
        JsonObject discovery = await _enabled.Discovery("/");
        string template = discovery["urls"]![EndpointMember]!.GetValue<string>();
        string expectedPath = new Uri(Substitute(template)).AbsolutePath;

        using HttpResponseMessage response = await _enabled.Get($"{expectedPath}?dataStoreId=3788");

        response.StatusCode.Should().Be(HttpStatusCode.OK, template);
        ShouldHaveDispatchedThisModesRequest(expectedPath);
    }

    [Test]
    public async Task It_does_not_map_the_route_when_disabled()
    {
        using HttpResponseMessage response = await _disabled.Get($"{mode.ProjectionPath}?dataStoreId=3788");
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
        _disabled.Calls.Should().BeEmpty();
    }

    [TestCase("/")]
    [TestCase(null)]
    public async Task It_removes_exactly_the_two_projection_members_when_disabled(string? path)
    {
        string discoveryPath = path ?? mode.ConcreteDiscoveryPath;
        JsonObject enabled = await _enabled.Discovery(discoveryPath);
        JsonObject disabled = await _disabled.Discovery(discoveryPath);

        disabled["urls"]!.AsObject().ContainsKey(EndpointMember).Should().BeFalse();
        disabled.ContainsKey(EndpointMember).Should().BeFalse();
        disabled["urls"]!["oauth"]!
            .GetValue<string>()
            .Should()
            .Be(enabled["urls"]!["oauth"]!.GetValue<string>())
            .And.EndWith("/oauth/token");
        disabled["urls"]!.AsObject().Count.Should().Be(7);

        enabled["urls"]!.AsObject().Remove(EndpointMember).Should().BeTrue();
        enabled.Remove(EndpointMember).Should().BeTrue();
        JsonNode.DeepEquals(enabled, disabled).Should().BeTrue(disabled.ToJsonString());
    }
}

/// <summary>
/// Paths that resemble the projection route but are not one of its forms.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_Paths_Outside_The_Projection_Route_Forms
{
    private ProjectionFrontendHost _multiTenant = null!;
    private ProjectionFrontendHost _qualified = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _multiTenant = new ProjectionFrontendHost(true, ProjectionRouteMode.MultiTenant);
        _qualified = new ProjectionFrontendHost(true, ProjectionRouteMode.MultiTenantWithQualifiers);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _multiTenant.DisposeAsync();
        await _qualified.DisposeAsync();
    }

    [TestCase("/management/education-organizations?dataStoreId=3788")]
    [TestCase("/management/Tenant_255901/education-organizations?dataStoreId=3788")]
    public async Task It_does_not_serve_the_prefixless_or_legacy_form_in_multi_tenant_mode(string path)
    {
        using HttpResponseMessage response = await _multiTenant.Get(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _multiTenant.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_serve_a_path_missing_a_route_qualifier()
    {
        using HttpResponseMessage response = await _qualified.Get(
            "/Tenant_255901/255901/management/education-organizations?dataStoreId=3788"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _qualified.Calls.Should().BeEmpty();
    }
}

/// <summary>
/// The Discovery URL carries the path base, and the route is served beneath it.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_The_Projection_Route_Under_A_Path_Base
{
    private ProjectionFrontendHost _host = null!;
    private string _template = "";
    private HttpResponseMessage _response = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _host = new ProjectionFrontendHost(true, ProjectionRouteMode.SingleTenant, pathBase: "dms-api");
        JsonObject discovery = await _host.Discovery("/dms-api");
        _template = discovery["urls"]!["educationOrganizationProjection"]!.GetValue<string>();
        _response = await _host.Get($"{new Uri(_template).AbsolutePath}?dataStoreId=3788");
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _response.Dispose();
        await _host.DisposeAsync();
    }

    [Test]
    public void It_advertises_the_url_beneath_the_path_base() =>
        _template.Should().Be("http://localhost/dms-api/management/education-organizations");

    [Test]
    public void It_serves_the_advertised_url()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.OK);
        _host.Calls.Should().ContainSingle();
    }
}

/// <summary>
/// What the frontend hands Core for one request, and what it adds to Core's answer.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Projection_Request_Through_The_Frontend
{
    private ProjectionFrontendHost _host = null!;

    [OneTimeSetUp]
    public void Setup() => _host = new ProjectionFrontendHost(true, ProjectionRouteMode.SingleTenant);

    [OneTimeTearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    [SetUp]
    public void ResetCalls()
    {
        _host.Calls.Clear();
        _host.Response = ProjectionFrontendHost.FakeResponse(200, "application/json");
    }

    private static IEnumerable<TestCaseData> RepeatedParameterCases()
    {
        yield return new TestCaseData(
            "?dataStoreId=1&limit=2&cursor=c&contractVersion=v&other=1",
            Array.Empty<string>()
        ).SetName("no parameter repeated");
        yield return new TestCaseData(
            "?dataStoreId=1&DataStoreId=2&LIMIT=5&cursor=a&CURSOR=b&contractversion=x&ContractVersion=y&other=1&other=2",
            new[] { "dataStoreId", "cursor", "contractVersion" }
        ).SetName(
            "three parameters repeated in other letter cases, one single in another case, one unowned repeat"
        );
        yield return new TestCaseData("?limit=1&limit=2", new[] { "limit" }).SetName(
            "limit repeated in the same letter case"
        );
        yield return new TestCaseData("?dataStoreId=1&dataStoreId=", new[] { "dataStoreId" }).SetName(
            "a repeat whose second value is empty"
        );
    }

    [TestCaseSource(nameof(RepeatedParameterCases))]
    public async Task It_reports_each_repeated_projection_parameter_to_core(string query, string[] expected)
    {
        using HttpResponseMessage response = await _host.Get($"/management/education-organizations{query}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _host.Calls.Single().Request.RepeatedQueryParameterNames.Should().Equal(expected);
    }

    [Test]
    public void It_watches_exactly_the_parameters_core_parses() =>
        AspNetCoreFrontend
            .EducationOrganizationProjectionParameterNames.Should()
            .Equal(
                ParseEducationOrganizationProjectionRequestMiddleware.DataStoreIdParameter,
                ParseEducationOrganizationProjectionRequestMiddleware.LimitParameter,
                ParseEducationOrganizationProjectionRequestMiddleware.CursorParameter,
                ParseEducationOrganizationProjectionRequestMiddleware.ContractVersionParameter
            );

    [TestCase(200, "application/json")]
    [TestCase(409, "application/problem+json")]
    [TestCase(503, "application/problem+json")]
    public async Task It_serves_cores_answer_with_its_media_type_and_no_store(int status, string contentType)
    {
        _host.Response = ProjectionFrontendHost.FakeResponse(status, contentType);

        using HttpResponseMessage response = await _host.Get(
            "/management/education-organizations?dataStoreId=1"
        );

        ((int)response.StatusCode).Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be(contentType);
        response.Headers.GetValues("Cache-Control").Should().Equal("no-store");
    }
}

/// <summary>
/// A client abort reaches Core through the cancellation token the frontend passes, while Core is still
/// working on the request.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_The_Client_Aborts_A_Projection_Request
{
    private readonly TaskCompletionSource<CancellationToken> _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _cancelled = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private ProjectionFrontendHost _host = null!;
    private CancellationToken _token;
    private bool _cancelledBeforeAbort;
    private Exception? _clientException;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _host = new ProjectionFrontendHost(
            true,
            ProjectionRouteMode.SingleTenant,
            handle: HoldUntilCancelled
        );
        using var abort = new CancellationTokenSource();

        Task<HttpResponseMessage> request = _host.Get(
            "/management/education-organizations?dataStoreId=1",
            abort.Token
        );

        _token = await _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _cancelledBeforeAbort = _token.IsCancellationRequested;

        await abort.CancelAsync();
        await _cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            using HttpResponseMessage unexpected = await request.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception)
        {
            _clientException = exception;
        }
    }

    [OneTimeTearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Signals entry, then waits until the token Core was given is cancelled.
    /// </summary>
    private async Task<IFrontendResponse> HoldUntilCancelled(FrontendRequest request, CancellationToken token)
    {
        token.Register(() => _cancelled.TrySetResult());
        _entered.TrySetResult(token);
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("The held request was released without cancellation.");
    }

    [Test]
    public void It_passes_core_a_token_the_request_can_cancel()
    {
        _token.CanBeCanceled.Should().BeTrue();
        _cancelledBeforeAbort.Should().BeFalse();
    }

    [Test]
    public void It_cancels_that_token_when_the_client_aborts() =>
        _token.IsCancellationRequested.Should().BeTrue();

    [Test]
    public void It_ends_the_client_request_as_cancelled() =>
        _clientException.Should().BeAssignableTo<OperationCanceledException>();
}
