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
            _body["detail"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
        }

        [Test]
        public void It_still_surfaces_the_parsed_oauth_error_as_the_title()
        {
            _body["title"]!.GetValue<string>().Should().Be("invalid_client");
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
            _body["detail"]!.GetValue<string>().Should().Be(UpstreamDescription);
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
            JsonNode.Parse(_rawResponseBody)!["title"]!
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
            _body["title"]!.GetValue<string>().Should().Be("Unauthorized");
        }

        [Test]
        public void It_falls_back_to_the_fixed_detail_rather_than_the_container()
        {
            _body["detail"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
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
            _body["detail"]!.GetValue<string>().Should().Be(ExpectedUnauthorizedFallbackDetail);
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
        public const string Type = "urn:ed-fi:api:security:authentication:too-many-tokens";

        public static string MessageWithLimit(string limit) =>
            $"Too many access tokens have been requested (limit is {limit}). Access tokens should "
            + "be reused until they expire.";

        public static string BodyWithMessage(string message) =>
            $$"""
                {
                  "detail": "The caller has authenticated too many times in too short of a time period.",
                  "type": "{{Type}}",
                  "title": "Too Many Tokens",
                  "status": 429,
                  "correlationId": "upstream-correlation-id",
                  "validationErrors": {},
                  "errors": [{{System.Text.Json.JsonSerializer.Serialize(message)}}]
                }
                """;

        public static string BodyWithLimit(string limit) => BodyWithMessage(MessageWithLimit(limit));
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
        body["type"]!.ToString().Should().Be("urn:ed-fi:api:too-many-requests");
        body["title"]!.ToString().Should().Be("Too Many Requests");
        body["status"]!.GetValue<int>().Should().Be(429);
        body["errors"]!.AsArray().Count.Should().Be(0);
        body["correlationId"]!.ToString().Should().Be(CorrelationId);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Token_Limit_Rejection
    {
        private HttpStatusCode _status;
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (_status, _, _body, _) = await UpstreamRejectsWith(TooManyTokensUpstream.BodyWithLimit("5"));
        }

        [Test]
        public void It_responds_with_too_many_requests()
        {
            _status.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_has_the_too_many_tokens_type()
        {
            _body["type"]!.ToString().Should().Be(TooManyTokensUpstream.Type);
        }

        [Test]
        public void It_has_the_too_many_tokens_title()
        {
            _body["title"]!.ToString().Should().Be("Too Many Tokens");
        }

        [Test]
        public void It_has_the_tickets_detail()
        {
            _body["detail"]!
                .ToString()
                .Should()
                .Be("The caller has authenticated too many times in too short of a time period.");
        }

        [Test]
        public void It_reconstructs_an_errors_entry_carrying_the_upstream_limit()
        {
            _body["errors"]!.AsArray().Count.Should().Be(1);
            _body["errors"]![0]!.ToString().Should().Be(TooManyTokensUpstream.MessageWithLimit("5"));
        }

        // The upstream body carries its own correlationId. Echoing it would point the caller at a
        // log entry in a service they cannot reach.
        [Test]
        public void It_carries_the_dms_trace_id_not_the_upstream_one()
        {
            _body["correlationId"]!.ToString().Should().Be(CorrelationId);
        }
    }

    /// <summary>
    /// A limit other than the default, so the parser is shown to read the number rather than to
    /// recognize one canonical sentence.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Token_Limit_Rejection_With_A_Different_Limit
    {
        private JsonNode _body = default!;

        [SetUp]
        public async Task Setup()
        {
            (_, _, _body, _) = await UpstreamRejectsWith(TooManyTokensUpstream.BodyWithLimit("37"));
        }

        [Test]
        public void It_reconstructs_the_message_with_that_limit()
        {
            _body["errors"]![0]!.ToString().Should().Be(TooManyTokensUpstream.MessageWithLimit("37"));
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

        [SetUp]
        public async Task Setup()
        {
            string upstreamBody = CmsFailureResponse
                .ForTooManyTokens(7, "upstream-correlation-id")
                .ToJsonString();
            (_status, _, _body, _logger) = await UpstreamRejectsWith(upstreamBody);
        }

        [Test]
        public void It_responds_with_too_many_requests()
        {
            _status.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_recognizes_the_token_limit_rejection()
        {
            _body["type"]!.ToString().Should().Be(TooManyTokensUpstream.Type);
        }

        [Test]
        public void It_reconstructs_the_message_with_the_configured_limit()
        {
            _body["errors"]!.AsArray().Count.Should().Be(1);
            _body["errors"]![0]!.ToString().Should().Be(TooManyTokensUpstream.MessageWithLimit("7"));
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
    /// would leak it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_Token_Limit_Rejection_Embedding_A_Secret
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
                TooManyTokensUpstream.BodyWithMessage(tampered)
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
        public void It_has_the_service_unavailable_type()
        {
            _body["type"]!.ToString().Should().Be("urn:ed-fi:api:service-unavailable");
        }

        [Test]
        public void It_carries_the_dms_trace_id_not_the_upstream_one()
        {
            _body["correlationId"]!.ToString().Should().Be(CorrelationId);
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
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Upstream_429_Whose_Body_Deviates
    {
        private static string BodyOfType(string type) =>
            $$"""
                {
                  "type": "{{type}}",
                  "title": "Too Many Tokens",
                  "status": 429,
                  "errors": ["{{"Too many access tokens have been requested (limit is 5). Access tokens should be reused until they expire."}}"]
                }
                """;

        private static string BodyWithStatus(int status) =>
            $$"""
                {
                  "type": "{{TooManyTokensUpstream.Type}}",
                  "status": {{status}},
                  "errors": ["Too many access tokens have been requested (limit is 5). Access tokens should be reused until they expire."]
                }
                """;

        private static string BodyWithErrors(string errorsJson) =>
            $$"""
                {
                  "type": "{{TooManyTokensUpstream.Type}}",
                  "status": 429,
                  "errors": {{errorsJson}}
                }
                """;

        private static IEnumerable<TestCaseData> DeviatingBodies()
        {
            yield return new TestCaseData("not json at all").SetName("Unparseable body");
            yield return new TestCaseData("\"a bare string\"").SetName("Body is not a JSON object");
            yield return new TestCaseData("null").SetName("Body is the JSON literal null");
            yield return new TestCaseData(BodyOfType("urn:ed-fi:api:too-many-requests")).SetName(
                "Valid problem details of a different type"
            );
            yield return new TestCaseData(BodyWithStatus(503)).SetName("Body declares another status");
            yield return new TestCaseData(BodyWithErrors("[]")).SetName("Empty errors array");
            yield return new TestCaseData(
                BodyWithErrors(
                    """["Too many access tokens have been requested (limit is 5). Access tokens should be reused until they expire.", "and another"]"""
                )
            ).SetName("Multi-element errors array");
            yield return new TestCaseData(BodyWithErrors("[5]")).SetName("Errors entry is not a string");
            yield return new TestCaseData(BodyWithErrors("""[{"message": "nested"}]""")).SetName(
                "Errors entry is an object"
            );
            yield return new TestCaseData(BodyWithErrors("""["Rate limit reached."]""")).SetName(
                "Unrecognized message"
            );
            yield return new TestCaseData(TooManyTokensUpstream.BodyWithLimit("many")).SetName(
                "Non-numeric limit"
            );
            yield return new TestCaseData(TooManyTokensUpstream.BodyWithLimit("")).SetName("Absent limit");
            yield return new TestCaseData(TooManyTokensUpstream.BodyWithLimit("0")).SetName("Zero limit");
            yield return new TestCaseData(TooManyTokensUpstream.BodyWithLimit("-5")).SetName(
                "Negative limit"
            );
            yield return new TestCaseData(TooManyTokensUpstream.BodyWithLimit("2147483648")).SetName(
                "Limit overflowing int"
            );
            yield return new TestCaseData(TooManyTokensUpstream.BodyWithLimit(" 5")).SetName(
                "Limit padded with whitespace"
            );
        }

        [TestCaseSource(nameof(DeviatingBodies))]
        public async Task It_falls_back_to_the_generic_rate_limit_contract(string upstreamBody)
        {
            (HttpStatusCode status, _, JsonNode body, _) = await UpstreamRejectsWith(upstreamBody);

            status.Should().Be(HttpStatusCode.TooManyRequests);
            ShouldBeTheGenericRateLimitContract(body);
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
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.Forbidden)]
        public async Task It_still_responds_with_the_bad_gateway_contract(HttpStatusCode upstreamStatus)
        {
            (HttpResponseMessage response, _) = await UpstreamResponds(
                upstreamStatus,
                TooManyTokensUpstream.BodyWithLimit("5")
            );
            JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            body["type"]!.ToString().Should().Be("urn:ed-fi:api:bad-gateway");
        }
    }
}
