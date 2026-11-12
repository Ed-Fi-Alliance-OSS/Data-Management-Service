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

        private const string TokenRequestCorrelationContextKey = "tokenRequestCorrelation";

        private const string CorrelationHeaderName = "X-Correlation-ID";

        private const string BasicChallenge = "Basic realm=\"EdFi.DataManagementService\"";

        private const string TokenContractCorrelationHeader = "X-DMS-1606-Correlation";

        [When("a token request is made with content type {string} and body")]
        public async Task WhenATokenRequestIsMadeWithContentTypeAndBody(string contentType, string body)
        {
            await SendTokenRequest(contentType, body, authorization: null);
        }

        [When("a token request is made with {int} repeated grant_type form parameters")]
        public async Task WhenATokenRequestIsMadeWithRepeatedGrantTypeFormParameters(int count)
        {
            await SendTokenRequest(
                "application/x-www-form-urlencoded",
                string.Join('&', Enumerable.Repeat("grant_type=client_credentials", count)),
                authorization: null
            );
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
            };
            string rootCorrelation = ResponseHeader(CorrelationHeaderName);

            await SendTokenRequest(
                "application/x-www-form-urlencoded",
                "grant_type=client_credentials",
                authorization,
                "2025/oauth/token"
            );
            await AssertTokenError(401, "invalid_client", "Fixture rejected credentials");
            (await _apiResponse.TextAsync()).Should().Be(root["body"]);
            ResponseHeaderOrNull("www-authenticate").Should().Be(root["challenge"]);
            ResponseHeader(CorrelationHeaderName).Should().NotBe(rootCorrelation);
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

            if (status == 401)
            {
                ResponseHeader("www-authenticate").Should().Be(BasicChallenge);
            }
            else
            {
                ResponseHeaderOrNull("www-authenticate").Should().BeNull();
            }

            string encodedTraceId = ResponseHeader(CorrelationHeaderName);
            encodedTraceId.Should().NotBeNullOrWhiteSpace();
            string traceId = Uri.UnescapeDataString(encodedTraceId);
            if (_scenarioContext.TryGetValue(TokenRequestCorrelationContextKey, out string expectedTraceId))
            {
                traceId.Should().Be(expectedTraceId);
            }

            await AssertDmsRequestLogContains(traceId, status);
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
                string requestCorrelation = $"dms1606-{Guid.NewGuid():N}";
                headers[TokenContractCorrelationHeader] = requestCorrelation;
                _scenarioContext[TokenRequestCorrelationContextKey] = requestCorrelation;
            }
            else
            {
                _scenarioContext.Remove(TokenRequestCorrelationContextKey);
            }

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

        private async Task AssertDmsRequestLogContains(string traceId, int status)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            if (AppSettings.DmsLogFilePath.Length > 0)
            {
                await using FileStream file = new(
                    AppSettings.DmsLogFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                using StreamReader reader = new(file);
                bool found = await TokenRequestLogContainsAsync(reader, traceId, status, true, timeout.Token);
                found
                    .Should()
                    .BeTrue("the token response correlation identifier must appear in DMS request logs");
                return;
            }

            using Process process = new();
            process.StartInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("logs");
            process.StartInfo.ArgumentList.Add("--follow");
            process.StartInfo.ArgumentList.Add(AppSettings.DmsContainerName);
            process.Start();
            // Read retained logs as well as newly flushed events. The response's unique TraceId
            // selects this request without relying on synchronized test-host and Docker clocks.
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                bool found = await TokenRequestLogContainsAsync(
                    process.StandardOutput,
                    traceId,
                    status,
                    false,
                    timeout.Token
                );
                found
                    .Should()
                    .BeTrue("the token response correlation identifier must appear in DMS request logs");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync();
                _ = await stderr;
            }
        }

        internal static async Task<bool> TokenRequestLogContainsAsync(
            TextReader reader,
            string traceId,
            int status,
            bool followEndOfFile,
            CancellationToken cancellationToken
        )
        {
            char[] buffer = new char[4096];
            string pending = string.Empty;
            try
            {
                while (true)
                {
                    int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (count == 0)
                    {
                        if (!followEndOfFile)
                        {
                            return TokenRequestLogContains(pending, traceId, status);
                        }

                        // A host file can reach EOF before the sink flushes this request's event.
                        // Poll for appended bytes, keeping the cursor and any incomplete JSON line.
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                        continue;
                    }

                    pending += new string(buffer, 0, count);
                    int newline;
                    while ((newline = pending.IndexOf('\n')) >= 0)
                    {
                        if (TokenRequestLogContains(pending[..newline], traceId, status))
                        {
                            return true;
                        }

                        pending = pending[(newline + 1)..];
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The deadline bounds waiting for the event; it is not a log-time cutoff.
                return false;
            }
        }

        internal static bool TokenRequestLogContains(string logs, string traceId, int status)
        {
            foreach (string line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (
                        root.ValueKind is JsonValueKind.Object
                        && root.TryGetProperty("Properties", out JsonElement properties)
                        && properties.ValueKind is JsonValueKind.Object
                        && properties.TryGetProperty("TraceId", out JsonElement loggedTraceId)
                        && loggedTraceId.ValueKind is JsonValueKind.String
                        && loggedTraceId.GetString() == traceId
                        && properties.TryGetProperty("EventName", out JsonElement eventName)
                        && eventName.ValueKind is JsonValueKind.String
                        && eventName.GetString() is "HttpRequestCompleted" or "HttpRequestFailed"
                        && properties.TryGetProperty("Method", out JsonElement method)
                        && method.ValueKind is JsonValueKind.String
                        && method.GetString() is "POST"
                        && properties.TryGetProperty("StatusCode", out JsonElement statusCode)
                        && statusCode.ValueKind is JsonValueKind.Number
                        && statusCode.TryGetInt32(out int loggedStatus)
                        && loggedStatus == status
                    )
                    {
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // Docker can interleave non-JSON startup diagnostics with structured request logs.
                }
            }

            return false;
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
