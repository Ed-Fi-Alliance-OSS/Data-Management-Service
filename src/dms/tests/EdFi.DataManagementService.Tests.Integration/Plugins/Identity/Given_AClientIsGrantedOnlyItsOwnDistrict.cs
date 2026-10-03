// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F5: within one tenant, client A is granted district A and client B district B, and the host
/// authorizes both for every identity operation. A request against the district the client is not
/// granted answers identity-not-found <c>404</c> and the provider reports no lookup, issuance or job
/// creation; own-district requests succeed. Also covers a client with no grant at all and a failing
/// policy source, which answers upstream-failure <c>502</c> rather than a grant.
/// </summary>
/// <remarks>
/// The control stub is the policy source, asked on every grant check, and the provider reports an
/// <c>invocation</c> before its grant check and a <c>lookup</c> only after it, so "denied before any
/// work" is read from the lookup, issuance and job counts rather than inferred from the status.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_AClientIsGrantedOnlyItsOwnDistrict
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string SeededId = "0123456789abcdef0123456789abcdef";

    private sealed record Answer(HttpStatusCode Status, string? ProblemType);

    private sealed record Counts(int Invocations, int Lookups, int Issuances, int Jobs);

    private IdentityFixtureControlStub? _stub;
    private IdentityPluginHost? _host;

    private readonly Dictionary<string, Answer> _own = [];
    private readonly Dictionary<string, Answer> _cross = [];
    private Counts _beforeCross = new(0, 0, 0, 0);
    private Counts _afterCross = new(0, 0, 0, 0);
    private Counts _afterOwn = new(0, 0, 0, 0);
    private Answer _otherClientOwn = new(HttpStatusCode.Unused, null);
    private Answer _noGrantAtAll = new(HttpStatusCode.Unused, null);
    private Counts _beforeNoGrant = new(0, 0, 0, 0);
    private Counts _afterNoGrant = new(0, 0, 0, 0);
    private Answer _policyFailing = new(HttpStatusCode.Unused, null);
    private Counts _beforePolicyFailing = new(0, 0, 0, 0);
    private Counts _afterPolicyFailing = new(0, 0, 0, 0);

    [OneTimeSetUp]
    public async Task Setup()
    {
        string[] districts = [.. IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)];
        string[] tenantTwo = [.. IdentityTestClients.DistrictsOf(IdentityTestClients.TenantTwo)];

        _stub = await IdentityFixtureControlStub.StartAsync();
        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .WithControlStub(_stub)
            .Namespace("district-a", (IdentityTestClients.TenantOne, districts[0]))
            .Seed(SeededId, "Rivera", "Ana")
            .Namespace("district-b", (IdentityTestClients.TenantOne, districts[1]))
            .Seed(SeededId, "Smith", "Bob")
            .Namespace("district-c", (IdentityTestClients.TenantTwo, tenantTwo[0]));
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings
        );

        _stub.Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "district-a");
        _stub.Grant(IdentityTestClients.ClientB.ClientId, IdentityTestClients.TenantOne, "district-b");

        using HttpClient clientA = _host.CreateClient(IdentityTestClients.ClientA.Token);
        using HttpClient clientB = _host.CreateClient(IdentityTestClients.ClientB.Token);
        using HttpClient clientC = _host.CreateClient(IdentityTestClients.ClientC.Token);

        string A(string path) => IdentityTestClients.Route(IdentityTestClients.TenantOne, districts[0], path);
        string B(string path) => IdentityTestClients.Route(IdentityTestClients.TenantOne, districts[1], path);

        // Own district: every operation answers successfully, which is also what makes the later
        // zero counts meaningful - the same four requests do reach the provider's work here.
        await RunFourAsync(clientA, A, _own, isAsync: false);
        _afterOwn = CountsNow();

        _otherClientOwn = await SendAsync(clientB, HttpMethod.Get, B($"identities/{SeededId}"));

        // Cross district: client A asks for district B's namespace.
        _beforeCross = CountsNow();
        await RunFourAsync(clientA, B, _cross, isAsync: true);
        _afterCross = CountsNow();

        // A client with no grant at all: client C is authorized by the host, mapped in tenant two,
        // and nobody granted it anything.
        _beforeNoGrant = CountsNow();
        _noGrantAtAll = await SendAsync(
            clientC,
            HttpMethod.Get,
            IdentityTestClients.Route(IdentityTestClients.TenantTwo, tenantTwo[0], $"identities/{SeededId}")
        );
        _afterNoGrant = CountsNow();

        // A failing policy source is never read as a grant, even for a client that holds one.
        _stub.FailPolicySource();
        _beforePolicyFailing = CountsNow();
        _policyFailing = await SendAsync(clientA, HttpMethod.Get, A($"identities/{SeededId}"));
        _afterPolicyFailing = CountsNow();
    }

    private Counts CountsNow() =>
        new(_stub!.InvocationCount(), _stub.LookupCount(), _stub.IssuanceCount, _stub.JobCount);

    private static async Task RunFourAsync(
        HttpClient client,
        Func<string, string> route,
        Dictionary<string, Answer> into,
        bool isAsync
    )
    {
        string findBody = isAsync ? $"""["{SeededId}", "~fixture:async"]""" : $"""["{SeededId}"]""";
        string searchBody = isAsync
            ? """[{ "LastSurname": "Rivera", "~FixtureAsync": true }]"""
            : """[{ "LastSurname": "Rivera" }]""";

        into["get"] = await SendAsync(client, HttpMethod.Get, route($"identities/{SeededId}"));
        into["find"] = await SendAsync(client, HttpMethod.Post, route("identities/find"), findBody);
        into["search"] = await SendAsync(client, HttpMethod.Post, route("identities/search"), searchBody);
        into["create"] = await SendAsync(
            client,
            HttpMethod.Post,
            route("identities"),
            """{ "LastSurname": "Created" }"""
        );
    }

    private static async Task<Answer> SendAsync(
        HttpClient client,
        HttpMethod method,
        string route,
        string? body = null
    )
    {
        using HttpRequestMessage request = new(method, route);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        string? type = text.StartsWith('{') ? JsonNode.Parse(text)?["type"]?.GetValue<string>() : null;
        return new Answer(response.StatusCode, type);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }

        if (_stub is not null)
        {
            await _stub.DisposeAsync();
        }
    }

    [Test]
    public void It_serves_the_own_district_for_get_find_search_and_create()
    {
        _own.Values.Select(answer => answer.Status)
            .Should()
            .Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK);
    }

    [Test]
    public void It_does_the_provider_work_for_the_own_district_requests()
    {
        // The positive control for the zero counts below: the same four requests are visible here.
        _afterOwn.Lookups.Should().Be(4);
        _afterOwn.Issuances.Should().Be(1);
    }

    [Test]
    public void It_serves_client_B_its_own_district()
    {
        _otherClientOwn.Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_answers_identity_not_found_for_get_find_search_and_create_against_the_other_district()
    {
        _cross
            .Values.Select(answer => answer.Status)
            .Should()
            .OnlyContain(status => status == HttpStatusCode.NotFound);
        _cross
            .Values.Select(answer => answer.ProblemType)
            .Should()
            .OnlyContain(type => type == IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_still_invoked_the_provider_for_each_denied_request()
    {
        // Pins that the zero counts below are about the grant check, not about a request that never arrived.
        (_afterCross.Invocations - _beforeCross.Invocations)
            .Should()
            .Be(4);
    }

    [Test]
    public void It_reports_no_lookup_for_the_denied_requests()
    {
        _afterCross.Lookups.Should().Be(_beforeCross.Lookups);
    }

    [Test]
    public void It_issues_nothing_for_the_denied_create()
    {
        _afterCross.Issuances.Should().Be(_beforeCross.Issuances);
    }

    [Test]
    public void It_creates_no_job_for_the_denied_async_find_and_search()
    {
        _afterCross.Jobs.Should().Be(_beforeCross.Jobs);
    }

    [Test]
    public void It_answers_identity_not_found_for_a_client_with_no_grant()
    {
        _noGrantAtAll.Status.Should().Be(HttpStatusCode.NotFound);
        _noGrantAtAll.ProblemType.Should().Be(IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_does_no_work_for_a_client_with_no_grant()
    {
        _afterNoGrant.Lookups.Should().Be(_beforeNoGrant.Lookups);
        _afterNoGrant.Invocations.Should().Be(_beforeNoGrant.Invocations + 1);
    }

    [Test]
    public void It_answers_upstream_failure_when_the_policy_source_fails()
    {
        _policyFailing.Status.Should().Be(HttpStatusCode.BadGateway);
        _policyFailing.ProblemType.Should().Be(IdentityFailureResponse.UpstreamFailureType);
    }

    [Test]
    public void It_does_no_work_when_the_policy_source_fails()
    {
        _afterPolicyFailing.Lookups.Should().Be(_beforePolicyFailing.Lookups);
        _afterPolicyFailing.Issuances.Should().Be(_beforePolicyFailing.Issuances);
        _afterPolicyFailing.Jobs.Should().Be(_beforePolicyFailing.Jobs);
    }
}
