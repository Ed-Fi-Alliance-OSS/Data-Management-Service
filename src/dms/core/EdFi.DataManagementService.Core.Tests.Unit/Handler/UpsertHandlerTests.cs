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
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.UpsertResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

public class UpsertHandlerTests
{
    internal static (IPipelineStep handler, IServiceProvider serviceProvider) Handler(
        IDocumentStoreRepository documentStoreRepository
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository)))
            .Returns(documentStoreRepository);

        var handler = new UpsertHandler(
            NullLogger.Instance,
            ResiliencePipeline.Empty,
            new UpdateByIdHandlerTests.Provider(),
            new NoAuthorizationServiceFactory()
        );

        return (handler, serviceProvider);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UpdateSuccess(upsertRequest.DocumentUuid));
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body.Should().BeNull();
            requestInfo.FrontendResponse.Headers.Count.Should().Be(1);
            requestInfo.FrontendResponse.LocationHeaderPath.Should().NotBeNullOrEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_References : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            private static readonly BaseResourceInfo _targetResource = new(
                new ProjectName("ed-fi"),
                new ResourceName("School"),
                false
            );

            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                var sharedReferentialId = new ReferentialId(Guid.NewGuid());

                return Task.FromResult<UpsertResult>(
                    new UpsertFailureReference(
                        [
                            new(
                                Path: new JsonPath("$.schoolReference"),
                                TargetResource: _targetResource,
                                DocumentIdentity: new([]),
                                ReferentialId: sharedReferentialId,
                                Reason: DocumentReferenceFailureReason.Missing
                            ),
                            new(
                                Path: new JsonPath("$.sessionReference.schoolReference"),
                                TargetResource: _targetResource,
                                DocumentIdentity: new([]),
                                ReferentialId: sharedReferentialId,
                                Reason: DocumentReferenceFailureReason.Missing
                            ),
                        ],
                        []
                    )
                );
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(409);

            var body = requestInfo.FrontendResponse.Body!.AsObject();
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("One or more references could not be resolved. See 'validationErrors' for details.");
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:data-conflict:unresolved-reference");
            body["title"]!.GetValue<string>().Should().Be("Unresolved Reference");

            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors.Count.Should().Be(2);
            validationErrors["$.schoolReference"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The referenced School item does not exist.");
            validationErrors["$.sessionReference.schoolReference"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The referenced School item does not exist.");
            body["errors"]!.AsArray().Count.Should().Be(0);
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_A_Missing_Descriptor_Failure : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(
                    new UpsertFailureReference(
                        [],
                        [
                            new(
                                Path: new JsonPath("$.schoolTypeDescriptor"),
                                TargetResource: new BaseResourceInfo(
                                    new ProjectName("ed-fi"),
                                    new ResourceName("SchoolTypeDescriptor"),
                                    true
                                ),
                                DocumentIdentity: new([
                                    new(
                                        DocumentIdentity.DescriptorIdentityJsonPath,
                                        "uri://ed-fi.org/schooltypedescriptor#elementary"
                                    ),
                                ]),
                                ReferentialId: new ReferentialId(Guid.NewGuid()),
                                Reason: DescriptorReferenceFailureReason.Missing
                            ),
                        ]
                    )
                );
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            var body = requestInfo.FrontendResponse.Body!.AsObject();
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");

            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors.Count.Should().Be(1);
            validationErrors["$.schoolTypeDescriptor"]![0]!
                .GetValue<string>()
                .Should()
                .Be(
                    "SchoolTypeDescriptor value 'uri://ed-fi.org/schooltypedescriptor#elementary' does not exist."
                );
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_A_Descriptor_Type_Mismatch_Failure : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(
                    new UpsertFailureReference(
                        [],
                        [
                            new(
                                Path: new JsonPath("$.schoolTypeDescriptor"),
                                TargetResource: new BaseResourceInfo(
                                    new ProjectName("ed-fi"),
                                    new ResourceName("SchoolTypeDescriptor"),
                                    true
                                ),
                                DocumentIdentity: new([
                                    new(
                                        DocumentIdentity.DescriptorIdentityJsonPath,
                                        "uri://ed-fi.org/gradeleveldescriptor#first-grade"
                                    ),
                                ]),
                                ReferentialId: new ReferentialId(Guid.NewGuid()),
                                Reason: DescriptorReferenceFailureReason.DescriptorTypeMismatch
                            ),
                        ]
                    )
                );
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            var body = requestInfo.FrontendResponse.Body!.AsObject();
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");

            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors.Count.Should().Be(1);
            validationErrors["$.schoolTypeDescriptor"]![0]!
                .GetValue<string>()
                .Should()
                .Be(
                    "SchoolTypeDescriptor value 'uri://ed-fi.org/gradeleveldescriptor#first-grade' is not a valid SchoolTypeDescriptor."
                );
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Mixed_Reference_Failures : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            private static readonly BaseResourceInfo _documentTargetResource = new(
                new ProjectName("ed-fi"),
                new ResourceName("School"),
                false
            );

            private static readonly BaseResourceInfo _descriptorTargetResource = new(
                new ProjectName("ed-fi"),
                new ResourceName("SchoolTypeDescriptor"),
                true
            );

            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(
                    new UpsertFailureReference(
                        [
                            new(
                                Path: new JsonPath("$.schoolReference"),
                                TargetResource: _documentTargetResource,
                                DocumentIdentity: new([]),
                                ReferentialId: new ReferentialId(Guid.NewGuid()),
                                Reason: DocumentReferenceFailureReason.Missing
                            ),
                        ],
                        [
                            new(
                                Path: new JsonPath("$.schoolTypeDescriptor"),
                                TargetResource: _descriptorTargetResource,
                                DocumentIdentity: new([
                                    new(
                                        DocumentIdentity.DescriptorIdentityJsonPath,
                                        "uri://ed-fi.org/schooltypedescriptor#elementary"
                                    ),
                                ]),
                                ReferentialId: new ReferentialId(Guid.NewGuid()),
                                Reason: DescriptorReferenceFailureReason.Missing
                            ),
                        ]
                    )
                );
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            var body = requestInfo.FrontendResponse.Body!.AsObject();
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
            body["title"]!.GetValue<string>().Should().Be("Bad Request");
            body["status"]!.GetValue<int>().Should().Be(400);

            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors.Count.Should().Be(2);
            validationErrors["$.schoolReference"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The referenced School item does not exist.");
            validationErrors["$.schoolTypeDescriptor"]![0]!
                .GetValue<string>()
                .Should()
                .Be(
                    "SchoolTypeDescriptor value 'uri://ed-fi.org/schooltypedescriptor#elementary' does not exist."
                );
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
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

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(409);
            requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("key = value");
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Write_Conflict : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(new UpsertFailureWriteConflict());
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo.FrontendResponse.Body.Should().NotBeNull();
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Immutable_Identity : UpsertHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
            {
                return Task.FromResult<UpsertResult>(
                    new UpsertFailureImmutableIdentity(
                        "Identifying values for the resource cannot be changed. Delete and recreate the resource item instead."
                    )
                );
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Key Change Not Supported");
            requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Identifying values for the resource cannot be changed.");
        }
    }

    [TestFixture]
    [Parallelizable]
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

        private static readonly string _traceId = "xyz";
        private readonly RequestInfo requestInfo = No.RequestInfo(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (upsertHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await upsertHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(500);

            var expected = $$"""
{
  "error": "FailureMessage",
  "correlationId": "{{_traceId}}"
}
""";

            requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(requestInfo.FrontendResponse.Body, JsonNode.Parse(expected))
                .Should()
                .BeTrue(
                    $"""
expected: {expected}

actual: {requestInfo.FrontendResponse.Body}
"""
                );
        }
    }
}
