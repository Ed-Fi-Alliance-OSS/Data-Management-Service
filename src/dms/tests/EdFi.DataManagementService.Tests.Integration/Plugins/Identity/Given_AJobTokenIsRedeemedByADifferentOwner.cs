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
/// A job token redeemed under a different client, qualifier set or tenant answers
/// identity-not-found <c>404</c>, although the host authorized the caller and the provider granted it
/// the very namespace the job lives in, so ownership is the only thing left to deny.
/// </summary>
/// <remarks>
/// One namespace is mapped from three contexts (two districts in tenant one and one district in tenant
/// two) and granted to client A and client B in tenant one and client C in tenant two. Each redeemer
/// also runs its own job successfully, which is the negative control: it is the same caller, route and
/// grant, and only the token's owner differs.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_AJobTokenIsRedeemedByADifferentOwner
{
    private const string FixturePlugin = "Acme.IdentityFixture";

    private IdentityPluginHost? _host;
    private string _tenantOneFirstDistrict = string.Empty;
    private string _tenantOneSecondDistrict = string.Empty;
    private string _tenantTwoDistrict = string.Empty;
    private string _token = string.Empty;

    private (HttpStatusCode Status, string? ProblemType) _owner;
    private (HttpStatusCode Status, string? ProblemType) _differentClient;
    private (HttpStatusCode Status, string? ProblemType) _differentQualifiers;
    private (HttpStatusCode Status, string? ProblemType) _differentTenant;
    private HttpStatusCode _differentClientOwnJob;
    private HttpStatusCode _differentQualifiersOwnJob;
    private HttpStatusCode _differentTenantOwnJob;

    [OneTimeSetUp]
    public async Task Setup()
    {
        string[] tenantOne = [.. IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)];
        _tenantOneFirstDistrict = tenantOne[0];
        _tenantOneSecondDistrict = tenantOne[1];
        _tenantTwoDistrict = IdentityTestClients.DistrictsOf(IdentityTestClients.TenantTwo)[0];

        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .Namespace(
                "ns-a",
                (IdentityTestClients.TenantOne, _tenantOneFirstDistrict),
                (IdentityTestClients.TenantOne, _tenantOneSecondDistrict),
                (IdentityTestClients.TenantTwo, _tenantTwoDistrict)
            )
            .Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-a")
            .Grant(IdentityTestClients.ClientB.ClientId, IdentityTestClients.TenantOne, "ns-a")
            .Grant(IdentityTestClients.ClientC.ClientId, IdentityTestClients.TenantTwo, "ns-a")
            .With("IdentityFixture:PollsUntilComplete", "0");
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings
        );

        using HttpClient clientA = _host.CreateClient(IdentityTestClients.ClientA.Token);
        using HttpClient clientB = _host.CreateClient(IdentityTestClients.ClientB.Token);
        using HttpClient clientC = _host.CreateClient(IdentityTestClients.ClientC.Token);

        _token = await StartJobAsync(clientA, IdentityTestClients.TenantOne, _tenantOneFirstDistrict);

        _owner = await PollAsync(clientA, IdentityTestClients.TenantOne, _tenantOneFirstDistrict, _token);
        _differentClient = await PollAsync(
            clientB,
            IdentityTestClients.TenantOne,
            _tenantOneFirstDistrict,
            _token
        );
        _differentQualifiers = await PollAsync(
            clientA,
            IdentityTestClients.TenantOne,
            _tenantOneSecondDistrict,
            _token
        );
        _differentTenant = await PollAsync(
            clientC,
            IdentityTestClients.TenantTwo,
            _tenantTwoDistrict,
            _token
        );

        _differentClientOwnJob = await OwnJobStatusAsync(
            clientB,
            IdentityTestClients.TenantOne,
            _tenantOneFirstDistrict
        );
        _differentQualifiersOwnJob = await OwnJobStatusAsync(
            clientA,
            IdentityTestClients.TenantOne,
            _tenantOneSecondDistrict
        );
        _differentTenantOwnJob = await OwnJobStatusAsync(
            clientC,
            IdentityTestClients.TenantTwo,
            _tenantTwoDistrict
        );
    }

    private static async Task<string> StartJobAsync(HttpClient client, string tenant, string district)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            IdentityTestClients.Route(tenant, district, "identities/find")
        )
        {
            Content = new StringContent("""["~fixture:async"]""", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage accepted = await client.SendAsync(request);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return accepted.Headers.Location!.OriginalString.Split('/')[^1];
    }

    private static async Task<(HttpStatusCode, string?)> PollAsync(
        HttpClient client,
        string tenant,
        string district,
        string token
    )
    {
        using HttpResponseMessage response = await client.GetAsync(
            IdentityTestClients.Route(tenant, district, $"identities/results/{token}")
        );
        string text = await response.Content.ReadAsStringAsync();
        return (
            response.StatusCode,
            string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)?["type"]?.GetValue<string>()
        );
    }

    private static async Task<HttpStatusCode> OwnJobStatusAsync(
        HttpClient client,
        string tenant,
        string district
    )
    {
        string token = await StartJobAsync(client, tenant, district);
        return (await PollAsync(client, tenant, district, token)).Item1;
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
    public void It_answers_the_client_that_was_issued_the_token()
    {
        _owner.Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_answers_identity_not_found_to_a_different_client_in_the_same_tenant_and_qualifiers()
    {
        _differentClient.Status.Should().Be(HttpStatusCode.NotFound);
        _differentClient.ProblemType.Should().Be(IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_answers_identity_not_found_to_the_same_client_under_a_different_qualifier_set()
    {
        _differentQualifiers.Status.Should().Be(HttpStatusCode.NotFound);
        _differentQualifiers.ProblemType.Should().Be(IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_answers_identity_not_found_to_a_client_authorized_in_a_different_tenant()
    {
        _differentTenant.Status.Should().Be(HttpStatusCode.NotFound);
        _differentTenant.ProblemType.Should().Be(IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_answers_each_of_those_callers_for_a_job_it_was_issued_itself()
    {
        _differentClientOwnJob.Should().Be(HttpStatusCode.OK);
        _differentQualifiersOwnJob.Should().Be(HttpStatusCode.OK);
        _differentTenantOwnJob.Should().Be(HttpStatusCode.OK);
    }
}
