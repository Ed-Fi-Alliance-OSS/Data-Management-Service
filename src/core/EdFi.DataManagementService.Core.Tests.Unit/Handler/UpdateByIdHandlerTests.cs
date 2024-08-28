// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Json.More;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.UpdateResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
public class UpdateByIdHandlerTests
{
    internal static IPipelineStep Handler(IDocumentStoreRepository documentStoreRepository)
    {
        return new UpdateByIdHandler(documentStoreRepository, NullLogger.Instance, ResiliencePipeline.Empty);
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Success : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateSuccess(updateRequest.DocumentUuid));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(204);
            context.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Not_Exists : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureNotExists());
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(404);
            context
                .FrontendResponse.Body?.AsJsonString()
                .Should()
                .Be(
                    """"
                    {"detail":"Resource to update was not found","type":"urn:ed-fi:api:not-found","title":"Not Found","status":404,"correlationId":"","validationErrors":{},"errors":[]}
                    """"
                );
        }
    }

    [TestFixture]
    public class Given_S_Repository_That_Returns_Failure_Reference : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "ReferencingDocumentInfo";

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureReference([new(ResponseBody)]));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(409);
            context.FrontendResponse.Body?.ToJsonString().Should().Contain(Repository.ResponseBody);
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Identity_Conflict : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureIdentityConflict(ResponseBody));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(400);
            context.FrontendResponse.Body?.AsValue().ToString().Should().Be(Repository.ResponseBody);
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Write_Conflict : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureWriteConflict());
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(409);
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Immutable_Identity : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(
                    new UpdateFailureImmutableIdentity(
                        "Identifying values for the resource cannot be changed. Delete and recreate the resource item instead."
                    )
                );
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Cascade_Required : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureCascadeRequired());
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(400);
            context.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Unknown_Failure : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UnknownFailure(ResponseBody));
            }
        }

        private static readonly string _traceId = "xyz";
        private readonly PipelineContext context = No.PipelineContext(_traceId);

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(500);

            var expected = $$"""
{
  "error": "FailureMessage",
  "correlationId": "{{_traceId}}"
}
""";

            context.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(context.FrontendResponse.Body, JsonNode.Parse(expected))
                .Should()
                .BeTrue(
                    $"""
expected: {expected}

actual: {context.FrontendResponse.Body}
"""
                );
        }
    }
}
