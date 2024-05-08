// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Core.Model;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Api.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.ApiSchema;

[TestFixture]
public class ExtractDocumentReferencesTests
{
    public static ApiSchemaDocument BuildApiSchemaDocument()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Section")
            .WithIdentityJsonPaths(
                [
                    "$.courseOfferingReference.localCourseCode",
                    "$.courseOfferingReference.schoolId",
                    "$.courseOfferingReference.schoolYear",
                    "$.courseOfferingReference.sessionName",
                    "$.sectionIdentifier"
                ]
            )
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("SectionIdentifier", "$.sectionIdentifier")
            .WithDocumentPathReference(
                "CourseOffering",
                [
                    new("$.localCourseCode", "$.courseOfferingReference.localCourseCode"),
                    new("$.schoolReference.schoolId", "$.courseOfferingReference.schoolId"),
                    new("$.sessionReference.schoolYear", "$.courseOfferingReference.schoolYear"),
                    new("$.sessionReference.sessionName", "$.courseOfferingReference.sessionName")
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
            .ToApiSchemaDocument();
    }

    [TestFixture]
    public class Given_extracting_document_references_with_one_as_scalar_and_another_as_collection
        : ExtractDocumentReferencesTests
    {
        public DocumentReference[] documentReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            documentReferences = resourceSchema.ExtractDocumentReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob",
                        "courseOfferingReference": {
                            "localCourseCode": "aLocalCourseCode",
                            "schoolId": "23",
                            "schoolYear": 1234,
                            "sessionName": "aSessionName"
                        },
                        "classPeriods": [
                            {
                                "classPeriodReference": {
                                    "schoolId": "111",
                                    "classPeriodName": "Class Period 1"
                                }
                            },
                            {
                                "classPeriodReference": {
                                    "schoolId": "222",
                                    "classPeriodName": "Class Period 2"
                                }
                            }
                        ]
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_extracted_three_references()
        {
            documentReferences.Should().HaveCount(3);
        }

        [Test]
        public void It_has_extracted_the_course_offering_reference()
        {
            var documentReference = documentReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CourseOffering");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(4);
            documentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.localCourseCode");
            documentIdentityElements[0].IdentityValue.Should().Be("aLocalCourseCode");

            documentIdentityElements[1].IdentityJsonPath.Value.Should().Be("$.schoolReference.schoolId");
            documentIdentityElements[1].IdentityValue.Should().Be("23");

            documentIdentityElements[2].IdentityJsonPath.Value.Should().Be("$.sessionReference.schoolYear");
            documentIdentityElements[2].IdentityValue.Should().Be("1234");

            documentIdentityElements[3].IdentityJsonPath.Value.Should().Be("$.sessionReference.sessionName");
            documentIdentityElements[3].IdentityValue.Should().Be("aSessionName");
        }

        [Test]
        public void It_has_extracted_the_first_class_period_reference()
        {
            var documentReference = documentReferences[1];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.classPeriodName");
            documentIdentityElements[0].IdentityValue.Should().Be("Class Period 1");

            documentIdentityElements[1].IdentityJsonPath.Value.Should().Be("$.schoolReference.schoolId");
            documentIdentityElements[1].IdentityValue.Should().Be("111");
        }

        [Test]
        public void It_has_extracted_the_second_class_period_reference()
        {
            var documentReference = documentReferences[2];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.classPeriodName");
            documentIdentityElements[0].IdentityValue.Should().Be("Class Period 2");

            documentIdentityElements[1].IdentityJsonPath.Value.Should().Be("$.schoolReference.schoolId");
            documentIdentityElements[1].IdentityValue.Should().Be("222");
        }
    }

    [TestFixture]
    public class Given_extracting_document_references_with_missing_optional_course_offering_reference_in_body
        : ExtractDocumentReferencesTests
    {
        public DocumentReference[] documentReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            documentReferences = resourceSchema.ExtractDocumentReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob",
                        "classPeriods": [
                            {
                                "classPeriodReference": {
                                    "schoolId": "111",
                                    "classPeriodName": "Class Period 1"
                                }
                            },
                            {
                                "classPeriodReference": {
                                    "schoolId": "222",
                                    "classPeriodName": "Class Period 2"
                                }
                            }
                        ]
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_extracted_two_references()
        {
            documentReferences.Should().HaveCount(2);
        }

        [Test]
        public void It_has_extracted_the_first_class_period_reference()
        {
            var documentReference = documentReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.classPeriodName");
            documentIdentityElements[0].IdentityValue.Should().Be("Class Period 1");

            documentIdentityElements[1].IdentityJsonPath.Value.Should().Be("$.schoolReference.schoolId");
            documentIdentityElements[1].IdentityValue.Should().Be("111");
        }

        [Test]
        public void It_has_extracted_the_second_class_period_reference()
        {
            var documentReference = documentReferences[1];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.classPeriodName");
            documentIdentityElements[0].IdentityValue.Should().Be("Class Period 2");

            documentIdentityElements[1].IdentityJsonPath.Value.Should().Be("$.schoolReference.schoolId");
            documentIdentityElements[1].IdentityValue.Should().Be("222");
        }
    }

    [TestFixture]
    public class Given_extracting_document_references_with_only_single_reference_in_collection_in_body
        : ExtractDocumentReferencesTests
    {
        public DocumentReference[] documentReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            documentReferences = resourceSchema.ExtractDocumentReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob",
                        "classPeriods": [
                            {
                                "classPeriodReference": {
                                    "schoolId": "111",
                                    "classPeriodName": "Class Period 1"
                                }
                            }
                        ]
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_extracted_one_reference()
        {
            documentReferences.Should().HaveCount(1);
        }

        [Test]
        public void It_has_extracted_the_first_class_period_reference()
        {
            var documentReference = documentReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.classPeriodName");
            documentIdentityElements[0].IdentityValue.Should().Be("Class Period 1");

            documentIdentityElements[1].IdentityJsonPath.Value.Should().Be("$.schoolReference.schoolId");
            documentIdentityElements[1].IdentityValue.Should().Be("111");
        }
    }

    [TestFixture]
    public class Given_extracting_document_references_with_empty_reference_collection_in_body
        : ExtractDocumentReferencesTests
    {
        public DocumentReference[] documentReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            documentReferences = resourceSchema.ExtractDocumentReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob",
                        "classPeriods": [
                        ]
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_extracted_no_references()
        {
            documentReferences.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_extracting_document_references_with_missing_optional_class_period_reference_collection_in_body
        : ExtractDocumentReferencesTests
    {
        public DocumentReference[] documentReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            documentReferences = resourceSchema.ExtractDocumentReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob",
                        "courseOfferingReference": {
                            "localCourseCode": "aLocalCourseCode",
                            "schoolId": "23",
                            "schoolYear": 1234,
                            "sessionName": "aSessionName"
                        }
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_extracted_one_reference()
        {
            documentReferences.Should().HaveCount(1);
        }

        [Test]
        public void It_has_extracted_the_course_offering_reference()
        {
            var documentReference = documentReferences[0];
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CourseOffering");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(4);
            documentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.localCourseCode");
            documentIdentityElements[0].IdentityValue.Should().Be("aLocalCourseCode");

            documentIdentityElements[1].IdentityJsonPath.Value.Should().Be("$.schoolReference.schoolId");
            documentIdentityElements[1].IdentityValue.Should().Be("23");

            documentIdentityElements[2].IdentityJsonPath.Value.Should().Be("$.sessionReference.schoolYear");
            documentIdentityElements[2].IdentityValue.Should().Be("1234");

            documentIdentityElements[3].IdentityJsonPath.Value.Should().Be("$.sessionReference.sessionName");
            documentIdentityElements[3].IdentityValue.Should().Be("aSessionName");
        }
    }

    [TestFixture]
    public class Given_extracting_document_references_with_no_references_in_body
        : ExtractDocumentReferencesTests
    {
        public DocumentReference[] documentReferences = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = BuildApiSchemaDocument();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            documentReferences = resourceSchema.ExtractDocumentReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob"
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_extracted_no_references()
        {
            documentReferences.Should().BeEmpty();
        }
    }
}
