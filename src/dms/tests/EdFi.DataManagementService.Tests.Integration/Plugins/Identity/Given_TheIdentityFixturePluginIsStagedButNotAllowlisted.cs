// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// Negative control for <see cref="Given_TheIdentityFixturePluginIsAllowlisted"/>: the same fixture
/// is staged in the plugin root and the same request is sent, but nothing is allowlisted.
/// </summary>
/// <remarks>
/// Without this, the not-found answer in the allowlisted case could come from something other than
/// the fixture. Here the host default answers, so the type differs.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheIdentityFixturePluginIsStagedButNotAllowlisted
{
    private IdentityPluginHost? _host;
    private HttpResponseMessage? _getByIdResponse;
    private JsonNode? _getByIdBody;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _host = IdentityPluginHost.Create(["Acme.IdentityFixture"], allowed: string.Empty);

        using HttpClient client = _host.CreateClient(IdentityTestClients.ClientA.Token);
        _getByIdResponse = await client.GetAsync(
            IdentityTestClients.RouteFor(IdentityTestClients.ClientA, "identities/605943412")
        );
        _getByIdBody = JsonNode.Parse(await _getByIdResponse.Content.ReadAsStringAsync());
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _getByIdResponse?.Dispose();

        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    [Test]
    public void It_loaded_no_plugin()
    {
        _host!.Capture.InventoryEvents.Should().BeEmpty();
    }

    [Test]
    public void It_answers_with_the_host_default_operation_not_supported_type()
    {
        _getByIdBody!["type"]!
            .GetValue<string>()
            .Should()
            .Be(IdentityFailureResponse.OperationNotSupportedType);
    }

    [Test]
    public void It_answers_not_found_at_the_http_layer()
    {
        _getByIdResponse!.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
