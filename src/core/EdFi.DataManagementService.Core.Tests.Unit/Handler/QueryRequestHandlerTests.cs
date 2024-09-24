// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.Handler.Utility;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
public class QueryRequestHandlerTests
{
    internal static IPipelineStep Handler(IQueryHandler queryHandler)
    {
        return new QueryRequestHandler(queryHandler, NullLogger.Instance, ResiliencePipeline.Empty);
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Success : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonArray ResponseBody = [];

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], 0));
            }
        }

        private readonly PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep queryHandler = Handler(new Repository());
            await queryHandler.Execute(_context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _context.FrontendResponse.StatusCode.Should().Be(200);
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Be(Repository.ResponseBody.ToJsonString());
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Invalid_Query : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.QueryFailureInvalidQuery("Error"));
            }
        }

        private readonly PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep queryHandler = Handler(new Repository());
            await queryHandler.Execute(_context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _context.FrontendResponse.StatusCode.Should().Be(404);
            _context.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Unknown_Failure : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.UnknownFailure(ResponseBody));
            }
        }

        private static readonly string _traceId = "xyz";
        private readonly PipelineContext _context = No.PipelineContext(_traceId);

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep queryHandler = Handler(new Repository());
            await queryHandler.Execute(_context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _context.FrontendResponse.StatusCode.Should().Be(500);

            var expected = ToJsonError("FailureMessage", new TraceId(_traceId));

            _context.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(_context.FrontendResponse.Body, expected)
                .Should()
                .BeTrue(
                    $"""
                    expected: {expected}

                    actual: {_context.FrontendResponse.Body}
                    """
                );
        }
    }
}
