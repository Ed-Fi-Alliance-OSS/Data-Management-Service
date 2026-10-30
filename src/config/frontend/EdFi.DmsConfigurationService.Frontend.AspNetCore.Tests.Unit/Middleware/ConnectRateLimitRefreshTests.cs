// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.ConnectRateLimitTestHost;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

public class ConnectRateLimitRefreshTests
{
    [TestFixture(10, 2, "198.51.100.1")]
    [TestFixture(2, 10, "198.51.100.1")]
    [TestFixture(10, 2, "0.0.0.0")]
    [TestFixture(2, 10, "0.0.0.0")]
    [NonParallelizable]
    public class Given_public_attempts_against_the_internal_client(
        int sourceLimit,
        int clientLimit,
        string forwardedSource
    )
    {
        private const string InternalClientId = "CMSReadOnlyAccess";
        private const string ProxyAddress = "172.30.148.2";
        private List<int> _publicStatuses = [];
        private string _privateToken = string.Empty;
        private int _publicStatusAfterRefresh;

        [SetUp]
        public async Task Setup()
        {
            Dictionary<string, string> settings = new()
            {
                ["AppSettings__ReverseProxy__UseForwardedHeaders"] = "true",
                ["AppSettings__ReverseProxy__KnownProxies"] = ProxyAddress,
                ["ConnectRateLimit__RequiredProxyAddress"] = ProxyAddress,
            };
            Dictionary<string, string?> previous = settings.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable
            );
            try
            {
                // Program reads forwarding settings before WebApplicationFactory's configuration
                // callback. Set them before creating the host and restore them even if setup fails.
                foreach ((string key, string value) in settings)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
                await ExhaustPublicBudgetAndRefresh();
            }
            finally
            {
                foreach ((string key, string? value) in previous)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }

        private async Task ExhaustPublicBudgetAndRefresh()
        {
            ITokenManager tokens = SucceedingTokenManager();
            A.CallTo(() =>
                    tokens.GetAccessTokenAsync(
                        A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(parameters =>
                            parameters.Any(pair =>
                                pair.Key == "client_secret" && pair.Value == "wrong-secret"
                            )
                        )
                    )
                )
                .Returns(new TokenResult.FailureAuthentication("invalid_client", "Invalid credentials"));
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: sourceLimit,
                clientPermitLimit: clientLimit,
                tokenManager: tokens
            );
            using HttpClient publicClient = factory.CreateClient();
            _publicStatuses = await SendAll(publicClient, [PublicAttempt, PublicAttempt, PublicAttempt]);

            // Match ConfigurationServiceTokenHandler's private POST: client credentials and scope,
            // from the DMS transport address with no X-Forwarded-For.
            using HttpClient privateClient = factory.CreateClient();
            using HttpResponseMessage refresh = await privateClient.SendAsync(
                Post(
                    "/connect/token",
                    new FormUrlEncodedContent([
                        new("client_id", InternalClientId),
                        new("client_secret", ClientSecret),
                        new("grant_type", "client_credentials"),
                        new("scope", "edfi_admin_api/readonly_access"),
                    ]),
                    ProxyAddress
                )
            );
            refresh.EnsureSuccessStatusCode();
            _privateToken = JsonNode.Parse(await refresh.Content.ReadAsStringAsync())![
                "access_token"
            ]!.GetValue<string>();
            using HttpResponseMessage stillRejected = await publicClient.SendAsync(PublicAttempt());
            _publicStatusAfterRefresh = (int)stillRejected.StatusCode;
        }

        private HttpRequestMessage PublicAttempt()
        {
            HttpRequestMessage request = Post(
                "/connect/token",
                new FormUrlEncodedContent([
                    new("client_id", InternalClientId),
                    new("client_secret", "wrong-secret"),
                    new("grant_type", "client_credentials"),
                ]),
                ProxyAddress
            );
            request.Headers.Add("X-Forwarded-For", forwardedSource);
            return request;
        }

        [Test]
        public void It_throttles_public_failed_attempts() => _publicStatuses.Should().Equal(400, 400, 429);

        [Test]
        public void It_allows_the_private_refresh_for_the_same_identity() =>
            _privateToken.Should().Be("input123token");

        [Test]
        public void It_keeps_the_public_budget_exhausted() => _publicStatusAfterRefresh.Should().Be(429);
    }
}
