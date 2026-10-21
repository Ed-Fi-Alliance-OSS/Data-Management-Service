// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using CmsFailureResponse = EdFi.DmsConfigurationService.DataModel.Infrastructure.FailureResponse;

namespace EdFi.DataManagementService.Core.Tests.Unit;

public class OAuthManagerTests
{
    [TestFixture]
    [Parallelizable]
    public class When_Getting_An_Access_Token
    {
        protected readonly IHttpClientWrapper _httpClient = A.Fake<IHttpClientWrapper>();

        protected readonly ILogger<OAuthManager> _logger = A.Fake<ILogger<OAuthManager>>();

        protected HttpResponseMessage _response = new();

        /// <summary>
        /// The request OAuthManager sent upstream, or <c>null</c> when it sent none.
        /// </summary>
        protected HttpRequestMessage? _upstreamRequest;

        private const string DestinationUri = "http://example.com/oauth/token";

        private readonly TraceId TraceId = new("trace-id");

        protected const string GrantType = "client_credentials";

        public async Task Act(string? authHeader, string grantType = GrantType)
        {
            await Act(authHeader, HttpStatusCode.OK, "{}", grantType);
        }

        public async Task Act(string? authHeader, TokenRequestCredentials bodyCredentials)
        {
            await Act(authHeader, HttpStatusCode.OK, "{}", GrantType, bodyCredentials);
        }

        /// <param name="authHeader">
        /// The caller's <c>Authorization</c> header, or <c>null</c> when the request carried none.
        /// </param>
        public async Task Act(
            string? authHeader,
            HttpStatusCode responseCode,
            string responseMessage,
            string grantType = "",
            TokenRequestCredentials? bodyCredentials = null
        )
        {
            // Arrange
            var fakeResponse = A.Fake<HttpResponseMessage>();
            fakeResponse.StatusCode = responseCode;
            fakeResponse.Content = new StringContent(responseMessage, Encoding.UTF8, "application/json");

            A.CallTo(() => _httpClient.SendAsync(A<HttpRequestMessage>._))
                .Invokes((HttpRequestMessage request) => _upstreamRequest = request)
                .ReturnsLazily(() => fakeResponse);

            var system = new OAuthManager(_logger);

            // Act
            _response = await system.GetAccessTokenAsync(
                _httpClient,
                grantType,
                authHeader,
                bodyCredentials ?? TokenRequestCredentials.None,
                DestinationUri,
                TraceId
            );
        }

        /// <summary>
        /// The single <c>Authorization</c> value OAuthManager sent upstream.
        /// </summary>
        protected string UpstreamAuthorization() =>
            _upstreamRequest!.Headers.GetValues("Authorization").Should().ContainSingle().Subject;

        /// <summary>
        /// The <c>detail</c> of the problem response OAuthManager returned.
        /// </summary>
        protected async Task<string> ResponseDetail() =>
            JsonNode.Parse(await _response.Content.ReadAsStringAsync())![
                "error_description"
            ]!.GetValue<string>();

        /// <summary>
        /// The <c>title</c> of the problem response OAuthManager returned, which carries the
        /// OAuth error code on a 401.
        /// </summary>
        protected async Task<string> ResponseTitle() =>
            JsonNode.Parse(await _response.Content.ReadAsStringAsync())!["error"]!.GetValue<string>();

        protected async Task AssertOAuthErrorResponse(
            HttpStatusCode expectedStatus,
            string expectedError,
            string expectedDescription
        )
        {
            _response.StatusCode.Should().Be(expectedStatus);
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            using JsonDocument body = JsonDocument.Parse(await _response.Content.ReadAsStringAsync());
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Should()
                .BeEquivalentTo("error", "error_description");
            body.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
            body.RootElement.GetProperty("error_description").GetString().Should().Be(expectedDescription);
            _response
                .Headers.GetValues("X-Correlation-ID")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(Uri.EscapeDataString(TraceId.Value));

            if (expectedStatus is HttpStatusCode.Unauthorized)
            {
                _response
                    .Headers.WwwAuthenticate.Select(challenge => challenge.ToString())
                    .Should()
                    .ContainSingle()
                    .Which.Should()
                    .Be("Basic realm=\"EdFi.DataManagementService\"");
            }
            else
            {
                _response.Headers.WwwAuthenticate.Should().BeEmpty();
            }
        }

        public async Task ActWithException(string message)
        {
            // Arrange
            A.CallTo(() => _httpClient.SendAsync(A<HttpRequestMessage>._))
                .ThrowsAsync(() => new InvalidOperationException(message));

            var system = new OAuthManager(A.Fake<ILogger<OAuthManager>>());

            // Act
            _response = await system.GetAccessTokenAsync(
                _httpClient,
                GrantType,
                "basic 123:abc",
                TokenRequestCredentials.None,
                DestinationUri,
                TraceId
            );
        }

        [TestFixture]
        [Parallelizable]
        public class Given_The_Request_Contains_Valid_Request_With_Lower_Basic : When_Getting_An_Access_Token
        {
            private const string AuthHeader = "basic abc:123";

            [SetUp]
            public async Task SetUp()
            {
                await Act(AuthHeader);
            }

            [Test]
            public void Then_It_Responds_With_Ok()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            [Test]
            public void Then_The_Original_Header_Should_Have_Been_Forwarded()
            {
                A.CallTo(() =>
                        _httpClient.SendAsync(
                            A<HttpRequestMessage>.That.Matches(m =>
                                m.Headers.Any(x =>
                                    x.Key == "Authorization" && x.Value.Any(y => y == AuthHeader)
                                )
                            )
                        )
                    )
                    .MustHaveHappened();
            }

            [Test]
            public void Then_The_Content_Type_Must_Have_Been_UrlEncoded()
            {
                A.CallTo(() =>
                        _httpClient.SendAsync(
                            A<HttpRequestMessage>.That.Matches(m =>
                                m.Content != null
                                && m.Content.Headers.ContentType != null
                                && m.Content!.Headers.ContentType!.MediaType
                                    == "application/x-www-form-urlencoded"
                            )
                        )
                    )
                    .MustHaveHappened();
            }

            [Test]
            public void Then_The_Grant_Type_Must_Be_Client_Credentials()
            {
                A.CallTo(() =>
                        _httpClient.SendAsync(
                            A<HttpRequestMessage>.That.Matches(m =>
                                m.Content!.ReadAsStringAsync().Result == "grant_type=client_credentials"
                            )
                        )
                    )
                    .MustHaveHappened();
            }

            [Test]
            public void Then_The_Proxy_Request_Should_Go_To_The_Right_Uri()
            {
                A.CallTo(() =>
                        _httpClient.SendAsync(
                            A<HttpRequestMessage>.That.Matches(m => m.RequestUri == new Uri(DestinationUri))
                        )
                    )
                    .MustHaveHappened();
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_The_Request_Contains_Valid_Request_With_Upper_Basic : When_Getting_An_Access_Token
        {
            private const string AuthHeader = "Basic abc:123";

            [SetUp]
            public async Task SetUp()
            {
                await Act(AuthHeader);
            }

            [Test]
            public void Then_The_Original_Header_Should_Have_Been_Forwarded()
            {
                A.CallTo(() =>
                        _httpClient.SendAsync(
                            A<HttpRequestMessage>.That.Matches(m =>
                                m.Headers.Any(x =>
                                    x.Key == "Authorization" && x.Value.Any(y => y == AuthHeader)
                                )
                            )
                        )
                    )
                    .MustHaveHappened();
            }

            // Not going to repeat the other assertions from
            // Given_The_Request_Contains_Valid_Request_With_Lower_Basic, which
            // already provide sufficient test coverage.
        }

        [TestFixture]
        [Parallelizable]
        public class Given_No_Authorization_Header_And_No_Body_Credentials : When_Getting_An_Access_Token
        {
            [SetUp]
            public async Task SetUp()
            {
                await Act(null);
            }

            [Test]
            public void Then_It_Responds_With_Unauthorized()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            [Test]
            public void Then_The_Response_ContentType_Is_Problem_JSON()
            {
                _response
                    .Content.Headers.ContentType!.MediaType.Should()
                    .NotBeNull()
                    .And.Be("application/json");
            }

            [Test]
            public async Task Then_The_Response_Carries_The_Invalid_Request_Error()
            {
                (await ResponseTitle()).Should().Be(ExpectedInvalidClientError);
            }

            [Test]
            public async Task Then_The_Response_Content_Says_Credentials_Are_Required()
            {
                (await ResponseDetail()).Should().Be(ExpectedMissingCredentialsDetail);
            }

            [Test]
            public void Then_Nothing_Is_Sent_Upstream()
            {
                _upstreamRequest.Should().BeNull();
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_the_Client_Credentials_Are_Invalid : When_Getting_An_Access_Token
        {
            private const string AuthHeader = "basic abc:123";

            [SetUp]
            public async Task SetUp()
            {
                await Act(
                    AuthHeader,
                    HttpStatusCode.Unauthorized,
                    """
{
    "error": "invalid_client",
    "error_description": "Invalid client or Invalid client credentials"
}
""",
                    GrantType
                );
            }

            [Test]
            public void Then_It_Responds_With_Unauthorized()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            [Test]
            public void Then_The_Response_ContentType_Is_Problem_JSON()
            {
                _response
                    .Content.Headers.ContentType!.MediaType.Should()
                    .NotBeNull()
                    .And.Be("application/json");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_Error_As_Title()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain("\"error\":\"invalid_client\"");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_ErrorDetail_As_Detail()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content
                    .Should()
                    .NotBeNull()
                    .And.Contain("\"error_description\":\"Invalid client or Invalid client credentials\"");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_TraceId()
            {
                _response
                    .Headers.GetValues(OAuthErrorResponse.CorrelationHeaderName)
                    .Should()
                    .ContainSingle()
                    .Which.Should()
                    .Be(Uri.EscapeDataString(TraceId.Value));
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_An_Error_Occurred_Without_Exception : When_Getting_An_Access_Token
        {
            private const string AuthHeader = "basic abc:123";

            [SetUp]
            public async Task SetUp()
            {
                await Act(AuthHeader, HttpStatusCode.BadRequest, "{}", GrantType);
            }

            [Test]
            public void Then_It_Responds_With_BadGateway()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            }

            [Test]
            public void Then_The_Response_ContentType_Is_Problem_JSON()
            {
                _response
                    .Content.Headers.ContentType!.MediaType.Should()
                    .NotBeNull()
                    .And.Be("application/json");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_A_Title()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain("\"error\":\"server_error\"");
                content.Should().Contain(OAuthErrorResponse.GatewayDescription);
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_TraceId()
            {
                _response
                    .Headers.GetValues(OAuthErrorResponse.CorrelationHeaderName)
                    .Should()
                    .ContainSingle()
                    .Which.Should()
                    .Be(Uri.EscapeDataString(TraceId.Value));
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_An_An_Exception_Occurs : When_Getting_An_Access_Token
        {
            private const string ExceptionMessage = "this is a problem";

            [SetUp]
            public async Task SetUp()
            {
                await ActWithException(ExceptionMessage);
            }

            [Test]
            public void Then_It_Responds_With_BadGateway()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            }

            [Test]
            public void Then_The_Response_ContentType_Is_Problem_JSON()
            {
                _response
                    .Content.Headers.ContentType!.MediaType.Should()
                    .NotBeNull()
                    .And.Be("application/json");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_A_Title()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain("\"error\":\"server_error\"");
                content.Should().Contain(OAuthErrorResponse.GatewayDescription);
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_TraceId()
            {
                _response
                    .Headers.GetValues(OAuthErrorResponse.CorrelationHeaderName)
                    .Should()
                    .ContainSingle()
                    .Which.Should()
                    .Be(Uri.EscapeDataString(TraceId.Value));
            }

            // This is a rare case where it would be nice to check that the log
            // has been accessed - but extension methods cannot be verified.
        }

        [TestFixture]
        [Parallelizable]
        public class Given_An_Invalid_Grant_Type : When_Getting_An_Access_Token
        {
            [SetUp]
            public async Task SetUp()
            {
                string authHeader = "basic abc:123";
                await Act(authHeader, "invalid_grant_type");
            }

            [Test]
            public void Then_It_Responds_With_BadRequest()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }

            [Test]
            public void Then_The_Response_ContentType_Is_Problem_JSON()
            {
                _response
                    .Content.Headers.ContentType!.MediaType.Should()
                    .NotBeNull()
                    .And.Be("application/json");
            }

            [Test]
            public async Task Then_The_Response_Content_Mentions_Malformed_Header()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull();
                (await ResponseTitle()).Should().Be("unsupported_grant_type");
                (await ResponseDetail()).Should().Be("Unsupported grant type");
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_Body_Credentials_And_No_Authorization_Header : When_Getting_An_Access_Token
        {
            [SetUp]
            public async Task SetUp()
            {
                await Act(null, new TokenRequestCredentials("my-client", "my-secret"));
            }

            [Test]
            public void Then_It_Responds_With_Ok()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            [Test]
            public void Then_A_Basic_Header_Built_From_The_Body_Is_Sent_Upstream()
            {
                UpstreamAuthorization()
                    .Should()
                    .Be($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("my-client:my-secret"))}");
            }

            [Test]
            public async Task Then_The_Upstream_Form_Body_Carries_Only_The_Grant_Type()
            {
                (await _upstreamRequest!.Content!.ReadAsStringAsync())
                    .Should()
                    .Be("grant_type=client_credentials");
            }

            [Test]
            public void Then_The_Proxy_Request_Should_Go_To_The_Right_Uri()
            {
                _upstreamRequest!.RequestUri.Should().Be(new Uri(DestinationUri));
            }
        }

        /// <summary>
        /// RFC 6749 section 2.3.1 has the client ID and secret encoded before they are joined with a
        /// colon and base64-encoded, so a colon in either one cannot move the split. The expected
        /// value is spelled out rather than computed with the production call, so that switching to
        /// form encoding - which writes a space as <c>+</c>, a character the Configuration Service's
        /// <c>Uri.UnescapeDataString</c> leaves as a literal plus - fails here.
        /// </summary>
        [TestFixture]
        [Parallelizable]
        public class Given_Body_Credentials_With_Reserved_And_Non_Ascii_Characters
            : When_Getting_An_Access_Token
        {
            private const string ClientId = "client:id é";

            private const string ClientSecret = "p a+s:s%wörd/\U0001F600";

            private const string ExpectedEncodedPair =
                "client%3Aid%20%C3%A9:p%20a%2Bs%3As%25w%C3%B6rd%2F%F0%9F%98%80";

            private string _decodedPair = default!;

            [SetUp]
            public async Task SetUp()
            {
                await Act(null, new TokenRequestCredentials(ClientId, ClientSecret));

                string authorization = UpstreamAuthorization();
                authorization.Should().StartWith("Basic ");
                _decodedPair = Encoding.UTF8.GetString(
                    Convert.FromBase64String(authorization["Basic ".Length..])
                );
            }

            [Test]
            public void Then_Each_Value_Is_Percent_Encoded_Before_Joining()
            {
                _decodedPair.Should().Be(ExpectedEncodedPair);
            }

            [Test]
            public void Then_The_Configuration_Service_Decoding_Recovers_The_Original_Values()
            {
                // The Configuration Service splits on the first colon and applies
                // Uri.UnescapeDataString to each half.
                string[] parts = _decodedPair.Split(':', 2);
                Uri.UnescapeDataString(parts[0]).Should().Be(ClientId);
                Uri.UnescapeDataString(parts[1]).Should().Be(ClientSecret);
            }

            [Test]
            public void Then_Form_Decoding_Recovers_The_Original_Values()
            {
                // Keycloak form-decodes each half, which also turns '+' into a space, so the
                // encoded pair must carry no literal '+'.
                string[] parts = _decodedPair.Split(':', 2);
                WebUtility.UrlDecode(parts[0]).Should().Be(ClientId);
                WebUtility.UrlDecode(parts[1]).Should().Be(ClientSecret);
            }
        }

        /// <summary>
        /// RFC 6749 section 2.3 allows one client authentication mechanism per request. One non-blank
        /// body field is enough, so a partial combination is still mixed, even when the other field
        /// is empty or whitespace-only.
        /// </summary>
        [TestFixture("my-client", "my-secret")]
        [TestFixture("my-client", "")]
        [TestFixture("   ", "my-secret")]
        [TestFixture(null, "my-secret")]
        [Parallelizable]
        public class Given_A_Basic_Authorization_Header_And_Body_Credentials(
            string? clientId,
            string? clientSecret
        ) : When_Getting_An_Access_Token
        {
            [SetUp]
            public async Task SetUp()
            {
                await Act("Basic abc:123", new TokenRequestCredentials(clientId, clientSecret));
            }

            [Test]
            public void Then_It_Responds_With_BadRequest()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }

            [Test]
            public void Then_The_Response_ContentType_Is_Problem_JSON()
            {
                _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            }

            [Test]
            public async Task Then_The_Response_Content_Says_Only_One_Mechanism_Is_Allowed()
            {
                (await ResponseDetail()).Should().Be(ExpectedMixedMechanismsDetail);
            }

            [Test]
            public void Then_Nothing_Is_Sent_Upstream()
            {
                _upstreamRequest.Should().BeNull();
            }
        }

        /// <summary>
        /// RFC 6749 section 3.2: parameters sent without a value must be treated as if they were
        /// omitted. An empty body field beside a Basic header is therefore no second mechanism, and
        /// the request is a plain Basic one. The ODS/API accepts it too.
        /// </summary>
        [TestFixture("", null)]
        [TestFixture(null, "")]
        [TestFixture("", "")]
        [Parallelizable]
        public class Given_A_Basic_Authorization_Header_And_Empty_Body_Fields(
            string? clientId,
            string? clientSecret
        ) : When_Getting_An_Access_Token
        {
            private const string AuthHeader = "Basic abc:123";

            [SetUp]
            public async Task SetUp()
            {
                await Act(AuthHeader, new TokenRequestCredentials(clientId, clientSecret));
            }

            [Test]
            public void Then_It_Responds_With_Ok()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            [Test]
            public void Then_The_Callers_Header_Is_Forwarded_Verbatim()
            {
                UpstreamAuthorization().Should().Be(AuthHeader);
            }

            [Test]
            public async Task Then_The_Upstream_Form_Body_Carries_Only_The_Grant_Type()
            {
                (await _upstreamRequest!.Content!.ReadAsStringAsync())
                    .Should()
                    .Be("grant_type=client_credentials");
            }
        }

        /// <summary>
        /// A whitespace-only value is a value under RFC 6749 section 3.2, but the ODS/API counts a
        /// body field as a second mechanism only when it is non-blank (<c>IsNullOrWhiteSpace</c>),
        /// and DMS follows the ODS/API here.
        /// </summary>
        [TestFixture("   ", null)]
        [TestFixture(null, "   ")]
        [TestFixture("   ", "")]
        [TestFixture("\t", "   ")]
        [Parallelizable]
        public class Given_A_Basic_Authorization_Header_And_Whitespace_Only_Body_Fields(
            string? clientId,
            string? clientSecret
        ) : When_Getting_An_Access_Token
        {
            private const string AuthHeader = "Basic abc:123";

            [SetUp]
            public async Task SetUp()
            {
                await Act(AuthHeader, new TokenRequestCredentials(clientId, clientSecret));
            }

            [Test]
            public void Then_It_Responds_With_Ok()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            [Test]
            public void Then_The_Callers_Header_Is_Forwarded_Verbatim()
            {
                UpstreamAuthorization().Should().Be(AuthHeader);
            }
        }

        /// <summary>
        /// The ODS/API answers any <c>Authorization</c> scheme other than Basic with
        /// <c>401 invalid_client</c>, whatever the body holds, so body credentials do not rescue the
        /// request, and the header outranks the mixed-mechanism check.
        /// </summary>
        [TestFixture("Bearer some-access-token", "my-client", "my-secret")]
        [TestFixture("Bearer some-access-token", "my-client", null)]
        [TestFixture("Bearer some-access-token", null, null)]
        [TestFixture("Digest username=\"my-client\"", "my-client", "my-secret")]
        [Parallelizable]
        public class Given_A_Non_Basic_Authorization_Header(
            string authHeader,
            string? clientId,
            string? clientSecret
        ) : When_Getting_An_Access_Token
        {
            [SetUp]
            public async Task SetUp()
            {
                await Act(authHeader, new TokenRequestCredentials(clientId, clientSecret));
            }

            [Test]
            public void Then_It_Responds_With_Unauthorized()
            {
                _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            [Test]
            public void Then_The_Response_ContentType_Is_Problem_JSON()
            {
                _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            }

            [Test]
            public async Task Then_The_Response_Carries_The_Invalid_Client_Error()
            {
                (await ResponseTitle()).Should().Be(ExpectedInvalidClientError);
            }

            [Test]
            public async Task Then_The_Response_Content_Says_The_Scheme_Must_Be_Basic()
            {
                (await ResponseDetail()).Should().Be(ExpectedNonBasicDetail);
            }

            [Test]
            public async Task Then_The_Response_Does_Not_Echo_The_Secret()
            {
                (await _response.Content.ReadAsStringAsync()).Should().NotContain("my-secret");
            }

            [Test]
            public void Then_Nothing_Is_Sent_Upstream()
            {
                _upstreamRequest.Should().BeNull();
            }
        }

        /// <summary>
        /// An empty field is an omitted one (RFC 6749 section 3.2), and a whitespace-only one is
        /// blank, so either leaves the pair partial or missing, as in the ODS/API.
        /// </summary>
        [TestFixture("my-client", null)]
        [TestFixture(null, "my-secret")]
        [TestFixture("my-client", "")]
        [TestFixture("", "my-secret")]
        [TestFixture("", "")]
        [TestFixture("   ", "my-secret")]
        [TestFixture("my-client", "   ")]
        [TestFixture("   ", "   ")]
        [TestFixture(null, null)]
        [Parallelizable]
        public class Given_Missing_Or_Partial_Body_Credentials_And_No_Authorization_Header(
            string? clientId,
            string? clientSecret
        ) : When_Getting_An_Access_Token
        {
            [SetUp]
            public async Task SetUp()
            {
                await Act(null, new TokenRequestCredentials(clientId, clientSecret));
            }

            [Test]
            public void Then_It_Responds_With_Unauthorized()
            {
                _response
                    .StatusCode.Should()
                    .Be(
                        string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(clientSecret)
                            ? HttpStatusCode.Unauthorized
                            : HttpStatusCode.BadRequest
                    );
            }

            [Test]
            public async Task Then_The_Response_Carries_The_Invalid_Request_Error()
            {
                (await ResponseTitle())
                    .Should()
                    .Be(
                        string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(clientSecret)
                            ? ExpectedInvalidClientError
                            : ExpectedInvalidRequestError
                    );
            }

            [Test]
            public async Task Then_The_Response_Content_Says_Credentials_Are_Required()
            {
                (await ResponseDetail()).Should().Be(ExpectedMissingCredentialsDetail);
            }

            [Test]
            public void Then_Nothing_Is_Sent_Upstream()
            {
                _upstreamRequest.Should().BeNull();
            }
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Local_OAuth_Validation_Error : When_Getting_An_Access_Token
    {
        [TestCase(
            null,
            null,
            null,
            "client_credentials",
            HttpStatusCode.Unauthorized,
            "invalid_client",
            "Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body"
        )]
        [TestCase(
            null,
            "client",
            null,
            "client_credentials",
            HttpStatusCode.BadRequest,
            "invalid_request",
            "Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body"
        )]
        [TestCase(
            null,
            null,
            "secret",
            "client_credentials",
            HttpStatusCode.BadRequest,
            "invalid_request",
            "Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body"
        )]
        [TestCase(
            "Bearer token",
            "client",
            "secret",
            "client_credentials",
            HttpStatusCode.Unauthorized,
            "invalid_client",
            "The Authorization header must use the Basic scheme"
        )]
        [TestCase(
            "",
            "client",
            "secret",
            "client_credentials",
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The Authorization header must not be empty"
        )]
        [TestCase(
            "Basicxyz:s3cr3t-sentinel",
            null,
            null,
            "client_credentials",
            HttpStatusCode.BadRequest,
            "invalid_request",
            "Malformed Authorization header"
        )]
        [TestCase(
            "Basic abc:123",
            "client",
            "secret",
            "client_credentials",
            HttpStatusCode.BadRequest,
            "invalid_request",
            "Only one client authentication mechanism may be used"
        )]
        [TestCase(
            null,
            "client",
            "secret",
            null,
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The grant_type request parameter must be supplied"
        )]
        [TestCase(
            null,
            "client",
            "secret",
            "password",
            HttpStatusCode.BadRequest,
            "unsupported_grant_type",
            "Unsupported grant type"
        )]
        public async Task It_returns_the_exact_oauth_contract_without_sending_upstream(
            string? authHeader,
            string? clientId,
            string? clientSecret,
            string? grantType,
            HttpStatusCode expectedStatus,
            string expectedError,
            string expectedDescription
        )
        {
            await Act(
                authHeader,
                HttpStatusCode.OK,
                "{}",
                grantType!,
                new TokenRequestCredentials(clientId, clientSecret)
            );

            await AssertOAuthErrorResponse(expectedStatus, expectedError, expectedDescription);
            A.CallTo(() => _httpClient.SendAsync(A<HttpRequestMessage>._)).MustNotHaveHappened();
        }

        [TestCase(2049, 8)]
        [TestCase(8, 2049)]
        public async Task It_rejects_either_over_cap_body_credential(int clientIdLength, int secretLength)
        {
            await Act(
                null,
                HttpStatusCode.OK,
                "{}",
                "client_credentials",
                new TokenRequestCredentials(new string('i', clientIdLength), new string('s', secretLength))
            );

            await AssertOAuthErrorResponse(
                HttpStatusCode.BadRequest,
                "invalid_request",
                ExpectedOversizedBodyCredentialDetail
            );
            A.CallTo(() => _httpClient.SendAsync(A<HttpRequestMessage>._)).MustNotHaveHappened();
        }
    }

    /// <summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Token_Error_OAuth_Contract : When_Getting_An_Access_Token
    {
        private const string UpstreamDescription = "Provider rejected the request.";
        private const string CredentialFallback =
            "The upstream identity service rejected the request credentials.";
        private const string ProviderFallback = "The upstream identity service rejected the token request.";
        private const string GenericRateLimit =
            "The number of allowed requests has been exceeded. Retry the request later.";
        private const string RetryDescription =
            "The service is temporarily unable to handle the request. Retry the request later.";

        [TestCase("")]
        [TestCase("<html>provider failure</html>")]
        [TestCase("[]")]
        [TestCase("\"text\"")]
        [TestCase("7")]
        [TestCase("true")]
        [TestCase("null")]
        [TestCase("{}")]
        public async Task It_keeps_any_upstream_401_as_invalid_client(string upstreamBody)
        {
            await Act("Basic abc:123", HttpStatusCode.Unauthorized, upstreamBody, GrantType);

            await AssertOAuthErrorResponse(HttpStatusCode.Unauthorized, "invalid_client", CredentialFallback);
        }

        [Test]
        public async Task It_preserves_a_usable_401_description_but_uses_invalid_client()
        {
            await Act(
                "Basic abc:123",
                HttpStatusCode.Unauthorized,
                JsonSerializer.Serialize(
                    new { error = "server_error", error_description = UpstreamDescription }
                ),
                GrantType
            );

            await AssertOAuthErrorResponse(
                HttpStatusCode.Unauthorized,
                "invalid_client",
                UpstreamDescription
            );
        }

        [TestCase("invalid_request")]
        [TestCase("invalid_client")]
        [TestCase("invalid_grant")]
        [TestCase("unauthorized_client")]
        [TestCase("unsupported_grant_type")]
        [TestCase("invalid_scope")]
        public async Task It_preserves_each_recognized_provider_400_code(string error)
        {
            await Act(
                null,
                HttpStatusCode.BadRequest,
                JsonSerializer.Serialize(new { error, error_description = UpstreamDescription }),
                GrantType,
                new TokenRequestCredentials("client", "secret")
            );

            await AssertOAuthErrorResponse(HttpStatusCode.BadRequest, error, UpstreamDescription);
        }

        [TestCase("unknown")]
        [TestCase("INVALID_CLIENT")]
        [TestCase(" invalid_client ")]
        [TestCase("temporarily_unavailable")]
        public async Task It_replaces_unrecognized_provider_400_with_a_safe_gateway_error(string error)
        {
            await Act(
                null,
                HttpStatusCode.BadRequest,
                JsonSerializer.Serialize(new { error, error_description = UpstreamDescription }),
                GrantType,
                new TokenRequestCredentials("client", "secret")
            );

            await AssertOAuthErrorResponse(
                HttpStatusCode.BadGateway,
                "server_error",
                "The upstream identity service did not return a usable response. Contact your system administrator with the X-Correlation-ID response header, decoded once using UTF-8 percent decoding, which identifies the corresponding server log entry."
            );
        }

        [TestCase(true, HttpStatusCode.Unauthorized)]
        [TestCase(false, HttpStatusCode.BadRequest)]
        public async Task It_classifies_provider_invalid_client_from_the_original_caller_mechanism(
            bool callerUsedAuthorization,
            HttpStatusCode expectedStatus
        )
        {
            await Act(
                callerUsedAuthorization ? "Basic abc:123" : null,
                HttpStatusCode.BadRequest,
                JsonSerializer.Serialize(
                    new { error = "invalid_client", error_description = UpstreamDescription }
                ),
                GrantType,
                callerUsedAuthorization
                    ? TokenRequestCredentials.None
                    : new TokenRequestCredentials("client", "secret")
            );

            await AssertOAuthErrorResponse(expectedStatus, "invalid_client", UpstreamDescription);
        }

        [TestCase(" ", true)]
        [TestCase("!", true)]
        [TestCase("#", true)]
        [TestCase("[", true)]
        [TestCase("]", true)]
        [TestCase("~", true)]
        [TestCase("\u001f", false)]
        [TestCase("\"", false)]
        [TestCase("\\", false)]
        [TestCase("\u007f", false)]
        [TestCase("ñ", false)]
        public async Task It_validates_the_decoded_provider_description_characters(
            string description,
            bool isUsable
        )
        {
            await Act(
                null,
                HttpStatusCode.BadRequest,
                JsonSerializer.Serialize(new { error = "invalid_request", error_description = description }),
                GrantType,
                new TokenRequestCredentials("client", "secret")
            );

            await AssertOAuthErrorResponse(
                HttpStatusCode.BadRequest,
                "invalid_request",
                isUsable ? description : ProviderFallback
            );
        }

        [Test]
        public async Task It_preserves_a_recognized_400_when_its_description_has_invalid_unicode()
        {
            await Act(
                null,
                HttpStatusCode.BadRequest,
                """{"error":"invalid_request","error_description":"\uD800"}""",
                GrantType,
                new TokenRequestCredentials("client", "secret")
            );

            await AssertOAuthErrorResponse(HttpStatusCode.BadRequest, "invalid_request", ProviderFallback);
        }

        [Test]
        public async Task It_accepts_the_management_api_token_limit_urn_and_rebuilds_guidance()
        {
            string upstreamBody = CmsFailureResponse
                .ForTooManyTokens(7, "upstream-correlation-id")
                .ToJsonString()
                .Replace(
                    "urn:ed-fi:api:security:authentication:too-many-tokens",
                    "urn:ed-fi:management-api:security:authentication:too-many-tokens",
                    StringComparison.Ordinal
                );
            await Act("Basic abc:123", HttpStatusCode.TooManyRequests, upstreamBody, GrantType);

            await AssertOAuthErrorResponse(
                HttpStatusCode.TooManyRequests,
                "temporarily_unavailable",
                "Too many access tokens have been requested (limit is 7). Access tokens should be reused until they expire."
            );
        }

        [Test]
        public async Task It_maps_unrecognized_429_to_the_documented_extension()
        {
            await Act("Basic abc:123", HttpStatusCode.TooManyRequests, "<provider body>", GrantType);

            await AssertOAuthErrorResponse(
                HttpStatusCode.TooManyRequests,
                "temporarily_unavailable",
                GenericRateLimit
            );
        }

        [Test]
        public async Task It_maps_contention_to_temporary_unavailability_without_upstream_text()
        {
            await Act("Basic abc:123", HttpStatusCode.Conflict, "private contention detail", GrantType);

            await AssertOAuthErrorResponse(
                HttpStatusCode.ServiceUnavailable,
                "temporarily_unavailable",
                RetryDescription
            );
            (await _response.Content.ReadAsStringAsync()).Should().NotContain("private contention detail");
        }
    }

    /// <summary>
    /// Client authentication is checked before the grant type, as in the ODS/API, whose controller
    /// rejects missing credentials before its token provider looks at <c>grant_type</c>.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Missing_Credentials_And_An_Unsupported_Grant_Type : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act(null, HttpStatusCode.OK, "{}", "password", TokenRequestCredentials.None);
        }

        [Test]
        public void Then_It_Responds_With_Unauthorized()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task Then_The_Response_Carries_The_Invalid_Request_Error()
        {
            (await ResponseTitle()).Should().Be(ExpectedInvalidClientError);
        }
    }

    /// <summary>
    /// A JSON <c>null</c> grant type reaches the manager as <c>null</c> when a caller binds it
    /// without coalescing. It is unsupported, not a fault.
    /// </summary>
    [TestFixture("basic abc:123", null, null)]
    [TestFixture(null, "my-client", "my-secret")]
    [Parallelizable]
    public class Given_A_Null_Grant_Type(string? authHeader, string? clientId, string? clientSecret)
        : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act(
                authHeader,
                HttpStatusCode.OK,
                "{}",
                null!,
                new TokenRequestCredentials(clientId, clientSecret)
            );
        }

        [Test]
        public void Then_It_Responds_With_BadRequest()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Then_The_Response_Content_Says_The_Grant_Type_Is_Unsupported()
        {
            (await ResponseTitle()).Should().Be(ExpectedInvalidRequestError);
            (await ResponseDetail()).Should().Be("The grant_type request parameter must be supplied");
        }

        [Test]
        public void Then_Nothing_Is_Sent_Upstream()
        {
            _upstreamRequest.Should().BeNull();
        }
    }

    /// <summary>
    /// The ODS/API answers an <c>Authorization</c> header that is present but empty with
    /// <c>400 invalid_request</c>, before it looks at the body: splitting the empty value does not
    /// yield a scheme and a parameter. A whitespace-only value arrives trimmed to empty.
    /// </summary>
    [TestFixture("", null, null)]
    [TestFixture("", "my-client", "my-secret")]
    [TestFixture("   ", "my-client", "my-secret")]
    [Parallelizable]
    public class Given_An_Empty_Authorization_Header(
        string authHeader,
        string? clientId,
        string? clientSecret
    ) : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act(authHeader, new TokenRequestCredentials(clientId, clientSecret));
        }

        [Test]
        public void Then_It_Responds_With_BadRequest()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Then_The_Response_Content_Says_The_Header_Is_Empty()
        {
            (await ResponseDetail()).Should().Be(ExpectedEmptyAuthorizationDetail);
        }

        [Test]
        public void Then_Nothing_Is_Sent_Upstream()
        {
            _upstreamRequest.Should().BeNull();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Evaluation order. Each fixture below satisfies two adjacent rules at once, so swapping
    // those two checks changes the answer: non-Basic header, then mixed mechanisms, then missing
    // credentials, then the length cap, then the grant type. Duplicate form parameters come first
    // of all, and are pinned in TokenEndpointModuleTests, where they are detected.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Mixed mechanisms outrank the length cap: an over-cap body field beside a Basic header is a
    /// second mechanism, not an oversized credential.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Basic_Header_And_An_Over_Cap_Body_Client_Id : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act(
                "Basic abc:123",
                new TokenRequestCredentials(new string('i', ExpectedMaxBodyCredentialLength + 1), "s")
            );
        }

        [Test]
        public async Task Then_The_Mixed_Mechanism_Rule_Answers()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ResponseDetail()).Should().Be(ExpectedMixedMechanismsDetail);
        }
    }

    /// <summary>
    /// A partial pair is classified before the length cap, so its fixed missing-credential
    /// description wins even when the supplied value is over the cap.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Partial_Body_Pair_With_An_Over_Cap_Client_Id : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act(
                null,
                new TokenRequestCredentials(new string('i', ExpectedMaxBodyCredentialLength + 1), null)
            );
        }

        [Test]
        public async Task Then_The_Missing_Credentials_Rule_Answers()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ResponseTitle()).Should().Be(ExpectedInvalidRequestError);
            (await ResponseDetail()).Should().Be(ExpectedMissingCredentialsDetail);
        }
    }

    /// <summary>
    /// The length cap outranks the grant type.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Over_Cap_Body_Client_Id_And_An_Unsupported_Grant_Type : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act(
                null,
                HttpStatusCode.OK,
                "{}",
                "password",
                new TokenRequestCredentials(new string('i', ExpectedMaxBodyCredentialLength + 1), "s")
            );
        }

        [Test]
        public async Task Then_The_Length_Cap_Answers()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ResponseDetail()).Should().Be(ExpectedOversizedBodyCredentialDetail);
        }
    }

    /// <summary>
    /// A non-Basic header outranks the grant type.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Bearer_Header_And_An_Unsupported_Grant_Type : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act("Bearer x", HttpStatusCode.OK, "{}", "password", TokenRequestCredentials.None);
        }

        [Test]
        public async Task Then_The_Non_Basic_Rule_Answers()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ResponseTitle()).Should().Be(ExpectedInvalidClientError);
        }
    }

    /// <summary>
    /// Mixed mechanisms outrank the grant type.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Basic_Header_Body_Credentials_And_An_Unsupported_Grant_Type
        : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act(
                "Basic abc:123",
                HttpStatusCode.OK,
                "{}",
                "password",
                new TokenRequestCredentials("my-client", "my-secret")
            );
        }

        [Test]
        public async Task Then_The_Mixed_Mechanism_Rule_Answers()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ResponseDetail()).Should().Be(ExpectedMixedMechanismsDetail);
        }
    }

    private const string ExpectedEmptyAuthorizationDetail = "The Authorization header must not be empty";

    /// <summary>
    /// The cap applies to each body value on its own, at exactly
    /// <see cref="ExpectedMaxBodyCredentialLength"/> characters.
    /// </summary>
    [TestFixture(ExpectedMaxBodyCredentialLength, 8)]
    [TestFixture(8, ExpectedMaxBodyCredentialLength)]
    [Parallelizable]
    public class Given_Body_Credentials_At_The_Length_Cap(int clientIdLength, int clientSecretLength)
        : When_Getting_An_Access_Token
    {
        [SetUp]
        public async Task SetUp()
        {
            await Act(
                null,
                new TokenRequestCredentials(
                    new string('i', clientIdLength),
                    new string('s', clientSecretLength)
                )
            );
        }

        [Test]
        public void Then_It_Responds_With_Ok()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void Then_The_Request_Is_Sent_Upstream()
        {
            _upstreamRequest.Should().NotBeNull();
        }
    }

    [TestFixture(ExpectedMaxBodyCredentialLength + 1, 8)]
    [TestFixture(8, ExpectedMaxBodyCredentialLength + 1)]
    [Parallelizable]
    public class Given_Body_Credentials_Over_The_Length_Cap(int clientIdLength, int clientSecretLength)
        : When_Getting_An_Access_Token
    {
        private string _clientId = default!;
        private string _clientSecret = default!;

        [SetUp]
        public async Task SetUp()
        {
            _clientId = new string('i', clientIdLength);
            _clientSecret = new string('s', clientSecretLength);
            await Act(null, new TokenRequestCredentials(_clientId, _clientSecret));
        }

        [Test]
        public void Then_It_Responds_With_BadRequest()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void Then_The_Response_ContentType_Is_Problem_JSON()
        {
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        }

        [Test]
        public async Task Then_The_Response_Content_States_The_Cap()
        {
            (await ResponseDetail()).Should().Be(ExpectedOversizedBodyCredentialDetail);
        }

        [Test]
        public async Task Then_The_Response_Does_Not_Echo_Either_Value()
        {
            string content = await _response.Content.ReadAsStringAsync();
            content.Should().NotContain(_clientId).And.NotContain(_clientSecret);
        }

        [Test]
        public void Then_Nothing_Is_Sent_Upstream()
        {
            _upstreamRequest.Should().BeNull();
        }
    }

    /// <summary>
    /// Spelled out rather than read from the production constant, for the same reason as
    /// <see cref="ExpectedGatewayDetail"/>.
    /// </summary>
    private const int ExpectedMaxBodyCredentialLength = 2048;

    private const string ExpectedOversizedBodyCredentialDetail =
        "client_id and client_secret must each be at most 2048 characters";

    private const string ExpectedMissingCredentialsDetail =
        "Client credentials must be supplied in an Authorization: Basic header or as client_id and "
        + "client_secret in the request body";

    private const string ExpectedNonBasicDetail = "The Authorization header must use the Basic scheme";

    private const string ExpectedInvalidRequestError = "invalid_request";

    private const string ExpectedInvalidClientError = "invalid_client";

    private const string ExpectedMixedMechanismsDetail =
        "Only one client authentication mechanism may be used";

    /// <summary>
    /// Body credentials never reach the log, in clear or as the Basic value built from them, on
    /// either the success path or the 502 path that logs the upstream response.
    /// </summary>
    [TestFixture(HttpStatusCode.OK)]
    [TestFixture(HttpStatusCode.InternalServerError)]
    [TestFixture(HttpStatusCode.Unauthorized)]
    [Parallelizable]
    public class Given_Body_Credentials_And_A_Recording_Logger(HttpStatusCode upstreamStatus)
    {
        private const string ClientSecret = "s3cr3t-sentinel";

        private RecordingLogger<OAuthManager> _logger = default!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<OAuthManager>();

            var upstreamResponse = A.Fake<HttpResponseMessage>();
            upstreamResponse.StatusCode = upstreamStatus;
            upstreamResponse.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var httpClient = A.Fake<IHttpClientWrapper>();
            A.CallTo(() => httpClient.SendAsync(A<HttpRequestMessage>._))
                .ReturnsLazily(() => upstreamResponse);

            await new OAuthManager(_logger).GetAccessTokenAsync(
                httpClient,
                "client_credentials",
                null,
                new TokenRequestCredentials("my-client", ClientSecret),
                "http://example.com/oauth/token",
                new TraceId(CorrelationId)
            );
        }

        [Test]
        public void It_never_logs_the_secret_or_the_built_header()
        {
            string builtCredentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"my-client:{ClientSecret}")
            );

            // Guards against passing vacuously on a run that logged nothing at all.
            _logger.Records.Should().NotBeEmpty();
            foreach (LogRecord record in _logger.Records)
            {
                record.Message.Should().NotContain(ClientSecret).And.NotContain(builtCredentials);
                record.Exception.Should().BeNull();
            }
        }
    }

    private const string ExpectedMalformedAuthorizationDetail = "Malformed Authorization header";

    /// <summary>
    /// A header that passes the lenient "contains basic" test but that <c>HttpHeaders.Add</c>
    /// cannot parse, such as two Authorization headers the frontend joined with a comma. Adding it
    /// throws a FormatException whose message quotes the whole header, so the request is answered
    /// 400 before anything is sent, and neither the header nor the exception may reach the log.
    /// </summary>
    [TestFixture("Basicxyz:s3cr3t-sentinel")]
    [TestFixture("Basic abc,Basic s3cr3t-sentinel")]
    [TestFixture("Basic abc,s3cr3t-sentinel")]
    [TestFixture("Basic \"s3cr3t-sentinel")]
    [TestFixture("Basic abc\ns3cr3t-sentinel")]
    [Parallelizable]
    public class Given_An_Unparseable_Basic_Authorization_Header(string authHeader)
    {
        private const string Sentinel = "s3cr3t-sentinel";

        private RecordingLogger<OAuthManager> _logger = default!;
        private IHttpClientWrapper _httpClient = default!;
        private HttpResponseMessage _response = default!;
        private Exception? _thrown;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<OAuthManager>();
            _httpClient = A.Fake<IHttpClientWrapper>();

            try
            {
                _response = await new OAuthManager(_logger).GetAccessTokenAsync(
                    _httpClient,
                    "client_credentials",
                    authHeader,
                    TokenRequestCredentials.None,
                    "http://example.com/oauth/token",
                    new TraceId(CorrelationId)
                );
            }
            catch (Exception ex)
            {
                _thrown = ex;
            }
        }

        [Test]
        public void It_does_not_throw()
        {
            _thrown.Should().BeNull();
        }

        [Test]
        public async Task It_responds_400_with_the_malformed_header_detail()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
            body["error"]!.GetValue<string>().Should().Be(ExpectedInvalidRequestError);
            body["error_description"]!.GetValue<string>().Should().Be(ExpectedMalformedAuthorizationDetail);
            body.ToJsonString().Should().NotContain(Sentinel);
        }

        [Test]
        public void It_sends_nothing_upstream()
        {
            A.CallTo(() => _httpClient.SendAsync(A<HttpRequestMessage>._)).MustNotHaveHappened();
        }

        [Test]
        public void It_never_logs_the_header_or_an_exception()
        {
            foreach (LogRecord record in _logger.Records)
            {
                record.Message.Should().NotContain(Sentinel);
                record.Exception.Should().BeNull();
            }
        }
    }

    /// <summary>
    /// FR-LOG-6 regression guard for the upstream-error (non-200, non-401) branch. OAuthManager
    /// produces a 502's log record and its response body from separate expressions in two
    /// places — this branch and the <c>catch</c> branch below it — so both must pass
    /// <c>traceId.Value</c>: passing the TraceId record struct makes its synthesized ToString
    /// render "TraceId { Value = ... }", so the logged value differs from the correlationId the
    /// client reads even though it still *contains* it. That is why the assertion below is exact
    /// equality rather than Contain. Only this branch is covered here; the <c>catch</c> branch is
    /// already correct.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Identity_Service_Error_With_An_Upstream_Style_Correlation_Id
    {
        /// <summary>
        /// Already normalized under the correlation-ID allowlist, but holds characters the stricter
        /// Method/Path allowlist would strip (+ = { }), so a value transformed or decorated a second
        /// time anywhere on this path is distinguishable from one carried through verbatim.
        /// </summary>
        private const string UpstreamCorrelationId = "3f2b+aQ==/{svc}";

        private RecordingLogger<OAuthManager> _logger = default!;
        private HttpResponseMessage _response = default!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<OAuthManager>();

            var upstreamResponse = A.Fake<HttpResponseMessage>();
            upstreamResponse.StatusCode = HttpStatusCode.InternalServerError;
            upstreamResponse.Content = new StringContent(
                """{ "error": "server_error" }""",
                Encoding.UTF8,
                "application/json"
            );

            var httpClient = A.Fake<IHttpClientWrapper>();
            A.CallTo(() => httpClient.SendAsync(A<HttpRequestMessage>._))
                .ReturnsLazily(() => upstreamResponse);

            _response = await new OAuthManager(_logger).GetAccessTokenAsync(
                httpClient,
                "client_credentials",
                "basic abc:123",
                TokenRequestCredentials.None,
                "http://example.com/oauth/token",
                new TraceId(UpstreamCorrelationId)
            );
        }

        [Test]
        public void It_logs_the_correlation_id_value_and_not_the_TraceId_struct()
        {
            LogRecord warning = _logger.Records.Single(record => record.Level == LogLevel.Warning);
            warning.Properties["TraceId"].Should().Be(UpstreamCorrelationId);
        }

        [Test]
        public void It_puts_the_identical_correlation_id_in_the_gateway_error_body()
        {
            _response
                .Headers.GetValues(OAuthErrorResponse.CorrelationHeaderName)
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(Uri.EscapeDataString(UpstreamCorrelationId));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Security regression guards for the unauthenticated /oauth/token endpoint.
    //
    // Both the 502 branch and the 401 branch used to place the upstream identity service's raw
    // response body into the client-facing problem+json `detail`. /oauth/token takes no
    // credentials, so that made the upstream's error taxonomy, internal hostnames, realm names and
    // any stack trace it emits readable by any caller, in a field whose length only the upstream
    // bounds. The upstream content now reaches the log only - sanitized and length-bounded - and
    // the client correlates via `correlationId`.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The exact client-facing <c>detail</c> of every 502 OAuthManager produces. Spelled out here
    /// rather than read from the production constant so that changing the message has to be a
    /// deliberate two-sided edit.
    /// </summary>
    private const string ExpectedGatewayDetail =
        "The upstream identity service did not return a usable response. Contact your system "
        + "administrator with the X-Correlation-ID response header, decoded once using UTF-8 "
        + "percent decoding, which identifies the corresponding server log entry.";

    /// <summary>
    /// The exact client-facing <c>detail</c> of a 401 whose upstream body carried no
    /// <c>error_description</c>. Spelled out for the same reason as
    /// <see cref="ExpectedGatewayDetail"/>.
    /// </summary>
    private const string ExpectedUnauthorizedFallbackDetail =
        "The upstream identity service rejected the request credentials.";

    private const int ExpectedMaxLoggedContentLength = 2048;

    private const string ExpectedTruncationSuffix = "...[truncated]";

    private const string CorrelationId = "trace-id";

    /// <summary>
    /// Drives OAuthManager against a faked upstream that answers with the given status and body.
    /// </summary>
    private static async Task<(
        HttpResponseMessage Response,
        RecordingLogger<OAuthManager> Logger
    )> UpstreamResponds(HttpStatusCode upstreamStatus, string upstreamBody) =>
        await UpstreamResponds(
            upstreamStatus,
            new StringContent(upstreamBody, Encoding.UTF8, "application/json")
        );

    private static async Task<(
        HttpResponseMessage Response,
        RecordingLogger<OAuthManager> Logger
    )> UpstreamResponds(HttpStatusCode upstreamStatus, HttpContent upstreamContent)
    {
        var logger = new RecordingLogger<OAuthManager>();

        var upstreamResponse = A.Fake<HttpResponseMessage>();
        upstreamResponse.StatusCode = upstreamStatus;
        upstreamResponse.Content = upstreamContent;

        var httpClient = A.Fake<IHttpClientWrapper>();
        A.CallTo(() => httpClient.SendAsync(A<HttpRequestMessage>._)).ReturnsLazily(() => upstreamResponse);

        HttpResponseMessage response = await new OAuthManager(logger).GetAccessTokenAsync(
            httpClient,
            "client_credentials",
            "basic abc:123",
            TokenRequestCredentials.None,
            "http://example.com/oauth/token",
            new TraceId(CorrelationId)
        );

        return (response, logger);
    }

    /// <summary>
    /// The value bound to the <c>{Content}</c> template parameter of the single warning the
    /// upstream-error branch emits.
    /// </summary>
    private static string LoggedUpstreamContent(RecordingLogger<OAuthManager> logger) =>
        (string)logger.Records.Single(record => record.Level == LogLevel.Warning).Properties["Content"]!;

    /// <summary>
    /// The single information event the 401 fallback emits, or <c>null</c> when it did not fire.
    /// Every call also writes two routine information events ("GetAccessTokenAsync" and
    /// "Forwarding token request"); the fallback event is told apart from those by its
    /// <c>{StandardFields}</c> parameter, which only it binds.
    /// </summary>
    private static LogRecord? DiscardedUnauthorizedDetailRecord(RecordingLogger<OAuthManager> logger) =>
        logger.Records.SingleOrDefault(record =>
            record.Level == LogLevel.Information && record.Properties.ContainsKey("StandardFields")
        );

    /// <summary>
    /// The marker the fallback event uses for a group of fields that turned out to be empty.
    /// Spelled out here rather than read from the production constant, for the same reason as
    /// <see cref="ExpectedGatewayDetail"/>.
    /// </summary>
    private const string ExpectedNoFieldsMarker = "(none)";

    [TestFixture(HttpStatusCode.Unauthorized, false)]
    [TestFixture(HttpStatusCode.TooManyRequests, false)]
    [TestFixture(HttpStatusCode.Unauthorized, true)]
    [TestFixture(HttpStatusCode.TooManyRequests, true)]
    public class Given_An_Upstream_Error_With_Unsupported_Charset(
        HttpStatusCode upstreamStatus,
        bool interruptedBody
    )
    {
        private HttpResponseMessage _response = default!;
        private RecordingLogger<OAuthManager> _logger = default!;
        private JsonObject _body = default!;

        [SetUp]
        public async Task Setup()
        {
            HttpContent content = interruptedBody
                ? new InterruptedProviderContent()
                : new ByteArrayContent(Encoding.UTF8.GetBytes("provider-content-sentinel"));
            content.Headers.ContentType = new("application/json") { CharSet = "not-a-real-charset" };
            (_response, _logger) = await UpstreamResponds(upstreamStatus, content);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [Test]
        public void It_preserves_the_status_only_when_the_body_was_received() =>
            _response.StatusCode.Should().Be(interruptedBody ? HttpStatusCode.BadGateway : upstreamStatus);

        [Test]
        public void It_returns_the_exact_safe_oauth_contract()
        {
            _body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            _body["error"]!
                .GetValue<string>()
                .Should()
                .Be(
                    interruptedBody ? "server_error"
                    : upstreamStatus is HttpStatusCode.Unauthorized ? "invalid_client"
                    : "temporarily_unavailable"
                );
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(
                    interruptedBody ? ExpectedGatewayDetail
                    : upstreamStatus is HttpStatusCode.Unauthorized ? ExpectedUnauthorizedFallbackDetail
                    : "The number of allowed requests has been exceeded. Retry the request later."
                );
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            _response.Headers.GetValues("X-Correlation-ID").Should().Equal(CorrelationId);
            _response
                .Headers.WwwAuthenticate.ToString()
                .Should()
                .Be(
                    !interruptedBody && upstreamStatus is HttpStatusCode.Unauthorized
                        ? "Basic realm=\"EdFi.DataManagementService\""
                        : string.Empty
                );
        }

        [Test]
        public void It_distinguishes_unusable_content_from_a_transport_failure()
        {
            if (interruptedBody)
            {
                _logger.Records.Should().ContainSingle(record => record.Level == LogLevel.Error);
                _logger
                    .Records.Should()
                    .NotContain(record => record.Properties.ContainsKey("StandardFields"));
                return;
            }

            LogRecord summary = _logger.Records.Single(record =>
                record.Properties.ContainsKey("StandardFields")
            );
            summary
                .Level.Should()
                .Be(upstreamStatus is HttpStatusCode.Unauthorized ? LogLevel.Information : LogLevel.Warning);
            summary.Properties["TraceId"].Should().Be(CorrelationId);
            summary.Properties["StandardFields"].Should().Be(ExpectedNoFieldsMarker);
            summary.Properties["OtherFieldNames"].Should().Be(ExpectedNoFieldsMarker);
            summary.Exception.Should().BeNull();
            _logger.Records.Should().NotContain(record => record.Level == LogLevel.Error);
        }
    }

    private sealed class InterruptedProviderContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("Provider body read interrupted"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [TestFixture("""{"error":"invalid_client","error":"invalid_client"}""")]
    [TestFixture("""{"error_description":"first","error_description":"second"}""")]
    [TestFixture("""{"error_description":"Do not relay this","extra":"secret","extra":"secret"}""")]
    public class Given_An_Upstream_401_With_Duplicate_Root_Members(string upstreamBody)
    {
        private HttpResponseMessage _response = default!;
        private RecordingLogger<OAuthManager> _logger = default!;
        private JsonObject _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (_response, _logger) = await UpstreamResponds(HttpStatusCode.Unauthorized, upstreamBody);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [Test]
        public void It_preserves_the_unauthorized_status() =>
            _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        [Test]
        public void It_uses_only_the_fixed_oauth_fallback()
        {
            _body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            _body["error"]!.GetValue<string>().Should().Be("invalid_client");
            _body["error_description"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        }

        [Test]
        public void It_delivers_the_dms_challenge_and_correlation()
        {
            _response
                .Headers.WwwAuthenticate.ToString()
                .Should()
                .Be("Basic realm=\"EdFi.DataManagementService\"");
            _response.Headers.GetValues("X-Correlation-ID").Should().Equal(CorrelationId);
        }

        [Test]
        public void It_records_only_the_empty_selected_summary()
        {
            LogRecord record = DiscardedUnauthorizedDetailRecord(_logger)!;
            record.Should().NotBeNull();
            record.Properties["TraceId"].Should().Be(CorrelationId);
            record.Properties["StandardFields"].Should().Be(ExpectedNoFieldsMarker);
            record.Properties["OtherFieldNames"].Should().Be(ExpectedNoFieldsMarker);
            record.Exception.Should().BeNull();
        }

        [Test]
        public void It_does_not_misreport_a_transport_failure() =>
            _logger.Records.Should().NotContain(record => record.Level == LogLevel.Error);
    }

    [TestFixture("""{"error":"invalid_client","error_description":"\uD800"}""")]
    [TestFixture("""{"error":"\uDC00"}""")]
    [TestFixture("""{"\uD800":"withheld"}""")]
    public class Given_An_Upstream_401_With_Invalid_Unicode(string upstreamBody)
    {
        private HttpResponseMessage _response = default!;
        private RecordingLogger<OAuthManager> _logger = default!;

        [SetUp]
        public async Task Setup() =>
            (_response, _logger) = await UpstreamResponds(HttpStatusCode.Unauthorized, upstreamBody);

        [Test]
        public async Task It_preserves_the_fixed_unauthorized_contract()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            JsonObject body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
            body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be("invalid_client");
            body["error_description"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
            _response
                .Headers.WwwAuthenticate.ToString()
                .Should()
                .Be("Basic realm=\"EdFi.DataManagementService\"");
            _response.Headers.GetValues("X-Correlation-ID").Should().Equal(CorrelationId);
        }

        [Test]
        public void It_records_a_safe_fallback_without_a_transport_error()
        {
            DiscardedUnauthorizedDetailRecord(_logger).Should().NotBeNull();
            _logger.Records.Should().NotContain(record => record.Level == LogLevel.Error);
        }
    }

    /// <summary>
    /// The marker the fallback event uses for a standard field that arrived as a JSON object or
    /// array instead of the string RFC 6749 section 5.2 defines.
    /// </summary>
    private const string ExpectedMalformedMarker = "(malformed)";

    /// <summary>
    /// The marker the fallback event uses for a standard field reported as present but never by
    /// value.
    /// </summary>
    private const string ExpectedWithheldMarker = "(withheld)";

    /// <summary>
    /// The number of non-standard field names the fallback event will name before it starts
    /// counting instead.
    /// </summary>
    private const int ExpectedMaxLoggedFieldNames = 20;

    /// <summary>
    /// The fallback event's fully rendered text, template and every bound parameter together.
    /// </summary>
    /// <remarks>
    /// The value-absence assertions go through this rather than through a single property. A
    /// leak that reached the log by some parameter the test did not think to check would still
    /// be a leak, and asserting on one property at a time cannot see it.
    /// </remarks>
    private static string RenderedFallbackEvent(RecordingLogger<OAuthManager> logger)
    {
        LogRecord? record = DiscardedUnauthorizedDetailRecord(logger);
        record.Should().NotBeNull();
        return record!.Message;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Error_Body_Carrying_Internal_Details
    {
        private const string InternalHostSentinel = "idp-internal-07.corp.local";

        private const string StackFrameSentinel = "at Acme.Identity.TokenEndpoint.Issue(TokenRequest)";

        private static readonly string _upstreamBody = $$"""
            { "error": "server_error", "error_description": "upstream connect to {{InternalHostSentinel}}:8443 failed", "trace": "{{StackFrameSentinel}}" }
            """;

        private HttpResponseMessage _response = default!;
        private RecordingLogger<OAuthManager> _logger = default!;
        private string _rawResponseBody = default!;
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (_response, _logger) = await UpstreamResponds(HttpStatusCode.InternalServerError, _upstreamBody);
            _rawResponseBody = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_rawResponseBody)!;
        }

        [Test]
        public void It_responds_with_bad_gateway()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        [Test]
        public void It_keeps_the_json_content_type()
        {
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        }

        [Test]
        public void It_does_not_disclose_the_upstream_internal_hostname()
        {
            _rawResponseBody.Should().NotContain(InternalHostSentinel);
        }

        [Test]
        public void It_does_not_disclose_the_upstream_stack_frame()
        {
            _rawResponseBody.Should().NotContain(StackFrameSentinel);
        }

        [Test]
        public void It_returns_the_fixed_non_revealing_detail()
        {
            _body["error_description"]!.GetValue<string>().Should().Be(ExpectedGatewayDetail);
        }

        [Test]
        public void It_keeps_the_title()
        {
            _body["error"]!.GetValue<string>().Should().Be("server_error");
        }

        [Test]
        public void It_keeps_the_correlation_id()
        {
            _response
                .Headers.GetValues(OAuthErrorResponse.CorrelationHeaderName)
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(Uri.EscapeDataString(CorrelationId));
        }

        [Test]
        public void It_still_records_the_upstream_content_on_the_log_entry()
        {
            // The detail is not discarded - it moves to the log, which is what makes
            // correlationId a usable substitute for echoing the body back.
            LoggedUpstreamContent(_logger).Should().Contain(InternalHostSentinel);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Error_Body_Containing_Line_Breaks
    {
        /// <summary>
        /// A pretty-printed identity provider error: real CR/LF and tabs, which would otherwise
        /// spread one log event over four lines of a line-oriented log.
        /// </summary>
        private const string UpstreamBody =
            "{\r\n\t\"error\": \"server_error\",\r\n\t\"error_description\": \"realm not found\"\r\n}";

        private const string ExpectedLoggedContent =
            "{\"error\": \"server_error\",\"error_description\": \"realm not found\"}";

        private string _loggedContent = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.InternalServerError,
                UpstreamBody
            );
            _loggedContent = LoggedUpstreamContent(logger);
        }

        [Test]
        public void It_does_not_log_a_raw_carriage_return()
        {
            _loggedContent.Should().NotContain("\r");
        }

        [Test]
        public void It_does_not_log_a_raw_line_feed()
        {
            _loggedContent.Should().NotContain("\n");
        }

        [Test]
        public void It_logs_exactly_the_sanitized_body()
        {
            _loggedContent.Should().Be(ExpectedLoggedContent);
        }

        [Test]
        public void It_keeps_the_json_punctuation_that_makes_the_entry_diagnostic()
        {
            // The strict Method/Path allowlist would have stripped every quote, brace and comma
            // here, leaving a run of bare words; the free-text sanitizer keeps them.
            _loggedContent.Should().Contain("\"error_description\": \"realm not found\"");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Over_Long_Upstream_Error_Body
    {
        private const int UpstreamBodyLength = 5000;

        private static readonly string _upstreamBody = new('a', UpstreamBodyLength);

        private string _loggedContent = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.InternalServerError,
                _upstreamBody
            );
            _loggedContent = LoggedUpstreamContent(logger);
        }

        [Test]
        public void It_caps_the_logged_content_at_the_documented_bound()
        {
            _loggedContent
                .Should()
                .HaveLength(ExpectedMaxLoggedContentLength + ExpectedTruncationSuffix.Length);
        }

        [Test]
        public void It_keeps_the_leading_characters_of_the_body()
        {
            _loggedContent.Should().StartWith(new string('a', ExpectedMaxLoggedContentLength));
        }

        [Test]
        public void It_marks_the_value_as_truncated()
        {
            _loggedContent.Should().EndWith(ExpectedTruncationSuffix);
        }
    }

    /// <summary>
    /// U+1F600 GRINNING FACE, built from its code point rather than written as a literal so the
    /// test data cannot be altered by a re-encoding of this file. It survives free-text
    /// sanitization intact - an emoji is neither a control nor a format character - which is what
    /// makes it available to be broken in half by the truncation that follows.
    /// </summary>
    private static readonly string _emoji = char.ConvertFromUtf32(0x1F600);

    /// <summary>
    /// Whether <paramref name="value"/> is malformed UTF-16: a high surrogate not followed by a
    /// low one, or a low surrogate not preceded by a high one. Written out rather than expressed
    /// as a comparison against an expected string so the assertion states the property at issue -
    /// a log sink disagreeing with another about what the upstream service said - rather than
    /// merely a value.
    /// </summary>
    private static bool ContainsUnpairedSurrogate(string value)
    {
        int index = 0;

        while (index < value.Length)
        {
            char unit = value[index];

            if (char.IsHighSurrogate(unit))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                // A well-formed pair is two code units, and its low half must not then be
                // examined on its own - it would read as an orphan.
                index += 2;
                continue;
            }

            if (char.IsLowSurrogate(unit))
            {
                return true;
            }

            index++;
        }

        return false;
    }

    /// <summary>
    /// The cap counts UTF-16 code units, so a body of 2047 ASCII characters followed by an astral
    /// character puts the cut between the halves of a well-formed surrogate pair. Taking the first
    /// 2048 units verbatim would leave a lone high surrogate in a value the sanitizer had already
    /// made well-formed - the defect the rest of this branch exists to prevent, reintroduced by
    /// the truncation three files away from the rule that forbids it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Truncation_Boundary_That_Splits_An_Astral_Character
    {
        private static readonly string _leadingText = new('a', ExpectedMaxLoggedContentLength - 1);

        private string _loggedContent = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.InternalServerError,
                _leadingText + _emoji
            );
            _loggedContent = LoggedUpstreamContent(logger);
        }

        [Test]
        public void It_does_not_log_an_unpaired_surrogate()
        {
            ContainsUnpairedSurrogate(_loggedContent).Should().BeFalse();
        }

        [Test]
        public void It_drops_the_split_character_whole_rather_than_keeping_half_of_it()
        {
            _loggedContent.Should().Be(_leadingText + ExpectedTruncationSuffix);
        }

        [Test]
        public void It_still_marks_the_value_as_truncated()
        {
            // Backing the boundary off must not cost the operator the one signal that anything
            // was cut at all.
            _loggedContent.Should().EndWith(ExpectedTruncationSuffix);
        }
    }

    /// <summary>
    /// The complement of the fixture above, in both directions: a cut landing on ordinary BMP text
    /// must still spend the whole budget, and a cut landing immediately after a complete surrogate
    /// pair must not back off either - the pair is intact, and giving up a code unit for it would
    /// be an off-by-one in the ordinary case rather than a fix for the astral one.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Truncation_Boundary_That_Does_Not_Split_A_Character
    {
        private static readonly string _bmpBody = new('a', ExpectedMaxLoggedContentLength + 500);

        /// <summary>
        /// The emoji occupies the last two code units of the budget, so the cut falls after its
        /// low half rather than between the two.
        /// </summary>
        private static readonly string _bodyEndingOnACompletePair =
            new string('a', ExpectedMaxLoggedContentLength - 2) + _emoji + new string('b', 500);

        private string _loggedBmpContent = default!;
        private string _loggedPairContent = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, RecordingLogger<OAuthManager> bmpLogger) = await UpstreamResponds(
                HttpStatusCode.InternalServerError,
                _bmpBody
            );
            _loggedBmpContent = LoggedUpstreamContent(bmpLogger);

            (_, RecordingLogger<OAuthManager> pairLogger) = await UpstreamResponds(
                HttpStatusCode.InternalServerError,
                _bodyEndingOnACompletePair
            );
            _loggedPairContent = LoggedUpstreamContent(pairLogger);
        }

        [Test]
        public void It_spends_the_whole_budget_on_ordinary_text()
        {
            _loggedBmpContent
                .Should()
                .Be(new string('a', ExpectedMaxLoggedContentLength) + ExpectedTruncationSuffix);
        }

        [Test]
        public void It_spends_the_whole_budget_when_the_boundary_follows_a_complete_pair()
        {
            _loggedPairContent
                .Should()
                .Be(new string('a', ExpectedMaxLoggedContentLength - 2) + _emoji + ExpectedTruncationSuffix);
        }

        [Test]
        public void It_keeps_the_astral_character_that_fits_within_the_budget()
        {
            _loggedPairContent.Should().Contain(_emoji);
            ContainsUnpairedSurrogate(_loggedPairContent).Should().BeFalse();
        }
    }

    /// <summary>
    /// Order-of-operations guard. Sanitizing and then truncating is not interchangeable with
    /// truncating and then sanitizing: an upstream body padded with control characters spends the
    /// whole budget on characters the sanitizer removes under the second order, so the diagnostic
    /// text after the padding never reaches the log even though the logged value is far shorter
    /// than the cap.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Error_Body_Padded_With_Control_Characters
    {
        private const string DiagnosticTail = "{\"error\":\"realm-config-missing\"}";

        private static readonly string _upstreamBody =
            new string((char)7, ExpectedMaxLoggedContentLength + 500) + DiagnosticTail;

        private string _loggedContent = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.InternalServerError,
                _upstreamBody
            );
            _loggedContent = LoggedUpstreamContent(logger);
        }

        [Test]
        public void It_logs_the_diagnostic_text_that_followed_the_padding()
        {
            _loggedContent.Should().Be(DiagnosticTail);
        }

        [Test]
        public void It_does_not_report_the_value_as_truncated()
        {
            _loggedContent.Should().NotContain(ExpectedTruncationSuffix);
        }
    }

    /// <summary>
    /// The two 502 branches - one reached from a non-200/non-401 upstream response, the other from
    /// a thrown send - build their bodies from separate expressions, so they can drift apart. A
    /// client cannot tell which one it hit, and must not be able to.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Both_Bad_Gateway_Branches
    {
        private JsonNode _fromUpstreamResponse = default!;
        private JsonNode _fromThrownException = default!;

        /// <summary>
        /// Drives OAuthManager against a faked upstream whose send throws, exercising the
        /// <c>catch</c> branch's 502.
        /// </summary>
        private static async Task<HttpResponseMessage> UpstreamThrows(string message)
        {
            var httpClient = A.Fake<IHttpClientWrapper>();
            A.CallTo(() => httpClient.SendAsync(A<HttpRequestMessage>._))
                .ThrowsAsync(() => new InvalidOperationException(message));

            return await new OAuthManager(new RecordingLogger<OAuthManager>()).GetAccessTokenAsync(
                httpClient,
                "client_credentials",
                "basic abc:123",
                TokenRequestCredentials.None,
                "http://example.com/oauth/token",
                new TraceId(CorrelationId)
            );
        }

        [SetUp]
        public async Task Setup()
        {
            (HttpResponseMessage responsePath, _) = await UpstreamResponds(
                HttpStatusCode.InternalServerError,
                """{ "error": "server_error" }"""
            );
            _fromUpstreamResponse = JsonNode.Parse(await responsePath.Content.ReadAsStringAsync())!;

            HttpResponseMessage exceptionPath = await UpstreamThrows("this is a problem");
            _fromThrownException = JsonNode.Parse(await exceptionPath.Content.ReadAsStringAsync())!;
        }

        [Test]
        public void It_produces_identical_bodies()
        {
            JsonNode.DeepEquals(_fromUpstreamResponse, _fromThrownException).Should().BeTrue();
        }

        [Test]
        public void It_uses_the_fixed_detail_on_the_response_path()
        {
            _fromUpstreamResponse["error_description"]!.GetValue<string>().Should().Be(ExpectedGatewayDetail);
        }

        [Test]
        public void It_uses_the_fixed_detail_on_the_exception_path()
        {
            _fromThrownException["error_description"]!.GetValue<string>().Should().Be(ExpectedGatewayDetail);
        }

        [Test]
        public void It_does_not_leak_the_exception_message_on_the_exception_path()
        {
            _fromThrownException.ToJsonString().Should().NotContain("this is a problem");
        }
    }

    /// <summary>
    /// The 401 branch's fallback carried the same defect as the 502 branch: <c>errorDescription</c>
    /// was initialized to the raw upstream body, so any upstream 401 that parsed as a JSON object
    /// but did not happen to carry an <c>error_description</c> was echoed to the caller verbatim.
    /// The two parsed OAuth fields still pass through - see
    /// <c>Given_the_Client_Credentials_Are_Invalid</c> - because those are the OAuth 2.0 error
    /// contract; an arbitrary body that merely failed to contain them is not.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_Without_An_Error_Description
    {
        private const string InternalRealmSentinel = "https://idp-internal-07.corp.local/realms/edfi";

        /// <summary>
        /// A non-standard field: the whole of why the upstream rejected the request, and not part
        /// of the OAuth 2.0 error contract, so it is exactly what withholding the body from the
        /// client costs an operator.
        /// </summary>
        private const string ReasonSentinel = "client disabled";

        private static readonly string _upstreamBody = $$"""
            { "error": "invalid_client", "reason": "{{ReasonSentinel}}", "realm": "{{InternalRealmSentinel}}" }
            """;

        private HttpResponseMessage _response = default!;
        private string _rawResponseBody = default!;
        private JsonNode _body = default!;
        private LogRecord? _logged;
        private string _rendered = default!;

        [SetUp]
        public async Task Setup()
        {
            (_response, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                _upstreamBody
            );
            _rawResponseBody = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_rawResponseBody)!;
            _logged = DiscardedUnauthorizedDetailRecord(logger);
            _rendered = RenderedFallbackEvent(logger);
        }

        [Test]
        public void It_responds_with_unauthorized()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public void It_does_not_disclose_the_raw_upstream_body()
        {
            _rawResponseBody.Should().NotContain(InternalRealmSentinel);
        }

        [Test]
        public void It_returns_the_fixed_fallback_detail()
        {
            _body["error_description"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
        }

        [Test]
        public void It_still_surfaces_the_parsed_oauth_error_as_the_title()
        {
            _body["error"]!.GetValue<string>().Should().Be("invalid_client");
        }

        [Test]
        public void It_withholds_the_reason_from_the_client()
        {
            // The paired half of It_records_the_discarded_reason_on_the_log below: the same
            // sentinel that must reach an operator must not reach an unauthenticated caller.
            _rawResponseBody.Should().NotContain(ReasonSentinel);
        }

        [Test]
        public void It_emits_a_single_event_recording_the_discarded_body()
        {
            _logged.Should().NotBeNull();
        }

        [Test]
        public void It_names_the_discarded_non_standard_fields_on_the_log()
        {
            // The diagnostic that survives the no-payload rule: searching by correlation ID
            // finds a request whose upstream rejection carried a `reason` and a `realm`, which is
            // the handle for pursuing it in the identity provider's own logs.
            //
            // Exact equality rather than Contain, so this also pins that the summary holds
            // nothing beyond the two names and that they appear in the upstream document's own
            // order.
            _logged.Should().NotBeNull();
            _logged!.Properties["OtherFieldNames"]!.ToString().Should().Be("reason, realm");
        }

        [Test]
        public void It_does_not_record_the_value_of_any_non_standard_field()
        {
            // `reason` and `realm` are not part of the RFC 6749 section 5.2 error contract, so
            // their contents are arbitrary upstream payload and must not be persisted -
            // docs/LOGGING.md forbids response bodies in Information-level logs outright, and
            // neither sanitizing nor bounding redacts anything.
            //
            // Against the rendered event rather than one property, because a leak by way of some
            // other parameter is still a leak.
            _rendered.Should().NotContain(ReasonSentinel);
            _rendered.Should().NotContain(InternalRealmSentinel);
        }

        [Test]
        public void It_records_the_standard_oauth_error_by_value()
        {
            // `error` is allowlisted, and logging it discloses nothing that is not disclosed
            // already: It_still_surfaces_the_parsed_oauth_error_as_the_title above shows the same
            // string going to the unauthenticated caller as the problem-details `title`.
            _logged.Should().NotBeNull();
            _logged!.Properties["StandardFields"]!.ToString().Should().Be("error=invalid_client");
        }

        [Test]
        public void It_records_the_discarded_body_against_the_correlation_id()
        {
            // Without this the entry exists but is unreachable: correlationId is the only handle
            // the client is given, so an event that does not carry it cannot be found.
            _logged.Should().NotBeNull();
            _logged!.Properties["TraceId"].Should().Be(CorrelationId);
        }

        [Test]
        public void It_records_the_discarded_body_at_information()
        {
            // Deliberately not Warning: a rejected credential is routine and client-triggered, so
            // it must not reach the stream an operator watches for conditions needing action.
            // Deliberately not Debug either - DMS ships at Information, so a Debug event would not
            // be emitted at all in a default deployment.
            _logged.Should().NotBeNull();
            _logged!.Level.Should().Be(LogLevel.Information);
        }

        [Test]
        public void It_binds_the_summaries_as_data_rather_than_as_a_message_template()
        {
            // Both summaries are built from a JSON body and can therefore contain braces.
            // Interpolating either into the template would have Microsoft.Extensions.Logging
            // read those braces as property holes: the event would gain holes named after
            // fragments of the upstream content and lose the named properties entirely. Exactly
            // these three properties, and no others, is what proves they were passed as
            // parameters.
            _logged.Should().NotBeNull();
            _logged!
                .Properties.Should()
                .ContainKeys("TraceId", "StandardFields", "OtherFieldNames")
                .And.HaveCount(3);
        }
    }

    /// <summary>
    /// The three RFC 6749 section 5.2 error response members are the allowlist, but membership
    /// only makes a field eligible. <c>error</c> is logged by value, <c>error_uri</c> is reported
    /// by presence alone, and a well-formed <c>error_description</c> cannot reach this path at
    /// all, since its presence is what suppresses the event.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_With_Only_Standard_Fields
    {
        private const string ErrorUri = "https://idp.example.com/docs/errors/invalid_client";

        private static readonly string _upstreamBody = $$"""
            { "error": "invalid_client", "error_uri": "{{ErrorUri}}" }
            """;

        private LogRecord _logged = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                _upstreamBody
            );
            _logged = DiscardedUnauthorizedDetailRecord(logger)!;
        }

        [Test]
        public void It_records_every_standard_field_in_upstream_order()
        {
            _logged.Properties["StandardFields"]!
                .ToString()
                .Should()
                .Be($"error=invalid_client, error_uri={ExpectedWithheldMarker}");
        }

        [Test]
        public void It_does_not_record_the_error_uri_itself()
        {
            // Even this benign documentation URI is withheld. The rule is on the field, not on
            // the individual value, because nothing at this point can tell a documentation link
            // from one carrying a credential.
            _logged.Message.Should().NotContain(ErrorUri);
        }

        [Test]
        public void It_reports_the_absence_of_non_standard_fields_rather_than_an_empty_string()
        {
            // An empty string reads as a logging defect; the marker says the body genuinely
            // carried nothing beyond the OAuth contract.
            _logged.Properties["OtherFieldNames"]!
                .ToString()
                .Should()
                .Be(ExpectedNoFieldsMarker);
        }
    }

    /// <summary>
    /// The count of names is attacker-influenceable independently of their length: ten thousand
    /// one-character names sit far inside the 2048-character bound and would still produce an
    /// unreadable log line. The cap is on the count, and applying it is reported rather than
    /// silent.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_With_More_Fields_Than_The_Cap
    {
        private const int NonStandardFieldCount = 5000;

        private const string LastFieldValueSentinel = "svalue-4999-should-never-be-logged";

        private static readonly string _upstreamBody = BuildBody();

        private static string BuildBody()
        {
            var obj = new JsonObject { ["error"] = "invalid_client" };
            for (int i = 0; i < NonStandardFieldCount; i++)
            {
                obj[$"f{i}"] = i == NonStandardFieldCount - 1 ? LastFieldValueSentinel : $"value-{i}";
            }
            return obj.ToJsonString();
        }

        private LogRecord _logged = default!;
        private string _rendered = default!;
        private string _otherFieldNames = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                _upstreamBody
            );
            _logged = DiscardedUnauthorizedDetailRecord(logger)!;
            _rendered = RenderedFallbackEvent(logger);
            _otherFieldNames = _logged.Properties["OtherFieldNames"]!.ToString()!;
        }

        [Test]
        public void It_names_no_more_fields_than_the_cap_allows()
        {
            // The cap plus the one overflow marker.
            _otherFieldNames
                .Split(", ", StringSplitOptions.None)
                .Should()
                .HaveCount(ExpectedMaxLoggedFieldNames + 1);
        }

        [Test]
        public void It_keeps_the_first_names_the_upstream_sent()
        {
            _otherFieldNames.Should().StartWith("f0, f1, f2,");
            _otherFieldNames.Should().Contain($"f{ExpectedMaxLoggedFieldNames - 1}, ");
        }

        [Test]
        public void It_reports_how_many_names_it_left_out()
        {
            // Visible truncation: without the count an operator cannot tell a body with twenty
            // fields from one with five thousand, and the second is a signal in its own right.
            _otherFieldNames
                .Should()
                .EndWith($"...[{NonStandardFieldCount - ExpectedMaxLoggedFieldNames} more]");
        }

        [Test]
        public void It_records_no_value_from_any_of_the_capped_fields()
        {
            _rendered.Should().NotContain(LastFieldValueSentinel);
            _rendered.Should().NotContain("value-0");
        }

        [Test]
        public void It_keeps_the_whole_event_bounded()
        {
            _rendered.Length.Should().BeLessThan(2 * ExpectedMaxLoggedContentLength);
        }

        [Test]
        public void It_still_records_the_standard_error_by_value()
        {
            _logged.Properties["StandardFields"]!.ToString().Should().Be("error=invalid_client");
        }
    }

    /// <summary>
    /// A field name is as attacker-influenceable as a field value, so the names the event records
    /// go through the same sanitizer the values do. An upstream that returns a member named with
    /// a CRLF and a forged log prefix must not be able to write a second line into the log.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_Whose_Field_Name_Carries_Control_Characters
    {
        private static readonly string _upstreamBody = new JsonObject
        {
            ["error"] = "invalid_client",
            ["rea\r\nson"] = "withheld anyway",
        }.ToJsonString();

        private string _otherFieldNames = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                _upstreamBody
            );
            _otherFieldNames = DiscardedUnauthorizedDetailRecord(logger)!.Properties[
                "OtherFieldNames"
            ]!.ToString()!;
        }

        [Test]
        public void It_sanitizes_the_field_name_before_logging_it()
        {
            _otherFieldNames.Should().NotContain("\r").And.NotContain("\n");
        }

        [Test]
        public void It_still_reports_the_rest_of_the_name()
        {
            _otherFieldNames.Should().Be("reason");
        }
    }

    /// <summary>
    /// The complement of <see cref="Given_An_Unauthorized_Upstream_Body_Without_An_Error_Description"/>.
    /// A 401 is routine and client-triggered - every mistyped client secret produces one - so the
    /// upstream body must reach the log only on the fallback, where information is actually being
    /// discarded. Logging it on every 401 would be volume noise proportional to bad-credential
    /// traffic and would widen the log-injection surface for nothing, since here the client already
    /// receives the description.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_Carrying_An_Error_Description
    {
        private const string UpstreamDescription = "Invalid client or Invalid client credentials";

        private static readonly string _upstreamBody = $$"""
            { "error": "invalid_client", "error_description": "{{UpstreamDescription}}" }
            """;

        private RecordingLogger<OAuthManager> _logger = default!;
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (HttpResponseMessage response, _logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                _upstreamBody
            );
            _body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }

        [Test]
        public void It_forwards_the_upstream_description_to_the_client()
        {
            _body["error_description"]!.GetValue<string>().Should().Be(UpstreamDescription);
        }

        [Test]
        public void It_does_not_record_the_upstream_body_on_the_log()
        {
            DiscardedUnauthorizedDetailRecord(_logger).Should().BeNull();
        }
    }

    /// <summary>
    /// A standard field name does not establish that its contents are safe: an <c>error_uri</c>
    /// that arrived as an object nesting a credential, where <c>JsonNode.ToString()</c> would
    /// have serialized the subtree into the event and neither sanitizing nor bounding would have
    /// removed any of it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_Whose_Error_Uri_Nests_A_Secret
    {
        private const string NestedSecretSentinel = "nested-client-secret-never-logged";

        // Three-brace interpolation: the body's own closing `}}` would otherwise be read as the
        // end of an interpolation hole.
        private static readonly string _upstreamBody = $$$"""
            {"error":"invalid_client","error_uri":{"client_secret":"{{{NestedSecretSentinel}}}"}}
            """;

        private LogRecord _logged = default!;
        private string _rendered = default!;
        private string _rawResponseBody = default!;

        [SetUp]
        public async Task Setup()
        {
            (HttpResponseMessage response, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                _upstreamBody
            );
            _rawResponseBody = await response.Content.ReadAsStringAsync();
            _logged = DiscardedUnauthorizedDetailRecord(logger)!;
            _rendered = RenderedFallbackEvent(logger);
        }

        [Test]
        public void It_does_not_record_the_nested_secret()
        {
            _rendered.Should().NotContain(NestedSecretSentinel);
        }

        [Test]
        public void It_does_not_disclose_the_nested_secret_to_the_client()
        {
            _rawResponseBody.Should().NotContain(NestedSecretSentinel);
        }

        [Test]
        public void It_reports_the_error_uri_as_present_and_withheld()
        {
            _logged.Properties["StandardFields"]!
                .ToString()
                .Should()
                .Be($"error=invalid_client, error_uri={ExpectedWithheldMarker}");
        }

        [Test]
        public void It_still_surfaces_the_well_formed_error_as_the_title()
        {
            // The malformed member is contained: a sibling that did arrive as a string is still
            // forwarded, so the fix costs nothing on the fields that are well formed.
            JsonNode.Parse(_rawResponseBody)!["error"]!
                .GetValue<string>()
                .Should()
                .Be("invalid_client");
        }
    }

    /// <summary>
    /// RFC 6749 section 5.2 defines <c>error_uri</c> as a URI, and a URI carries credentials in
    /// its query and userinfo components. The field is reported by presence alone, so a
    /// well-formed URI string is withheld exactly as a malformed container is.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_Whose_Error_Uri_Carries_Query_Credentials
    {
        private const string QuerySecretSentinel = "uri-query-secret-never-logged";

        private const string UserInfoSentinel = "uri-userinfo-secret-never-logged";

        private static readonly string _errorUri =
            $"https://svc:{UserInfoSentinel}@idp.example.com/docs?client_secret={QuerySecretSentinel}";

        private static readonly string _upstreamBody = $$"""
            {"error":"invalid_client","error_uri":"{{_errorUri}}"}
            """;

        private LogRecord _logged = default!;
        private string _rendered = default!;
        private string _rawResponseBody = default!;

        [SetUp]
        public async Task Setup()
        {
            (HttpResponseMessage response, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                _upstreamBody
            );
            _rawResponseBody = await response.Content.ReadAsStringAsync();
            _logged = DiscardedUnauthorizedDetailRecord(logger)!;
            _rendered = RenderedFallbackEvent(logger);
        }

        [Test]
        public void It_does_not_record_the_credential_in_the_query_string()
        {
            _rendered.Should().NotContain(QuerySecretSentinel);
        }

        [Test]
        public void It_does_not_record_the_credential_in_the_userinfo_component()
        {
            _rendered.Should().NotContain(UserInfoSentinel);
        }

        [Test]
        public void It_does_not_record_any_part_of_the_uri()
        {
            // Not only the credential-bearing components. Nothing decides per-URI which part is
            // sensitive, so the whole value is withheld, host included.
            _rendered.Should().NotContain("idp.example.com");
        }

        [Test]
        public void It_does_not_disclose_the_uri_to_the_client()
        {
            _rawResponseBody.Should().NotContain(QuerySecretSentinel);
            _rawResponseBody.Should().NotContain(UserInfoSentinel);
        }

        [Test]
        public void It_still_reports_that_an_error_uri_was_present()
        {
            // Presence is the diagnostic that survives: the operator knows to look for the
            // upstream's own record of this request rather than assuming it sent nothing.
            _logged.Properties["StandardFields"]!
                .ToString()
                .Should()
                .Contain($"error_uri={ExpectedWithheldMarker}");
        }
    }

    /// <summary>
    /// Both container kinds, on the two standard fields whose values would otherwise be
    /// forwarded. <c>error</c> reaches the client as the problem-details <c>title</c> and
    /// <c>error_description</c> as its <c>detail</c>, so a container here is a disclosure to an
    /// unauthenticated caller as well as to the log.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_Whose_Standard_Fields_Arrived_As_Containers
    {
        private const string NestedSecretSentinel = "object-nested-secret-never-disclosed";

        private const string ArrayElementSentinel = "array-nested-secret-never-disclosed";

        private static readonly string _upstreamBody = $$"""
            {"error":{"client_secret":"{{NestedSecretSentinel}}"},"error_description":["{{ArrayElementSentinel}}"]}
            """;

        private LogRecord _logged = default!;
        private string _rendered = default!;
        private string _rawResponseBody = default!;
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (HttpResponseMessage response, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                _upstreamBody
            );
            _rawResponseBody = await response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_rawResponseBody)!;
            _logged = DiscardedUnauthorizedDetailRecord(logger)!;
            _rendered = RenderedFallbackEvent(logger);
        }

        [Test]
        public void It_does_not_disclose_the_nested_object_to_the_client()
        {
            _rawResponseBody.Should().NotContain(NestedSecretSentinel);
        }

        [Test]
        public void It_does_not_disclose_the_nested_array_element_to_the_client()
        {
            _rawResponseBody.Should().NotContain(ArrayElementSentinel);
        }

        [Test]
        public void It_does_not_record_either_container_on_the_log()
        {
            _rendered.Should().NotContain(NestedSecretSentinel);
            _rendered.Should().NotContain(ArrayElementSentinel);
        }

        [Test]
        public void It_falls_back_to_the_fixed_title_rather_than_the_container()
        {
            _body["error"]!.GetValue<string>().Should().Be("invalid_client");
        }

        [Test]
        public void It_falls_back_to_the_fixed_detail_rather_than_the_container()
        {
            _body["error_description"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
        }

        [Test]
        public void It_reports_both_fields_as_present_and_malformed()
        {
            _logged.Properties["StandardFields"]!
                .ToString()
                .Should()
                .Be($"error={ExpectedMalformedMarker}, error_description={ExpectedMalformedMarker}");
        }

        [Test]
        public void It_treats_a_malformed_description_as_no_description_at_all()
        {
            // The event has to fire. A container in `error_description` is not a description,
            // so the client gets the fixed detail and the correlation ID still has to lead to a
            // log entry saying what the upstream actually sent.
            _logged.Should().NotBeNull();
        }
    }

    /// <summary>
    /// A JSON null is reported distinctly from a container, so an operator can tell an upstream
    /// that sent nothing from one that sent something unloggable. It also cannot be dereferenced:
    /// <c>JsonObject</c> surfaces it as a null node.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_Whose_Error_Is_A_Json_Null
    {
        private const string UpstreamBody = """
            {"error":null,"reason":"client disabled"}
            """;

        private HttpResponseMessage _response = default!;
        private LogRecord _logged = default!;

        [SetUp]
        public async Task Setup()
        {
            (_response, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                UpstreamBody
            );
            _logged = DiscardedUnauthorizedDetailRecord(logger)!;
        }

        [Test]
        public void It_still_responds_with_unauthorized()
        {
            // Rather than the 502 a dereference of the null node used to produce by way of the
            // enclosing catch.
            _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public void It_records_the_null_distinctly_from_a_malformed_container()
        {
            _logged.Properties["StandardFields"]!.ToString().Should().Be("error=null");
        }
    }

    /// <summary>
    /// A JSON null in <c>error_description</c> is not a description, so it does not suppress the
    /// fallback event the way a well-formed one does. The field therefore does reach the summary,
    /// by way of its allowlist entry, and is reported there as a null.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unauthorized_Upstream_Body_Whose_Error_Description_Is_A_Json_Null
    {
        private const string UpstreamBody = """
            {"error":"invalid_client","error_description":null}
            """;

        private HttpResponseMessage _response = default!;
        private LogRecord _logged = default!;
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (_response, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                UpstreamBody
            );
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
            _logged = DiscardedUnauthorizedDetailRecord(logger)!;
        }

        [Test]
        public void It_treats_the_null_as_no_description_at_all()
        {
            _body["error_description"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
        }

        [Test]
        public void It_records_the_error_description_by_name_and_value_rather_than_by_name_alone()
        {
            // The allowlist entry is what puts `error_description` in StandardFields rather than
            // among the names-only members, and the null is what it contributed.
            _logged.Properties["StandardFields"]!
                .ToString()
                .Should()
                .Be("error=invalid_client, error_description=null");
        }
    }

    /// <summary>
    /// The canonical CMS token-limit body, and the pieces tests vary to walk away from it.
    /// </summary>
    /// <remarks>
    /// Hand-built so the deviation fixtures can walk away from the canonical shape in ways CMS's
    /// formatter never would. Agreement with what CMS actually emits is proven separately, by
    /// <see cref="Given_A_Token_Limit_Rejection_Built_By_The_Configuration_Service"/>.
    /// </remarks>
    private static class TooManyTokensUpstream
    {
        /// <summary>The type an earlier Configuration Service used.</summary>
        public const string LegacyType = "urn:ed-fi:api:security:authentication:too-many-tokens";

        /// <summary>The Management API type the Configuration Service uses.</summary>
        public const string ManagementApiType =
            "urn:ed-fi:management-api:security:authentication:too-many-tokens";

        public static string MessageWithLimit(string limit) =>
            $"Too many access tokens have been requested (limit is {limit}). Access tokens should "
            + "be reused until they expire.";

        public static string BodyWithMessage(string message, string type) =>
            $$"""
                {
                  "detail": "The caller has authenticated too many times in too short of a time period.",
                  "type": "{{type}}",
                  "title": "Too Many Tokens",
                  "status": 429,
                  "correlationId": "upstream-correlation-id",
                  "validationErrors": {},
                  "errors": [{{System.Text.Json.JsonSerializer.Serialize(message)}}]
                }
                """;

        public static string BodyWithLimit(string limit, string type) =>
            BodyWithMessage(MessageWithLimit(limit), type);
    }

    /// <summary>
    /// Drives an upstream 429 and returns the parsed response body, plus the logger, because the
    /// fallback branch's warning is the only record a deviating body leaves anywhere.
    /// </summary>
    private static async Task<(
        HttpStatusCode Status,
        string Raw,
        JsonNode Body,
        RecordingLogger<OAuthManager> Logger
    )> UpstreamRejectsWith(string upstreamBody)
    {
        (HttpResponseMessage response, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
            HttpStatusCode.TooManyRequests,
            upstreamBody
        );
        string raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, raw, JsonNode.Parse(raw)!, logger);
    }

    /// <summary>
    /// The generic rate-limit contract every deviating upstream 429 falls back to. It is still a
    /// 429 because the upstream <em>status</em> is trustworthy even when its body is not, and it
    /// is the honest answer when the 429 came from a gateway limiter rather than the token limit.
    /// </summary>
    private static void ShouldBeTheGenericRateLimitContract(JsonNode body)
    {
        body.AsObject()
            .Select(property => property.Key)
            .Should()
            .BeEquivalentTo("error", "error_description");
        body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
        body["error_description"]!.GetValue<string>().Should().Be(OAuthErrorResponse.RateLimitDescription);
    }

    [TestFixture(TooManyTokensUpstream.LegacyType)]
    [TestFixture(TooManyTokensUpstream.ManagementApiType)]
    [Parallelizable]
    public class Given_An_Upstream_Token_Limit_Rejection(string upstreamType)
    {
        private HttpStatusCode _status;
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (_status, _, _body, _) = await UpstreamRejectsWith(
                TooManyTokensUpstream.BodyWithLimit("5", upstreamType)
            );
        }

        [Test]
        public void It_responds_with_too_many_requests()
        {
            _status.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_uses_the_documented_rate_limit_extension_code()
        {
            _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
        }

        [Test]
        public void It_carries_the_rebuilt_guidance_in_the_description()
        {
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(TooManyTokensUpstream.MessageWithLimit("5"));
        }

        [Test]
        public void It_emits_only_the_oauth_error_members()
        {
            _body
                .AsObject()
                .Select(property => property.Key)
                .Should()
                .BeEquivalentTo("error", "error_description");
        }
    }

    /// <summary>
    /// A limit other than the default, so the parser is shown to read the number rather than to
    /// recognize one canonical sentence.
    /// </summary>
    [TestFixture(TooManyTokensUpstream.LegacyType)]
    [TestFixture(TooManyTokensUpstream.ManagementApiType)]
    [Parallelizable]
    public class Given_An_Upstream_Token_Limit_Rejection_With_A_Different_Limit(string upstreamType)
    {
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, _, _body, _) = await UpstreamRejectsWith(
                TooManyTokensUpstream.BodyWithLimit("37", upstreamType)
            );
        }

        [Test]
        public void It_reconstructs_the_message_with_that_limit()
        {
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(TooManyTokensUpstream.MessageWithLimit("37"));
        }
    }

    /// <summary>
    /// The cross-service contract: the body comes from the Configuration Service's own formatter
    /// rather than from <see cref="TooManyTokensUpstream"/>, so rewording either the CMS message or
    /// the DMS parser without the other fails here. A limit other than the default shows the number
    /// was read from the body.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Token_Limit_Rejection_Built_By_The_Configuration_Service
    {
        private HttpStatusCode _status;
        private JsonNode _body = default!;
        private RecordingLogger<OAuthManager> _logger = default!;
        private string _upstreamType = default!;

        [SetUp]
        public async Task Setup()
        {
            string upstreamBody = CmsFailureResponse
                .ForTooManyTokens(7, "upstream-correlation-id")
                .ToJsonString();
            _upstreamType = JsonNode.Parse(upstreamBody)!["type"]!.GetValue<string>();
            (_status, _, _body, _logger) = await UpstreamRejectsWith(upstreamBody);
        }

        // Shows this fixture exercises the Management API type the Configuration Service emits,
        // not the legacy one the hand-built fixtures also cover.
        [Test]
        public void It_is_fed_the_management_api_type()
        {
            _upstreamType.Should().Be(TooManyTokensUpstream.ManagementApiType);
        }

        [Test]
        public void It_responds_with_too_many_requests()
        {
            _status.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_uses_the_documented_rate_limit_extension_code()
        {
            _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
        }

        [Test]
        public void It_reconstructs_the_message_with_the_configured_limit()
        {
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(TooManyTokensUpstream.MessageWithLimit("7"));
        }

        [Test]
        public void It_does_not_log_the_unrecognized_body_warning()
        {
            _logger.Records.Should().NotContain(record => record.Level == LogLevel.Warning);
        }
    }

    /// <summary>
    /// The test that proves local reconstruction rather than assuming it: the upstream message is
    /// canonical in shape but carries extra text, so a parser that relayed the entry verbatim
    /// would leak it. Run under both accepted upstream types, so neither one opens a path for
    /// upstream text to reach the caller or the log.
    /// </summary>
    [TestFixture(TooManyTokensUpstream.LegacyType)]
    [TestFixture(TooManyTokensUpstream.ManagementApiType)]
    [Parallelizable]
    public class Given_An_Upstream_Token_Limit_Rejection_Embedding_A_Secret(string upstreamType)
    {
        private const string Sentinel = "idp-internal-07.corp.local/secret=hunter2";

        private string _raw = default!;
        private JsonNode _body = default!;
        private RecordingLogger<OAuthManager> _logger = default!;

        [SetUp]
        public async Task Setup()
        {
            string tampered = TooManyTokensUpstream.MessageWithLimit("5") + " " + Sentinel;
            (_, _raw, _body, _logger) = await UpstreamRejectsWith(
                TooManyTokensUpstream.BodyWithMessage(tampered, upstreamType)
            );
        }

        [Test]
        public void It_does_not_disclose_the_upstream_text()
        {
            _raw.Should().NotContain(Sentinel);
        }

        // The log receives a selected summary, never the body
        // (reference/adr-oauth-upstream-error-disclosure.md), so the text withheld from the
        // caller is withheld from the log as well. The whole rendered event is checked, so a
        // leak through any bound parameter would fail this.
        [Test]
        public void It_does_not_write_the_upstream_text_to_the_log()
        {
            Unrecognized429Record(_logger).Message.Should().NotContain(Sentinel);
        }

        // What the operator gets instead: the member names, under the trace id the client was
        // given, so the correlationId in the response still leads to this event.
        [Test]
        public void It_records_the_member_names_under_the_trace_id()
        {
            LogRecord record = Unrecognized429Record(_logger);
            record.Properties["TraceId"].Should().Be(CorrelationId);
            ((string)record.Properties["OtherFieldNames"]!).Should().Contain("errors").And.Contain("type");
        }

        // A trailing suffix makes the message non-canonical, so the limit is not believed either.
        [Test]
        public void It_falls_back_to_the_generic_rate_limit_contract()
        {
            ShouldBeTheGenericRateLimitContract(_body);
        }
    }

    /// <summary>
    /// The Configuration Service's 409 for a token grant that timed out on, or deadlocked over, a
    /// database lock. Retrying is the answer, so the proxy must not report it as a 502.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Lock_Contention_Conflict
    {
        private const string UpstreamDetail =
            "Unable to process the request due to a concurrent modification. Retry the request.";

        private HttpResponseMessage _response = default!;
        private string _raw = default!;
        private JsonNode _body = default!;
        private RecordingLogger<OAuthManager> _logger = default!;

        [SetUp]
        public async Task Setup()
        {
            string upstreamBody = $$"""
                {
                  "detail": "{{UpstreamDetail}}",
                  "type": "urn:ed-fi:api:conflict",
                  "title": "Conflict",
                  "status": 409,
                  "correlationId": "upstream-correlation-id",
                  "validationErrors": {},
                  "errors": []
                }
                """;
            (_response, _logger) = await UpstreamResponds(HttpStatusCode.Conflict, upstreamBody);
            _raw = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_raw)!;
        }

        [Test]
        public void It_responds_with_service_unavailable()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        [Test]
        public void It_uses_the_documented_temporary_unavailability_code()
        {
            _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
        }

        [Test]
        public void It_carries_the_dms_trace_id_not_the_upstream_one()
        {
            _response
                .Headers.GetValues(OAuthErrorResponse.CorrelationHeaderName)
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(Uri.EscapeDataString(CorrelationId));
        }

        [Test]
        public void It_does_not_disclose_the_upstream_text()
        {
            _raw.Should().NotContain("concurrent modification");
        }

        [Test]
        public void It_does_not_take_the_bad_gateway_branch()
        {
            _logger.Records.Should().NotContain(record => record.Level == LogLevel.Warning);
        }

        [Test]
        public void It_records_the_contention_under_the_trace_id_without_the_body()
        {
            LogRecord record = _logger.Records.Single(record =>
                record.Level == LogLevel.Information && record.Message.Contains("lock contention")
            );
            record.Properties["TraceId"].Should().Be(CorrelationId);
            record.Message.Should().NotContain("concurrent modification");
        }
    }

    /// <summary>
    /// The single warning the 429 fallback emits, told apart from the 502 branch's warning by the
    /// <c>{OtherFieldNames}</c> parameter, which only it binds.
    /// </summary>
    private static LogRecord Unrecognized429Record(RecordingLogger<OAuthManager> logger) =>
        logger.Records.Single(record =>
            record.Level == LogLevel.Warning && record.Properties.ContainsKey("OtherFieldNames")
        );

    /// <summary>
    /// The fallback branch's warning carries a summary, and the summary is held to the same
    /// treatment as the 401 fallback's: values of non-standard members never reach it, member
    /// names are newline-sanitized, and a body that is not a JSON object leaves nothing to name.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unrecognized_429_Body_Reaching_The_Log
    {
        [Test]
        public async Task It_withholds_the_values_of_non_standard_members()
        {
            (_, _, _, RecordingLogger<OAuthManager> logger) = await UpstreamRejectsWith(
                """{ "type": "urn:ed-fi:api:too-many-requests", "status": 429, "errors": ["upstream-value-never-logged"] }"""
            );

            LogRecord record = Unrecognized429Record(logger);
            record.Message.Should().NotContain("upstream-value-never-logged");
            record.Properties["OtherFieldNames"].Should().Be("type, status, errors");
        }

        [Test]
        public async Task It_does_not_log_raw_newlines_from_a_member_name()
        {
            (_, _, _, RecordingLogger<OAuthManager> logger) = await UpstreamRejectsWith(
                """{ "first\r\nWARN forged second line": 1 }"""
            );

            string names = (string)Unrecognized429Record(logger).Properties["OtherFieldNames"]!;
            names.Should().NotContain("\r");
            names.Should().NotContain("\n");
            names.Should().Contain("forged second line");
        }

        [Test]
        public async Task It_names_nothing_for_a_body_that_is_not_a_json_object()
        {
            (_, _, _, RecordingLogger<OAuthManager> logger) = await UpstreamRejectsWith(
                new string('a', 5000)
            );

            LogRecord record = Unrecognized429Record(logger);
            record.Properties["StandardFields"].Should().Be(ExpectedNoFieldsMarker);
            record.Properties["OtherFieldNames"].Should().Be(ExpectedNoFieldsMarker);
            record.Message.Should().NotContain("aaaa");
        }
    }

    /// <summary>
    /// Every way an upstream 429 body can deviate from the canonical one. All of them answer 429
    /// with the generic rate-limit contract rather than a partially believed token-limit body.
    /// Each deviation that keeps a recognizable type runs once per accepted upstream type, and the
    /// near-miss types show that only the two exact values are accepted.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_429_Whose_Body_Deviates
    {
        private const string CanonicalMessage =
            "Too many access tokens have been requested (limit is 5). Access tokens should be reused until they expire.";

        private static readonly string[] _acceptedTypes =
        [
            TooManyTokensUpstream.LegacyType,
            TooManyTokensUpstream.ManagementApiType,
        ];

        private static string TypeLabel(string type) =>
            type == TooManyTokensUpstream.LegacyType ? "legacy type" : "management-api type";

        // The minimal body the parser accepts, with "type" supplied as raw JSON so that a
        // non-string type can be expressed.
        private static string BodyWithTypeJson(string typeJson) =>
            $$"""
                {
                  "type": {{typeJson}},
                  "title": "Too Many Tokens",
                  "status": 429,
                  "errors": ["{{CanonicalMessage}}"]
                }
                """;

        private static string BodyOfType(string type) =>
            BodyWithTypeJson(System.Text.Json.JsonSerializer.Serialize(type));

        private static string BodyWithStatus(string statusJson, string type) =>
            $$"""
                {
                  "type": "{{type}}",
                  "status": {{statusJson}},
                  "errors": ["{{CanonicalMessage}}"]
                }
                """;

        private static string BodyWithErrors(string errorsJson, string type) =>
            $$"""
                {
                  "type": "{{type}}",
                  "status": 429,
                  "errors": {{errorsJson}}
                }
                """;

        // The canonical body of the given type with one top-level member removed.
        private static string BodyWithout(string member, string type)
        {
            JsonObject body = JsonNode.Parse(TooManyTokensUpstream.BodyWithLimit("5", type))!.AsObject();
            body.Remove(member);
            return body.ToJsonString();
        }

        private static TestCaseData Case(string upstreamBody, string name) =>
            new TestCaseData(upstreamBody).SetName(name);

        private static IEnumerable<TestCaseData> DeviatingBodies()
        {
            yield return new TestCaseData("not json at all").SetName("Unparseable body");
            yield return new TestCaseData("""{"type":"\uD800"}""").SetName(
                "Invalid Unicode type retains generic 429"
            );
            yield return new TestCaseData(
                """{"type":"urn:ed-fi:management-api:security:authentication:too-many-tokens","status":429,"errors":["\uD800"]}"""
            ).SetName("Invalid Unicode limit message retains generic 429");
            yield return new TestCaseData("""{"error":"slow_down","extra":1,"extra":2}""").SetName(
                "Duplicate root names retain generic 429"
            );
            yield return new TestCaseData("\"a bare string\"").SetName("Body is not a JSON object");
            yield return new TestCaseData("null").SetName("Body is the JSON literal null");
            yield return new TestCaseData(BodyOfType("urn:ed-fi:api:too-many-requests")).SetName(
                "Valid problem details of a different type"
            );

            foreach (string type in _acceptedTypes)
            {
                string under = $" under the {TypeLabel(type)}";
                string canonicalMessageJson = System.Text.Json.JsonSerializer.Serialize(CanonicalMessage);

                yield return Case(BodyWithStatus("503", type), "Body declares another status" + under);
                yield return Case(BodyWithout("status", type), "Status member absent" + under);
                yield return Case(BodyWithStatus("null", type), "Status is the JSON literal null" + under);
                yield return Case(BodyWithStatus("\"429\"", type), "Status is a string" + under);
                yield return Case(BodyWithStatus("429.5", type), "Status is not an integer" + under);
                yield return Case(BodyWithout("errors", type), "Errors member absent" + under);
                yield return Case(BodyWithErrors("null", type), "Errors is the JSON literal null" + under);
                yield return Case(BodyWithErrors(canonicalMessageJson, type), "Errors is a string" + under);
                yield return Case(
                    BodyWithErrors($$"""{ "0": {{canonicalMessageJson}} }""", type),
                    "Errors is an object" + under
                );
                yield return Case(BodyWithErrors("[]", type), "Empty errors array" + under);
                yield return Case(
                    BodyWithErrors($"""[{canonicalMessageJson}, "and another"]""", type),
                    "Multi-element errors array" + under
                );
                yield return Case(BodyWithErrors("[5]", type), "Errors entry is not a string" + under);
                yield return Case(
                    BodyWithErrors("""[{"message": "nested"}]""", type),
                    "Errors entry is an object" + under
                );
                yield return Case(
                    BodyWithErrors("""["Rate limit reached."]""", type),
                    "Unrecognized message" + under
                );
                yield return Case(
                    TooManyTokensUpstream.BodyWithLimit("many", type),
                    "Non-numeric limit" + under
                );
                yield return Case(TooManyTokensUpstream.BodyWithLimit("", type), "Absent limit" + under);
                yield return Case(TooManyTokensUpstream.BodyWithLimit("0", type), "Zero limit" + under);
                yield return Case(TooManyTokensUpstream.BodyWithLimit("-5", type), "Negative limit" + under);
                yield return Case(
                    TooManyTokensUpstream.BodyWithLimit("2147483648", type),
                    "Limit overflowing int" + under
                );
                yield return Case(
                    TooManyTokensUpstream.BodyWithLimit(" 5", type),
                    "Limit padded with whitespace" + under
                );
            }

            // Near misses of the accepted types. Each is otherwise the body the control test below
            // shows is accepted, so the type alone is what turns it away.
            const string ManagementApiType = TooManyTokensUpstream.ManagementApiType;
            yield return Case(
                BodyOfType(ManagementApiType + " "),
                "Management API type with a trailing space"
            );
            yield return Case(
                BodyOfType(ManagementApiType.ToUpperInvariant()),
                "Management API type upper-cased"
            );
            yield return Case(
                BodyOfType("urn:ed-fi:management-api:security:authentication"),
                "Parent of the Management API type"
            );
            yield return Case(
                BodyOfType(ManagementApiType + ":extra"),
                "Extension of the Management API type"
            );
            yield return Case(
                BodyOfType("urn:ed-fi:management-api:too-many-requests"),
                "Management API rate-limit type"
            );
            yield return Case(
                BodyOfType("urn:ed-fi:admin-api:security:authentication:too-many-tokens"),
                "Published specification admin-api spelling"
            );
            yield return Case(BodyWithout("type", ManagementApiType), "Type member absent");
            yield return Case(
                BodyWithTypeJson($"[{System.Text.Json.JsonSerializer.Serialize(ManagementApiType)}]"),
                "Type is an array holding the Management API type"
            );
            yield return Case(BodyWithTypeJson("null"), "Type is the JSON literal null");
        }

        [TestCaseSource(nameof(DeviatingBodies))]
        public async Task It_falls_back_to_the_generic_rate_limit_contract(string upstreamBody)
        {
            (HttpStatusCode status, _, JsonNode body, _) = await UpstreamRejectsWith(upstreamBody);

            status.Should().Be(HttpStatusCode.TooManyRequests);
            ShouldBeTheGenericRateLimitContract(body);
        }

        // The control for the near-miss cases: the same minimal body with an accepted type is
        // believed, so a near miss is turned away by its type and not by anything else.
        [TestCase(TooManyTokensUpstream.LegacyType)]
        [TestCase(TooManyTokensUpstream.ManagementApiType)]
        public async Task It_accepts_the_near_miss_body_when_its_type_is_accepted(string upstreamType)
        {
            (HttpStatusCode status, _, JsonNode body, _) = await UpstreamRejectsWith(
                BodyOfType(upstreamType)
            );

            status.Should().Be(HttpStatusCode.TooManyRequests);
            body.AsObject()
                .Select(property => property.Key)
                .Should()
                .BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
            body["error_description"]!.GetValue<string>().Should().Be(CanonicalMessage);
        }
    }

    /// <summary>
    /// The new 429 arm must not have widened the default arm, which keeps arbitrary upstream
    /// content away from an unauthenticated caller.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Status_The_New_Arm_Does_Not_Claim
    {
        [TestCase(HttpStatusCode.ServiceUnavailable, TooManyTokensUpstream.LegacyType)]
        [TestCase(HttpStatusCode.ServiceUnavailable, TooManyTokensUpstream.ManagementApiType)]
        [TestCase(HttpStatusCode.Forbidden, TooManyTokensUpstream.LegacyType)]
        [TestCase(HttpStatusCode.Forbidden, TooManyTokensUpstream.ManagementApiType)]
        public async Task It_still_responds_with_the_bad_gateway_contract(
            HttpStatusCode upstreamStatus,
            string upstreamType
        )
        {
            (HttpResponseMessage response, _) = await UpstreamResponds(
                upstreamStatus,
                TooManyTokensUpstream.BodyWithLimit("5", upstreamType)
            );
            JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            body["error"]!.GetValue<string>().Should().Be("server_error");
        }
    }
}
