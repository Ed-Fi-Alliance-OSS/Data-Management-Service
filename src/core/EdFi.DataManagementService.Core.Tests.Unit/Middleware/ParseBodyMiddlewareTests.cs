// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
public class ParseBodyMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ParseBodyMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_A_Post_Request_With_Null_Body : ParseBodyMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: null,
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("A non-empty request body is required");
        }
    }

    [TestFixture]
    public class Given_A_Post_Request_With_Empty_Body : ParseBodyMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: "",
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("A non-empty request body is required");
        }
    }

    [TestFixture]
    public class Given_A_Post_Request_With_Invalid_Json : ParseBodyMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: """{ "id":"value" "name":"firstname"}""",
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Data validation failed.");
        }
    }
}
