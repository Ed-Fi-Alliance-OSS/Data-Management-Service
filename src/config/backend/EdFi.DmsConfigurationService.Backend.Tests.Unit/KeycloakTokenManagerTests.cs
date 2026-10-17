// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// Classification of Keycloak's token endpoint answers by <see cref="KeycloakTokenManager"/>. The
/// request goes through a real <see cref="HttpClient"/> over a stub handler, so the form body and the
/// response handling are the production ones.
/// </summary>
public class KeycloakTokenManagerTests
{
    private static readonly KeycloakContext _context = new(
        "http://keycloak.test",
        "edfi",
        "cms-service",
        "SECRET-SERVICE-SENTINEL",
        "role"
    );

    private static readonly KeyValuePair<string, string>[] _credentials =
    [
        new("grant_type", "client_credentials"),
        new("client_id", "caller-client"),
        new("client_secret", "caller-secret"),
        new("scope", "edfi_admin_api/full_access"),
    ];

    public abstract class TokenManagerFixture
    {
        protected TokenResult _result = null!;
        protected List<KeyValuePair<string, string>> _postedForm = [];
        protected Exception? _thrown;

        protected abstract HttpResponseMessage Answer();

        [SetUp]
        public async Task Setup()
        {
            using var handler = new StubHandler(async request =>
            {
                string body = await request.Content!.ReadAsStringAsync();
                _postedForm =
                [
                    .. body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(pair => pair.Split('=', 2))
                        .Select(parts => new KeyValuePair<string, string>(
                            WebUtility.UrlDecode(parts[0]),
                            WebUtility.UrlDecode(parts.Length > 1 ? parts[1] : "")
                        )),
                ];
                return Answer();
            });
            var httpClientFactory = A.Fake<IHttpClientFactory>();
            A.CallTo(() => httpClientFactory.CreateClient(A<string>._))
                .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));
            var manager = new KeycloakTokenManager(
                _context,
                A.Fake<ILogger<KeycloakTokenManager>>(),
                httpClientFactory
            );

            try
            {
                _result = await manager.GetAccessTokenAsync(_credentials);
            }
            catch (Exception ex)
            {
                _thrown = ex;
            }
        }

        protected static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        protected IdentityProviderError ProviderError() =>
            _result.Should().BeOfType<TokenResult.FailureIdentityProvider>().Subject.IdentityProviderError;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => respond(request);
    }

    [TestFixture]
    public class Given_keycloak_answers_400_invalid_scope : TokenManagerFixture
    {
        private const string Body =
            """{"error":"invalid_scope","error_description":"Invalid scopes: bogus"}""";

        protected override HttpResponseMessage Answer() => Json(HttpStatusCode.BadRequest, Body);

        [Test]
        public void It_returns_InvalidScope() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError.InvalidScope));

        [Test]
        public void It_keeps_the_raw_body_as_the_failure_message() =>
            ProviderError().FailureMessage.Should().Be(Body);
    }

    [TestFixture]
    public class Given_keycloak_answers_400_invalid_request : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() =>
            Json(HttpStatusCode.BadRequest, """{"error":"invalid_request","error_description":"x"}""");

        [Test]
        public void It_returns_the_base_type() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError));
    }

    [TestFixture]
    public class Given_keycloak_answers_400_with_a_non_json_body : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() =>
            new(HttpStatusCode.BadRequest) { Content = new StringContent("<html>Bad Request</html>") };

        [Test]
        public void It_does_not_throw() => _thrown.Should().BeNull();

        [Test]
        public void It_returns_the_base_type() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError));
    }

    [TestFixture]
    public class Given_keycloak_answers_400_with_a_json_array_body : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() =>
            Json(HttpStatusCode.BadRequest, """["invalid_scope"]""");

        [Test]
        public void It_returns_the_base_type() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError));
    }

    [TestFixture]
    public class Given_keycloak_answers_400_with_an_empty_body : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() => Json(HttpStatusCode.BadRequest, "");

        [Test]
        public void It_returns_the_base_type() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError));
    }

    [TestFixture]
    public class Given_keycloak_answers_400_with_a_non_string_error : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() =>
            Json(HttpStatusCode.BadRequest, """{"error":["invalid_scope"]}""");

        [Test]
        public void It_returns_the_base_type() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError));
    }

    [TestFixture]
    public class Given_keycloak_answers_400_with_a_differently_cased_error : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() =>
            Json(HttpStatusCode.BadRequest, """{"error":"Invalid_Scope"}""");

        [Test]
        public void It_returns_the_base_type() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError));
    }

    [TestFixture]
    public class Given_keycloak_answers_500 : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() =>
            Json(HttpStatusCode.InternalServerError, """{"error":"invalid_scope"}""");

        [Test]
        public void It_returns_the_base_type() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError));
    }

    [TestFixture]
    public class Given_keycloak_answers_401 : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() =>
            Json(HttpStatusCode.Unauthorized, """{"error":"unauthorized_client"}""");

        [Test]
        public void It_returns_Unauthorized() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError.Unauthorized));
    }

    [TestFixture]
    public class Given_the_connection_fails : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");

        [Test]
        public void It_returns_Unreachable() =>
            ProviderError().GetType().Should().Be(typeof(IdentityProviderError.Unreachable));
    }

    [TestFixture]
    public class Given_keycloak_answers_200 : TokenManagerFixture
    {
        private const string Body = """{"access_token":"abc","expires_in":300}""";

        protected override HttpResponseMessage Answer() => Json(HttpStatusCode.OK, Body);

        [Test]
        public void It_returns_Success_with_the_body() =>
            _result.Should().BeOfType<TokenResult.Success>().Which.Token.Should().Be(Body);
    }

    [TestFixture]
    public class Given_a_token_request : TokenManagerFixture
    {
        protected override HttpResponseMessage Answer() => Json(HttpStatusCode.OK, "{}");

        [Test]
        public void It_posts_exactly_the_given_form_fields_in_order() =>
            _postedForm.Should().Equal(_credentials);
    }
}
