// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

/// <summary>
/// Bindings for token_info requests made to the URLs advertised by routed Discovery documents.
/// Tokens, fixture keys and raw payloads are never written to output or failure messages.
/// </summary>
[Binding]
public class TokenIntrospectionStepDefinitions(InstanceManagementContext context)
{
    private const string FixtureClaimSetName = "E2E-NoFurtherAuthRequiredClaimSet";
    private const string FixtureNamespacePrefix = "uri://ed-fi.org";
    private const string MismatchMessage =
        "The Authorization header token does not match the token in the request body.";

    private readonly List<HttpResponseMessage> _ownedResponses = [];
    private string _owningTenant = string.Empty;
    private string _owningToken = string.Empty;
    private string _owningTokenInfoUrl = string.Empty;

    [Given("tenant {string} has a fresh token from discovery route {string}")]
    public async Task GivenTenantHasAFreshTokenFromDiscoveryRouteAsync(string tenantName, string route)
    {
        context
            .CredentialsByTenant.ContainsKey(tenantName)
            .Should()
            .BeTrue("the fixture must provide credentials for the requested tenant");
        route
            .Split('/', StringSplitOptions.RemoveEmptyEntries)[0]
            .Should()
            .Be(tenantName, "the owning route must begin with the owning tenant");

        var (key, secret) = context.CredentialsByTenant[tenantName];
        var (oauthUrl, tokenInfoUrl) = await DiscoverOauthAndTokenInfoUrlsAsync(route);

        // Fresh acquisition from the advertised endpoint: no cache and no identity-provider fallback.
        var token = await TokenHelper.GetDmsTokenAsync(oauthUrl, key, secret);

        _owningTenant = tenantName;
        _owningToken = token;
        _owningTokenInfoUrl = tokenInfoUrl;
    }

    [When("the advertised token info endpoint is called with matching bearer and body tokens")]
    public async Task WhenAdvertisedTokenInfoIsCalledWithMatchingTokensAsync() =>
        await PostTokenInfoAsync(_owningTokenInfoUrl, _owningToken, _owningToken);

    [When("the advertised token info endpoint is called without bearer authentication")]
    public async Task WhenAdvertisedTokenInfoIsCalledWithoutBearerAsync() =>
        await PostTokenInfoAsync(_owningTokenInfoUrl, string.Empty, _owningToken);

    [When("the advertised token info endpoint is called with a nonmatching body token")]
    public async Task WhenAdvertisedTokenInfoIsCalledWithMismatchedBodyAsync() =>
        await PostTokenInfoAsync(_owningTokenInfoUrl, _owningToken, "non-matching token");

    [When("the token info endpoint advertised for route {string} is called with the original tenant token")]
    public async Task WhenOtherRouteTokenInfoIsCalledAsync(string route)
    {
        route
            .Split('/', StringSplitOptions.RemoveEmptyEntries)[0]
            .Should()
            .NotBe(
                _owningTenant,
                "the target route must belong to a different tenant than the credential owner"
            );

        // Independently discover the target route; the credential owner is not changed.
        var (_, targetTokenInfoUrl) = await DiscoverOauthAndTokenInfoUrlsAsync(route);
        await PostTokenInfoAsync(targetTokenInfoUrl, _owningToken, _owningToken);
    }

    [Then("token info identifies fixture tenant {string} without caching")]
    public async Task ThenTokenInfoIdentifiesFixtureTenantAsync(string tenantName)
    {
        context.LastResponse.Should().NotBeNull();
        var response = context.LastResponse!;
        response.StatusCode.Should().Be(HttpStatusCode.OK, "token info should succeed for the owning tenant");

        var expectedKey = context.CredentialsByTenant[tenantName].Key;

        using var document = await ParseBodyAsync(response);
        var root = document.RootElement;

        root.GetProperty("active").GetBoolean().Should().BeTrue();

        // Compare as a boolean so a failure never renders the key values.
        string.Equals(root.GetProperty("client_id").GetString(), expectedKey, StringComparison.Ordinal)
            .Should()
            .BeTrue("token info must identify the owning fixture application");

        root.GetProperty("claim_set").GetProperty("name").GetString().Should().Be(FixtureClaimSetName);

        root.GetProperty("namespace_prefixes")
            .EnumerateArray()
            .Select(prefix => prefix.GetString())
            .Should()
            .Equal(FixtureNamespacePrefix);

        response.Headers.CacheControl.Should().NotBeNull("token info must send Cache-Control");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("token info must not be cached");
    }

    [Then("the response requires bearer authentication")]
    public void ThenBearerAuthenticationIsRequired()
    {
        context.LastResponse.Should().NotBeNull();
        context
            .LastResponse!.Headers.WwwAuthenticate.Any(challenge =>
                string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            )
            .Should()
            .BeTrue("the 401 must carry a Bearer authentication challenge");
    }

    [Then("the response reports a token identity mismatch")]
    public async Task ThenTokenMismatchIsReportedAsync()
    {
        context.LastResponse.Should().NotBeNull();
        using var document = await ParseBodyAsync(context.LastResponse!);
        var root = document.RootElement;

        root.TryGetProperty("errors", out var errors).Should().BeTrue("the 400 must carry errors");
        errors
            .EnumerateArray()
            .Select(error => error.GetString())
            .Should()
            .Equal([MismatchMessage], "the 400 must report exactly the token mismatch");
    }

    [Then("the response contains no active token info")]
    public async Task ThenNoActiveTokenInfoIsReturnedAsync()
    {
        context.LastResponse.Should().NotBeNull();
        using var document = await ParseBodyAsync(context.LastResponse!);
        var root = document.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Object, "the 404 must be an error object");
        root.TryGetProperty("active", out var active)
            .Should()
            .BeFalse("a rejected request must not return introspection data");
        active.ValueKind.Should().Be(JsonValueKind.Undefined);
        root.TryGetProperty("client_id", out _).Should().BeFalse();
        root.TryGetProperty("status", out var status).Should().BeTrue("the 404 body must be an error");
        status.GetInt32().Should().Be(404);
    }

    [AfterScenario(Order = 900)]
    public void DisposeOwnedResponses()
    {
        foreach (var response in _ownedResponses)
        {
            response.Dispose();
        }
        _ownedResponses.Clear();
    }

    private async Task PostTokenInfoAsync(string tokenInfoUrl, string bearerToken, string bodyToken)
    {
        // Each POST uses its own short-lived client so no shared header is mutated.
        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, bearerToken);
        var response = await client.PostByLocationAsync(tokenInfoUrl, new { Token = bodyToken });
        _ownedResponses.Add(response);
        context.LastResponse = response;
    }

    private static async Task<JsonDocument> ParseBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private static async Task<(string Oauth, string TokenInfo)> DiscoverOauthAndTokenInfoUrlsAsync(
        string route
    )
    {
        using var discoveryClient = new DmsApiClient(TestConfiguration.DmsApiUrl, "");
        using var response = await discoveryClient.GetDiscoveryWithRouteAsync(route);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "Discovery should resolve for the fixture route");

        var discoveryUri = response.RequestMessage?.RequestUri;
        discoveryUri.Should().NotBeNull();

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var urls = document.RootElement.GetProperty("urls");

        var oauth = ReadConcreteUrl(urls, "oauth", "/oauth/token", discoveryUri!);
        var tokenInfo = ReadConcreteUrl(urls, "tokenInfo", "/oauth/token_info", discoveryUri!);
        return (oauth, tokenInfo);
    }

    private static string ReadConcreteUrl(
        JsonElement urls,
        string key,
        string expectedPathSuffix,
        Uri discoveryUri
    )
    {
        urls.TryGetProperty(key, out var property).Should().BeTrue($"Discovery should advertise urls.{key}");
        property.ValueKind.Should().Be(JsonValueKind.String);
        var url = property.GetString();
        url.Should().NotBeNullOrWhiteSpace();

        var uri = DiscoveryUrlAssertions.AssertConcreteContextUrl(url!, discoveryUri);
        uri.AbsolutePath.TrimEnd('/')
            .Should()
            .Be(
                $"{discoveryUri.AbsolutePath.TrimEnd('/')}{expectedPathSuffix}",
                $"urls.{key} must be the route-qualified {expectedPathSuffix} endpoint"
            );
        return url!;
    }
}
