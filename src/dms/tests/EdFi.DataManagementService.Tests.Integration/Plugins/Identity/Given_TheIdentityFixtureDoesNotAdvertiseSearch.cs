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
/// A provider that does not advertise <c>Search</c> gets operation-unsupported 404 for search
/// without being invoked, while the other four operations still reach it.
/// </summary>
/// <remarks>
/// The reaching operations are the negative control: the same client, grant and route answer
/// something other than operation-unsupported, and the control stub's invocation events show the
/// provider was called once for each of them and never for search.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheIdentityFixtureDoesNotAdvertiseSearch
{
    private const string FixturePlugin = "Acme.IdentityFixture";

    private IdentityFixtureControlStub? _stub;
    private IdentityPluginHost? _host;
    private readonly Dictionary<string, (HttpStatusCode Status, JsonNode? Body)> _answers = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _stub = await IdentityFixtureControlStub.StartAsync();
        _stub.Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-a");
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: new Dictionary<string, string>
            {
                ["IdentityFixture:Capabilities"] = "Create,GetById,Find,Results",
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
        }

        if (_stub is not null)
        {
            await _stub.DisposeAsync();
        }
    }

    [Test]
    public void It_answers_search_with_operation_not_supported()
    {
        _answers["search"].Status.Should().Be(HttpStatusCode.NotFound);
        _answers["search"].Body!["type"]!
            .GetValue<string>()
            .Should()
            .Be(IdentityFailureResponse.OperationNotSupportedType);
    }

    [Test]
    public void It_did_not_invoke_the_provider_for_search()
    {
        _stub!.InvocationCount("search").Should().Be(0);
    }

    [TestCase("create")]
    [TestCase("getById")]
    [TestCase("find")]
    [TestCase("results")]
    public void It_does_not_answer_operation_not_supported_for_an_advertised_operation(string operation)
    {
        string? type = _answers[operation].Body is JsonObject problem
            ? problem["type"]?.GetValue<string>()
            : null;

        type.Should().NotBe(IdentityFailureResponse.OperationNotSupportedType);
    }

    [TestCase("create")]
    [TestCase("getById")]
    [TestCase("find")]
    [TestCase("results")]
    public void It_invoked_the_provider_once_for_an_advertised_operation(string operation)
    {
        _stub!.InvocationCount(operation).Should().Be(1);
    }
}
