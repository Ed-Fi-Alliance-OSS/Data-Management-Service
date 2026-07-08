// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.DeleteResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
[Parallelizable]
public class DeleteByIdHandlerTests
{
    internal static RelationshipAuthorizationFailure CreateRelationshipFailure() =>
        new(
            RelationshipAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 12,
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
                                ResourceName: "School",
                                TableName: "edfi.School",
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
                                    JsonPath: "$.schoolId",
                                    ReadableName: "SchoolId"
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            ClaimEducationOrganizationIds: [new EducationOrganizationId(255901)]
        );

    internal static (IPipelineStep handler, IServiceProvider serviceProvider) Handler(
        IDocumentStoreRepository documentStoreRepository
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository)))
            .Returns(documentStoreRepository);

        var handler = new DeleteByIdHandler(NullLogger.Instance, ResiliencePipeline.Empty);

        return (handler, serviceProvider);
    }

    internal static ResourceSchema GetResourceSchema()
    {
        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Assessment")
            .WithNamespaceSecurityElements(["$.namespace"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("Namespace", "$.namespace")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "assessments");
        return resourceSchema;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteSuccess());
            }
        }

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(204);
            _requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Not_Exists : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureNotExists());
            }
        }

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);

            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            _requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
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

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);

            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(409);
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(string.Join(", ", Repository.ResponseBody));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Write_Conflict : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureWriteConflict());
            }
        }

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Etag_Mismatch : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureETagMisMatch());
            }
        }

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet("trace-id");

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(412);
            JsonNode
                .DeepEquals(
                    _requestInfo.FrontendResponse.Body,
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
            _requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Etag_Mismatch_Target_Does_Not_Exist
        : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(
                    new DeleteFailureETagMisMatch(ETagPreconditionFailureReason.TargetDoesNotExist)
                );
            }
        }

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet("trace-id");

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(412);

            var body = _requestInfo.FrontendResponse.Body!.AsObject();
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
            _requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Relationship_Not_Authorized : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(
                    new DeleteFailureRelationshipNotAuthorized(CreateRelationshipFailure())
                );
            }
        }

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(
            "relationship-delete-403"
        );

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            _requestInfo.ResourceSchema = GetResourceSchema();

            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_relationship_failure_to_http_403()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be("urn:ed-fi:api:security:authorization");
            _requestInfo.FrontendResponse.Body!["title"]!.ToString().Should().Be("Authorization Denied");
            _requestInfo.FrontendResponse.Body!["status"]!.GetValue<int>().Should().Be(403);
            _requestInfo.FrontendResponse.Body!["detail"]!
                .ToString()
                .Should()
                .Be("Access to the requested data could not be authorized.");
            _requestInfo.FrontendResponse.Body!["correlationId"]!
                .ToString()
                .Should()
                .Be("relationship-delete-403");
            _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(static error => error!.ToString())
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    "No relationships have been established between the caller's education organization id claim ('255901') and the resource item's 'SchoolId' value."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Namespace_Not_Authorized : DeleteByIdHandlerTests
    {
        internal static readonly NamespaceAuthorizationFailure Failure = new(
            NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            StrategyName: "NamespaceBased",
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureNamespaceNotAuthorized(Failure));
            }
        }

        private static readonly string _traceId = "namespace-delete-403";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            _requestInfo.ResourceSchema = GetResourceSchema();

            var (deleteHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await deleteHandler.Execute(_requestInfo, NullNext);
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
    public class Given_A_Repository_That_Returns_Failure_Not_Implemented : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureNotImplemented(ResponseBody));
            }
        }

        private static readonly string _traceId = "relationship-delete-501";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            _requestInfo.ResourceSchema = GetResourceSchema();

            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_staged_failure_to_http_501()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(501);

            var expected = Utility.ToJsonError(Repository.ResponseBody, new TraceId(_traceId));

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

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Security_Configuration_Failure : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string[] ResponseErrors =
            [
                "Resource 'Ed-Fi.School' has relationship authorization metadata that cannot be resolved.",
            ];

            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureSecurityConfiguration(ResponseErrors));
            }
        }

        private static readonly string _traceId = "relationship-delete-500";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            _requestInfo.ResourceSchema = GetResourceSchema();

            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_security_configuration_failure_to_the_canonical_http_500()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            var expected = FailureResponse.ForSecurityConfiguration(
                new TraceId(_traceId),
                Repository.ResponseErrors
            );

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

    [TestFixture]
    [Parallelizable]
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

        private static readonly string _traceId = "xyz";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);

            var expected = $$"""
{
  "error": "FailureMessage",
  "correlationId": "{{_traceId}}"
}
""";

            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(_requestInfo.FrontendResponse.Body, JsonNode.Parse(expected))
                .Should()
                .BeTrue(
                    $"""
expected: {expected}

actual: {_requestInfo.FrontendResponse.Body}
"""
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Profiled_Delete_Request : DeleteByIdHandlerTests
    {
        internal sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IDeleteRequest CapturedRequest { get; private set; } = null!;

            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                CapturedRequest = deleteRequest;
                return Task.FromResult<DeleteResult>(new DeleteSuccess());
            }
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(
            "delete-profile-trace"
        );

        private static ResourceInfo CreateResourceInfo() =>
            new(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("Assessment"),
                IsDescriptor: false,
                ResourceVersion: new SemVer("1.0.0"),
                AllowIdentityUpdates: false
            );

        private static ProfileContext CreateWriteProfileContext() =>
            new(
                ProfileName: "ReadableProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: new ResourceProfile(
                    ResourceName: "Assessment",
                    LogicalSchema: null,
                    ReadContentType: new ContentTypeDefinition(
                        MemberSelection.IncludeOnly,
                        [new PropertyRule("assessmentTitle")],
                        [],
                        [],
                        []
                    ),
                    WriteContentType: new ContentTypeDefinition(
                        MemberSelection.IncludeOnly,
                        [new PropertyRule("assessmentTitle")],
                        [],
                        [],
                        []
                    )
                ),
                WasExplicitlySpecified: true
            );

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.FrontendRequest = new FrontendRequest(
                Body: "{}",
                Form: [],
                Headers: new Dictionary<string, string> { ["If-Match"] = "\"72\"" },
                Path: "/ed-fi/assessments/aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
                QueryParameters: [],
                TraceId: new TraceId("delete-profile-trace"),
                RouteQualifiers: []
            );
            _requestInfo.ProjectSchema = new ProjectSchema(
                new JsonObject
                {
                    ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
                },
                NullLogger.Instance
            );
            _requestInfo.PathComponents = new PathComponents(
                new ProjectEndpointName("ed-fi"),
                new EndpointName("assessments"),
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            );
            _requestInfo.ResourceInfo = CreateResourceInfo();
            _requestInfo.ResourceSchema = GetResourceSchema();
            _requestInfo.ProfileContext = CreateWriteProfileContext();
            _requestInfo.AuthorizationStrategyEvaluators =
            [
                new("RelationshipsWithEdOrgsOnly", [], FilterOperator.Or),
            ];
            _requestInfo.ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token-id",
                ClientId: "client-id",
                ClaimSetName: "claim-set",
                EducationOrganizationIds:
                [
                    new EducationOrganizationId(255902),
                    new EducationOrganizationId(255901),
                    new EducationOrganizationId(255902),
                ],
                NamespacePrefixes:
                [
                    new NamespacePrefix("uri://sample-b.org"),
                    new NamespacePrefix("uri://sample-a.org"),
                    new NamespacePrefix("uri://sample-b.org"),
                ],
                DataStoreIds: []
            );

            var (deleteByIdHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_normalizes_if_match_to_the_unquoted_opaque_tag()
        {
            // The inbound If-Match arrives quoted ("72"); WritePreconditionFactory strips the strong
            // entity-tag quotes so the backend compares the unquoted opaque tag (72).
            _repository.CapturedRequest.WritePrecondition.Should().Be(new WritePrecondition.IfMatch("72"));
        }

        [Test]
        public void It_carries_relational_authorization_inputs()
        {
            var relationalRequest = _repository
                .CapturedRequest.Should()
                .BeAssignableTo<IDeleteRequest>()
                .Subject;

            relationalRequest
                .AuthorizationStrategyEvaluators.Should()
                .BeSameAs(_requestInfo.AuthorizationStrategyEvaluators);
            relationalRequest
                .AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .Equal(255901L, 255902L);
            relationalRequest
                .AuthorizationContext.NamespacePrefixes.Should()
                .Equal("uri://sample-a.org", "uri://sample-b.org");
        }
    }
}
