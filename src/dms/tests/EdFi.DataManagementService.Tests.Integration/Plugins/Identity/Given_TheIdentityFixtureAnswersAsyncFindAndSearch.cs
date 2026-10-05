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
/// An async find and an async search answer 202 with a
/// <c>Location</c>, following that <c>Location</c> exactly as returned reaches the results route,
/// and the poll answers incomplete until the fixture's configured poll count has passed and complete
/// after it. Every results payload conforms to the served results schema.
/// </summary>
/// <remarks>
/// The fixture is configured with two incomplete polls, so a host that stopped after one poll or
/// treated the first answer as final would fail the ordering assertions.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheIdentityFixtureAnswersAsyncFindAndSearch
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const int PollsUntilComplete = 2;
    private const string ResultsPath = "/identities/results/{id}";

    private IdentityPluginHost? _host;
    private IdentityServedOpenApi? _openApi;
    private string _anaId = string.Empty;

    private sealed record Flow(
        HttpStatusCode AcceptedStatus,
        string? Location,
        List<(HttpStatusCode Status, JsonNode? Body)> Polls
    );

    private Flow? _find;
    private Flow? _search;
    private JsonNode? _repeatedPoll;

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
                ["IdentityFixture:PollsUntilComplete"] = PollsUntilComplete.ToString(),
            }
        );

        using HttpClient client = _host.CreateClient(IdentityTestClients.ClientA.Token);
        _openApi = await IdentityServedOpenApi.FetchAsync(client);

        using HttpResponseMessage created = await client.PostAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, "identities"),
            Json("""{ "LastSurname": "Rivera", "FirstName": "Ana" }""")
        );
        _anaId = JsonNode.Parse(await created.Content.ReadAsStringAsync())!.GetValue<string>();

        _find = await RunAsync(client, "identities/find", $"""["{_anaId}", "~fixture:async"]""");
        _search = await RunAsync(
            client,
            "identities/search",
            """[{ "LastSurname": "Rivera", "FirstName": "Ana", "~FixtureAsync": true }]"""
        );
        using HttpResponseMessage again = await client.GetAsync(_find.Location!);
        _repeatedPoll = JsonNode.Parse(await again.Content.ReadAsStringAsync());
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<Flow> RunAsync(HttpClient client, string path, string body)
    {
        using HttpResponseMessage accepted = await client.PostAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, path),
            Json(body)
        );
        string? location = accepted.Headers.TryGetValues("Location", out IEnumerable<string>? values)
            ? values.Single()
            : null;
        List<(HttpStatusCode, JsonNode?)> polls = [];

        if (location is not null)
        {
            // Bounded and poll-count based: the location is used byte for byte as returned.
            for (int attempt = 0; attempt <= PollsUntilComplete; attempt++)
            {
                using HttpResponseMessage poll = await client.GetAsync(location);
                string text = await poll.Content.ReadAsStringAsync();
                polls.Add((poll.StatusCode, JsonNode.Parse(text)));
            }
        }

        return new Flow(accepted.StatusCode, location, polls);
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
    public void It_accepts_an_async_find_with_202()
    {
        _find!.AcceptedStatus.Should().Be(HttpStatusCode.Accepted);
    }

    [Test]
    public void It_accepts_an_async_search_with_202()
    {
        _search!.AcceptedStatus.Should().Be(HttpStatusCode.Accepted);
    }

    [Test]
    public void It_returns_a_location_for_the_async_find()
    {
        _find!.Location.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void It_returns_a_location_for_the_async_search()
    {
        _search!.Location.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void It_points_both_locations_at_distinct_results_routes()
    {
        _find!.Location.Should().Contain("/identity/v2/identities/results/");
        _search!.Location.Should().Contain("/identity/v2/identities/results/");
        _find.Location.Should().NotBe(_search.Location);
    }

    [Test]
    public void It_polls_the_async_find_through_incomplete_to_complete()
    {
        AssertIncompleteThenComplete(_find!);
    }

    [Test]
    public void It_polls_the_async_search_through_incomplete_to_complete()
    {
        AssertIncompleteThenComplete(_search!);
    }

    private static void AssertIncompleteThenComplete(Flow flow)
    {
        flow.Polls.Should().HaveCount(PollsUntilComplete + 1);
        flow.Polls.Select(poll => poll.Status).Should().OnlyContain(status => status == HttpStatusCode.OK);
        flow.Polls.Take(PollsUntilComplete)
            .Select(poll => poll.Body!["Status"]!.GetValue<string>())
            .Should()
            .OnlyContain(status => status == "Incomplete");
        flow.Polls[PollsUntilComplete].Body!["Status"]!.GetValue<string>().Should().Be("Complete");
    }

    [Test]
    public void It_answers_the_pending_results_with_an_empty_response_list()
    {
        _find!.Polls[0].Body!["SearchResponses"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public void It_answers_the_pending_results_with_a_payload_that_conforms_to_the_results_schema()
    {
        _openApi!.Conforms(ResultsPath, "get", "200", _find!.Polls[0].Body!).Should().BeTrue();
        _openApi.Conforms(ResultsPath, "get", "200", _search!.Polls[0].Body!).Should().BeTrue();
    }

    [Test]
    public void It_answers_the_complete_results_with_payloads_that_conform_to_the_results_schema()
    {
        _openApi!
            .Conforms(ResultsPath, "get", "200", _find!.Polls[PollsUntilComplete].Body!)
            .Should()
            .BeTrue();
        _openApi
            .Conforms(ResultsPath, "get", "200", _search!.Polls[PollsUntilComplete].Body!)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_rejects_a_results_payload_whose_status_is_not_a_known_wire_value()
    {
        JsonNode broken = _find!.Polls[0].Body!.DeepClone();
        broken["Status"] = "Pending";

        _openApi!.Conforms(ResultsPath, "get", "200", broken).Should().BeFalse();
    }

    [Test]
    public void It_completes_the_async_find_with_one_positional_group_per_submitted_element()
    {
        JsonNode complete = _find!.Polls[PollsUntilComplete].Body!;

        complete["SearchResponses"]!.AsArray().Should().HaveCount(2);
        complete["SearchResponses"]![0]!["Responses"]![0]!["UniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(_anaId);
        complete["SearchResponses"]![1]!["Responses"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public void It_completes_the_async_search_with_the_scored_match()
    {
        JsonNode match = _search!.Polls[PollsUntilComplete].Body!["SearchResponses"]![0]!["Responses"]![0]!;

        match["UniqueId"]!.GetValue<string>().Should().Be(_anaId);
        match["Score"]!.GetValue<double>().Should().Be(100);
    }

    [Test]
    public void It_answers_a_repeated_poll_of_a_complete_job_with_the_same_payload()
    {
        // The fixture documents that a complete job answers every later poll identically.
        IdentityServedOpenApi
            .AreCanonicallyEqual(_repeatedPoll!, _find!.Polls[PollsUntilComplete].Body!)
            .Should()
            .BeTrue();
    }
}
