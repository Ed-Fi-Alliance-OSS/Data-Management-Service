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
/// F6 over HTTP: requests whose tenant and route words differ from the issuing request only by case
/// reach the same namespace and the same job, while a different qualifier value, a different client and
/// the client whose id differs only by case are denied.
/// </summary>
/// <remarks>
/// District ids and school years are numeric, so a qualifier value cannot differ by case over HTTP;
/// value case, missing and extra qualifiers, dictionary order and case-distinct client ids are covered at
/// the provider boundary by <c>Given_CraftedRequestContextsReachTheIdentityProvider</c>.
/// Every denied caller is granted the namespace, so the denial is job ownership. The case-variant
/// client has its own successful job as the control that its token and grant work.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_EquivalentAndDifferentHttpRequestsPollOneJob
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string SeededId = "0123456789abcdef0123456789abcdef";

    private IdentityPluginHost? _host;
    private string _district = string.Empty;
    private string _token = string.Empty;
    private readonly Dictionary<string, (HttpStatusCode Status, JsonNode? Body)> _polls = [];
    private HttpStatusCode _caseVariantClientOwnJob;
    private HttpStatusCode _tenantCaseGetById;

    [OneTimeSetUp]
    public async Task Setup()
    {
        string[] districts = [.. IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)];
        _district = districts[0];

        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .Namespace("ns-a", (IdentityTestClients.TenantOne, _district))
            .Seed(SeededId, "Rivera", "Ana")
            .Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-a")
            .Grant(IdentityTestClients.ClientB.ClientId, IdentityTestClients.TenantOne, "ns-a")
            .Grant(IdentityTestClients.CaseVariantOfA.ClientId, IdentityTestClients.TenantOne, "ns-a")
            .With("IdentityFixture:PollsUntilComplete", "0");
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings
        );

        using HttpClient clientA = _host.CreateClient(IdentityTestClients.ClientA.Token);
        using HttpClient clientB = _host.CreateClient(IdentityTestClients.ClientB.Token);
        using HttpClient caseVariant = _host.CreateClient(IdentityTestClients.CaseVariantOfA.Token);

        using HttpRequestMessage submit = new(
            HttpMethod.Post,
            IdentityTestClients.Route(IdentityTestClients.TenantOne, _district, "identities/find")
        )
        {
            Content = new StringContent(
                $"""["{SeededId}", "~fixture:async"]""",
                Encoding.UTF8,
                "application/json"
            ),
        };
        using HttpResponseMessage accepted = await clientA.SendAsync(submit);
        _token = accepted.Headers.Location!.OriginalString.Split('/')[^1];

        string Results(string tenant, string district, string year = IdentityTestClients.SchoolYear) =>
            IdentityTestClients.Route(tenant, district, $"identities/results/{_token}", year);

        _polls["same"] = await GetAsync(clientA, Results("tenant-one", _district));
        _polls["tenantUpper"] = await GetAsync(clientA, Results("TENANT-ONE", _district));
        _polls["tenantMixed"] = await GetAsync(clientA, Results("Tenant-One", _district));
        _polls["routeWordsUpper"] = await GetAsync(
            clientA,
            $"/tenant-one/{_district}/{IdentityTestClients.SchoolYear}/IDENTITY/V2/IDENTITIES/RESULTS/{_token}"
        );
        _polls["otherDistrict"] = await GetAsync(clientA, Results("tenant-one", districts[1]));
        _polls["otherYear"] = await GetAsync(clientA, Results("tenant-one", _district, "2027"));
        _polls["otherClient"] = await GetAsync(clientB, Results("tenant-one", _district));
        _polls["caseVariantClient"] = await GetAsync(caseVariant, Results("tenant-one", _district));

        using HttpRequestMessage ownSubmit = new(
            HttpMethod.Post,
            IdentityTestClients.Route(IdentityTestClients.TenantOne, _district, "identities/find")
        )
        {
            Content = new StringContent("""["~fixture:async"]""", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage ownAccepted = await caseVariant.SendAsync(ownSubmit);
        string ownToken = ownAccepted.Headers.Location!.OriginalString.Split('/')[^1];
        _caseVariantClientOwnJob = (
            await GetAsync(
                caseVariant,
                IdentityTestClients.Route(
                    IdentityTestClients.TenantOne,
                    _district,
                    $"identities/results/{ownToken}"
                )
            )
        ).Status;

        _tenantCaseGetById = (
            await GetAsync(
                clientA,
                IdentityTestClients.Route("TENANT-ONE", _district, $"identities/{SeededId}")
            )
        ).Status;
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(
        HttpClient client,
        string route
    )
    {
        using HttpResponseMessage response = await client.GetAsync(route);
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
    public void It_polls_the_job_under_the_issuing_spelling()
    {
        _polls["same"].Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_polls_the_same_job_when_the_tenant_differs_only_by_case()
    {
        _polls["tenantUpper"].Status.Should().Be(HttpStatusCode.OK);
        _polls["tenantMixed"].Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_polls_the_same_job_when_the_route_words_differ_only_by_case()
    {
        _polls["routeWordsUpper"].Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_answers_the_same_namespace_person_when_the_tenant_differs_only_by_case()
    {
        _tenantCaseGetById.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_denies_a_different_qualifier_value()
    {
        _polls["otherDistrict"].Status.Should().Be(HttpStatusCode.NotFound);
        _polls["otherYear"].Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public void It_denies_a_different_client()
    {
        _polls["otherClient"].Status.Should().Be(HttpStatusCode.NotFound);
        _polls["otherClient"].Body!["type"]!
            .GetValue<string>()
            .Should()
            .Be(IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_denies_the_client_whose_id_differs_only_by_case()
    {
        _polls["caseVariantClient"].Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public void It_answers_the_case_variant_client_for_a_job_it_was_issued()
    {
        _caseVariantClientOwnJob.Should().Be(HttpStatusCode.OK);
    }
}
