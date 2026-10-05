// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A request token that needs escaping is escaped once into the <c>202 Location</c>, and the
/// provider's <c>ResultsAsync</c> receives the original token character for character after the
/// client follows that <c>Location</c> exactly as returned, for find and for search.
/// </summary>
/// <remarks>
/// The tokens cover a space and non-ASCII letter with a percent sign, a token that is already a valid
/// escape sequence (<c>50%25</c>, which a second unescape would turn into <c>50%</c>), a percent
/// sequence for the path separator, and reserved characters that are legal inside one segment. The
/// provider's own report of the token it received is the evidence, not the client's view of the URL.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_AProviderTokenNeedsEscapingInThePollUrl
{
    private static readonly string[] Tokens = ["a%b cé", "50%25", "tok%2Fen", "a:b@c+d;e=f,g$h&i"];

    private IdentityHttpRun? _run;
    private readonly Dictionary<string, IdentityHttpOutcome> _accepted = [];
    private readonly Dictionary<string, IdentityHttpOutcome> _polled = [];

    private static string Key(string kind, string token) => $"{kind}:{token}";

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        foreach (string token in Tokens)
        {
            string quoted = JsonSerializer.Serialize(token);
            string findBody = $"[{JsonSerializer.Serialize($"~fixture:token:{token}")}]";
            string searchBody = $$"""[{ "~FixtureToken": {{quoted}} }]""";

            _accepted[Key("find", token)] = await _run.PostAsync("identities/find", findBody);
            _polled[Key("find", token)] = await _run.GetAsync(_accepted[Key("find", token)].Location!);
            _accepted[Key("search", token)] = await _run.PostAsync("identities/search", searchBody);
            _polled[Key("search", token)] = await _run.GetAsync(_accepted[Key("search", token)].Location!);
        }
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_run is not null)
        {
            await _run.DisposeAsync();
            _run = null;
        }
    }

    [TestCase("a%b cé")]
    [TestCase("50%25")]
    [TestCase("tok%2Fen")]
    [TestCase("a:b@c+d;e=f,g$h&i")]
    public void It_accepts_an_escaped_token_from_find_and_search(string token)
    {
        _accepted[Key("find", token)].Status.Should().Be(HttpStatusCode.Accepted);
        _accepted[Key("search", token)].Status.Should().Be(HttpStatusCode.Accepted);
    }

    [TestCase("a%b cé")]
    [TestCase("50%25")]
    [TestCase("tok%2Fen")]
    [TestCase("a:b@c+d;e=f,g$h&i")]
    public void It_ends_the_location_with_the_token_escaped_exactly_once(string token)
    {
        _accepted[Key("find", token)].Location.Should().EndWith("/" + Uri.EscapeDataString(token));
        _accepted[Key("search", token)].Location.Should().EndWith("/" + Uri.EscapeDataString(token));
    }

    [TestCase("a%b cé")]
    [TestCase("50%25")]
    [TestCase("tok%2Fen")]
    [TestCase("a:b@c+d;e=f,g$h&i")]
    public void It_polls_the_followed_location_to_a_provider_owned_answer(string token)
    {
        _polled[Key("find", token)].Status.Should().Be(HttpStatusCode.OK);
        _polled[Key("search", token)].Status.Should().Be(HttpStatusCode.OK);
    }

    [TestCase("a%b cé")]
    [TestCase("50%25")]
    [TestCase("tok%2Fen")]
    [TestCase("a:b@c+d;e=f,g$h&i")]
    public void It_hands_the_provider_the_original_token_for_find_and_search(string token)
    {
        _run!.Stub.ReceivedResultTokens.Count(received => received == token).Should().Be(2);
    }

    [Test]
    public void It_polls_exactly_the_tokens_it_issued_and_nothing_else()
    {
        string[] expected = [.. Tokens.SelectMany(token => new[] { token, token })];
        _run!.Stub.ReceivedResultTokens.Should().Equal(expected);
    }

    [Test]
    public void It_does_not_unescape_the_token_a_second_time()
    {
        _run!.Stub.ReceivedResultTokens.Should().NotContain("50%");
    }
}
