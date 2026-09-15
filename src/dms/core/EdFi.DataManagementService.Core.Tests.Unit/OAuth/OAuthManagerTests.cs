// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

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

        private const string DestinationUri = "http://example.com/oauth/token";

        private readonly TraceId TraceId = new("trace-id");

        private const string GrantType = "client_credentials";

        public async Task Act(string authHeader, string grantType = GrantType)
        {
            await Act(authHeader, HttpStatusCode.OK, "{}", grantType);
        }

        public async Task Act(
            string authHeader,
            HttpStatusCode responseCode,
            string responseMessage,
            string grantType = ""
        )
        {
            // Arrange
            var fakeResponse = A.Fake<HttpResponseMessage>();
            fakeResponse.StatusCode = responseCode;
            fakeResponse.Content = new StringContent(responseMessage, Encoding.UTF8, "application/json");

            A.CallTo(() => _httpClient.SendAsync(A<HttpRequestMessage>._)).ReturnsLazily(() => fakeResponse);

            var system = new OAuthManager(_logger);

            // Act
            _response = await system.GetAccessTokenAsync(
                _httpClient,
                grantType,
                authHeader,
                DestinationUri,
                TraceId
            );
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
                    .And.Be("application/problem+json");
            }

            [Test]
            public async Task Then_The_Response_Content_Mentions_Malformed_Header()
            {
                var content = await _response.Content.ReadAsStringAsync();
                content.Should().NotBeNull();
                content!.Should().Contain("\"detail\": \"Unsupported grant type\"");
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
        private JsonNode _body = default!;

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

            HttpResponseMessage response = await new OAuthManager(_logger).GetAccessTokenAsync(
                httpClient,
                "client_credentials",
                "basic abc:123",
                "http://example.com/oauth/token",
                new TraceId(UpstreamCorrelationId)
            );

            _body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
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
            _body["correlationId"]!.GetValue<string>().Should().Be(UpstreamCorrelationId);
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
        + "administrator with the correlationId from this response, which identifies the "
        + "corresponding server log entry.";

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
    )> UpstreamResponds(HttpStatusCode upstreamStatus, string upstreamBody)
    {
        var logger = new RecordingLogger<OAuthManager>();

        var upstreamResponse = A.Fake<HttpResponseMessage>();
        upstreamResponse.StatusCode = upstreamStatus;
        upstreamResponse.Content = new StringContent(upstreamBody, Encoding.UTF8, "application/json");

        var httpClient = A.Fake<IHttpClientWrapper>();
        A.CallTo(() => httpClient.SendAsync(A<HttpRequestMessage>._)).ReturnsLazily(() => upstreamResponse);

        HttpResponseMessage response = await new OAuthManager(logger).GetAccessTokenAsync(
            httpClient,
            "client_credentials",
            "basic abc:123",
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
        public void It_keeps_the_problem_json_content_type()
        {
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
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
            _body["detail"]!.GetValue<string>().Should().Be(ExpectedGatewayDetail);
        }

        [Test]
        public void It_keeps_the_title()
        {
            _body["title"]!.GetValue<string>().Should().Be("Upstream service unavailable");
        }

        [Test]
        public void It_keeps_the_correlation_id()
        {
            _body["correlationId"]!.GetValue<string>().Should().Be(CorrelationId);
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
            _fromUpstreamResponse["detail"]!.GetValue<string>().Should().Be(ExpectedGatewayDetail);
        }

        [Test]
        public void It_uses_the_fixed_detail_on_the_exception_path()
        {
            _fromThrownException["detail"]!.GetValue<string>().Should().Be(ExpectedGatewayDetail);
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

        private static readonly string _upstreamBody = $$"""
            { "error": "invalid_client", "realm": "{{InternalRealmSentinel}}" }
            """;

        private HttpResponseMessage _response = default!;
        private string _rawResponseBody = default!;
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (_response, _) = await UpstreamResponds(HttpStatusCode.Unauthorized, _upstreamBody);
            _rawResponseBody = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_rawResponseBody)!;
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
            _body["detail"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
        }

        [Test]
        public void It_still_surfaces_the_parsed_oauth_error_as_the_title()
        {
            _body["title"]!.GetValue<string>().Should().Be("invalid_client");
        }
    }
}
