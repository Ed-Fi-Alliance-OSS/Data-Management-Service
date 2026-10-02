// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F5: when the policy source revokes a grant after a client was issued a job, the client's next poll
/// of that job answers identity-not-found <c>404</c>, and restoring the grant answers the same job
/// again, so the denial came from the grant and not from the job having gone.
/// </summary>
/// <remarks>
/// The fixture caches no control-channel answer and asks the stub on every grant check, so the
/// revocation is observed on the next poll.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_AGrantIsRevokedAfterAJobWasIssued
{
    private const string FixturePlugin = "Acme.IdentityFixture";

    private IdentityFixtureControlStub? _stub;
    private IdentityPluginHost? _host;
    private HttpStatusCode _beforeRevoke;
    private HttpStatusCode _afterRevoke;
    private HttpStatusCode _afterRegrant;

    [OneTimeSetUp]
    public async Task Setup()
    {
        string district = IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)[0];

        _stub = await IdentityFixtureControlStub.StartAsync();
        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .WithControlStub(_stub)
            .Namespace("district-a", (IdentityTestClients.TenantOne, district))
            .With("IdentityFixture:PollsUntilComplete", "0");
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings
        );

        _stub.Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "district-a");

        using HttpClient client = _host.CreateClient(IdentityTestClients.ClientA.Token);
        string route = IdentityTestClients.Route(IdentityTestClients.TenantOne, district, "identities/find");

        using HttpRequestMessage submit = new(HttpMethod.Post, route)
        {
            Content = new StringContent("""["~fixture:async"]""", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage accepted = await client.SendAsync(submit);
        string location = accepted.Headers.Location!.OriginalString;

        _beforeRevoke = await PollAsync(client, location);

        _stub.Revoke(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "district-a");
        _afterRevoke = await PollAsync(client, location);

        _stub.Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "district-a");
        _afterRegrant = await PollAsync(client, location);
    }

    private static async Task<HttpStatusCode> PollAsync(HttpClient client, string location)
    {
        using HttpResponseMessage response = await client.GetAsync(location);
        return response.StatusCode;
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }

        if (_stub is not null)
        {
            await _stub.DisposeAsync();
        }
    }

    [Test]
    public void It_answers_the_owners_poll_while_the_grant_stands()
    {
        _beforeRevoke.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_answers_identity_not_found_to_the_owners_poll_once_the_grant_is_revoked()
    {
        _afterRevoke.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public void It_answers_the_same_job_again_when_the_grant_is_restored()
    {
        _afterRegrant.Should().Be(HttpStatusCode.OK);
    }
}
