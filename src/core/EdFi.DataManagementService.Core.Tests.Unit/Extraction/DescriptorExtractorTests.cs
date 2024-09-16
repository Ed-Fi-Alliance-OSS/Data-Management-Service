// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Extraction;

[TestFixture]
public class DescriptorExtractorTests
{
    internal static ApiSchemaDocument BuildApiSchemaDocument()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("SlimCourse")
            .WithIdentityJsonPaths(
                ["$.courseTitle", "$.careerPathwayDescriptor", "$.gradingPeriodDescriptor"]
            )
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
            .ToApiSchemaDocument();
    }

    [TestFixture]
    public class Given_Extracting_Descriptor_References_With_One_As_Scalar_And_Another_As_Collection
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
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
                .Be("uri://ed-fi.org/CareerPathwayDescriptor#Other");
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
                .Be("uri://ed-fi.org/CompetencyLevelDescriptor#Basic");
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
                .Be("uri://ed-fi.org/CompetencyLevelDescriptor#Advanced");
        }
    }

    [TestFixture]
    public class Given_Extracting_Descriptor_References_As_Collection_With_Index : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
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
    public class Given_Extracting_Descriptor_References_With_Missing_Optional_Scalar_Descriptor_In_Body
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
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
                .Be("uri://ed-fi.org/CompetencyLevelDescriptor#Basic");
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
                .Be("uri://ed-fi.org/CompetencyLevelDescriptor#Advanced");
        }
    }

    [TestFixture]
    public class Given_Extracting_Descriptor_References_With_Only_Single_Reference_In_Collection_In_Body
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
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
                .Be("uri://ed-fi.org/CompetencyLevelDescriptor#Advanced");
        }
    }

    [TestFixture]
    public class Given_Extracting_Descriptor_References_With_Empty_Reference_Collection_In_Body
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
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
    public class Given_Extracting_Descriptor_References_With_Missing_Optional_Class_Period_Reference_Collection_In_Body
        : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
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
                .Be("uri://ed-fi.org/CareerPathwayDescriptor#Other");
        }
    }

    [TestFixture]
    public class Given_Extracting_Descriptor_References_With_No_References_In_Body : DescriptorExtractorTests
    {
        internal DescriptorReference[] descriptorReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
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
}
