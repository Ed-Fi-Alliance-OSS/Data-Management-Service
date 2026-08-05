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
public class MethodNotAllowedMiddlewareTests
{
    private const string TraceId = "method-not-allowed-trace-id";

    private static IPipelineStep Middleware()
    {
        return new MethodNotAllowedMiddleware(NullLogger.Instance);
    }

    private static PathComponents PathComponents(bool hasDocumentUuidSegment)
    {
        return new(
            ProjectEndpointName: new("ed-fi"),
            EndpointName: new("schools"),
            DocumentUuid: hasDocumentUuidSegment ? new(Guid.NewGuid()) : No.DocumentUuid,
            HasDocumentUuidSegment: hasDocumentUuidSegment
        );
    }

    private static RequestInfo RequestInfoFor(string unsupportedMethodName, bool hasDocumentUuidSegment)
    {
        RequestInfo requestInfo = No.RequestInfo(TraceId);
        requestInfo.Method = RequestMethod.UNSUPPORTED;
        requestInfo.UnsupportedMethodName = unsupportedMethodName;
        requestInfo.PathComponents = PathComponents(hasDocumentUuidSegment);
        return requestInfo;
    }

    /// <summary>
    /// Asserts the Ed-Fi method-not-allowed problem-details contract member by member. The
    /// frontend's tracked-change terminal produces the same body from the same factory, and its
    /// test asserts these same members so the two 405 producers cannot drift.
    /// </summary>
    private static void AssertMethodNotAllowedProblemDetails(
        IFrontendResponse response,
        string expectedMethodName
    )
    {
        response.Body.Should().NotBeNull();
        JsonNode body = response.Body!;

        body["detail"]!.GetValue<string>().Should().Be("The request construction was invalid.");
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:method-not-allowed");
        body["title"]!.GetValue<string>().Should().Be("Method Not Allowed");
        body["status"]!.GetValue<int>().Should().Be(405);
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!
            .AsArray()
            .Select(error => error!.GetValue<string>())
            .Should()
            .Equal($"The endpoint of the request does not support the '{expectedMethodName}' method.");
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unsupported_Method_On_A_Collection_Route : MethodNotAllowedMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = RequestInfoFor("PATCH", hasDocumentUuidSegment: false);

        [SetUp]
        public async Task Setup()
        {
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_an_allow_header_of_the_collection_methods()
        {
            _requestInfo.FrontendResponse.Headers.Should().Contain("Allow", "GET, POST");
        }

        [Test]
        public void It_returns_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/json; charset=utf-8");
        }

        [Test]
        public void It_returns_the_ed_fi_method_not_allowed_body()
        {
            AssertMethodNotAllowedProblemDetails(_requestInfo.FrontendResponse, "PATCH");
        }

        [Test]
        public void It_returns_the_correlation_id_of_the_request()
        {
            _requestInfo.FrontendResponse.Body!["correlationId"]!.GetValue<string>().Should().Be(TraceId);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unsupported_Method_On_An_Item_Route : MethodNotAllowedMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = RequestInfoFor("PATCH", hasDocumentUuidSegment: true);

        [SetUp]
        public async Task Setup()
        {
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_an_allow_header_of_the_item_methods()
        {
            _requestInfo.FrontendResponse.Headers.Should().Contain("Allow", "GET, PUT, DELETE");
        }

        [Test]
        public void It_returns_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/json; charset=utf-8");
        }

        [Test]
        public void It_returns_the_ed_fi_method_not_allowed_body()
        {
            AssertMethodNotAllowedProblemDetails(_requestInfo.FrontendResponse, "PATCH");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unsupported_Method_On_A_Tracked_Change_Route : MethodNotAllowedMiddlewareTests
    {
        /// <summary>
        /// Mirrors what ParseTrackedChangePathMiddleware leaves behind for a /deletes or
        /// /keyChanges request: the operation set, and no document uuid segment.
        /// </summary>
        private static RequestInfo TrackedChangeRequestInfoFor(
            string unsupportedMethodName,
            ChangeQueryEndpointOperation operation
        )
        {
            RequestInfo requestInfo = RequestInfoFor(unsupportedMethodName, hasDocumentUuidSegment: false);
            requestInfo.ChangeQueryOperation = operation;
            return requestInfo;
        }

        [TestCase(ChangeQueryEndpointOperation.Deletes)]
        [TestCase(ChangeQueryEndpointOperation.KeyChanges)]
        public async Task It_returns_405_allowing_get_only(ChangeQueryEndpointOperation operation)
        {
            RequestInfo requestInfo = TrackedChangeRequestInfoFor("PATCH", operation);

            await Middleware().Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(405);
            // A tracked-change path leaves HasDocumentUuidSegment false, so without the
            // ChangeQueryOperation check this would advertise the collection methods.
            requestInfo.FrontendResponse.Headers.Should().Contain("Allow", "GET");
            requestInfo.FrontendResponse.ContentType.Should().Be("application/json; charset=utf-8");
            AssertMethodNotAllowedProblemDetails(requestInfo.FrontendResponse, "PATCH");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Any_Unsupported_Method : MethodNotAllowedMiddlewareTests
    {
        [TestCase("PATCH")]
        [TestCase("OPTIONS")]
        [TestCase("TRACE")]
        [TestCase("FOO")]
        // HEAD is rejected rather than served, matching ODS/API, so it reaches this step too.
        [TestCase("HEAD")]
        public async Task It_names_the_method_of_the_request_in_the_errors(string unsupportedMethodName)
        {
            RequestInfo requestInfo = RequestInfoFor(unsupportedMethodName, hasDocumentUuidSegment: false);

            await Middleware().Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(405);
            AssertMethodNotAllowedProblemDetails(requestInfo.FrontendResponse, unsupportedMethodName);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Step_Is_Terminal : MethodNotAllowedMiddlewareTests
    {
        [Test]
        public async Task It_does_not_invoke_the_next_step()
        {
            RequestInfo requestInfo = RequestInfoFor("PATCH", hasDocumentUuidSegment: false);
            bool nextWasInvoked = false;

            await Middleware()
                .Execute(
                    requestInfo,
                    () =>
                    {
                        nextWasInvoked = true;
                        return Task.CompletedTask;
                    }
                );

            nextWasInvoked.Should().BeFalse();
        }
    }
}
