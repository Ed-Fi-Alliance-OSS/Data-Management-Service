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
/// R3 and F27: the loader is on (a non-empty allowlist naming a staged plugin) but no plugin
/// supplies an identity provider, so the host starts and every identity operation answers the
/// host default, operation-unsupported 404.
/// </summary>
/// <remarks>
/// The negative control is <c>Given_TheIdentityFixturePluginIsAllowlisted</c>, where the same
/// route answers the provider's not-found type instead of this one. The loader-on premise is
/// asserted here through the inventory event, because an empty allowlist never touches the
/// filesystem and would pass every other assertion.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheLoaderIsOnWithNoIdentityPlugin
{
    private const string ContributorPlugin = "Acme.DmsContributor";

    private IdentityPluginHost? _host;
    private readonly Dictionary<string, (HttpStatusCode Status, JsonNode? Body)> _answers = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _host = IdentityPluginHost.Create(
            [],
            allowed: ContributorPlugin,
            projectReferenceFixtures: [ContributorPlugin]
        );

        using HttpClient client = _host.CreateClient(IdentityTestClients.ClientA.Token);

        _answers["create"] = await SendAsync(
            client,
            HttpMethod.Post,
            "identities",
            """{ "LastSurname": "Rivera", "FirstName": "Ana" }"""
        );
        _answers["getById"] = await SendAsync(client, HttpMethod.Get, "identities/605943412", null);
        _answers["find"] = await SendAsync(client, HttpMethod.Post, "identities/find", """["605943412"]""");
        _answers["search"] = await SendAsync(
            client,
            HttpMethod.Post,
            "identities/search",
            """[{ "LastSurname": "Rivera" }]"""
        );
        _answers["results"] = await SendAsync(
            client,
            HttpMethod.Get,
            "identities/results/0123456789abcdef0123456789abcdef",
            null
        );
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
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync()));
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
    public void It_loaded_the_allowlisted_non_identity_plugin()
    {
        _host!
            .Capture.InventoryEvents.Select(logEvent => PluginLogCapture.ScalarText(logEvent, "PluginName"))
            .Should()
            .BeEquivalentTo(ContributorPlugin);
    }

    [Test]
    public void It_reached_the_ready_phase()
    {
        PluginHostProbe.ReadStartupStatus(_host!.StartupStatusFilePath)["State"]!
            .GetValue<string>()
            .Should()
            .Be("Ready");
    }

    [TestCase("create")]
    [TestCase("getById")]
    [TestCase("find")]
    [TestCase("search")]
    [TestCase("results")]
    public void It_answers_not_found_at_the_http_layer(string operation)
    {
        _answers[operation].Status.Should().Be(HttpStatusCode.NotFound);
    }

    [TestCase("create")]
    [TestCase("getById")]
    [TestCase("find")]
    [TestCase("search")]
    [TestCase("results")]
    public void It_answers_with_the_operation_not_supported_type(string operation)
    {
        _answers[operation].Body!["type"]!
            .GetValue<string>()
            .Should()
            .Be(IdentityFailureResponse.OperationNotSupportedType);
    }
}
