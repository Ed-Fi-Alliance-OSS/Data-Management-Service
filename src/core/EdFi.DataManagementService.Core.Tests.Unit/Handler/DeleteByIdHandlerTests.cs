// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.External.Backend.DeleteResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
public class DeleteByIdHandlerTests
{
    internal static IPipelineStep Handler(IDocumentStoreRepository documentStoreRepository)
    {
        return new DeleteByIdHandler(documentStoreRepository, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Success : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteSuccess());
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep deleteByIdHandler = Handler(new Repository());
            await deleteByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(204);
            context.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Not_Exists : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureNotExists());
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep deleteByIdHandler = Handler(new Repository());
            await deleteByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(404);
            context.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Reference : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string[] ResponseBody = ["ReferencingDocumentInfo"];

            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureReference(ResponseBody));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep deleteByIdHandler = Handler(new Repository());
            await deleteByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(409);
            context.FrontendResponse.Body.Should().Contain(string.Join(", ", Repository.ResponseBody));
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Write_Conflict : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureWriteConflict());
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep deleteByIdHandler = Handler(new Repository());
            await deleteByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(409);
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Unknown_Failure : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new UnknownFailure(ResponseBody));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep deleteByIdHandler = Handler(new Repository());
            await deleteByIdHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(500);
            context.FrontendResponse.Body.Should().Be(Repository.ResponseBody);
        }
    }
}
