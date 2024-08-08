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
using static EdFi.DataManagementService.Core.External.Backend.UpsertResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
public class UpsertHandlerTests
{
    internal static IPipelineStep Handler(IDocumentStoreRepository documentStoreRepository)
    {
        return new UpsertHandler(documentStoreRepository, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Success : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UpdateSuccess(upsertRequest.DocumentUuid));
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
            context.FrontendResponse.Headers.Count.Should().Be(0);
            context.FrontendResponse.LocationHeaderPath.Should().NotBeNullOrEmpty();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_References : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string BadResourceName1 = "BadResourceName1";
            public static readonly string BadResourceName2 = "BadResourceName2";

            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(
                    new UpsertFailureReference([new(BadResourceName1), new(BadResourceName2)])
                );
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
            context
                .FrontendResponse.Body.Should()
                .Be(
                    """
                    {"detail":"The referenced BadResourceName1, BadResourceName2 item(s) do not exist.","type":"urn:ed-fi:api:data-conflict:unresolved-reference","title":"Unresolved Reference","status":409,"correlationId":""}
                    """
                );
            context.FrontendResponse.Headers.Should().BeEmpty();
            context.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Identity_Conflict : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(
                    new UpsertFailureIdentityConflict(
                        new(""),
                        [new KeyValuePair<string, string>("key", "value")]
                    )
                );
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
            context.FrontendResponse.Body.Should().Contain("key = value");
            context.FrontendResponse.Headers.Should().BeEmpty();
            context.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Failure_Write_Conflict : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UpsertFailureWriteConflict());
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
            context.FrontendResponse.Headers.Should().BeEmpty();
            context.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Repository_That_Returns_Unknown_Failure : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
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
            context.FrontendResponse.Body.Should().Be($"{{\"error\":\"{Repository.ResponseBody}\"}}");
            context.FrontendResponse.Headers.Should().BeEmpty();
            context.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }
}
