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
/// The fixture's <c>ThrowAt</c> variants <c>Factory</c> and <c>Constructor</c> permit only the startup
/// probe's activation: the host still reaches <c>Ready</c>, nothing person-shaped reaches the startup
/// logs, and the first request-time activation fails as a provider-configuration problem.
/// </summary>
[Category("PluginIntegration")]
[TestFixture("Factory")]
[TestFixture("Constructor")]
public sealed class Given_TheIdentityFixtureActivationFailsAfterTheStartupProbe(string throwAt)
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string Sentinel = "SENTINEL-Jane-Doe-1999-01-01";

    private IdentityPluginHost? _host;
    private Exception? _bootFailure;
    private IReadOnlyList<string> _startupLeaks = [];
    private HttpStatusCode _requestStatus;
    private JsonNode? _requestBody;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: new Dictionary<string, string>
            {
                ["IdentityFixture:ThrowAt"] = throwAt,
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

        _bootFailure = _host.TryBoot();
        _startupLeaks = IdentityLogAssertions.EventsCarrying(_host.Capture.Events, Sentinel);

        if (_bootFailure is not null)
        {
            return;
        }

        using HttpClient client = _host.CreateClient(IdentityTestClients.ClientA.Token);
        using HttpResponseMessage response = await client.PostAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, "identities"),
            new StringContent("""{ "LastSurname": "Rivera" }""", Encoding.UTF8, "application/json")
        );
        _requestStatus = response.StatusCode;
        _requestBody = JsonNode.Parse(await response.Content.ReadAsStringAsync());
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
    public void It_boots_without_a_failure()
    {
        _bootFailure.Should().BeNull();
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
    public void It_logged_no_person_text_during_startup()
    {
        _startupLeaks.Should().BeEmpty();
    }

    [Test]
    public void It_fails_the_first_request_time_activation_as_a_provider_configuration_problem()
    {
        _requestStatus.Should().Be(HttpStatusCode.InternalServerError);
        _requestBody!["type"]!
            .GetValue<string>()
            .Should()
            .Be(IdentityFailureResponse.ProviderConfigurationType);
    }
}
