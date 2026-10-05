// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// The provider and its scoped dependency are created per request. Two get-by-id requests
/// report different provider and scope instances, and each reports exactly one read of
/// <c>Capabilities</c> on the instance that served it.
/// </summary>
/// <remarks>
/// The distinct-id assertions fail on a singleton or cached provider; the read-count assertion
/// fails if the capability gate and the invocation used different instances, or if the gate read
/// the getter more than once.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TwoGetByIdRequestsReachTheIdentityFixture
{
    private const string FixturePlugin = "Acme.IdentityFixture";

    private IdentityPluginHost? _host;
    private JsonNode? _first;
    private JsonNode? _second;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: new Dictionary<string, string>
            {
                ["IdentityFixture:Namespaces:0:Name"] = "ns-a",
                ["IdentityFixture:Namespaces:0:Contexts:0:Tenant"] = IdentityTestClients.TenantOne,
                ["IdentityFixture:Namespaces:0:Contexts:0:Qualifiers:districtId"] =
                    IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)[0],
                ["IdentityFixture:Namespaces:0:Contexts:0:Qualifiers:schoolYear"] =
                    IdentityTestClients.SchoolYear,
                ["IdentityFixture:Grants:0:ClientId"] = IdentityTestClients.ClientA.ClientId,
                ["IdentityFixture:Grants:0:Tenant"] = IdentityTestClients.TenantOne,
                ["IdentityFixture:Grants:0:Namespace"] = "ns-a",
            }
        );

        using HttpClient client = _host.CreateClient(IdentityTestClients.ClientA.Token);

        using HttpResponseMessage created = await client.PostAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, "identities"),
            new StringContent("""{ "LastSurname": "Rivera" }""", Encoding.UTF8, "application/json")
        );
        string id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!.GetValue<string>();

        _first = await GetAsync(client, id);
        _second = await GetAsync(client, id);
    }

    private static async Task<JsonNode?> GetAsync(HttpClient client, string id)
    {
        using HttpResponseMessage response = await client.GetAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, $"identities/{id}")
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    [Test]
    public void It_used_a_different_provider_instance_per_request()
    {
        _first!["FixtureProviderId"]!
            .GetValue<string>()
            .Should()
            .NotBe(_second!["FixtureProviderId"]!.GetValue<string>());
    }

    [Test]
    public void It_used_a_different_scoped_dependency_instance_per_request()
    {
        _first!["FixtureScopeId"]!
            .GetValue<string>()
            .Should()
            .NotBe(_second!["FixtureScopeId"]!.GetValue<string>());
    }

    [Test]
    public void It_read_capabilities_once_on_the_first_requests_instance()
    {
        _first!["FixtureCapabilitiesReadCount"]!.GetValue<int>().Should().Be(1);
    }

    [Test]
    public void It_read_capabilities_once_on_the_second_requests_instance()
    {
        _second!["FixtureCapabilitiesReadCount"]!.GetValue<int>().Should().Be(1);
    }
}
