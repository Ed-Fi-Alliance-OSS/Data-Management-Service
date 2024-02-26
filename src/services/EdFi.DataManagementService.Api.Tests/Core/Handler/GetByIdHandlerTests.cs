// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Backend;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Api.Backend.GetResult;

namespace EdFi.DataManagementService.Api.Core.Handler;

[TestFixture]
public class GetByIdHandlerTests
{
    private static readonly Func<Task> _nullNext = () => Task.CompletedTask;

    public static IPipelineStep Handler(IDocumentStoreRepository documentStoreRepository)
    {
        return new GetByIdHandler(documentStoreRepository, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_a_repository_that_returns_success : GetByIdHandlerTests
    {
        public class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "{}";

            public override Task<GetResult> GetDocumentById(GetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, new JsonObject(), DateTime.Now)
                );
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep getByIdHandler = Handler(new Repository());
            await getByIdHandler.Execute(context, _nullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(200);
            context.FrontendResponse.Body.Should().Be(Repository.ResponseBody);
        }
    }

    [TestFixture]
    public class Given_a_repository_that_returns_failure_not_exists : GetByIdHandlerTests
    {
        public class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<GetResult> GetDocumentById(GetRequest deleteRequest)
            {
                return Task.FromResult<GetResult>(new GetFailureNotExists());
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep getByIdHandler = Handler(new Repository());
            await getByIdHandler.Execute(context, _nullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(404);
            context.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_a_repository_that_returns_unknown_failure : GetByIdHandlerTests
    {
        public class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<GetResult> GetDocumentById(GetRequest deleteRequest)
            {
                return Task.FromResult<GetResult>(new UnknownFailure(ResponseBody));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep getByIdHandler = Handler(new Repository());
            await getByIdHandler.Execute(context, _nullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(500);
            context.FrontendResponse.Body.Should().Be(Repository.ResponseBody);
        }
    }
}
