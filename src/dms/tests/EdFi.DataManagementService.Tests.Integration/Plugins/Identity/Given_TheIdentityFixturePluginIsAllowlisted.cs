// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// The packed identity fixture loads through the real plugin loader, replaces the host's default
/// <c>IIdentityService</c>, and an authorized request reaches it.
/// </summary>
/// <remarks>
/// One boot for all of it. The identity-not-found type is what shows a call reached the fixture:
/// the host default answers <c>operation-not-supported</c>. The fixture is configured with one
/// namespace for client A's district and a grant for client A, so the smoke tests below show a
/// create, get-by-id round trip and an async find followed to completion; the behavior itself is
/// proven by the dedicated classes.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheIdentityFixturePluginIsAllowlisted
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string IdentityServiceType = "EdFi.DataManagementService.Identity.IIdentityService";

    private IdentityPluginHost? _host;
    private HttpResponseMessage? _getByIdResponse;
    private JsonNode? _getByIdBody;
    private IdentityServedOpenApi? _openApi;
    private string? _createdUniqueId;
    private JsonNode? _roundTrip;
    private HttpStatusCode _asyncFindStatus;
    private JsonNode? _asyncPollBody;
    private JsonNode? _asyncCompleteBody;

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
        _getByIdResponse = await client.GetAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, "identities/605943412")
        );
        _getByIdBody = JsonNode.Parse(await _getByIdResponse.Content.ReadAsStringAsync());
        _openApi = await IdentityServedOpenApi.FetchAsync(client);

        using HttpResponseMessage created = await client.PostAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, "identities"),
            Json("""{ "LastSurname": "Rivera", "FirstName": "Ana", "Favorite": "blue" }""")
        );
        _createdUniqueId = JsonNode.Parse(await created.Content.ReadAsStringAsync())?.GetValue<string>();
        using HttpResponseMessage fetched = await client.GetAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, $"identities/{_createdUniqueId}")
        );
        _roundTrip = JsonNode.Parse(await fetched.Content.ReadAsStringAsync());

        using HttpResponseMessage accepted = await client.PostAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, "identities/find"),
            Json($"""["{_createdUniqueId}", "~fixture:async"]""")
        );
        _asyncFindStatus = accepted.StatusCode;
        Uri location = accepted.Headers.Location!;
        using HttpResponseMessage first = await client.GetAsync(location);
        _asyncPollBody = JsonNode.Parse(await first.Content.ReadAsStringAsync());
        using HttpResponseMessage second = await client.GetAsync(location);
        _asyncCompleteBody = JsonNode.Parse(await second.Content.ReadAsStringAsync());
    }

    private static StringContent Json(string body) =>
        new(body, System.Text.Encoding.UTF8, "application/json");

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _getByIdResponse?.Dispose();

        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    [Test]
    public void It_reached_the_ready_phase()
    {
        PluginHostProbe
            .ReadStartupStatus(_host!.StartupStatusFilePath)["State"]
            ?.GetValue<string>()
            .Should()
            .Be("Ready");
    }

    [Test]
    public void It_named_the_fixture_in_one_inventory_event()
    {
        _host!
            .Capture.InventoryEvents.Select(logEvent => PluginLogCapture.ScalarText(logEvent, "PluginName"))
            .Should()
            .BeEquivalentTo(FixturePlugin);
    }

    [Test]
    public void It_reported_the_identity_service_type_the_fixture_registered()
    {
        PluginLogCapture
            .Sequence(_host!.Capture.InventoryEvents.Single(), "RegisteredServiceTypes")
            .Select(entry => PluginLogCapture.Member(entry, "ServiceType"))
            .Should()
            .Contain(IdentityServiceType);
    }

    [Test]
    public void It_answers_an_authorized_get_by_id_with_not_found()
    {
        _getByIdResponse!.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public void It_answers_with_the_identity_not_found_type_rather_than_the_host_default()
    {
        _getByIdBody!["type"]!
            .GetValue<string>()
            .Should()
            .Be(IdentityFailureResponse.NotFoundType)
            .And.NotBe(IdentityFailureResponse.OperationNotSupportedType);
    }

    [Test]
    public void It_serves_the_identity_openapi_document()
    {
        _openApi!.Paths.Select(pair => pair.Key).Should().Contain("/identities/{id}");
    }

    [Test]
    public void It_issues_a_thirty_two_character_alphanumeric_id_on_create()
    {
        _createdUniqueId.Should().MatchRegex("^[0-9A-Za-z]{32}$");
    }

    [Test]
    public void It_returns_the_created_person_with_unsupported_attributes_as_null_and_custom_properties()
    {
        _roundTrip!["UniqueId"]!.GetValue<string>().Should().Be(_createdUniqueId);
        _roundTrip["LastSurname"]!.GetValue<string>().Should().Be("Rivera");
        _roundTrip["MiddleName"].Should().BeNull();
        _roundTrip["Score"].Should().BeNull();
        _roundTrip["BirthLocation"]!["City"].Should().BeNull();
        _roundTrip["Favorite"]!.GetValue<string>().Should().Be("blue");
    }

    [Test]
    public void It_echoes_the_lifetime_probe_on_get_by_id()
    {
        _roundTrip!["FixtureCapabilitiesReadCount"]!.GetValue<int>().Should().Be(1);
        Guid.TryParse(_roundTrip["FixtureProviderId"]!.GetValue<string>(), out _).Should().BeTrue();
        Guid.TryParse(_roundTrip["FixtureScopeId"]!.GetValue<string>(), out _).Should().BeTrue();
    }

    [Test]
    public void It_accepts_an_async_find_with_a_location()
    {
        _asyncFindStatus.Should().Be(HttpStatusCode.Accepted);
    }

    [Test]
    public void It_polls_an_async_find_from_incomplete_to_complete()
    {
        _asyncPollBody!["Status"]!.GetValue<string>().Should().Be("Incomplete");
        _asyncCompleteBody!["Status"]!.GetValue<string>().Should().Be("Complete");
        _asyncCompleteBody["SearchResponses"]!.AsArray().Should().HaveCount(2);
        _asyncCompleteBody["SearchResponses"]![0]!["Responses"]![0]!["UniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(_createdUniqueId);
    }
}
