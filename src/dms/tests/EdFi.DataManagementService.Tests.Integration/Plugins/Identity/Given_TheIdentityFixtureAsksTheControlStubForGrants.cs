// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// With a control address configured, the fixture takes its grant decisions from the control stub,
/// and a failing policy source is never read as a grant.
/// </summary>
[Category("PluginIntegration")]
public sealed class Given_TheIdentityFixtureAsksTheControlStubForGrants
{
    private const string FixturePlugin = "Acme.IdentityFixture";

    private IdentityFixtureControlStub? _stub;
    private IdentityPluginHost? _host;
    private HttpStatusCode _beforeGrant;
    private HttpStatusCode _afterGrant;
    private HttpStatusCode _whileFailing;
    private HttpStatusCode _afterRevoke;
    private int _lookupsAfterRevoke;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _stub = await IdentityFixtureControlStub.StartAsync();
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: new Dictionary<string, string>
            {
                ["IdentityFixture:ControlBaseAddress"] = _stub.BaseAddress.ToString(),
                ["IdentityFixture:Namespaces:0:Name"] = "ns-a",
                ["IdentityFixture:Namespaces:0:Contexts:0:Tenant"] = IdentityTestClients.TenantOne,
                ["IdentityFixture:Namespaces:0:Contexts:0:Qualifiers:districtId"] =
                    IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)[0],
                ["IdentityFixture:Namespaces:0:Contexts:0:Qualifiers:schoolYear"] =
                    IdentityTestClients.SchoolYear,
            }
        );

        using HttpClient client = _host.CreateClient(IdentityTestClients.ClientA.Token);
        string route = IdentityTestClients.RouteFor(IdentityTestClients.ClientA, "identities");

        _beforeGrant = await CreateAsync(client, route);

        _stub.Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-a");
        _afterGrant = await CreateAsync(client, route);

        _stub.FailPolicySource();
        _whileFailing = await CreateAsync(client, route);

        _stub.FailPolicySource(false);
        _stub.Revoke(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-a");
        _afterRevoke = await CreateAsync(client, route);
        _lookupsAfterRevoke = _stub.PolicyLookupCount;
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        if (_stub is not null)
        {
            await _stub.DisposeAsync();
        }
    }

    [Test]
    public void It_denies_a_client_the_stub_has_not_granted()
    {
        _beforeGrant.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public void It_allows_a_client_the_stub_grants()
    {
        _afterGrant.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_fails_with_an_upstream_failure_rather_than_granting_when_the_policy_source_fails()
    {
        _whileFailing.Should().Be(HttpStatusCode.BadGateway);
    }

    [Test]
    public void It_denies_again_once_the_stub_revokes_the_grant()
    {
        _afterRevoke.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public void It_asked_the_stub_once_per_request()
    {
        _lookupsAfterRevoke.Should().Be(4);
    }

    private static async Task<HttpStatusCode> CreateAsync(HttpClient client, string route)
    {
        using HttpResponseMessage response = await client.PostAsync(
            route,
            new StringContent("""{ "LastSurname": "Rivera" }""", Encoding.UTF8, "application/json")
        );
        return response.StatusCode;
    }
}
