// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
        (await IsCurrentTokenActive()).Should().BeTrue("the current token should still be usable");

    [Then("the current token is inactive at the identity provider")]
    public async Task ThenTheCurrentTokenIsInactive() =>
        (await IsCurrentTokenActive()).Should().BeFalse("the current token should have been revoked");

    /// <summary>
    /// The self-contained provider is observed through CMS <c>/connect/introspect</c>, which checks
    /// the token's stored status. Keycloak tokens are observed through Keycloak's own introspection
    /// endpoint by a dedicated observer client whose credentials are never sent to revocation
    /// (D-16); DMS-1327 P3.2 provisions that client, so the Keycloak path fails until then rather
    /// than passing on an observation it cannot make. Does not replace the scenario's last response.
    /// </summary>
    private async Task<bool> IsCurrentTokenActive()
    {
        string? identityProvider = Environment.GetEnvironmentVariable("DMS_CONFIG_IDENTITY_PROVIDER");
        if (string.Equals(identityProvider, "keycloak", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail(
                "Keycloak token state is observed by the Keycloak introspection observer client, which DMS-1327 P3.2 provisions."
            );
        }

        APIRequestContextOptions options = new()
        {
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/x-www-form-urlencoded" },
            },
            Data = await new FormUrlEncodedContent(
                new Dictionary<string, string> { { "token", _token } }
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
}
