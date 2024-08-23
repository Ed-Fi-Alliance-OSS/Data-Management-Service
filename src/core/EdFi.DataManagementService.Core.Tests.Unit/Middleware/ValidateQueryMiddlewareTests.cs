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
public class ValidateQueryMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ValidateQueryMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Wrong_Query_Parameters : ValidateQueryMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string>
            {
                { "offset", "I" },
                { "limit", "-1" },
                { "totalCount", "100" }
            };

            FrontendRequest frontendRequest =
                new(
                    Path: "/ed-fi/schools",
                    Body: null,
                    QueryParameters: queryParameters,
                    TraceId: new TraceId("")
                );
            _context = new(frontendRequest, RequestMethod.GET);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_be_errors()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("The request could not be processed.");
        }

        [Test]
        public void It_should_be_offset_errors()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Offset must be a numeric value greater than or equal to 0.");
        }

        [Test]
        public void It_should_be_limit_errors()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Limit must be a numeric value greater than or equal to 0.");
        }

        [Test]
        public void It_should_be_total_count_errors()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("TotalCount must be a boolean value.");
        }
    }
}
