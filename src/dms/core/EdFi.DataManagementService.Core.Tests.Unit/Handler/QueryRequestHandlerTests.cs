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
[Parallelizable]
public class QueryRequestHandlerTests
{
    internal static IPipelineStep Handler(IQueryHandler queryHandler)
    {
        return new QueryRequestHandler(queryHandler, NullLogger.Instance, ResiliencePipeline.Empty);
    }

    [TestFixture]
    [Parallelizable]
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

        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep queryHandler = Handler(new Repository());
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Be(Repository.ResponseBody.ToJsonString());
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Invalid_Query : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.QueryFailureKnownError("Error"));
            }
        }

        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep queryHandler = Handler(new Repository());
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
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
        private readonly RequestInfo _requestInfo = No.RequestInfo(_traceId);

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep queryHandler = Handler(new Repository());
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);

            var expected = ToJsonError("FailureMessage", new TraceId(_traceId));

            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(_requestInfo.FrontendResponse.Body, expected)
                .Should()
                .BeTrue(
                    $"""
                    expected: {expected}

                    actual: {_requestInfo.FrontendResponse.Body}
                    """
                );
        }
    }
}
