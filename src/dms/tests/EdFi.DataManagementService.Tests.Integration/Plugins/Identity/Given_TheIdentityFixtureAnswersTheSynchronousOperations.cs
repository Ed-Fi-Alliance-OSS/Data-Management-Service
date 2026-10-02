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
/// F2, F3 (synchronous payloads), F21, F22, F23, F25 and F26: create, get-by-id, find and search
/// succeed over HTTP through the fixture, their payloads conform to the served document, standard
/// attributes, scores and custom properties arrive as the fixture produced them, and no match or an
/// unknown id is answered as a successful empty group or an identity-not-found 404.
/// </summary>
/// <remarks>
/// The conformance assertions are paired with a negative control: the same payload with the wire
/// <c>Status</c> changed to the incomplete value fails the same schema.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheIdentityFixtureAnswersTheSynchronousOperations
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string MissingId = "ffffffffffffffffffffffffffffffff";

    private IdentityPluginHost? _host;
    private IdentityServedOpenApi? _openApi;
    private readonly Dictionary<string, (HttpStatusCode Status, JsonNode? Body)> _answers = [];
    private string _anaId = string.Empty;
    private string _bobId = string.Empty;

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
        _openApi = await IdentityServedOpenApi.FetchAsync(client);

        _answers["createAna"] = await SendAsync(
            client,
            HttpMethod.Post,
            "identities",
            """{ "LastSurname": "Rivera", "FirstName": "Ana", "Favorite": "blue" }"""
        );
        _anaId = _answers["createAna"].Body!.GetValue<string>();
        _answers["createBob"] = await SendAsync(
            client,
            HttpMethod.Post,
            "identities",
            """{ "LastSurname": "Smith", "FirstName": "Bob" }"""
        );
        _bobId = _answers["createBob"].Body!.GetValue<string>();

        _answers["getAna"] = await SendAsync(client, HttpMethod.Get, $"identities/{_anaId}", null);
        _answers["find"] = await SendAsync(
            client,
            HttpMethod.Post,
            "identities/find",
            $"""["{_bobId}", "{_anaId}"]"""
        );
        _answers["search"] = await SendAsync(
            client,
            HttpMethod.Post,
            "identities/search",
            """[{ "LastSurname": "Rivera", "FirstName": "Ana" }, { "LastSurname": "Rivera", "FirstName": "Zed" }]"""
        );
        _answers["findNoMatch"] = await SendAsync(
            client,
            HttpMethod.Post,
            "identities/find",
            $"""["{MissingId}"]"""
        );
        _answers["searchNoMatch"] = await SendAsync(
            client,
            HttpMethod.Post,
            "identities/search",
            """[{ "LastSurname": "Nobody" }]"""
        );
        _answers["getMissing"] = await SendAsync(client, HttpMethod.Get, $"identities/{MissingId}", null);
    }

    private static async Task<(HttpStatusCode, JsonNode?)> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? body
    )
    {
        using HttpRequestMessage request = new(
            method,
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, path)
        );
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

    private JsonNode Body(string key) => _answers[key].Body!;

    private JsonNode Group(string key, int index) => Body(key)["SearchResponses"]![index]!;

    [Test]
    public void It_succeeds_for_create_get_by_id_find_and_search()
    {
        foreach (string key in new[] { "createAna", "createBob", "getAna", "find", "search" })
        {
            _answers[key].Status.Should().Be(HttpStatusCode.OK, key);
        }
    }

    [Test]
    public void It_answers_create_with_a_payload_that_conforms_to_the_served_create_schema()
    {
        _openApi!.Conforms("/identities", "post", "200", Body("createAna")).Should().BeTrue();
    }

    [Test]
    public void It_rejects_a_create_payload_that_is_not_a_string_against_the_served_create_schema()
    {
        _openApi!.Conforms("/identities", "post", "200", JsonNode.Parse("42")!).Should().BeFalse();
    }

    [Test]
    public void It_answers_get_by_id_with_a_payload_that_conforms_to_the_served_get_schema()
    {
        _openApi!.Conforms("/identities/{id}", "get", "200", Body("getAna")).Should().BeTrue();
    }

    [Test]
    public void It_answers_find_with_wire_status_complete()
    {
        Body("find")["Status"]!.GetValue<string>().Should().Be("Complete");
    }

    [Test]
    public void It_answers_find_with_a_payload_that_conforms_to_the_served_find_schema()
    {
        _openApi!.Conforms("/identities/find", "post", "200", Body("find")).Should().BeTrue();
    }

    [Test]
    public void It_rejects_the_find_payload_once_its_status_is_changed_to_incomplete()
    {
        JsonNode broken = Body("find").DeepClone();
        broken["Status"] = "Incomplete";

        _openApi!.Conforms("/identities/find", "post", "200", broken).Should().BeFalse();
    }

    [Test]
    public void It_answers_search_with_wire_status_complete()
    {
        Body("search")["Status"]!.GetValue<string>().Should().Be("Complete");
    }

    [Test]
    public void It_answers_search_with_a_payload_that_conforms_to_the_served_search_schema()
    {
        _openApi!.Conforms("/identities/search", "post", "200", Body("search")).Should().BeTrue();
    }

    [Test]
    public void It_rejects_the_search_payload_once_its_status_is_changed_to_incomplete()
    {
        JsonNode broken = Body("search").DeepClone();
        broken["Status"] = "Incomplete";

        _openApi!.Conforms("/identities/search", "post", "200", broken).Should().BeFalse();
    }

    [Test]
    public void It_answers_find_with_one_positional_group_per_submitted_id()
    {
        Body("find")["SearchResponses"]!.AsArray().Should().HaveCount(2);
        Group("find", 0)["Responses"]![0]!["UniqueId"]!.GetValue<string>().Should().Be(_bobId);
        Group("find", 1)["Responses"]![0]!["UniqueId"]!.GetValue<string>().Should().Be(_anaId);
    }

    [Test]
    public void It_carries_a_custom_property_from_create_to_get_by_id()
    {
        Body("getAna")["Favorite"]!.GetValue<string>().Should().Be("blue");
    }

    [Test]
    public void It_does_not_invent_a_custom_property_the_created_person_never_had()
    {
        Body("getAna").AsObject().ContainsKey("Unfavorite").Should().BeFalse();
    }

    [Test]
    public void It_passes_the_properties_the_fixture_adds_to_a_response_through_unchanged()
    {
        JsonNode body = Body("getAna");

        Guid.TryParse(body["FixtureProviderId"]!.GetValue<string>(), out _).Should().BeTrue();
        Guid.TryParse(body["FixtureScopeId"]!.GetValue<string>(), out _).Should().BeTrue();
        body["FixtureCapabilitiesReadCount"]!.GetValue<int>().Should().Be(1);
    }

    [Test]
    public void It_returns_the_standard_identifying_attributes_the_person_has()
    {
        JsonNode body = Body("getAna");

        body["UniqueId"]!.GetValue<string>().Should().Be(_anaId);
        body["LastSurname"]!.GetValue<string>().Should().Be("Rivera");
        body["FirstName"]!.GetValue<string>().Should().Be("Ana");
    }

    [Test]
    [TestCase("MiddleName")]
    [TestCase("GenerationCodeSuffix")]
    [TestCase("SexType")]
    [TestCase("BirthDate")]
    [TestCase("BirthOrder")]
    [TestCase("Score")]
    public void It_returns_an_attribute_the_person_lacks_as_an_explicit_null(string property)
    {
        Body("getAna").AsObject().ContainsKey(property).Should().BeTrue();
        Body("getAna")[property].Should().BeNull();
    }

    [Test]
    public void It_returns_the_birth_location_with_its_four_children_as_null()
    {
        JsonObject birthLocation = Body("getAna")["BirthLocation"]!.AsObject();

        birthLocation
            .Select(pair => pair.Key)
            .Should()
            .BeEquivalentTo("City", "StateAbbreviation", "InternationalProvince", "Country");
        birthLocation.All(pair => pair.Value is null).Should().BeTrue();
    }

    [Test]
    public void It_carries_the_score_the_fixture_produced_on_a_full_match()
    {
        Group("search", 0)["Responses"]![0]!["Score"]!.GetValue<double>().Should().Be(100);
    }

    [Test]
    public void It_carries_the_score_the_fixture_produced_on_a_half_match()
    {
        JsonNode match = Group("search", 1)["Responses"]![0]!;

        match["UniqueId"]!.GetValue<string>().Should().Be(_anaId);
        match["Score"]!.GetValue<double>().Should().Be(50);
    }

    [Test]
    public void It_does_not_return_a_person_that_agrees_with_none_of_the_supplied_attributes()
    {
        Group("search", 0)["Responses"]!
            .AsArray()
            .Select(match => match!["UniqueId"]!.GetValue<string>())
            .Should()
            .NotContain(_bobId);
    }

    [Test]
    public void It_answers_a_find_that_matches_nothing_with_successful_empty_groups()
    {
        _answers["findNoMatch"].Status.Should().Be(HttpStatusCode.OK);
        Group("findNoMatch", 0)["Responses"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public void It_answers_a_search_that_matches_nothing_with_successful_empty_groups()
    {
        _answers["searchNoMatch"].Status.Should().Be(HttpStatusCode.OK);
        Group("searchNoMatch", 0)["Responses"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public void It_answers_the_empty_groups_with_payloads_that_conform_to_the_served_schemas()
    {
        _openApi!.Conforms("/identities/find", "post", "200", Body("findNoMatch")).Should().BeTrue();
        _openApi.Conforms("/identities/search", "post", "200", Body("searchNoMatch")).Should().BeTrue();
    }

    [Test]
    public void It_answers_get_by_id_of_an_unknown_id_with_identity_not_found()
    {
        _answers["getMissing"].Status.Should().Be(HttpStatusCode.NotFound);
        Body("getMissing")["type"]!.GetValue<string>().Should().Be(IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_does_not_answer_an_unknown_id_as_operation_not_supported()
    {
        Body("getMissing")["type"]!
            .GetValue<string>()
            .Should()
            .NotBe(IdentityFailureResponse.OperationNotSupportedType);
    }
}
