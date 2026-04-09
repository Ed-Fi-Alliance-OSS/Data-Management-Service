// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Extraction;

[TestFixture]
[Parallelizable]
public class DescriptorExtractorTests
{
    internal static ApiSchemaDocuments BuildApiSchemaDocuments()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("SlimCourse")
            .WithIdentityJsonPaths([
                "$.courseTitle",
                "$.careerPathwayDescriptor",
                "$.gradingPeriodDescriptor",
            ])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("CourseTitle", "$.courseTitle")
            .WithDocumentPathDescriptor("CareerPathwayDescriptor", "$.careerPathwayDescriptor")
            .WithDocumentPathDescriptor(
                "CompetencyLevelDescriptor",
                "$.competencyLevels[*].competencyLevelDescriptor"
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    internal static ApiSchemaDocuments BuildRelationalReferenceDescriptorApiSchemaDocuments()
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

    internal static ApiSchemaDocuments BuildDescriptorResourceApiSchemaDocuments()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("ProgramTypeDescriptor", isDescriptor: true)
            .WithIdentityJsonPaths(["$.codeValue"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("CodeValue", "$.codeValue")
            .WithDocumentPathDescriptor("AcademicSubjectDescriptor", "$.academicSubjectDescriptor")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    internal static ResourceInfo CreateResourceInfo(string resourceName, bool isDescriptor = false)
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName(resourceName),
            IsDescriptor: isDescriptor,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    internal static MappingSet CreateRelationalMappingSet(
        ResourceInfo resourceInfo,
        bool includeWritePlan,
        params DescriptorEdgeSource[] descriptorEdgeSources
    )
    {
        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );
        var rootTableName = new DbTableName(new DbSchemaName("edfi"), resourceInfo.ResourceName.Value);
        var rootTable = new DbTableModel(
            Table: rootTableName,
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
        var resourceKey = new ResourceKeyEntry(1, resource, resourceInfo.ResourceVersion.Value, false);
        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: new EffectiveSchemaInfo(
                "1.0",
                "v1",
                "descriptor-extractor-test",
                1,
                [],
                [],
                [resourceKey]
            ),
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder:
            [
                new ConcreteResourceModel(resourceKey, resourceModel.StorageKind, resourceModel),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey("descriptor-extractor-test", SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: includeWritePlan
                ? new Dictionary<QualifiedResourceName, ResourceWritePlan> { [resource] = writePlan }
                : new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    internal static MappingSet CreateRelationalReferenceDescriptorMappingSet(bool includeWritePlan)
    {
        var resourceInfo = CreateResourceInfo("StudentProgramAssociation");
        var referenceDescriptorPath = CreatePath(
            "$.programs[*].programReference.programTypeDescriptor",
            new JsonPathSegment.Property("programs"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("programReference"),
            new JsonPathSegment.Property("programTypeDescriptor")
        );

        return CreateRelationalMappingSet(
            resourceInfo,
            includeWritePlan,
            new DescriptorEdgeSource(
                IsIdentityComponent: false,
                DescriptorValuePath: referenceDescriptorPath,
                Table: new DbTableName(new DbSchemaName("edfi"), resourceInfo.ResourceName.Value),
                FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                DescriptorResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
            )
        );
    }

    internal static MappingSet CreateConflictingDescriptorPathMappingSet()
    {
        var resourceInfo = CreateResourceInfo("SlimCourse");
        var conflictingDescriptorPath = CreatePath(
            "$.careerPathwayDescriptor",
            new JsonPathSegment.Property("careerPathwayDescriptor")
        );

        return CreateRelationalMappingSet(
            resourceInfo,
            includeWritePlan: true,
            new DescriptorEdgeSource(
                IsIdentityComponent: false,
                DescriptorValuePath: conflictingDescriptorPath,
                Table: new DbTableName(new DbSchemaName("edfi"), resourceInfo.ResourceName.Value),
                FkColumn: new DbColumnName("CareerPathwayDescriptorId"),
                DescriptorResource: new QualifiedResourceName("Ed-Fi", "GradingPeriodDescriptor")
            )
        );
    }

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Descriptor_References_With_One_As_Scalar_And_Another_As_Collection
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "slimCourses");

            descriptorReferences = resourceSchema.ExtractDescriptors(
                JsonNode.Parse(
                    """
                    {
                        "courseTitle": "Math",
                        "careerPathwayDescriptor": "uri://ed-fi.org/CareerPathwayDescriptor#Other",
                        "competencyLevels": [
                            {
                                "competencyLevelDescriptor": "uri://ed-fi.org/CompetencyLevelDescriptor#Basic"
                            },
                            {
                                "competencyLevelDescriptor": "uri://ed-fi.org/CompetencyLevelDescriptor#Advanced"
                            }
                        ]
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_three_references()
        {
            descriptorReferences.Should().HaveCount(3);
        }

        [Test]
        public void It_has_extracted_the_career_pathway()
        {
            var documentReference = descriptorReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CareerPathwayDescriptor");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(1);
            documentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DocumentIdentity.DescriptorIdentityJsonPath);
            documentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/careerpathwaydescriptor#other");
        }

        [Test]
        public void It_has_extracted_the_first_competency_Level()
        {
            var documentReference = descriptorReferences[1];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CompetencyLevelDescriptor");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(1);
            documentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DocumentIdentity.DescriptorIdentityJsonPath);
            documentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/competencyleveldescriptor#basic");
        }

        [Test]
        public void It_has_extracted_the_second_competency_Level()
        {
            var documentReference = descriptorReferences[2];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CompetencyLevelDescriptor");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(1);
            documentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DocumentIdentity.DescriptorIdentityJsonPath);
            documentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/competencyleveldescriptor#advanced");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Descriptor_References_As_Collection_With_Index : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "slimCourses");

            descriptorReferences = resourceSchema.ExtractDescriptors(
                JsonNode.Parse(
                    """
                    {
                        "courseTitle": "Math",
                        "careerPathwayDescriptor": "uri://ed-fi.org/CareerPathwayDescriptor#Other",
                        "competencyLevels": [
                            {
                                "competencyLevelDescriptor": "uri://ed-fi.org/CompetencyLevelDescriptor#Basic"
                            },
                            {
                                "competencyLevelDescriptor": "uri://ed-fi.org/CompetencyLevelDescriptor#Advanced"
                            }
                        ]
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_three_references()
        {
            descriptorReferences.Should().HaveCount(3);
        }

        [Test]
        public void It_has_extracted_the_career_pathway()
        {
            var documentReference = descriptorReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CareerPathwayDescriptor");
            documentReference.Path.Value.Should().Be("$.careerPathwayDescriptor");
        }

        [Test]
        public void It_has_extracted_the_first_competency_Level_Path_With_Index()
        {
            var documentReference = descriptorReferences[1];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CompetencyLevelDescriptor");
            documentReference.Path.Value.Should().Be("$.competencyLevels[0].competencyLevelDescriptor");
        }

        [Test]
        public void It_has_extracted_the_second_competency_Level_Path_With_Index()
        {
            var documentReference = descriptorReferences[2];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CompetencyLevelDescriptor");
            documentReference.Path.Value.Should().Be("$.competencyLevels[1].competencyLevelDescriptor");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Descriptor_References_With_Missing_Optional_Scalar_Descriptor_In_Body
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "slimCourses");
            descriptorReferences = resourceSchema.ExtractDescriptors(
                JsonNode.Parse(
                    """
                    {
                        "courseTitle": "Math",
                        "competencyLevels": [
                            {
                                "competencyLevelDescriptor": "uri://ed-fi.org/CompetencyLevelDescriptor#Basic"
                            },
                            {
                                "competencyLevelDescriptor": "uri://ed-fi.org/CompetencyLevelDescriptor#Advanced"
                            }
                        ]
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_two_references()
        {
            descriptorReferences.Should().HaveCount(2);
        }

        [Test]
        public void It_has_extracted_the_first_competency_Level()
        {
            var documentReference = descriptorReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CompetencyLevelDescriptor");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(1);
            documentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DocumentIdentity.DescriptorIdentityJsonPath);
            documentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/competencyleveldescriptor#basic");
        }

        [Test]
        public void It_has_extracted_the_second_competency_Level()
        {
            var documentReference = descriptorReferences[1];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CompetencyLevelDescriptor");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(1);
            documentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DocumentIdentity.DescriptorIdentityJsonPath);
            documentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/competencyleveldescriptor#advanced");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Descriptor_References_With_Only_Single_Reference_In_Collection_In_Body
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "slimCourses");
            descriptorReferences = resourceSchema.ExtractDescriptors(
                JsonNode.Parse(
                    """
                    {
                        "courseTitle": "Math",
                        "competencyLevels": [
                            {
                                "competencyLevelDescriptor": "uri://ed-fi.org/CompetencyLevelDescriptor#Advanced"
                            }
                        ]
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_one_reference()
        {
            descriptorReferences.Should().HaveCount(1);
        }

        [Test]
        public void It_has_extracted_the_competency_Level()
        {
            var documentReference = descriptorReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CompetencyLevelDescriptor");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(1);
            documentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DocumentIdentity.DescriptorIdentityJsonPath);
            documentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/competencyleveldescriptor#advanced");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Descriptor_References_With_Empty_Reference_Collection_In_Body
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "slimCourses");

            descriptorReferences = resourceSchema.ExtractDescriptors(
                JsonNode.Parse(
                    """
                    {
                        "courseTitle": "Math",
                        "competencyLevels": [
                        ]
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_no_references()
        {
            descriptorReferences.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Descriptor_References_With_Missing_Optional_Class_Period_Reference_Collection_In_Body
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "slimCourses");

            descriptorReferences = resourceSchema.ExtractDescriptors(
                JsonNode.Parse(
                    """
                    {
                        "courseTitle": "Math",
                        "careerPathwayDescriptor": "uri://ed-fi.org/CareerPathwayDescriptor#Other"
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_one_reference()
        {
            descriptorReferences.Should().HaveCount(1);
        }

        [Test]
        public void It_has_extracted_the_career_pathway()
        {
            var documentReference = descriptorReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CareerPathwayDescriptor");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(1);
            documentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DocumentIdentity.DescriptorIdentityJsonPath);
            documentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/careerpathwaydescriptor#other");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Descriptor_References_With_No_References_In_Body : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "slimCourses");

            descriptorReferences = resourceSchema.ExtractDescriptors(
                JsonNode.Parse(
                    """
                    {
                        "courseTitle": "Math"
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_no_references()
        {
            descriptorReferences.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Descriptor_References_With_Mixed_Case_Namespace_Portions
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        internal static ApiSchemaDocuments BuildMixedCaseApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("TestResource")
                .WithIdentityJsonPaths(["$.testTitle"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("TestTitle", "$.testTitle")
                .WithDocumentPathDescriptor(
                    "COURSEGPAApplicabilityDescriptor",
                    "$.courseGpaApplicabilityDescriptor"
                )
                .WithDocumentPathDescriptor("AcademicSUBJECTDescriptor", "$.academicSubjectDescriptor")
                .WithDocumentPathDescriptor("MixedCASEDescriptor", "$.mixedCaseDescriptor")
                .WithDocumentPathDescriptor(
                    "CollectionMIXEDCaseDescriptor",
                    "$.testCollection[*].mixedCaseDescriptor"
                )
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildMixedCaseApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "testResources");

            descriptorReferences = resourceSchema.ExtractDescriptors(
                JsonNode.Parse(
                    """
                    {
                        "testTitle": "Test",
                        "courseGpaApplicabilityDescriptor": "uri://ed-fi.org/COURSEGPAApplicabilityDescriptor#Applicable",
                        "academicSubjectDescriptor": "uri://ed-fi.org/academicSUBJECTDescriptor#Mathematics", 
                        "mixedCaseDescriptor": "uri://ed-fi.org/MixedCASEDescriptor#SomeValue",
                        "testCollection": [
                            {
                                "mixedCaseDescriptor": "uri://ed-fi.org/CollectionMIXEDCaseDescriptor#First"
                            },
                            {
                                "mixedCaseDescriptor": "uri://ed-fi.org/CollectionMIXEDCaseDescriptor#Second"
                            }
                        ]
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_five_references()
        {
            descriptorReferences.Should().HaveCount(5);
        }

        [Test]
        public void It_normalizes_COURSEGPAApplicabilityDescriptor_to_lowercase()
        {
            var courseGpaDescriptor = Array.Find(
                descriptorReferences,
                r => r.ResourceInfo.ResourceName.Value == "COURSEGPAApplicabilityDescriptor"
            );

            courseGpaDescriptor.Should().NotBeNull();
            courseGpaDescriptor!
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/coursegpaapplicabilitydescriptor#applicable");
        }

        [Test]
        public void It_normalizes_academicSUBJECTDescriptor_to_lowercase()
        {
            var academicSubjectDescriptor = Array.Find(
                descriptorReferences,
                r => r.ResourceInfo.ResourceName.Value == "AcademicSUBJECTDescriptor"
            );

            academicSubjectDescriptor.Should().NotBeNull();
            academicSubjectDescriptor!
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/academicsubjectdescriptor#mathematics");
        }

        [Test]
        public void It_normalizes_MixedCASEDescriptor_to_lowercase()
        {
            var mixedCaseDescriptor = Array.Find(
                descriptorReferences,
                r => r.ResourceInfo.ResourceName.Value == "MixedCASEDescriptor"
            );

            mixedCaseDescriptor.Should().NotBeNull();
            mixedCaseDescriptor!
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/mixedcasedescriptor#somevalue");
        }

        [Test]
        public void It_normalizes_collection_descriptor_references_to_lowercase()
        {
            var collectionDescriptors = descriptorReferences
                .Where(r => r.ResourceInfo.ResourceName.Value == "CollectionMIXEDCaseDescriptor")
                .ToArray();

            collectionDescriptors.Should().HaveCount(2);

            collectionDescriptors[0]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/collectionmixedcasedescriptor#first");

            collectionDescriptors[1]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/collectionmixedcasedescriptor#second");
        }

        [Test]
        public void It_preserves_original_resource_names_in_mixed_case()
        {
            var resourceNames = descriptorReferences
                .Select(r => r.ResourceInfo.ResourceName.Value)
                .Distinct()
                .ToArray();

            resourceNames.Should().Contain("COURSEGPAApplicabilityDescriptor");
            resourceNames.Should().Contain("AcademicSUBJECTDescriptor");
            resourceNames.Should().Contain("MixedCASEDescriptor");
            resourceNames.Should().Contain("CollectionMIXEDCaseDescriptor");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Relational_Descriptor_References_For_A_Non_Descriptor_Resource
        : DescriptorExtractorTests
    {
        private DescriptorReference[] _descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildRelationalReferenceDescriptorApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocument,
                "studentProgramAssociations"
            );

            _descriptorReferences = resourceSchema.ExtractRelationalDescriptors(
                CreateResourceInfo("StudentProgramAssociation"),
                CreateRelationalReferenceDescriptorMappingSet(includeWritePlan: true),
                JsonNode.Parse(
                    """
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
                    """
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_the_descriptor_references()
        {
            _descriptorReferences.Should().HaveCount(2);
        }

        [Test]
        public void It_has_extracted_the_first_collection_member_with_the_expected_resource_type_and_path()
        {
            _descriptorReferences[0].ResourceInfo.ResourceName.Value.Should().Be("ProgramTypeDescriptor");
            _descriptorReferences[0]
                .Path.Value.Should()
                .Be("$.programs[0].programReference.programTypeDescriptor");
            _descriptorReferences[0]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/programtypedescriptor#stem");
        }

        [Test]
        public void It_has_extracted_the_second_collection_member_with_the_expected_resource_type_and_path()
        {
            _descriptorReferences[1].ResourceInfo.ResourceName.Value.Should().Be("ProgramTypeDescriptor");
            _descriptorReferences[1]
                .Path.Value.Should()
                .Be("$.programs[1].programReference.programTypeDescriptor");
            _descriptorReferences[1]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/programtypedescriptor#finearts");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Relational_Descriptor_References_Without_A_Compiled_Write_Plan
        : DescriptorExtractorTests
    {
        private DescriptorReference[] _descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildRelationalReferenceDescriptorApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocument,
                "studentProgramAssociations"
            );

            _descriptorReferences = resourceSchema.ExtractRelationalDescriptors(
                CreateResourceInfo("StudentProgramAssociation"),
                CreateRelationalReferenceDescriptorMappingSet(includeWritePlan: false),
                JsonNode.Parse(
                    """
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
                    """
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_the_descriptor_references()
        {
            _descriptorReferences.Should().HaveCount(2);
        }

        [Test]
        public void It_has_extracted_the_first_collection_member_with_the_expected_concrete_path()
        {
            _descriptorReferences[0].ResourceInfo.ResourceName.Value.Should().Be("ProgramTypeDescriptor");
            _descriptorReferences[0]
                .Path.Value.Should()
                .Be("$.programs[0].programReference.programTypeDescriptor");
            _descriptorReferences[0]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/programtypedescriptor#stem");
        }

        [Test]
        public void It_has_extracted_the_second_collection_member_with_the_expected_concrete_path()
        {
            _descriptorReferences[1].ResourceInfo.ResourceName.Value.Should().Be("ProgramTypeDescriptor");
            _descriptorReferences[1]
                .Path.Value.Should()
                .Be("$.programs[1].programReference.programTypeDescriptor");
            _descriptorReferences[1]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/programtypedescriptor#finearts");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Relational_Descriptor_References_For_A_Descriptor_Resource
        : DescriptorExtractorTests
    {
        private DescriptorReference[] _descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildDescriptorResourceApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "programTypeDescriptors");

            _descriptorReferences = resourceSchema.ExtractRelationalDescriptors(
                CreateResourceInfo("ProgramTypeDescriptor", isDescriptor: true),
                CreateRelationalReferenceDescriptorMappingSet(includeWritePlan: false),
                JsonNode.Parse(
                    """
                    {
                        "codeValue": "STEM",
                        "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#Mathematics"
                    }
                    """
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_short_circuits_to_schema_based_descriptor_extraction()
        {
            _descriptorReferences.Should().ContainSingle();
        }

        [Test]
        public void It_has_extracted_the_descriptor_reference_with_the_expected_resource_type_and_path()
        {
            _descriptorReferences[0].ResourceInfo.ResourceName.Value.Should().Be("AcademicSubjectDescriptor");
            _descriptorReferences[0].Path.Value.Should().Be("$.academicSubjectDescriptor");
            _descriptorReferences[0]
                .DocumentIdentity.DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/academicsubjectdescriptor#mathematics");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Relational_Descriptor_References_With_A_Conflicting_Concrete_Path
        : DescriptorExtractorTests
    {
        private InvalidOperationException _exception = null!;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "slimCourses");

            Action act = () =>
                resourceSchema.ExtractRelationalDescriptors(
                    CreateResourceInfo("SlimCourse"),
                    CreateConflictingDescriptorPathMappingSet(),
                    JsonNode.Parse(
                        """
                        {
                            "courseTitle": "Math",
                            "careerPathwayDescriptor": "uri://ed-fi.org/CareerPathwayDescriptor#Other"
                        }
                        """
                    )!,
                    NullLogger.Instance
                );

            _exception = act.Should().Throw<InvalidOperationException>().Which;
        }

        [Test]
        public void It_fails_with_a_deterministic_conflict_message()
        {
            _exception
                .Message.Should()
                .Be(
                    "Descriptor path '$.careerPathwayDescriptor' resolved to conflicting descriptor references."
                );
        }
    }
}
