// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>Absolute request targets on the identity host, and one raw exchange with any client.</summary>
internal static class RateLimitIdentityRequests
{
    public const string CreateBody = """{ "LastSurname": "Rivera" }""";

    private static string TenantOneDistrict(int index) =>
        IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)[index];

    private static string TenantTwoDistrict =>
        IdentityTestClients.DistrictsOf(IdentityTestClients.TenantTwo)[0];

    public static string Url(string path, string host = "localhost") => $"http://{host}{path}";

    /// <summary>The metadata root under the client's tenant and that tenant's first district.</summary>
    public static string MetadataRootFor(IdentityTestClient client) =>
        Url(
            IdentityTestClients.MetadataRoute(
                client.Tenant,
                IdentityTestClients.DistrictsOf(client.Tenant)[0],
                string.Empty
            )
        );

    public static string TenantOneMetadataRoot(string host = "localhost") =>
        Url(
            IdentityTestClients.MetadataRoute(
                IdentityTestClients.TenantOne,
                TenantOneDistrict(0),
                string.Empty
            ),
            host
        );

    public static string TenantTwoMetadataRoot =>
        Url(
            IdentityTestClients.MetadataRoute(IdentityTestClients.TenantTwo, TenantTwoDistrict, string.Empty)
        );

    public static string TenantOneCreate(int districtIndex = 0) =>
        Url(
            IdentityTestClients.Route(
                IdentityTestClients.TenantOne,
                TenantOneDistrict(districtIndex),
                "identities"
            )
        );

    public static string TenantTwoCreate =>
        Url(IdentityTestClients.Route(IdentityTestClients.TenantTwo, TenantTwoDistrict, "identities"));

    public static async Task<IdentityHttpOutcome> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? jsonBody = null
    )
    {
        using HttpRequestMessage request = new(method, url);

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        JsonNode? body = null;

        if (text.StartsWith('{'))
        {
            try
            {
                body = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                body = null;
            }
        }

        Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (
            (string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers)
        )
        {
            headers[name] = [.. values];
        }

        return new IdentityHttpOutcome(
            response.StatusCode,
            response.Headers.Location?.OriginalString,
            text,
            body,
            headers
        );
    }
}

/// <summary>
/// On the non-bypass, multi-tenant, route-qualified identity host with one permit per sixty-second
/// window, client A spends its permit on tenant one's first metadata root. Client A is then refused
/// on another route qualifier and route family, another tenant, the unqualified metadata route,
/// <c>/health</c> and another <c>Host</c>, while clients B and C and the client whose id differs from
/// A's only by case are each admitted from the same address.
/// </summary>
/// <remarks>
/// Every in-process request has the same (null) remote address, so only the validated client id
/// separates the budgets: under address partitioning the B, C and case-variant controls would be
/// refused too. A refusal by the limiter does not show which route the request named, because the
/// limiter answers before routing matters, so a second host at the default limit sends client A's
/// same requests and each one gets its own route's answer.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_OneClientExhaustsItsBudgetOnTheIdentityHost
{
    private sealed record SpannedRequest(HttpMethod Method, string Url, string? Body);

    private static readonly IReadOnlyDictionary<string, SpannedRequest> _spanned = new Dictionary<
        string,
        SpannedRequest
    >
    {
        ["second-district-create"] = new(
            HttpMethod.Post,
            RateLimitIdentityRequests.TenantOneCreate(districtIndex: 1),
            RateLimitIdentityRequests.CreateBody
        ),
        ["tenant-two-metadata"] = new(HttpMethod.Get, RateLimitIdentityRequests.TenantTwoMetadataRoot, null),
        ["unqualified-metadata"] = new(HttpMethod.Get, RateLimitIdentityRequests.Url("/metadata"), null),
        ["health"] = new(HttpMethod.Get, RateLimitIdentityRequests.Url("/health"), null),
        ["other-host-metadata"] = new(
            HttpMethod.Get,
            RateLimitIdentityRequests.TenantOneMetadataRoot(host: "other-host.example"),
            null
        ),
    };

    private IdentityHttpRun? _limited;
    private IdentityHttpRun? _unlimited;
    private IdentityHttpOutcome _permitSpender = null!;
    private readonly Dictionary<string, IdentityHttpOutcome> _refused = [];
    private readonly Dictionary<string, IdentityHttpOutcome> _controls = [];
    private readonly Dictionary<string, IdentityHttpOutcome> _unlimitedAnswers = [];
    private int _limitedInvocations = -1;
    private int _unlimitedCreateInvocations = -1;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _limited = await IdentityHttpRun.StartAsync(
            settings: new Dictionary<string, string>
            {
                ["RateLimit:PermitLimit"] = "1",
                ["RateLimit:Window"] = "60",
                ["RateLimit:QueueLimit"] = "0",
            }
        );

        _permitSpender = await RateLimitIdentityRequests.SendAsync(
            _limited.Client,
            HttpMethod.Get,
            RateLimitIdentityRequests.TenantOneMetadataRoot()
        );

        foreach ((string name, SpannedRequest request) in _spanned)
        {
            _refused[name] = await RateLimitIdentityRequests.SendAsync(
                _limited.Client,
                request.Method,
                request.Url,
                request.Body
            );
        }

        foreach (
            IdentityTestClient control in new[]
            {
                IdentityTestClients.ClientB,
                IdentityTestClients.ClientC,
                IdentityTestClients.CaseVariantOfA,
            }
        )
        {
            using HttpClient client = _limited.Host.CreateClient(control.Token);
            _controls[control.Name] = await RateLimitIdentityRequests.SendAsync(
                client,
                HttpMethod.Get,
                RateLimitIdentityRequests.MetadataRootFor(control)
            );
        }

        _limitedInvocations = _limited.Stub.InvocationCount();

        _unlimited = await IdentityHttpRun.StartAsync();

        foreach ((string name, SpannedRequest request) in _spanned)
        {
            _unlimitedAnswers[name] = await RateLimitIdentityRequests.SendAsync(
                _unlimited.Client,
                request.Method,
                request.Url,
                request.Body
            );
        }

        _unlimitedCreateInvocations = _unlimited.Stub.InvocationCount("create");
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_limited is not null)
        {
            await _limited.DisposeAsync();
            _limited = null;
        }

        if (_unlimited is not null)
        {
            await _unlimited.DisposeAsync();
            _unlimited = null;
        }
    }

    [Test]
    public void It_spent_client_As_permit_on_a_metadata_request_that_was_answered()
    {
        _permitSpender.Status.Should().Be(HttpStatusCode.OK);
    }

    [TestCase("second-district-create")]
    [TestCase("tenant-two-metadata")]
    [TestCase("unqualified-metadata")]
    [TestCase("health")]
    [TestCase("other-host-metadata")]
    public void It_refuses_client_A_with_the_rate_limit_problem(string name)
    {
        _refused[name].Status.Should().Be(HttpStatusCode.TooManyRequests);
        _refused[name].ProblemType.Should().Be("urn:ed-fi:api:too-many-requests");
    }

    [Test]
    public void It_never_invoked_the_provider_for_client_As_refused_create()
    {
        _limitedInvocations.Should().Be(0);
    }

    [TestCase("B")]
    [TestCase("C")]
    [TestCase("A-case-variant")]
    public void It_admits_another_client_from_the_same_address(string name)
    {
        _controls[name].Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_reaches_the_second_districts_identity_create_without_the_low_limit()
    {
        // No namespace maps the second district, so the provider itself answers identity-not-found.
        _unlimitedAnswers["second-district-create"].Status.Should().Be(HttpStatusCode.NotFound);
        _unlimitedAnswers["second-district-create"]
            .ProblemType.Should()
            .Be(IdentityFailureResponse.NotFoundType);
        _unlimitedCreateInvocations.Should().Be(1);
    }

    [TestCase("tenant-two-metadata")]
    [TestCase("other-host-metadata")]
    public void It_serves_the_metadata_root_without_the_low_limit(string name)
    {
        _unlimitedAnswers[name].Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_answers_the_unqualified_metadata_guidance_without_the_low_limit()
    {
        _unlimitedAnswers["unqualified-metadata"].Status.Should().Be(HttpStatusCode.NotFound);
        _unlimitedAnswers["unqualified-metadata"]
            .Text.Should()
            .Contain("Metadata requests must include the tenant and the configured route qualifiers");
    }

    [Test]
    public void It_answers_the_health_report_without_the_low_limit()
    {
        // The overall status depends on the database check, which has no data store here, so only the
        // health report's own shape is pinned.
        _unlimitedAnswers["health"].Body!["Results"]!
            .AsArray()
            .Select(result => result!["Name"]!.GetValue<string>())
            .Should()
            .Contain("ApplicationHealthCheck");
    }
}

/// <summary>
/// On the non-bypass, multi-tenant identity host at the default limit, requests the limiter admits
/// keep Core's existing answers: an unknown bearer token, a missing credential and a client bound to
/// another tenant each answer the authentication-failure <c>401</c> with its <c>WWW-Authenticate</c>
/// challenge, and a malformed tenant segment answers the tenant-validation <c>400</c> before the
/// unknown token it carries is authenticated.
/// </summary>
/// <remarks>
/// These pass with and without client partitioning; they guard against the limiter's identity
/// resolution changing what Core answers. The controls show the same create route serves client A
/// under its own tenant, and that client C passes the binding under tenant two.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheIdentityHostRejectsRequestsTheLimiterAdmitted
{
    private const string AuthenticationFailureType = "urn:ed-fi:api:security:authentication";

    private IdentityHttpRun? _run;
    private readonly Dictionary<string, IdentityHttpOutcome> _answers = [];
    private int _invocationsAfterRejections = -1;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        using HttpClient unknown = _run.Host.CreateClient(IdentityTestClients.UnknownToken);
        using HttpClient anonymous = _run.Host.CreateClient();
        using HttpClient clientC = _run.Host.CreateClient(IdentityTestClients.ClientC.Token);

        _answers["unknown-token"] = await PostCreateAsync(
            unknown,
            RateLimitIdentityRequests.TenantOneCreate()
        );
        _answers["no-token"] = await PostCreateAsync(anonymous, RateLimitIdentityRequests.TenantOneCreate());
        _answers["other-tenant"] = await PostCreateAsync(
            _run.Client,
            RateLimitIdentityRequests.TenantTwoCreate
        );
        _answers["malformed-tenant"] = await PostCreateAsync(
            unknown,
            RateLimitIdentityRequests.Url(
                $"/tenant!one/{IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)[0]}/{IdentityTestClients.SchoolYear}/identity/v2/identities"
            )
        );

        _invocationsAfterRejections = _run.Stub.InvocationCount();

        _answers["own-tenant"] = await PostCreateAsync(
            _run.Client,
            RateLimitIdentityRequests.TenantOneCreate()
        );
        _answers["client-c-own-tenant"] = await PostCreateAsync(
            clientC,
            RateLimitIdentityRequests.TenantTwoCreate
        );
    }

    private static Task<IdentityHttpOutcome> PostCreateAsync(HttpClient client, string url) =>
        RateLimitIdentityRequests.SendAsync(
            client,
            HttpMethod.Post,
            url,
            RateLimitIdentityRequests.CreateBody
        );

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_run is not null)
        {
            await _run.DisposeAsync();
            _run = null;
        }
    }

    [TestCase("unknown-token", "Invalid token")]
    [TestCase("no-token", "Authorization header is missing.")]
    [TestCase("other-tenant", "Unable to resolve application context for the authenticated client.")]
    public void It_answers_the_existing_authentication_failure(string name, string error)
    {
        IdentityHttpOutcome answer = _answers[name];

        answer.Status.Should().Be(HttpStatusCode.Unauthorized);
        answer.ProblemType.Should().Be(AuthenticationFailureType);
        answer.Body!["title"]!.GetValue<string>().Should().Be("Authentication Failed");
        answer.Body["status"]!.GetValue<int>().Should().Be(401);
        answer.Body["errors"]!.AsArray().Select(item => item!.GetValue<string>()).Should().Equal(error);
        answer.Body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        answer.HeaderValues("WWW-Authenticate").Should().Equal("Bearer error=\"invalid_token\"");
    }

    [Test]
    public void It_validates_the_tenant_before_authenticating_the_unknown_token()
    {
        IdentityHttpOutcome answer = _answers["malformed-tenant"];

        answer.Status.Should().Be(HttpStatusCode.BadRequest);
        answer.Body!["detail"]!
            .GetValue<string>()
            .Should()
            .Be("The tenant identifier has an invalid format.");
        answer.Body["errors"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .Should()
            .Equal("Tenant identifier must contain only alphanumeric characters, hyphens, and underscores.");
        answer.HeaderValues("WWW-Authenticate").Should().BeEmpty();
    }

    [Test]
    public void It_never_invoked_the_provider_for_a_rejected_request()
    {
        _invocationsAfterRejections.Should().Be(0);
    }

    [Test]
    public void It_serves_client_A_the_same_create_under_its_own_tenant()
    {
        _answers["own-tenant"].Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_passes_client_C_through_the_binding_under_its_own_tenant()
    {
        // Client C holds no grant, so the provider answers identity-not-found after the binding passed.
        _answers["client-c-own-tenant"].Status.Should().Be(HttpStatusCode.NotFound);
        _answers["client-c-own-tenant"].ProblemType.Should().Be(IdentityFailureResponse.NotFoundType);
    }
}
