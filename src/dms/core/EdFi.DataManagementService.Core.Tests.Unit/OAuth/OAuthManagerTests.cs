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
            // As much of the first finding as the second finding leaves reachable: searching by
            // correlation ID finds a request whose upstream rejection carried a `reason` and a
            // `realm`, which is the handle for pursuing it in the identity provider's own logs.
            _logged.Should().NotBeNull();
            _logged!.Properties["OtherFieldNames"]!
                .ToString()
                .Should()
                .Contain("reason")
                .And.Contain("realm");
        }

        [Test]
        public void It_does_not_record_the_value_of_any_non_standard_field()
        {
            // The second finding. `reason` and `realm` are not part of the RFC 6749 section 5.2
            // error contract, so their contents are arbitrary upstream payload and must not be
            // persisted - docs/LOGGING.md forbids response bodies in Information-level logs
            // outright, and neither sanitizing nor bounding redacts anything.
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
    /// Brad Banister's example from the review, verbatim. The point of it is that <c>reason</c>
    /// is "the whole of why the upstream rejected the request" and is not an RFC 6749 field, so
    /// it is precisely the field an allowlist of standard members cannot anticipate. The
    /// resolution keeps its name and discards its value; this fixture pins both halves.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Reviewers_Example_Unauthorized_Body
    {
        private const string ReasonValueSentinel = "client disabled";

        private const string UpstreamBody = """
            {"error":"invalid_client","reason":"client disabled"}
            """;

        private LogRecord _logged = default!;
        private string _rendered = default!;
        private string _rawResponseBody = default!;

        [SetUp]
        public async Task Setup()
        {
            (HttpResponseMessage response, RecordingLogger<OAuthManager> logger) = await UpstreamResponds(
                HttpStatusCode.Unauthorized,
                UpstreamBody
            );
            _rawResponseBody = await response.Content.ReadAsStringAsync();
            _logged = DiscardedUnauthorizedDetailRecord(logger)!;
            _rendered = RenderedFallbackEvent(logger);
        }

        [Test]
        public void It_records_the_standard_error_field_by_value()
        {
            _logged.Properties["StandardFields"]!.ToString().Should().Be("error=invalid_client");
        }

        [Test]
        public void It_records_the_non_standard_field_by_name()
        {
            _logged.Properties["OtherFieldNames"]!.ToString().Should().Be("reason");
        }

        [Test]
        public void It_never_records_the_non_standard_fields_value()
        {
            // The assertion the whole change exists for. An operator learns a `reason` was sent
            // and goes to the identity provider for what it said; DMS persists none of it.
            _rendered.Should().NotContain(ReasonValueSentinel);
        }

        [Test]
        public void It_still_withholds_the_reason_from_the_client()
        {
            _rawResponseBody.Should().NotContain(ReasonValueSentinel);
        }
    }

    /// <summary>
    /// The three RFC 6749 section 5.2 error response members are the allowlist, and all three are
    /// logged by value. <c>error_description</c> cannot reach this path - its presence is what
    /// suppresses the event - so <c>error</c> and <c>error_uri</c> are what a standard-only body
    /// can carry here.
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
        public void It_records_every_standard_field_by_value_in_upstream_order()
        {
            _logged.Properties["StandardFields"]!
                .ToString()
                .Should()
                .Be($"error=invalid_client, error_uri={ErrorUri}");
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
}
