// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.E2E.Keycloak;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

/// <summary>
/// Steps for <c>/connect/revoke</c> (DMS-1327). Every revocation request targets the current token,
/// so the scenario's other state is untouched, and token state is read through the provider's
/// validation path rather than inferred from the revocation response (D-16).
/// </summary>
public partial class StepDefinitions
{
    /// <summary>
    /// The scenario's Keycloak public client (DMS-1327 D-11), deleted with its user after the
    /// scenario. Its credentials are its client id alone; it never holds a secret.
    /// </summary>
    private KeycloakPublicClient? _publicClient;

    /// <summary>
    /// RFC 6749 §2.3.1: each credential is form-urlencoded before the two are joined and base64
    /// encoded. Percent-encoding is a valid form encoding and decodes identically on both CMS token
    /// endpoints.
    /// </summary>
    private static string BasicAuthorization(string clientId, string clientSecret) =>
        "Basic "
        + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}"
            )
        );

    private async Task PostRevocation(Dictionary<string, string> form, string? authorization)
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
        _apiResponse = await playwrightContext.ApiRequestContext!.PostAsync("/connect/revoke", options);
    }

    [When("the current token is revoked using form credentials")]
    public async Task WhenTheCurrentTokenIsRevokedUsingFormCredentials() =>
        await PostRevocation(
            new()
            {
                { "token", _token },
                { "client_id", _lastClientId },
                { "client_secret", _lastClientSecret },
            },
            authorization: null
        );

    [When("a revocation of the current token is attempted with no client credentials")]
    public async Task WhenARevocationOfTheCurrentTokenIsAttemptedWithNoClientCredentials() =>
        await PostRevocation(new() { { "token", _token } }, authorization: null);

    [When("a revocation without a token is attempted with the current client's Basic credentials")]
    public async Task WhenARevocationWithoutATokenIsAttempted() =>
        await PostRevocation([], BasicAuthorization(_lastClientId, _lastClientSecret));

    [When("a revocation of the current token is attempted with Basic and form client credentials")]
    public async Task WhenARevocationIsAttemptedWithMixedCredentials() =>
        await PostRevocation(
            new() { { "token", _token }, { "client_id", _lastClientId } },
            BasicAuthorization(_lastClientId, _lastClientSecret)
        );

    [When("a revocation of the current token is attempted with a wrong Basic client secret")]
    public async Task WhenARevocationIsAttemptedWithAWrongBasicClientSecret() =>
        await PostRevocation(
            new() { { "token", _token } },
            BasicAuthorization(_lastClientId, $"{_lastClientSecret}-wrong")
        );

    [When("a revocation of the current token is attempted with the credentials captured as {string}")]
    public async Task WhenARevocationIsAttemptedWithTheCredentialsCapturedAs(string slot)
    {
        _credentialSlots.Should().ContainKey(slot, $"credentials should have been captured as '{slot}'");
        (string key, string secret) = _credentialSlots[slot];
        await PostRevocation(
            new()
            {
                { "token", _token },
                { "client_id", key },
                { "client_secret", secret },
            },
            authorization: null
        );
    }

    [Given("a Keycloak public client holds a user-flow access token")]
    public async Task GivenAKeycloakPublicClientHoldsAUserFlowAccessToken() =>
        _publicClient = await RequireKeycloakObserver().CreatePublicClientAsync();

    [When("the public client attempts to revoke its token with no client secret")]
    public async Task WhenThePublicClientAttemptsToRevokeItsTokenWithNoClientSecret() =>
        await PostRevocation(
            new() { { "token", PublicClient.AccessToken }, { "client_id", PublicClient.ClientId } },
            authorization: null
        );

    // Keycloak ignores any secret a public client sends and would revoke the token (§9.1, K-19), so
    // only the Configuration Service's client-type gate can turn these two requests away.
    [When("the public client attempts to revoke its token with an arbitrary form client secret")]
    public async Task WhenThePublicClientAttemptsToRevokeItsTokenWithAnArbitraryFormClientSecret() =>
        await PostRevocation(
            new()
            {
                { "token", PublicClient.AccessToken },
                { "client_id", PublicClient.ClientId },
                { "client_secret", ArbitraryPublicClientSecret },
            },
            authorization: null
        );

    [When("the public client attempts to revoke its token with an arbitrary Basic client secret")]
    public async Task WhenThePublicClientAttemptsToRevokeItsTokenWithAnArbitraryBasicClientSecret() =>
        await PostRevocation(
            new() { { "token", PublicClient.AccessToken } },
            BasicAuthorization(PublicClient.ClientId, ArbitraryPublicClientSecret)
        );

    [Then("the public client's token is active at the identity provider")]
    public async Task ThenThePublicClientsTokenIsActive() =>
        (await IsTokenActive(PublicClient.AccessToken))
            .Should()
            .BeTrue("the public client's token should still be usable");

    [AfterScenario]
    public async Task DeleteThePublicClient()
    {
        if (_publicClient is not null)
        {
            await RequireKeycloakObserver().DeletePublicClientAsync(_publicClient);
            _publicClient = null;
        }
    }

    private const string ArbitraryPublicClientSecret = "arbitrary-public-client-secret";

    private KeycloakPublicClient PublicClient =>
        _publicClient
        ?? throw new InvalidOperationException("The scenario has not created a Keycloak public client.");

    [Then("the response is the OAuth error {string} with description {string}")]
    public async Task ThenTheResponseIsTheOAuthError(string error, string description)
    {
        string content = await _apiResponse.TextAsync();
        _apiResponse.Headers["content-type"].Should().StartWith("application/json", content);

        JsonObject body = JsonNode.Parse(content)!.AsObject();
        body.Select(member => member.Key).Should().BeEquivalentTo(["error", "error_description"], content);
        body["error"]!.GetValue<string>().Should().Be(error);
        body["error_description"]!.GetValue<string>().Should().Be(description);
    }

    [Then("the current token is active at the identity provider")]
    public async Task ThenTheCurrentTokenIsActive() =>
        (await IsTokenActive(_token)).Should().BeTrue("the current token should still be usable");

    [Then("the current token is inactive at the identity provider")]
    public async Task ThenTheCurrentTokenIsInactive() =>
        (await IsTokenActive(_token)).Should().BeFalse("the current token should have been revoked");

    /// <summary>
    /// The self-contained provider is observed through CMS <c>/connect/introspect</c>, which checks
    /// the token's stored status. Keycloak access tokens are observed through Keycloak's own
    /// introspection endpoint by the feature's dedicated observer client, whose credentials are
    /// never sent to revocation (D-16). Does not replace the scenario's last response.
    /// </summary>
    private async Task<bool> IsTokenActive(string token)
    {
        if (KeycloakCharacterizationEnvironment.IsKeycloakProvider)
        {
            return await RequireKeycloakObserver().IsActiveAsync(token);
        }

        APIRequestContextOptions options = new()
        {
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/x-www-form-urlencoded" },
            },
            Data = await new FormUrlEncodedContent(
                new Dictionary<string, string> { { "token", token } }
            ).ReadAsStringAsync(),
        };
        IAPIResponse introspection = await playwrightContext.ApiRequestContext!.PostAsync(
            "/connect/introspect",
            options
        );
        string content = await introspection.TextAsync();
        introspection.Status.Should().Be(200, content);
        return JsonNode.Parse(content)!["active"]!.GetValue<bool>();
    }

    private KeycloakRevocationObserver RequireKeycloakObserver()
    {
        if (!featureContext.TryGetValue(out KeycloakRevocationObserver observer))
        {
            Assert.Fail(
                "Keycloak token state is observed by the revocation observer client, which a feature tagged @KeycloakRevocationObserver provisions under the keycloak provider."
            );
        }

        return observer;
    }
}
