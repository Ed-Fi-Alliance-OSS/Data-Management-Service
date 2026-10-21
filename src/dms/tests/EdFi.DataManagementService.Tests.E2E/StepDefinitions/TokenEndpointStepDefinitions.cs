// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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

        private const string TokenRequestStartedContextKey = "tokenRequestStartedUnixSeconds";

        private const string CorrelationHeaderName = "X-Correlation-ID";

        private const string BasicChallenge = "Basic realm=\"EdFi.DataManagementService\"";

        private const string TokenContractCorrelationHeader = "X-DMS-1606-Correlation";

        private const string TokenContractCorrelationValue = "dms1606-token-contract";

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

        [When("a token request is made with fixture Basic client ID {string}")]
        public async Task WhenATokenRequestIsMadeWithFixtureBasicClientId(string clientId)
        {
            await SendTokenRequest(
                "application/x-www-form-urlencoded",
                "grant_type=client_credentials",
                BasicAuthorization(clientId, "fixture-secret")
            );
        }

        [When("a token request is made with fixture Basic client ID {string} at path {string}")]
        public async Task WhenATokenRequestIsMadeWithFixtureBasicClientIdAtPath(string clientId, string path)
        {
            await SendTokenRequest(
                "application/x-www-form-urlencoded",
                "grant_type=client_credentials",
                BasicAuthorization(clientId, "fixture-secret"),
                path
            );
        }

        [When(
            "equivalent token errors are requested from the root and qualified routes using fixture client ID {string}"
        )]
        public async Task WhenEquivalentTokenErrorsAreRequestedFromRootAndQualifiedRoutes(string clientId)
        {
            string authorization = BasicAuthorization(clientId, "fixture-secret");
            await SendTokenRequest(
                "application/x-www-form-urlencoded",
                "grant_type=client_credentials",
                authorization
            );
            await AssertTokenError(401, "invalid_client", "Fixture rejected credentials");
            Dictionary<string, string?> root = new()
            {
                ["body"] = await _apiResponse.TextAsync(),
                ["challenge"] = ResponseHeaderOrNull("www-authenticate"),
                ["correlation"] = ResponseHeader("X-Correlation-ID"),
            };

            await SendTokenRequest(
                "application/x-www-form-urlencoded",
                "grant_type=client_credentials",
                authorization,
                "2025/oauth/token"
            );
            await AssertTokenError(401, "invalid_client", "Fixture rejected credentials");
            (await _apiResponse.TextAsync()).Should().Be(root["body"]);
            ResponseHeaderOrNull("www-authenticate").Should().Be(root["challenge"]);
            ResponseHeader("X-Correlation-ID").Should().Be(root["correlation"]);
        }

        [When(
            "a token request is made with a Basic Authorization header at path {string}, content type {string} and body"
        )]
        public async Task WhenATokenRequestIsMadeWithBasicAuthorizationHeaderAtPath(
            string path,
            string contentType,
            string body
        )
        {
            ClientCredentials credentials = AuthorizationDataProvider.CurrentClientCredentials;
            await SendTokenRequest(
                contentType,
                body,
                BasicAuthorization(credentials.key, credentials.secret),
                path
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

        [Then("the token response body matches the one stored as {string}")]
        public async Task ThenTheTokenResponseBodyMatchesTheOneStoredAs(string name)
        {
            (await _apiResponse.TextAsync()).Should().Be(StoredTokenResponses()[name]);
        }

        [Then("the token error is {int} {string} with description {string}")]
        public async Task ThenTheTokenErrorIs(int status, string code, string description)
        {
            await AssertTokenError(status, code, description);
        }

        [Then("the token error is {int} {string} with provider-supplied description")]
        public async Task ThenTheTokenErrorHasProviderSuppliedDescription(int status, string code)
        {
            await AssertTokenError(status, code, expectedDescription: null);
        }

        private async Task AssertTokenError(int status, string code, string? expectedDescription)
        {
            _apiResponse.Status.Should().Be(status);
            string contentType = ResponseHeader("content-type");
            contentType.Split(';', 2)[0].Trim().Should().Be("application/json");

            JsonNode body = JsonNode.Parse(await _apiResponse.TextAsync())!;
            JsonObject error = body.AsObject();
            error.Select(property => property.Key).Should().BeEquivalentTo("error", "error_description");
            error["error"]!.GetValue<string>().Should().Be(code);
            string actualDescription = error["error_description"]!.GetValue<string>();
            if (expectedDescription is null)
            {
                actualDescription.Should().NotBeNullOrWhiteSpace();
            }
            else
            {
                actualDescription.Should().Be(expectedDescription);
            }

            string? challenge = ResponseHeaderOrNull("www-authenticate");
            if (status == 401)
            {
                challenge.Should().Be(BasicChallenge);
            }
            else
            {
                challenge.Should().BeNull();
            }

            string encodedTraceId = ResponseHeader(CorrelationHeaderName);
            encodedTraceId.Should().NotBeNullOrWhiteSpace();
            string traceId = Uri.UnescapeDataString(encodedTraceId);
            await AssertDmsRequestLogContains(traceId);
        }

        /// <summary>
        /// Posts <paramref name="body"/> to the token endpoint after replacing <c>{clientId}</c> and
        /// <c>{clientSecret}</c> with the provisioned client's credentials, encoded for the content
        /// type so that any character a generated key or secret contains survives the trip.
        /// </summary>
        private async Task SendTokenRequest(
            string contentType,
            string body,
            string? authorization,
            string path = TokenEndpoint
        )
        {
            ClientCredentials credentials = AuthorizationDataProvider.CurrentClientCredentials;
            Func<string, string> encode = contentType.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase
            )
                ? value => JsonEncodedText.Encode(value).ToString()
                : Uri.EscapeDataString;

            body = body.Replace("{oversizedClientId}", new string('x', 2049))
                .Replace("{clientId}", encode(credentials.key))
                .Replace("{clientSecret}", encode(credentials.secret));

            Dictionary<string, string> headers = new() { ["Content-Type"] = contentType };
            if (authorization is not null)
            {
                headers["Authorization"] = authorization;
            }

            if (Environment.GetEnvironmentVariable("DMS_TOKEN_CONTRACT_PROXY_ACTIVE") == "1")
            {
                headers[TokenContractCorrelationHeader] = TokenContractCorrelationValue;
            }

            _scenarioContext[TokenRequestStartedContextKey] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SetCurrentApiResponse(
                await _playwrightContext.ApiRequestContext!.PostAsync(
                    path,
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

        private async Task AssertDmsRequestLogContains(string traceId)
        {
            _scenarioContext.TryGetValue(TokenRequestStartedContextKey, out long startedAt).Should().BeTrue();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            while (!timeout.IsCancellationRequested)
            {
                using Process process = new();
                process.StartInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                process.StartInfo.ArgumentList.Add("logs");
                process.StartInfo.ArgumentList.Add("--since");
                process.StartInfo.ArgumentList.Add(
                    startedAt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                );
                process.StartInfo.ArgumentList.Add(AppSettings.DmsContainerName);
                process.Start();

                try
                {
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                    string logs = await stdout;
                    _ = await stderr;
                    if (process.ExitCode == 0 && logs.Contains(traceId, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }
            }

            Assert.Fail("The token response correlation identifier did not appear in DMS request logs.");
        }

        private string ResponseHeader(string name) =>
            ResponseHeaderOrNull(name)
            ?? throw new AssertionException($"The {name} response header was missing.");

        private string? ResponseHeaderOrNull(string name) =>
            _apiResponse
                .Headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Value;
    }
}
