// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class KeycloakTokenManagerTests
{
    // Returns a single preconfigured response for the token POST so GetAccessTokenAsync can be exercised
    // without a live Keycloak.
    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
    }

    private static KeycloakTokenManager CreateTokenManager(HttpStatusCode statusCode, string body)
    {
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient("KeycloakClient"))
            .Returns(new HttpClient(new StubHttpMessageHandler(statusCode, body)));

        return new KeycloakTokenManager(
            new KeycloakContext("http://localhost:8045", "edfi", "admin-client", "secret", "role"),
            A.Fake<ILogger<KeycloakTokenManager>>(),
            httpClientFactory
        );
    }

    private static readonly KeyValuePair<string, string>[] Credentials =
    [
        new("client_id", "CSClient1"),
        new("client_secret", "secret"),
        new("grant_type", "client_credentials"),
    ];

    [TestFixture]
    public class Given_The_Provider_Returns_A_400_Oauth_Error : KeycloakTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var tokenManager = CreateTokenManager(
                HttpStatusCode.BadRequest,
                """{"error":"invalid_scope","error_description":"Invalid scopes: edfi_admin_api/bogus"}"""
            );
            _result = await tokenManager.GetAccessTokenAsync(Credentials);
        }

        [Test]
        public void It_preserves_the_oauth_error_code_as_a_bad_request_failure()
        {
            _result.Should().BeOfType<TokenResult.FailureIdentityProvider>();
            var error = ((TokenResult.FailureIdentityProvider)_result).IdentityProviderError;
            error.Should().BeOfType<IdentityProviderError.BadRequest>();
            ((IdentityProviderError.BadRequest)error).Error.Should().Be("invalid_scope");
        }
    }

    [TestFixture]
    public class Given_The_Provider_Returns_A_400_With_A_Non_Json_Body : KeycloakTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var tokenManager = CreateTokenManager(HttpStatusCode.BadRequest, "not a json body");
            _result = await tokenManager.GetAccessTokenAsync(Credentials);
        }

        [Test]
        public void It_falls_back_to_the_generic_invalid_request_code()
        {
            _result.Should().BeOfType<TokenResult.FailureIdentityProvider>();
            var error = ((TokenResult.FailureIdentityProvider)_result).IdentityProviderError;
            error.Should().BeOfType<IdentityProviderError.BadRequest>();
            ((IdentityProviderError.BadRequest)error).Error.Should().Be("invalid_request");
        }
    }

    [TestFixture]
    public class Given_The_Provider_Returns_A_400_Invalid_Client : KeycloakTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // Keycloak reports a client-authentication failure as HTTP 400 with error=invalid_client. It
            // must be preserved as an InvalidClient failure so the token endpoint can answer 401 with the
            // Basic challenge, rather than collapsing to a generic 400 client error (which the endpoint's
            // normalization would turn into invalid_request).
            var tokenManager = CreateTokenManager(
                HttpStatusCode.BadRequest,
                """{"error":"invalid_client","error_description":"Invalid client or Invalid client credentials"}"""
            );
            _result = await tokenManager.GetAccessTokenAsync(Credentials);
        }

        [Test]
        public void It_preserves_invalid_client_as_a_client_authentication_failure()
        {
            _result.Should().BeOfType<TokenResult.FailureIdentityProvider>();
            var error = ((TokenResult.FailureIdentityProvider)_result).IdentityProviderError;
            error.Should().BeOfType<IdentityProviderError.InvalidClient>();
        }
    }

    [TestFixture]
    public class Given_The_Provider_Returns_A_Successful_Token : KeycloakTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var tokenManager = CreateTokenManager(
                HttpStatusCode.OK,
                """{"access_token":"abc","token_type":"bearer","expires_in":900}"""
            );
            _result = await tokenManager.GetAccessTokenAsync(Credentials);
        }

        [Test]
        public void It_returns_a_success_result() => _result.Should().BeOfType<TokenResult.Success>();
    }
}
