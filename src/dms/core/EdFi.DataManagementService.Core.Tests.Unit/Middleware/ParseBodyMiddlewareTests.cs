// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ParseBodyMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ParseBodyMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Null_Body : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("A non-empty request body is required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Empty_Body : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: "",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("A non-empty request body is required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Whitespace_Only_Body : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: " \r\n\t ",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("A non-empty request body is required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Invalid_Json : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: """{ "id":"value" "name":"firstname"}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data validation failed.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Preparsed_Json : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private JsonNode _parsedBody = new JsonObject();
        private bool _nextWasCalled;

        [SetUp]
        public async Task Setup()
        {
            _parsedBody = JsonNode.Parse("""{"id":"value","name":"firstname"}""")!;

            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: [],
                ParsedBody: _parsedBody
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextWasCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_sets_the_parsed_body()
        {
            _requestInfo.ParsedBody.Should().BeSameAs(_parsedBody);
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _nextWasCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_set_an_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Frontend_Parse_Error : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("correlation-id"),
                RouteQualifiers: [],
                BodyParseErrorMessage: "'n' is invalid after a value."
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_data_validation_error_message_body()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Data validation failed.")
                .And.Contain("invalid after a value.");
        }

        [Test]
        public void It_preserves_the_correlation_id()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("\"correlationId\":\"correlation-id\"");
        }
    }

    /// <summary>
    /// The middleware owns body parsing only. A failure raised further down the pipeline is not a
    /// client validation error, and its message is internal, so the exception must travel on to the
    /// pipeline's exception handler rather than being answered here.
    ///
    /// Only the pre-parsed case below is a regression guard. The middleware-parsed case passed
    /// before the fix too, because on that path the downstream call already sat outside the guarded
    /// block; the pre-parsed path is the one that ran the whole pipeline inside it, and the one the
    /// production frontend always takes.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Downstream_Step_Throws : ParseBodyMiddlewareTests
    {
        private const string DownstreamMessage =
            "Transaction (Process ID 112) was deadlocked on lock resources with another process "
            + "and has been chosen as the deadlock victim. Rerun the transaction.";

        private static RequestInfo RequestWith(JsonNode? parsedBody, string? body) =>
            new(
                new FrontendRequest(
                    Path: "ed-fi/schools",
                    Body: body,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: [],
                    ParsedBody: parsedBody
                ),
                RequestMethod.POST,
                No.ServiceProvider
            );

        private static Func<Task> ThrowingNext =>
            () => throw new InvalidOperationException(DownstreamMessage);

        [Test]
        public async Task It_propagates_the_exception_when_the_frontend_pre_parsed_the_body()
        {
            var requestInfo = RequestWith(JsonNode.Parse("""{ "schoolId": 1 }"""), body: null);

            Func<Task> act = () => Middleware().Execute(requestInfo, ThrowingNext);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(DownstreamMessage);
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_propagates_the_exception_when_the_middleware_parsed_the_body()
        {
            var requestInfo = RequestWith(parsedBody: null, body: """{ "schoolId": 1 }""");

            Func<Task> act = () => Middleware().Execute(requestInfo, ThrowingNext);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(DownstreamMessage);
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    /// <summary>
    /// Builds a request the way each parse path does: the frontend hands over a node parsed from UTF-8
    /// bytes and no raw body, while the Core path gets only the raw body text.
    /// </summary>
    internal static RequestInfo RequestFor(
        string body,
        bool frontendParsed,
        string? duplicatePropertyPath = null
    ) =>
        new(
            new FrontendRequest(
                Path: "ed-fi/schools",
                Body: frontendParsed ? null : body,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: [],
                ParsedBody: frontendParsed ? JsonNode.Parse(System.Text.Encoding.UTF8.GetBytes(body)) : null,
                DuplicatePropertyPath: duplicatePropertyPath
            ),
            RequestMethod.POST,
            No.ServiceProvider
        );

    [TestFixture("""{"name":"\uD800"}""", "$.name", true)]
    [TestFixture("""{"name":"\uD800"}""", "$.name", false)]
    [TestFixture("""{"name":"\uDC00"}""", "$.name", true)]
    [TestFixture("""{"name":"\uDC00"}""", "$.name", false)]
    [TestFixture("""{"name":"\uD800A"}""", "$.name", true)]
    [TestFixture("""{"name":"\uD800A"}""", "$.name", false)]
    [TestFixture("""{"address":{"city":"\uD800"}}""", "$.address.city", true)]
    [TestFixture("""{"address":{"city":"\uD800"}}""", "$.address.city", false)]
    [TestFixture("""{"addresses":[{"city":"ok"},{"city":"\uD800"}]}""", "$.addresses[1].city", true)]
    [TestFixture("""{"addresses":[{"city":"ok"},{"city":"\uD800"}]}""", "$.addresses[1].city", false)]
    [TestFixture("""{"first":"\uD800","second":"\uDC00"}""", "$.first", true)]
    [TestFixture("""{"first":"\uD800","second":"\uDC00"}""", "$.first", false)]
    [Parallelizable]
    public class Given_A_Body_With_An_Unpaired_Surrogate_String_Value(
        string body,
        string expectedPath,
        bool frontendParsed
    ) : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextWasCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestFor(body, frontendParsed);
            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextWasCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_returns_the_malformed_body_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body!["validationErrors"]!["$."]!
                .AsArray()
                .Should()
                .ContainSingle();
        }

        [Test]
        public void It_names_the_first_offending_string()
        {
            var message = _requestInfo.FrontendResponse.Body!["validationErrors"]!["$."]![
                0
            ]!.GetValue<string>();
            message.Should().EndWith($" Path: {expectedPath}");
        }

        [Test]
        public void It_does_not_continue_the_pipeline()
        {
            _nextWasCalled.Should().BeFalse();
        }
    }

    [TestFixture("""{"outer":{"\uD800":1}}""", "$.outer")]
    [TestFixture("""{"\uD800":1}""", "$")]
    [TestFixture("""{"items":[{"id":1,"\uD800":1}]}""", "$.items[0]")]
    [TestFixture("""{"items":[{"ref":{"a":1},"\uD800":1}]}""", "$.items[0]")]
    [TestFixture("""{"items":[{"id":1},{"id":2,"\uD800":1}]}""", "$.items[1]")]
    // The malformed name comes before a duplicate key in its parent, so walking the parent first would
    // throw ArgumentException instead.
    [TestFixture("""{"outer":{"\uD800":1},"x":1,"x":2}""", "$.outer")]
    [Parallelizable]
    public class Given_A_Core_Parsed_Body_With_A_Malformed_Property_Name(string body, string expectedPath)
        : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextWasCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestFor(body, frontendParsed: false);
            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextWasCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_returns_the_malformed_body_400_naming_the_containing_object()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body!["validationErrors"]!["$."]![0]!
                .GetValue<string>()
                .Should()
                .EndWith($" Path: {expectedPath}");
        }

        [Test]
        public void It_does_not_continue_the_pipeline()
        {
            _nextWasCalled.Should().BeFalse();
        }
    }

    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable]
    public class Given_A_Body_With_Valid_Surrogate_Pairs_And_Non_Ascii_Text(bool frontendParsed)
        : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextWasCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestFor(
                """{"name":"😀","city":"École","tags":["Ünïcødé","é"]}""",
                frontendParsed
            );
            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextWasCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _nextWasCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_set_an_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    /// <summary>
    /// Duplicate detection runs after this step, and enumerating an object with a duplicate key throws, so
    /// a body with a duplicate must reach DuplicatePropertiesMiddleware untouched and get its 400.
    /// </summary>
    [TestFixture("""{"a":1,"a":2}""", "$.a", true)]
    [TestFixture("""{"a":1,"a":2}""", "$.a", false)]
    [TestFixture("""{"outer":{"a":1,"a":2}}""", "$.outer.a", true)]
    [TestFixture("""{"outer":{"a":1,"a":2}}""", "$.outer.a", false)]
    [TestFixture("""{"a":"\uD800","a":2}""", "$.a", true)]
    [TestFixture("""{"a":"\uD800","a":2}""", "$.a", false)]
    [Parallelizable]
    public class Given_A_Body_With_A_Duplicate_Property_Followed_By_Duplicate_Detection(
        string body,
        string duplicatePath,
        bool frontendParsed
    ) : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextWasCalled;

        [SetUp]
        public async Task Setup()
        {
            // The frontend reports the duplicate it found while reading the bytes.
            _requestInfo = RequestFor(body, frontendParsed, frontendParsed ? duplicatePath : null);
            var duplicateDetection = DuplicatePropertiesMiddlewareTest.Middleware();
            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                        duplicateDetection.Execute(
                            _requestInfo,
                            () =>
                            {
                                _nextWasCalled = true;
                                return Task.CompletedTask;
                            }
                        )
                );
        }

        [Test]
        public void It_returns_the_duplicate_property_400_at_its_path()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body!["validationErrors"]![duplicatePath]![0]!
                .GetValue<string>()
                .Should()
                .Be("An item with the same key has already been added.");
        }

        [Test]
        public void It_does_not_continue_the_pipeline()
        {
            _nextWasCalled.Should().BeFalse();
        }
    }
}
