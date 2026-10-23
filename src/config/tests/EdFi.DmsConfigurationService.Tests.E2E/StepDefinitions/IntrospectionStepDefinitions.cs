// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

/// <summary>
/// Steps for the authenticated <c>/connect/introspect</c> endpoint (RFC 7662). Each request is
/// stored as the scenario's last response so the generic Then steps can assert on it.
/// </summary>
public partial class StepDefinitions
{
    private async Task PostIntrospection(Dictionary<string, string> form, string? authorization)
    {
        Dictionary<string, string> headers = new()
        {
            { "Content-Type", "application/x-www-form-urlencoded" },
        };
        if (authorization is not null)
        {
            headers["Authorization"] = authorization;
        }

        APIRequestContextOptions options = new()
        {
            Headers = headers,
            Data = await new FormUrlEncodedContent(form).ReadAsStringAsync(),
        };
        _apiResponse = await playwrightContext.ApiRequestContext!.PostAsync("/connect/introspect", options);
    }

    [When("an introspection of the current token is attempted with no client credentials")]
    public async Task WhenAnIntrospectionIsAttemptedWithNoClientCredentials() =>
        await PostIntrospection(new() { { "token", _token } }, authorization: null);

    [When("an introspection of the current token is attempted with a wrong Basic client secret")]
    public async Task WhenAnIntrospectionIsAttemptedWithAWrongBasicClientSecret() =>
        await PostIntrospection(
            new() { { "token", _token } },
            BasicAuthorization(_lastClientId, $"{_lastClientSecret}-wrong")
        );

    [When("an introspection of the current token is attempted with only a Bearer authorization header")]
    public async Task WhenAnIntrospectionIsAttemptedWithOnlyABearerHeader() =>
        await PostIntrospection(new() { { "token", _token } }, $"Bearer {_token}");

    [When("an introspection of the current token is attempted with Basic and form client credentials")]
    public async Task WhenAnIntrospectionIsAttemptedWithMixedCredentials() =>
        await PostIntrospection(
            new() { { "token", _token }, { "client_id", _lastClientId } },
            BasicAuthorization(_lastClientId, _lastClientSecret)
        );

    [When("an introspection without a token is attempted with the current client's Basic credentials")]
    public async Task WhenAnIntrospectionWithoutATokenIsAttempted() =>
        await PostIntrospection([], BasicAuthorization(_lastClientId, _lastClientSecret));

    [When("the current token is introspected by its owner using Basic credentials")]
    public async Task WhenTheCurrentTokenIsIntrospectedUsingBasic() =>
        await PostIntrospection(
            new() { { "token", _token } },
            BasicAuthorization(_lastClientId, _lastClientSecret)
        );

    [When("the current token is introspected by its owner using form credentials")]
    public async Task WhenTheCurrentTokenIsIntrospectedUsingFormCredentials() =>
        await PostIntrospection(
            new()
            {
                { "token", _token },
                { "client_id", _lastClientId },
                { "client_secret", _lastClientSecret },
            },
            authorization: null
        );

    // Keycloak refuses a public client at introspection whatever secret it sends (403 "Client not
    // allowed."), which the Configuration Service answers as invalid_client.
    [When("the public client attempts to introspect its token with an arbitrary form client secret")]
    public async Task WhenThePublicClientAttemptsToIntrospectItsTokenWithAnArbitraryFormClientSecret() =>
        await PostIntrospection(
            new()
            {
                { "token", PublicClient.AccessToken },
                { "client_id", PublicClient.ClientId },
                { "client_secret", ArbitraryPublicClientSecret },
            },
            authorization: null
        );

    [When("the public client attempts to introspect its token with an arbitrary Basic client secret")]
    public async Task WhenThePublicClientAttemptsToIntrospectItsTokenWithAnArbitraryBasicClientSecret() =>
        await PostIntrospection(
            new() { { "token", PublicClient.AccessToken } },
            BasicAuthorization(PublicClient.ClientId, ArbitraryPublicClientSecret)
        );

    [When("the current token is introspected with the credentials captured as {string}")]
    public async Task WhenTheCurrentTokenIsIntrospectedWithTheCredentialsCapturedAs(string slot)
    {
        _credentialSlots.Should().ContainKey(slot, $"credentials should have been captured as '{slot}'");
        (string key, string secret) = _credentialSlots[slot];
        await PostIntrospection(new() { { "token", _token } }, BasicAuthorization(key, secret));
    }

    [Then("the introspection response reports the current client as active with numeric exp and iat")]
    public async Task ThenTheIntrospectionResponseReportsTheCurrentClientAsActive()
    {
        string content = await _apiResponse.TextAsync();
        JsonObject body = JsonNode.Parse(content)!.AsObject();

        body["active"]!.GetValue<bool>().Should().BeTrue(content);
        body["client_id"]!.GetValue<string>().Should().Be(_lastClientId, content);
        body["exp"]!.GetValueKind().Should().Be(JsonValueKind.Number, content);
        body["iat"]!.GetValueKind().Should().Be(JsonValueKind.Number, content);
    }
}
