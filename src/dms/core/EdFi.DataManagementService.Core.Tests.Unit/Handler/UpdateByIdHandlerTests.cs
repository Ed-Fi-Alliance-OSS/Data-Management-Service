// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.UpdateResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
[Parallelizable]
public class UpdateByIdHandlerTests
{
    internal static (IPipelineStep handler, IServiceProvider serviceProvider) Handler(
        IDocumentStoreRepository documentStoreRepository
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository)))
            .Returns(documentStoreRepository);

        var handler = new UpdateByIdHandler(NullLogger.Instance, ResiliencePipeline.Empty);

        return (handler, serviceProvider);
    }

    internal static RelationshipAuthorizationFailure CreateStoredRelationshipFailure() =>
        new(
            RelationshipAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 8,
            FailedStrategies:
            [
                new RelationshipAuthorizationFailedStrategy(
                    ConfiguredStrategyIndex: 0,
                    RelationshipLocalOrder: 0,
                    StrategyName: "RelationshipsWithEdOrgsOnly",
                    StrategyKind: "RelationshipsWithEdOrgsOnly",
                    AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                        Name: "auth.EdOrgIdToEdOrgId",
                        SubjectValueColumn: "TargetEdOrgId",
                        ClaimEducationOrganizationIdColumn: "SourceEdOrgId"
                    ),
                    FailedSubjects:
                    [
                        new RelationshipAuthorizationFailedSubject(
                            SubjectIndex: 0,
                            FailureKind: RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                            RootBinding: new RelationshipAuthorizationRootBinding(
                                ResourceName: "StudentSchoolAssociation",
                                TableName: "edfi.StudentSchoolAssociation",
                                ColumnName: "SchoolId"
                            ),
                            AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                                Name: "auth.EdOrgIdToEdOrgId",
                                SubjectValueColumn: "TargetEdOrgId",
                                ClaimEducationOrganizationIdColumn: "SourceEdOrgId"
                            ),
                            SecurableElements:
                            [
                                new RelationshipAuthorizationSecurableElement(
                                    Kind: "EducationOrganization",
                                    JsonPath: "$.schoolReference.schoolId",
                                    ReadableName: "SchoolId"
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            ClaimEducationOrganizationIds: [new EducationOrganizationId(255901)]
        );

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateSuccess(updateRequest.DocumentUuid));
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(204);
            requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_Etag : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateSuccess(updateRequest.DocumentUuid, "\"72\""));
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_uses_the_repository_etag_header()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(204);
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("\"72\"");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Not_Exists : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureNotExists());
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Be(
                    """"
                    {"detail":"Resource to update was not found","type":"urn:ed-fi:api:not-found","title":"Not Found","status":404,"correlationId":"","validationErrors":{},"errors":[]}
                    """"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Etag_Mismatch : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureETagMisMatch());
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet("trace-id");

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(412);
            JsonNode
                .DeepEquals(
                    requestInfo.FrontendResponse.Body,
                    FailureResponse.ForETagMisMatch(
                        "The item has been modified by another user.",
                        new TraceId("trace-id"),
                        [
                            "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved.",
                        ]
                    )
                )
                .Should()
                .BeTrue();
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Etag_Mismatch_Target_Does_Not_Exist
        : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(
                    new UpdateFailureETagMisMatch(ETagPreconditionFailureReason.TargetDoesNotExist)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet("trace-id");

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(412);

            var body = requestInfo.FrontendResponse.Body!.AsObject();
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The If-Match precondition failed because the resource does not exist.");
            body["errors"]![0]!
                .GetValue<string>()
                .Should()
                .Be(
                    "The 'If-Match' request header requires a current representation of the resource, but none exists. Do not retry with If-Match; create the resource first, or omit If-Match."
                );
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Profiled_Request : UpdateByIdHandlerTests
    {
        internal sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IUpdateRequest CapturedRequest { get; private set; } = null!;

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                CapturedRequest = updateRequest;

                return Task.FromResult<UpdateResult>(
                    new UpdateSuccess(CapturedRequest.DocumentUuid, "\"72\"")
                );
            }
        }

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();
        private readonly Repository _repository = new();
        private ContentTypeDefinition _readContentType = null!;
        private JsonNode _writableRequestBody = null!;

        private static ResourceInfo CreateResourceInfo() =>
            new(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("Student"),
                IsDescriptor: false,
                ResourceVersion: new SemVer("1.0.0"),
                AllowIdentityUpdates: false
            );

        private static ResourceSchema CreateProfileAwareResourceSchema() =>
            new(
                JsonNode.Parse(
                    """
                    {
                      "identityJsonPaths": [
                        "$.studentUniqueId",
                        "$.schoolReference.schoolId"
                      ]
                    }
                    """
                )!
            );

        private static ProfileContext CreateWriteProfileContext(ContentTypeDefinition readContentType) =>
            new(
                ProfileName: "ReadableProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: new ResourceProfile(
                    ResourceName: "Student",
                    LogicalSchema: null,
                    ReadContentType: readContentType,
                    WriteContentType: new ContentTypeDefinition(
                        MemberSelection.IncludeOnly,
                        [new PropertyRule("studentUniqueId")],
                        [],
                        [],
                        []
                    )
                ),
                WasExplicitlySpecified: true
            );

        private static BackendProfileWriteContext CreateBackendProfileWriteContext(
            JsonNode writableRequestBody
        ) =>
            new(
                Request: new ProfileAppliedWriteRequest(
                    WritableRequestBody: writableRequestBody,
                    RootResourceCreatable: true,
                    RequestScopeStates: [],
                    VisibleRequestCollectionItems: []
                ),
                ProfileName: "ReadableProfile",
                CompiledScopeCatalog: [],
                StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
            );

        [SetUp]
        public async Task Setup()
        {
            _readContentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")],
                [],
                [],
                []
            );
            _writableRequestBody = JsonNode.Parse("""{"studentUniqueId":"1000"}""")!;
            _requestInfo.ResourceInfo = CreateResourceInfo();
            _requestInfo.ResourceSchema = CreateProfileAwareResourceSchema();
            _requestInfo.ProfileContext = CreateWriteProfileContext(_readContentType);
            _requestInfo.BackendProfileWriteContext = CreateBackendProfileWriteContext(_writableRequestBody);
            _requestInfo.ParsedBody = JsonNode.Parse("""{"studentUniqueId":"1000","firstName":"Lincoln"}""")!;

            var (updateByIdHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await updateByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_keeps_profile_write_state_outside_the_write_precondition_contract()
        {
            _repository.CapturedRequest.WritePrecondition.Should().BeOfType<WritePrecondition.None>();
            _repository.CapturedRequest.BackendProfileWriteContext.Should().NotBeNull();
            _repository
                .CapturedRequest.BackendProfileWriteContext!.Request.WritableRequestBody.Should()
                .BeSameAs(_writableRequestBody);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_S_Repository_That_Returns_Failure_Reference : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            private static readonly BaseResourceInfo _targetResource = new(
                new ProjectName("ed-fi"),
                new ResourceName("School"),
                false
            );

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                var sharedReferentialId = new ReferentialId(Guid.NewGuid());

                return Task.FromResult<UpdateResult>(
                    new UpdateFailureReference(
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

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
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
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_A_Missing_Descriptor_Failure : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(
                    new UpdateFailureReference(
                        [],
                        [
                            new(
                                Path: new JsonPath("$.calendarReference.calendarTypeDescriptor"),
                                TargetResource: new BaseResourceInfo(
                                    new ProjectName("ed-fi"),
                                    new ResourceName("CalendarTypeDescriptor"),
                                    true
                                ),
                                DocumentIdentity: new([
                                    new(
                                        DocumentIdentity.DescriptorIdentityJsonPath,
                                        "uri://ed-fi.org/calendartypedescriptor#spring"
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

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
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
            validationErrors["$.calendarReference.calendarTypeDescriptor"]![0]!
                .GetValue<string>()
                .Should()
                .Be(
                    "CalendarTypeDescriptor value 'uri://ed-fi.org/calendartypedescriptor#spring' does not exist."
                );
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_A_Descriptor_Type_Mismatch_Failure : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(
                    new UpdateFailureReference(
                        [],
                        [
                            new(
                                Path: new JsonPath("$.calendarReference.calendarTypeDescriptor"),
                                TargetResource: new BaseResourceInfo(
                                    new ProjectName("ed-fi"),
                                    new ResourceName("CalendarTypeDescriptor"),
                                    true
                                ),
                                DocumentIdentity: new([
                                    new(
                                        DocumentIdentity.DescriptorIdentityJsonPath,
                                        "uri://ed-fi.org/schooltypedescriptor#elementary"
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

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
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
            validationErrors["$.calendarReference.calendarTypeDescriptor"]![0]!
                .GetValue<string>()
                .Should()
                .Be(
                    "CalendarTypeDescriptor value 'uri://ed-fi.org/schooltypedescriptor#elementary' is not a valid CalendarTypeDescriptor."
                );
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Mixed_Reference_Failures : UpdateByIdHandlerTests
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
                new ResourceName("CalendarTypeDescriptor"),
                true
            );

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(
                    new UpdateFailureReference(
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
                                Path: new JsonPath("$.calendarReference.calendarTypeDescriptor"),
                                TargetResource: _descriptorTargetResource,
                                DocumentIdentity: new([
                                    new(
                                        DocumentIdentity.DescriptorIdentityJsonPath,
                                        "uri://ed-fi.org/calendartypedescriptor#spring"
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

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
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
            validationErrors["$.calendarReference.calendarTypeDescriptor"]![0]!
                .GetValue<string>()
                .Should()
                .Be(
                    "CalendarTypeDescriptor value 'uri://ed-fi.org/calendartypedescriptor#spring' does not exist."
                );
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Identity_Conflict : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(
                    new UpdateFailureIdentityConflict(
                        new(""),
                        [new KeyValuePair<string, string>("key", "value")]
                    )
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
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
    public class Given_A_Repository_That_Returns_Failure_Write_Conflict : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureWriteConflict());
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo.FrontendResponse.Body.Should().NotBeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
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

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Relationship_Not_Authorized : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly UpdateFailureRelationshipNotAuthorized Response = new(
                CreateStoredRelationshipFailure()
            );

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(Response);
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet(
            "relationship-put-403"
        );

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_relationship_denial_to_http_403()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            requestInfo.FrontendResponse.Body.Should().NotBeNull();
            requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be("urn:ed-fi:api:security:authorization");
            requestInfo.FrontendResponse.Body!["title"]!.ToString().Should().Be("Authorization Denied");
            requestInfo.FrontendResponse.Body!["status"]!.GetValue<int>().Should().Be(403);
            requestInfo.FrontendResponse.Body!["detail"]!
                .ToString()
                .Should()
                .Be("Access to the requested data could not be authorized.");
            requestInfo.FrontendResponse.Body!["correlationId"]!
                .ToString()
                .Should()
                .Be("relationship-put-403");
            requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(static error => error!.ToString())
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    "No relationships have been established between the caller's education organization id claim ('255901') and the resource item's 'SchoolId' value."
                );
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }

        [Test]
        public void It_carries_stored_value_source_metadata()
        {
            Repository
                .Response.RelationshipFailure.ValueSource.Should()
                .Be(RelationshipAuthorizationFailureValueSource.Stored);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Namespace_Not_Authorized : UpdateByIdHandlerTests
    {
        internal static readonly NamespaceAuthorizationFailure Failure = new(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            StrategyName: "NamespaceBased",
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureNamespaceNotAuthorized(Failure));
            }
        }

        private static readonly string _traceId = "namespace-put-403";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (updateHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await updateHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_namespace_denial_to_the_canonical_problem_details_403()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            var expected = NamespaceAuthorizationFailureResponse.ForFailure(Failure, new TraceId(_traceId));

            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode.DeepEquals(_requestInfo.FrontendResponse.Body, expected).Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Not_Implemented : UpdateByIdHandlerTests
    {
        internal sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public const string ResponseBody =
                "Relational PUT authorization is not implemented for this authorization path.";

            public UpdateFailureNotImplemented Response { get; } = new(ResponseBody);

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(Response);
            }
        }

        private static readonly Repository _repository = new();
        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet(
            "relationship-put-501"
        );

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(_repository);
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_staged_fail_closed_result_to_http_501()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(501);

            var expected = Utility.ToJsonError(Repository.ResponseBody, new TraceId("relationship-put-501"));

            requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(requestInfo.FrontendResponse.Body, expected)
                .Should()
                .BeTrue(
                    $"""
                    expected: {expected}

                    actual: {requestInfo.FrontendResponse.Body}
                    """
                );
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }

        [Test]
        public void It_carries_the_staging_reason()
        {
            _repository.Response.Reason.Should().Be(UpdateFailureNotImplementedReason.StrategyNotEnabled);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Security_Configuration_Failure : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string[] ResponseErrors =
            [
                "Resource 'Ed-Fi.StudentSchoolAssociation' has relationship authorization metadata that cannot be resolved.",
            ];

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureSecurityConfiguration(ResponseErrors));
            }
        }

        private static readonly string _traceId = "relationship-put-500";
        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_security_configuration_failure_to_the_canonical_http_500()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            var expected = FailureResponse.ForSecurityConfiguration(
                new TraceId(_traceId),
                Repository.ResponseErrors
            );

            requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(requestInfo.FrontendResponse.Body, expected)
                .Should()
                .BeTrue(
                    $"""
                    expected: {expected}

                    actual: {requestInfo.FrontendResponse.Body}
                    """
                );
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Profile_Data_Policy_Failure : UpdateByIdHandlerTests
    {
        private const string ProfileName = "TestWriteProfile";

        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureProfileDataPolicy(ProfileName));
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            var body = requestInfo.FrontendResponse.Body!.AsObject();
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:data-policy-enforced");
            body["title"]!.GetValue<string>().Should().Be("Data Policy Enforced");
            body["status"]!.GetValue<int>().Should().Be(400);
            body["errors"]![0]!.GetValue<string>().Should().Contain(ProfileName);
        }
    }

    [TestFixture]
    [Parallelizable]
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
        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
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
