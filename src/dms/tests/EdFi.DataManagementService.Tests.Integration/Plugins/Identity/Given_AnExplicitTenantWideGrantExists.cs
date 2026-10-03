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
/// F5: an explicit tenant-wide grant (<c>ClientId</c> of <c>*</c>) lets another host-authorized client
/// read the namespace, but a grant never waives job ownership: client B cannot redeem the job token
/// client A was given.
/// </summary>
/// <remarks>
/// The negative control for the grant is the same client B against a namespace nothing grants it.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_AnExplicitTenantWideGrantExists
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string SeededId = "0123456789abcdef0123456789abcdef";

    private IdentityPluginHost? _host;
    private HttpStatusCode _clientBReadsGrantedNamespace;
    private HttpStatusCode _clientBReadsUngrantedNamespace;
    private HttpStatusCode _clientAPollsOwnJob;
    private HttpStatusCode _clientBPollsClientAsJob;
    private string? _clientBPollProblemType;

    [OneTimeSetUp]
    public async Task Setup()
    {
        string[] districts = [.. IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)];

        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .Namespace("district-a", (IdentityTestClients.TenantOne, districts[0]))
            .Seed(SeededId, "Rivera", "Ana")
            .Namespace("district-b", (IdentityTestClients.TenantOne, districts[1]))
            .Seed(SeededId, "Smith", "Bob")
            .Grant("*", IdentityTestClients.TenantOne, "district-a")
            .With("IdentityFixture:PollsUntilComplete", "0");
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings
        );

        using HttpClient clientA = _host.CreateClient(IdentityTestClients.ClientA.Token);
        using HttpClient clientB = _host.CreateClient(IdentityTestClients.ClientB.Token);

        string A(string path) => IdentityTestClients.Route(IdentityTestClients.TenantOne, districts[0], path);
        string B(string path) => IdentityTestClients.Route(IdentityTestClients.TenantOne, districts[1], path);

        _clientBReadsGrantedNamespace = (
            await SendAsync(clientB, HttpMethod.Get, A($"identities/{SeededId}"))
        ).Status;
        _clientBReadsUngrantedNamespace = (
            await SendAsync(clientB, HttpMethod.Get, B($"identities/{SeededId}"))
        ).Status;

        (HttpStatusCode _, JsonNode? accepted, string? location) = await SendAsync(
            clientA,
            HttpMethod.Post,
            A("identities/find"),
            $"""["{SeededId}", "~fixture:async"]"""
        );
        accepted.Should().BeNull("an accepted async find has no body");

        _clientAPollsOwnJob = (await SendAsync(clientA, HttpMethod.Get, location!)).Status;
        (HttpStatusCode bStatus, JsonNode? bBody, string? _) = await SendAsync(
            clientB,
            HttpMethod.Get,
            location!
        );
        _clientBPollsClientAsJob = bStatus;
        _clientBPollProblemType = bBody?["type"]?.GetValue<string>();
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body, string? Location)> SendAsync(
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
        return (
            response.StatusCode,
            string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text),
            response.Headers.Location?.OriginalString
        );
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
    public void It_lets_a_client_with_no_grant_of_its_own_read_the_namespace()
    {
        _clientBReadsGrantedNamespace.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_does_not_extend_the_grant_to_another_namespace()
    {
        _clientBReadsUngrantedNamespace.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public void It_lets_the_issuing_client_poll_its_own_job()
    {
        _clientAPollsOwnJob.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_still_refuses_another_client_the_job_token_under_the_grant()
    {
        _clientBPollsClientAsJob.Should().Be(HttpStatusCode.NotFound);
        _clientBPollProblemType.Should().Be(IdentityFailureResponse.NotFoundType);
    }
}
