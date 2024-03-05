// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Backend;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Api.Backend.UpsertResult;
using static EdFi.DataManagementService.Api.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Api.Core.Handler;

[TestFixture]
public class UpsertHandlerTests
{
    public static IPipelineStep Handler(IDocumentStoreRepository documentStoreRepository)
    {
        return new UpsertHandler(documentStoreRepository, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_a_repository_that_returns_success : UpsertHandlerTests
    {
        public class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UpdateSuccess());
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep upsertHandler = Handler(new Repository());
            await upsertHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(200);
            context.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_a_repository_that_returns_failure_reference : UpsertHandlerTests
    {
        public class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "ReferencingDocumentInfo";

            public override Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UpsertFailureReference(ResponseBody));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep upsertHandler = Handler(new Repository());
            await upsertHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(409);
            context.FrontendResponse.Body.Should().Be(Repository.ResponseBody);
        }
    }

    [TestFixture]
    public class Given_a_repository_that_returns_failure_identity_conflict : UpsertHandlerTests
    {
        public class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UpsertFailureIdentityConflict(ResponseBody));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep upsertHandler = Handler(new Repository());
            await upsertHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(400);
            context.FrontendResponse.Body.Should().Be(Repository.ResponseBody);
        }
    }

    [TestFixture]
    public class Given_a_repository_that_returns_failure_write_conflict : UpsertHandlerTests
    {
        public class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UpsertFailureWriteConflict(ResponseBody));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep upsertHandler = Handler(new Repository());
            await upsertHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(409);
            context.FrontendResponse.Body.Should().Be(Repository.ResponseBody);
        }
    }

    [TestFixture]
    public class Given_a_repository_that_returns_unknown_failure : UpsertHandlerTests
    {
        public class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UnknownFailure(ResponseBody));
            }
        }

        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep upsertHandler = Handler(new Repository());
            await upsertHandler.Execute(context, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            context.FrontendResponse.StatusCode.Should().Be(500);
            context.FrontendResponse.Body.Should().Be(Repository.ResponseBody);
        }
    }
}
