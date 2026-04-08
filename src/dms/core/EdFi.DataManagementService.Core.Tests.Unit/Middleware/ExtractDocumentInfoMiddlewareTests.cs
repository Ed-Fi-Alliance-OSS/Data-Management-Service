// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ExtractDocumentInfoMiddlewareTests
{
    internal static IPipelineStep BuildMiddleware(bool useRelationalBackend = true)
    {
        return new ExtractDocumentInfoMiddleware(
            Options.Create(
                new AppSettings
                {
                    AllowIdentityUpdateOverrides = "",
                    UseRelationalBackend = useRelationalBackend,
                }
            ),
            NullLogger.Instance
        );
    }

    internal static RequestInfo CreateRequestInfo(
        ResourceSchema resourceSchema,
        RequestMethod method,
        string body,
        string path = "/ed-fi/sections"
    )
    {
        return new(
            new(
                Body: body,
                Form: null,
                Headers: [],
                QueryParameters: [],
                Path: path,
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            method,
            No.ServiceProvider
        )
        {
            ResourceSchema = resourceSchema,
            ParsedBody = JsonNode.Parse(body)!,
            ResourceInfo = CreateResourceInfo(resourceSchema),
            MappingSet = CreateMappingSet(CreateResourceInfo(resourceSchema)),
        };
    }

    internal static ResourceInfo CreateResourceInfo(ResourceSchema resourceSchema)
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    internal static MappingSet CreateMappingSet(
        ResourceInfo resourceInfo,
        params DescriptorEdgeSource[] descriptorEdgeSources
    )
    {
        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), resourceInfo.ResourceName.Value),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: $"PK_{resourceInfo.ResourceName.Value}",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: null,
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        var resourceModel = new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: descriptorEdgeSources
        );
        var writePlan = new ResourceWritePlan(
            resourceModel,
            [
                new TableWritePlan(
                    TableModel: rootTable,
                    InsertSql: $"insert into edfi.\"{resourceInfo.ResourceName.Value}\" values (...)",
                    UpdateSql: null,
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(100, 1, 1000),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            rootTable.Columns[0],
                            new WriteValueSource.DocumentId(),
                            "DocumentId"
                        ),
                    ],
                    KeyUnificationPlans: []
                ),
            ]
        );
        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: new EffectiveSchemaInfo(
                "1.0",
                "v1",
                "extract-document-info-test",
                0,
                [],
                [],
                []
            ),
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder:
            [
                new ConcreteResourceModel(
                    new ResourceKeyEntry(1, resource, resourceInfo.ResourceVersion.Value, false),
                    resourceModel.StorageKind,
                    resourceModel
                ),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey("extract-document-info-test", SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = writePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    internal static ApiSchemaDocuments BuildReferenceValidationApiSchemaDocuments()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Section")
            .WithIdentityJsonPaths(["$.sectionIdentifier"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("SectionIdentifier", "$.sectionIdentifier")
            .WithDocumentPathReference(
                "CourseOffering",
                [
                    new("$.localCourseCode", "$.courseOfferingReference.localCourseCode"),
                    new("$.schoolReference.schoolId", "$.courseOfferingReference.schoolId"),
                    new("$.sessionReference.schoolYear", "$.courseOfferingReference.schoolYear"),
                    new("$.sessionReference.sessionName", "$.courseOfferingReference.sessionName"),
                ]
            )
            .WithDocumentPathReference(
                "ClassPeriod",
                [
                    new("$.classPeriodName", "$.classPeriods[*].classPeriodReference.classPeriodName"),
                    new("$.schoolReference.schoolId", "$.classPeriods[*].classPeriodReference.schoolId"),
                ]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    internal static ApiSchemaDocuments BuildReferenceDescriptorApiSchemaDocuments()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("StudentProgramAssociation")
            .WithIdentityJsonPaths(["$.associationIdentifier"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("AssociationIdentifier", "$.associationIdentifier")
            .WithDocumentPathReference(
                "Program",
                [
                    new("$.programName", "$.programs[*].programReference.programName"),
                    new("$.programTypeDescriptor", "$.programs[*].programReference.programTypeDescriptor"),
                ]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("Program")
            .WithIdentityJsonPaths(["$.programName", "$.programTypeDescriptor"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("ProgramName", "$.programName")
            .WithDocumentPathDescriptor("ProgramTypeDescriptor", "$.programTypeDescriptor")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    internal static MappingSet CreateReferenceDescriptorMappingSet()
    {
        var resourceInfo = new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("StudentProgramAssociation"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor");
        var referenceDescriptorPath = CreatePath(
            "$.programs[*].programReference.programTypeDescriptor",
            new JsonPathSegment.Property("programs"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("programReference"),
            new JsonPathSegment.Property("programTypeDescriptor")
        );
        var rootTable = new DbTableName(new DbSchemaName("edfi"), resourceInfo.ResourceName.Value);

        return CreateMappingSet(
            resourceInfo,
            new DescriptorEdgeSource(
                IsIdentityComponent: false,
                DescriptorValuePath: referenceDescriptorPath,
                Table: rootTable,
                FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                DescriptorResource: descriptorResource
            )
        );
    }

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);

    [TestFixture]
    [Parallelizable]
    public class Given_a_school_that_is_a_subclass_with_no_outbound_references
        : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("School")
                .WithIdentityJsonPaths(["$.schoolId"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("SchoolId", "$.schoolId")
                .WithEndDocumentPathsMapping()
                .WithSuperclassInformation(
                    subclassType: "domainEntity",
                    superclassIdentityJsonPath: "$.educationOrganizationId",
                    superclassResourceName: "EducationOrganization"
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "schools");

            string body = """{"schoolId": "123"}""";

            requestInfo = CreateRequestInfo(resourceSchema, RequestMethod.POST, body, "/ed-fi/schools");

            await BuildMiddleware().Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_no_document_references()
        {
            requestInfo.DocumentInfo.DocumentReferences.Should().HaveCount(0);
        }

        [Test]
        public void It_has_no_descriptor_references()
        {
            requestInfo.DocumentInfo.DescriptorReferences.Should().HaveCount(0);
        }

        [Test]
        public void It_has_built_the_document_identity()
        {
            var identityElements = requestInfo.DocumentInfo.DocumentIdentity.DocumentIdentityElements;
            identityElements.Should().HaveCount(1);
            identityElements[0].IdentityJsonPath.Value.Should().Be("$.schoolId");
            identityElements[0].IdentityValue.Should().Be("123");
        }

        public void It_has_derived_the_superclass_identity()
        {
            var superclassIdentityElements = requestInfo
                .DocumentInfo
                .SuperclassIdentity!
                .DocumentIdentity
                .DocumentIdentityElements;
            superclassIdentityElements.Should().HaveCount(1);
            superclassIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.educationOrganizationId");
            superclassIdentityElements[0].IdentityValue.Should().Be("123");
        }

        [Test]
        public void It_has_derived_the_superclass_resource_info()
        {
            var superclassResourceInfo = requestInfo.DocumentInfo.SuperclassIdentity!.ResourceInfo;

            superclassResourceInfo.IsDescriptor.Should().Be(false);
            superclassResourceInfo.ProjectName.Value.Should().Be("Ed-Fi");
            superclassResourceInfo.ResourceName.Value.Should().Be("EducationOrganization");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_An_Empty_Reference_Object : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildReferenceValidationApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");
            string body = """
                {
                    "sectionIdentifier": "Bob",
                    "courseOfferingReference": {}
                }
                """;

            _requestInfo = CreateRequestInfo(resourceSchema, RequestMethod.POST, body);

            await BuildMiddleware()
                .Execute(_requestInfo, () => throw new AssertionException("next should not run"));
        }

        [Test]
        public void It_returns_a_validation_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body!["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");
        }

        [Test]
        public void It_reports_the_empty_reference_at_the_root_reference_path()
        {
            _requestInfo.FrontendResponse.Body!["validationErrors"]!["$.courseOfferingReference"]![0]!
                .GetValue<string>()
                .Should()
                .Contain("$.courseOfferingReference.localCourseCode");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Legacy_Post_Request_With_An_Empty_Reference_Object
        : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildReferenceValidationApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");
            string body = """
                {
                    "sectionIdentifier": "Bob",
                    "courseOfferingReference": {}
                }
                """;

            _requestInfo = CreateRequestInfo(resourceSchema, RequestMethod.POST, body);

            await BuildMiddleware(useRelationalBackend: false)
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_continues_the_pipeline_without_creating_a_validation_response()
        {
            _nextCalled.Should().BeTrue();
            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }

        [Test]
        public void It_omits_the_malformed_reference_from_document_info()
        {
            _requestInfo.DocumentInfo.DocumentReferences.Should().BeEmpty();
            _requestInfo.DocumentInfo.DocumentReferenceArrays.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Put_Request_With_A_Partial_Nested_Reference_Object
        : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildReferenceValidationApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");
            string body = """
                {
                    "sectionIdentifier": "Bob",
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": "Class Period 1"
                            }
                        }
                    ]
                }
                """;

            _requestInfo = CreateRequestInfo(resourceSchema, RequestMethod.PUT, body);

            await BuildMiddleware()
                .Execute(_requestInfo, () => throw new AssertionException("next should not run"));
        }

        [Test]
        public void It_returns_a_validation_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body!["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");
        }

        [Test]
        public void It_reports_the_partial_reference_at_the_nested_reference_path()
        {
            _requestInfo.FrontendResponse.Body!["validationErrors"]![
                "$.classPeriods[0].classPeriodReference"
            ]![0]!
                .GetValue<string>()
                .Should()
                .Contain("$.classPeriods[0].classPeriodReference.schoolId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Legacy_Put_Request_With_A_Partial_Nested_Reference_Object
        : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildReferenceValidationApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");
            string body = """
                {
                    "sectionIdentifier": "Bob",
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": "Class Period 1"
                            }
                        }
                    ]
                }
                """;

            _requestInfo = CreateRequestInfo(resourceSchema, RequestMethod.PUT, body);

            await BuildMiddleware(useRelationalBackend: false)
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_continues_the_pipeline_without_creating_a_validation_response()
        {
            _nextCalled.Should().BeTrue();
            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }

        [Test]
        public void It_omits_the_partial_nested_reference_from_document_info()
        {
            _requestInfo.DocumentInfo.DocumentReferences.Should().BeEmpty();
            _requestInfo.DocumentInfo.DocumentReferenceArrays.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Relational_Post_Request_With_A_Collection_Reference_Descriptor_Identity_Member
        : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildReferenceDescriptorApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocument,
                "studentProgramAssociations"
            );
            string body = """
                {
                    "associationIdentifier": "A-1",
                    "programs": [
                        {
                            "programReference": {
                                "programName": "STEM",
                                "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#STEM"
                            }
                        },
                        {
                            "programReference": {
                                "programName": "Arts",
                                "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#FineArts"
                            }
                        }
                    ]
                }
                """;

            _requestInfo = CreateRequestInfo(
                resourceSchema,
                RequestMethod.POST,
                body,
                "/ed-fi/studentProgramAssociations"
            );
            _requestInfo.ResourceInfo = new ResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("StudentProgramAssociation"),
                IsDescriptor: false,
                ResourceVersion: new SemVer("1.0.0"),
                AllowIdentityUpdates: false,
                EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
                AuthorizationSecurableInfo: []
            );
            _requestInfo.MappingSet = CreateReferenceDescriptorMappingSet();

            await BuildMiddleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_extracts_descriptor_references_for_the_collection_members()
        {
            _requestInfo.DocumentInfo.DescriptorReferences.Should().HaveCount(2);
        }

        [Test]
        public void It_extracts_the_first_collection_member_with_the_expected_resource_type_and_path()
        {
            _requestInfo
                .DocumentInfo.DescriptorReferences[0]
                .ResourceInfo.ResourceName.Value.Should()
                .Be("ProgramTypeDescriptor");
            _requestInfo
                .DocumentInfo.DescriptorReferences[0]
                .Path.Value.Should()
                .Be("$.programs[0].programReference.programTypeDescriptor");
            _requestInfo
                .DocumentInfo.DescriptorReferences[0]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/programtypedescriptor#stem");
        }

        [Test]
        public void It_extracts_the_second_collection_member_with_the_expected_resource_type_and_path()
        {
            _requestInfo
                .DocumentInfo.DescriptorReferences[1]
                .ResourceInfo.ResourceName.Value.Should()
                .Be("ProgramTypeDescriptor");
            _requestInfo
                .DocumentInfo.DescriptorReferences[1]
                .Path.Value.Should()
                .Be("$.programs[1].programReference.programTypeDescriptor");
            _requestInfo
                .DocumentInfo.DescriptorReferences[1]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/programtypedescriptor#finearts");
        }
    }

    [TestFixture("null", "null")]
    [TestFixture("{}", "a JSON object")]
    [TestFixture("[]", "a JSON array")]
    [Parallelizable]
    public class Given_A_Post_Request_With_A_Malformed_Root_Reference_Identity_Member(
        string _invalidValueJson,
        string _expectedInvalidValueDescription
    ) : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildReferenceValidationApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");
            string body = $$"""
                {
                    "sectionIdentifier": "Bob",
                    "courseOfferingReference": {
                        "localCourseCode": {{_invalidValueJson}},
                        "schoolId": "23",
                        "schoolYear": 1234,
                        "sessionName": "aSessionName"
                    }
                }
                """;

            _requestInfo = CreateRequestInfo(resourceSchema, RequestMethod.POST, body);

            await BuildMiddleware()
                .Execute(_requestInfo, () => throw new AssertionException("next should not run"));
        }

        [Test]
        public void It_returns_a_validation_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body!["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");
        }

        [Test]
        public void It_reports_the_invalid_root_member_at_its_concrete_path()
        {
            _requestInfo.FrontendResponse.Body!["validationErrors"]![
                "$.courseOfferingReference.localCourseCode"
            ]![0]!
                .GetValue<string>()
                .Should()
                .Contain("must be a scalar value when present")
                .And.Contain(_expectedInvalidValueDescription);
        }
    }

    [TestFixture("null", "null")]
    [TestFixture("{}", "a JSON object")]
    [TestFixture("[]", "a JSON array")]
    [Parallelizable]
    public class Given_A_Put_Request_With_A_Malformed_Nested_Reference_Identity_Member(
        string _invalidValueJson,
        string _expectedInvalidValueDescription
    ) : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildReferenceValidationApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");
            string body = $$"""
                {
                    "sectionIdentifier": "Bob",
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": {{_invalidValueJson}},
                                "schoolId": "111"
                            }
                        }
                    ]
                }
                """;

            _requestInfo = CreateRequestInfo(resourceSchema, RequestMethod.PUT, body);

            await BuildMiddleware()
                .Execute(_requestInfo, () => throw new AssertionException("next should not run"));
        }

        [Test]
        public void It_returns_a_validation_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body!["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");
        }

        [Test]
        public void It_reports_the_invalid_nested_member_at_its_concrete_path()
        {
            _requestInfo.FrontendResponse.Body!["validationErrors"]![
                "$.classPeriods[0].classPeriodReference.classPeriodName"
            ]![0]!
                .GetValue<string>()
                .Should()
                .Contain("must be a scalar value when present")
                .And.Contain(_expectedInvalidValueDescription);
        }
    }
}
