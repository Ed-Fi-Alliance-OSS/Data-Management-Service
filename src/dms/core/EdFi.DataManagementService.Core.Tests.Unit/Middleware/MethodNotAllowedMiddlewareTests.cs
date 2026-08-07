// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
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

    private static IPipelineStep Middleware(bool isTrackedChangeRoute = false)
    {
        return new MethodNotAllowedMiddleware(NullLogger.Instance, isTrackedChangeRoute);
    }

    private static PathComponents PathComponents(ResourcePathOperation operation)
    {
        return new(ProjectEndpointName: new("ed-fi"), EndpointName: new("schools"), Operation: operation);
    }

    private static ResourcePathOperation CollectionRoute() => ResourcePathOperation.Collection.Instance;

    private static RequestInfo RequestInfoFor(string unsupportedMethodName, ResourcePathOperation operation)
    {
        RequestInfo requestInfo = No.RequestInfo(TraceId);
        requestInfo.Method = RequestMethod.UNSUPPORTED;
        requestInfo.UnsupportedMethodName = unsupportedMethodName;
        requestInfo.PathComponents = PathComponents(operation);
        return requestInfo;
    }

    /// <summary>
    /// Asserts the Ed-Fi method-not-allowed problem-details contract member by member. This
    /// middleware is the sole producer of that body: both frontend terminals hand the request to
    /// Core and carry its response out through ToResult, so this is where the contract is pinned.
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
        private readonly RequestInfo _requestInfo = RequestInfoFor("PATCH", CollectionRoute());

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
        private readonly RequestInfo _requestInfo = RequestInfoFor(
            "PATCH",
            new ResourcePathOperation.ById(new(Guid.NewGuid()))
        );

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
        [Test]
        public async Task It_returns_405_allowing_get_only()
        {
            // ParseTrackedChangePathMiddleware classifies its path as the Collection operation,
            // exactly as ParsePathMiddleware does for a collection route, which is why the pipeline
            // tells this step which route family it terminates rather than the step reading it off
            // the request.
            //
            // These tests supply that flag themselves, so none of them can show it being wired
            // correctly. That is pinned in PipelineOrderingTests, which exercises the terminal each
            // pipeline factory actually constructed.
            RequestInfo requestInfo = RequestInfoFor("PATCH", CollectionRoute());

            await Middleware(isTrackedChangeRoute: true).Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(405);
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
            RequestInfo requestInfo = RequestInfoFor(unsupportedMethodName, CollectionRoute());

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
            RequestInfo requestInfo = RequestInfoFor("PATCH", CollectionRoute());
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
