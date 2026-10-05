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
/// Two independent namespaces hold the same UniqueId for different people and each answers only
/// its own, while two contexts mapped to one shared namespace resolve an id the same way.
/// </summary>
/// <remarks>
/// The fixture issues random ids, so the reuse is arranged with seeded persons under an explicit id.
/// The negative control is an id seeded in one namespace only, which the other must not answer.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TwoIndependentNamespacesReuseAnId
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string ReusedId = "0123456789abcdef0123456789abcdef";
    private const string OnlyInFirstId = "fedcba9876543210fedcba9876543210";
    private const string SharedSeedId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private IdentityPluginHost? _host;
    private (HttpStatusCode Status, JsonNode? Body) _firstOwnsReusedId;
    private (HttpStatusCode Status, JsonNode? Body) _secondOwnsReusedId;
    private (HttpStatusCode Status, JsonNode? Body) _secondAsksForFirstOnlyId;
    private (HttpStatusCode Status, JsonNode? Body) _firstFind;
    private (HttpStatusCode Status, JsonNode? Body) _secondFind;
    private (HttpStatusCode Status, JsonNode? Body) _sharedViaFirstContext;
    private (HttpStatusCode Status, JsonNode? Body) _sharedViaSecondContext;
    private (HttpStatusCode Status, JsonNode? Body) _createdViaFirstContext;
    private (HttpStatusCode Status, JsonNode? Body) _createdViaSecondContext;

    [OneTimeSetUp]
    public async Task Setup()
    {
        string[] tenantOne = [.. IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)];
        string[] tenantTwo = [.. IdentityTestClients.DistrictsOf(IdentityTestClients.TenantTwo)];

        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .Namespace("ns-first", (IdentityTestClients.TenantOne, tenantOne[0]))
            .Seed(ReusedId, "Rivera", "Ana")
            .Seed(OnlyInFirstId, "Solo", "Sam", seedIndex: 1)
            .Namespace("ns-second", (IdentityTestClients.TenantOne, tenantOne[1]))
            .Seed(ReusedId, "Smith", "Bob")
            .Namespace(
                "ns-shared",
                (IdentityTestClients.TenantTwo, tenantTwo[0]),
                (IdentityTestClients.TenantTwo, tenantTwo[1])
            )
            .Seed(SharedSeedId, "Lee", "Kim")
            .Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-first")
            .Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-second")
            .Grant(IdentityTestClients.ClientC.ClientId, IdentityTestClients.TenantTwo, "ns-shared");

        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings
        );

        using HttpClient clientA = _host.CreateClient(IdentityTestClients.ClientA.Token);
        using HttpClient clientC = _host.CreateClient(IdentityTestClients.ClientC.Token);

        string FirstRoute(string path) =>
            IdentityTestClients.Route(IdentityTestClients.TenantOne, tenantOne[0], path);
        string SecondRoute(string path) =>
            IdentityTestClients.Route(IdentityTestClients.TenantOne, tenantOne[1], path);
        string SharedRoute(int context, string path) =>
            IdentityTestClients.Route(IdentityTestClients.TenantTwo, tenantTwo[context], path);

        _firstOwnsReusedId = await SendAsync(clientA, HttpMethod.Get, FirstRoute($"identities/{ReusedId}"));
        _secondOwnsReusedId = await SendAsync(clientA, HttpMethod.Get, SecondRoute($"identities/{ReusedId}"));
        _secondAsksForFirstOnlyId = await SendAsync(
            clientA,
            HttpMethod.Get,
            SecondRoute($"identities/{OnlyInFirstId}")
        );
        _firstFind = await SendAsync(
            clientA,
            HttpMethod.Post,
            FirstRoute("identities/find"),
            $"""["{ReusedId}"]"""
        );
        _secondFind = await SendAsync(
            clientA,
            HttpMethod.Post,
            SecondRoute("identities/find"),
            $"""["{ReusedId}"]"""
        );

        _sharedViaFirstContext = await SendAsync(
            clientC,
            HttpMethod.Get,
            SharedRoute(0, $"identities/{SharedSeedId}")
        );
        _sharedViaSecondContext = await SendAsync(
            clientC,
            HttpMethod.Get,
            SharedRoute(1, $"identities/{SharedSeedId}")
        );

        (HttpStatusCode _, JsonNode? created) = await SendAsync(
            clientC,
            HttpMethod.Post,
            SharedRoute(0, "identities"),
            """{ "LastSurname": "Park", "FirstName": "Jo" }"""
        );
        string createdId = created!.GetValue<string>();
        _createdViaFirstContext = await SendAsync(
            clientC,
            HttpMethod.Get,
            SharedRoute(0, $"identities/{createdId}")
        );
        _createdViaSecondContext = await SendAsync(
            clientC,
            HttpMethod.Get,
            SharedRoute(1, $"identities/{createdId}")
        );
    }

    private static async Task<(HttpStatusCode, JsonNode?)> SendAsync(
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
        return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
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
    public void It_answers_the_reused_id_with_the_first_namespaces_person_from_the_first_namespace()
    {
        _firstOwnsReusedId.Status.Should().Be(HttpStatusCode.OK);
        _firstOwnsReusedId.Body!["LastSurname"]!.GetValue<string>().Should().Be("Rivera");
        _firstOwnsReusedId.Body["FirstName"]!.GetValue<string>().Should().Be("Ana");
    }

    [Test]
    public void It_answers_the_reused_id_with_the_second_namespaces_person_from_the_second_namespace()
    {
        _secondOwnsReusedId.Status.Should().Be(HttpStatusCode.OK);
        _secondOwnsReusedId.Body!["LastSurname"]!.GetValue<string>().Should().Be("Smith");
        _secondOwnsReusedId.Body["FirstName"]!.GetValue<string>().Should().Be("Bob");
    }

    [Test]
    public void It_does_not_answer_an_id_seeded_only_in_the_first_namespace_from_the_second()
    {
        _secondAsksForFirstOnlyId.Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public void It_finds_the_reused_id_as_each_namespaces_own_person()
    {
        _firstFind.Body!["SearchResponses"]![0]!["Responses"]![0]!["LastSurname"]!
            .GetValue<string>()
            .Should()
            .Be("Rivera");
        _secondFind.Body!["SearchResponses"]![0]!["Responses"]![0]!["LastSurname"]!
            .GetValue<string>()
            .Should()
            .Be("Smith");
    }

    [Test]
    public void It_resolves_a_seeded_id_the_same_way_from_each_context_of_a_shared_namespace()
    {
        _sharedViaFirstContext.Status.Should().Be(HttpStatusCode.OK);
        _sharedViaSecondContext.Status.Should().Be(HttpStatusCode.OK);
        _sharedViaSecondContext.Body!["LastSurname"]!
            .GetValue<string>()
            .Should()
            .Be(_sharedViaFirstContext.Body!["LastSurname"]!.GetValue<string>())
            .And.Be("Lee");
    }

    [Test]
    public void It_resolves_an_issued_id_through_the_other_context_of_a_shared_namespace()
    {
        _createdViaFirstContext.Status.Should().Be(HttpStatusCode.OK);
        _createdViaSecondContext.Status.Should().Be(HttpStatusCode.OK);
        _createdViaSecondContext.Body!["LastSurname"]!.GetValue<string>().Should().Be("Park");
    }
}
