// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    /// <summary>
    /// Steps that send their own requests to <c>/oauth/token</c>, so a scenario can choose where the
    /// client credentials travel: a Basic header, the request body, both, or neither.
    /// </summary>
    public partial class StepDefinitions
    {
        private const string TokenEndpoint = "oauth/token";

        private const string StoredTokenResponsesContextKey = "storedTokenResponses";

        [When("a token request is made with content type {string} and body")]
        public async Task WhenATokenRequestIsMadeWithContentTypeAndBody(string contentType, string body)
        {
            await SendTokenRequest(contentType, body, authorization: null);
        }

        [When("a token request is made with a Basic Authorization header, content type {string} and body")]
        public async Task WhenATokenRequestIsMadeWithABasicAuthorizationHeader(
            string contentType,
            string body
        )
        {
            ClientCredentials credentials = AuthorizationDataProvider.CurrentClientCredentials;
            await SendTokenRequest(
                contentType,
                body,
                BasicAuthorization(credentials.key, credentials.secret)
            );
        }

        [When("a token request is made with Authorization header {string}, content type {string} and body")]
        public async Task WhenATokenRequestIsMadeWithAuthorizationHeader(
            string authorization,
            string contentType,
            string body
        )
        {
            await SendTokenRequest(contentType, body, authorization);
        }

        [When(
            "a token request is made with a Basic Authorization header carrying a wrong secret, content type {string} and body"
        )]
        public async Task WhenATokenRequestIsMadeWithAWrongBasicSecret(string contentType, string body)
        {
            ClientCredentials credentials = AuthorizationDataProvider.CurrentClientCredentials;
            await SendTokenRequest(contentType, body, BasicAuthorization(credentials.key, "wrong-secret"));
        }

        [Then("the token response carries an access token")]
        public async Task ThenTheTokenResponseCarriesAnAccessToken()
        {
            (await AccessTokenFromResponse()).Should().NotBeNullOrWhiteSpace();
        }

        [When("the access token from the token response is used for authorization")]
        public async Task WhenTheAccessTokenFromTheTokenResponseIsUsedForAuthorization()
        {
            _scenarioContext["dmsToken"] = $"Bearer {await AccessTokenFromResponse()}";
        }

        [Then("the token response is stored as {string}")]
        public async Task ThenTheTokenResponseIsStoredAs(string name)
        {
            StoredTokenResponses()[name] = await _apiResponse.TextAsync();
        }

        [Then("the token response body matches the one stored as {string}, apart from the correlationId")]
        public async Task ThenTheTokenResponseBodyMatchesTheOneStoredAs(string name)
        {
            JsonNode expected = WithoutCorrelationId(StoredTokenResponses()[name]);
            JsonNode actual = WithoutCorrelationId(await _apiResponse.TextAsync());

            JsonNode
                .DeepEquals(expected, actual)
                .Should()
                .BeTrue($"Expected:\n{expected}\n\nActual:\n{actual}");
        }

        /// <summary>
        /// Posts <paramref name="body"/> to the token endpoint after replacing <c>{clientId}</c> and
        /// <c>{clientSecret}</c> with the provisioned client's credentials, encoded for the content
        /// type so that any character a generated key or secret contains survives the trip.
        /// </summary>
        private async Task SendTokenRequest(string contentType, string body, string? authorization)
        {
            ClientCredentials credentials = AuthorizationDataProvider.CurrentClientCredentials;
            Func<string, string> encode = contentType.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase
            )
                ? value => JsonEncodedText.Encode(value).ToString()
                : Uri.EscapeDataString;

            body = body.Replace("{clientId}", encode(credentials.key))
                .Replace("{clientSecret}", encode(credentials.secret));

            Dictionary<string, string> headers = new() { ["Content-Type"] = contentType };
            if (authorization is not null)
            {
                headers["Authorization"] = authorization;
            }

            SetCurrentApiResponse(
                await _playwrightContext.ApiRequestContext!.PostAsync(
                    TokenEndpoint,
                    new() { DataByte = System.Text.Encoding.UTF8.GetBytes(body), Headers = headers }
                )
            );
        }

        private static string BasicAuthorization(string clientId, string clientSecret) =>
            $"Basic {OAuthClientCredentialsEncoder.CreateBasicSchemeParameter(clientId, clientSecret)}";

        private async Task<string?> AccessTokenFromResponse()
        {
            string responseBody = await _apiResponse.TextAsync();
            _apiResponse.Status.Should().Be(200, responseBody);
            return JsonNode.Parse(responseBody)?["access_token"]?.GetValue<string>();
        }

        private Dictionary<string, string> StoredTokenResponses()
        {
            if (
                !_scenarioContext.TryGetValue(
                    StoredTokenResponsesContextKey,
                    out Dictionary<string, string> stored
                )
            )
            {
                stored = [];
                _scenarioContext[StoredTokenResponsesContextKey] = stored;
            }

            return stored;
        }

        private static JsonNode WithoutCorrelationId(string body)
        {
            JsonNode node = JsonNode.Parse(body)!;
            (node as JsonObject)?.Remove("correlationId");
            return node;
        }
    }
}
