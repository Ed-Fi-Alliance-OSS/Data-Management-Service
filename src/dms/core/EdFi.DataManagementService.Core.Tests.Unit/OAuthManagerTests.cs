// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

public class OAuthManagerTests
{
    [TestFixture]
    public class When_Getting_An_Access_Token
    {
        protected readonly IHttpClientWrapper _httpClient = A.Fake<IHttpClientWrapper>();

        protected readonly ILogger<OAuthManager> _logger = A.Fake<ILogger<OAuthManager>>();

        protected HttpResponseMessage _response = new();

        private const string DestinationUri = "http://example.com/oauth/token";

        private readonly TraceId TraceId = new("trace-id");

        public async Task Act(string authHeader)
        {
            await Act(authHeader, HttpStatusCode.OK, "{}");
        }

        public async Task Act(string authHeader, HttpStatusCode responseCode, string responseMessage)
        {
            // Arrange
            var fakeResponse = A.Fake<HttpResponseMessage>();
            fakeResponse.StatusCode = responseCode;
            fakeResponse.Content = new StringContent(responseMessage, Encoding.UTF8, "application/json");

            A.CallTo(() => _httpClient.SendAsync(A<HttpRequestMessage>._)).ReturnsLazily(() => fakeResponse);

            var system = new OAuthManager(_logger);

            // Act
            _response = await system.GetAccessTokenAsync(_httpClient, authHeader, DestinationUri, TraceId);
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
                "basic 123:abc",
                DestinationUri,
                TraceId
            );
        }

        [TestFixture]
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
                A.CallTo(
                        () =>
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
                A.CallTo(
                        () =>
                            _httpClient.SendAsync(
                                A<HttpRequestMessage>.That.Matches(m =>
                                    m.Content != null
                                    && m.Content.Headers.ContentType != null
                                    && m.Content!.Headers.ContentType!.ToString()
                                        == "application/x-www-form-urlencoded; charset=utf-8"
                                )
                            )
                    )
                    .MustHaveHappened();
            }

            [Test]
            public void Then_The_Grant_Type_Must_Be_Client_Credentials()
            {
                A.CallTo(
                        () =>
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
                A.CallTo(
                        () =>
                            _httpClient.SendAsync(
                                A<HttpRequestMessage>.That.Matches(m =>
                                    m.RequestUri == new Uri(DestinationUri)
                                )
                            )
                    )
                    .MustHaveHappened();
            }
        }

        [TestFixture]
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
                A.CallTo(
                        () =>
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
        public class Given_the_Request_Has_A_Blank_Authorization_Header : When_Getting_An_Access_Token
        {
            [SetUp]
            public async Task SetUp()
            {
                await Act(string.Empty);
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
                    .And.Be("application/problem+json");
            }

            // Only testing the most important part of the problem+json response - the detail
            [Test]
            public async Task Then_The_Response_Content_Mentions_Malformed_Header()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull();
                content!.Should().Contain("\"detail\": \"Malformed Authorization header\"");
            }
        }

        [TestFixture]
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
"""
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
                    .And.Be("application/problem+json");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_Error_As_Title()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain("\"title\": \"invalid_client\"");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_ErrorDetail_As_Detail()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content
                    .Should()
                    .NotBeNull()
                    .And.Contain("\"detail\": \"Invalid client or Invalid client credentials\"");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_TraceId()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain($"\"correlationId\": \"{TraceId.Value}\"");
            }
        }

        [TestFixture]
        public class Given_An_Error_Occurred_Without_Exception : When_Getting_An_Access_Token
        {
            private const string AuthHeader = "basic abc:123";

            [SetUp]
            public async Task SetUp()
            {
                await Act(AuthHeader, HttpStatusCode.BadRequest, "{}");
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
                    .And.Be("application/problem+json");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_A_Title()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain("\"title\": \"Upstream service unavailable\"");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_TraceId()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain($"\"correlationId\": \"{TraceId.Value}\"");
            }
        }

        [TestFixture]
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
                    .And.Be("application/problem+json");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_A_Title()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain("\"title\": \"Upstream service unavailable\"");
            }

            [Test]
            public async Task Then_The_Response_Content_Contains_TraceId()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull().And.Contain($"\"correlationId\": \"{TraceId.Value}\"");
            }

            // This is a rare case where it would be nice to check that the log
            // has been accessed - but extension methods cannot be verified.
        }
    }
}
