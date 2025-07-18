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
[Parallelizable]
public class ExtractDocumentReferencesTests
{
    internal static ApiSchemaDocuments BuildApiSchemaDocuments()
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
                    "$.sectionIdentifier",
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

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_One_As_Scalar_And_Another_As_Collection
        : ExtractDocumentReferencesTests
    {
        internal DocumentReference[] documentReferences = [];
        internal DocumentReferenceArray[] documentReferenceArrays = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            (documentReferences, documentReferenceArrays) = resourceSchema.ExtractReferences(
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
                )!,
                NullLogger.Instance
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

        [Test]
        public void It_has_extracted_the_expected_document_reference_arrays()
        {
            documentReferenceArrays.Should().HaveCount(2);

            var courseOfferingArray = Array.Find(
                documentReferenceArrays,
                a => a.arrayPath.Value.Contains("courseOfferingReference")
            );
            courseOfferingArray.Should().NotBeNull();
            courseOfferingArray!.DocumentReferences.Should().ContainSingle();
            courseOfferingArray
                .DocumentReferences[0]
                .ResourceInfo.ResourceName.Value.Should()
                .Be("CourseOffering");
            var classPeriodArray = Array.Find(
                documentReferenceArrays,
                a => a.arrayPath.Value.Contains("classPeriods")
            );
            classPeriodArray.Should().NotBeNull();
            classPeriodArray!.DocumentReferences.Should().HaveCount(2);
            foreach (var docRef in classPeriodArray.DocumentReferences)
            {
                docRef.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");
            }
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_Missing_Optional_Course_Offering_Reference_In_Body
        : ExtractDocumentReferencesTests
    {
        internal DocumentReference[] documentReferences = [];
        internal DocumentReferenceArray[] documentReferenceArrays = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");
            (documentReferences, documentReferenceArrays) = resourceSchema.ExtractReferences(
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
                )!,
                NullLogger.Instance
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

        [Test]
        public void It_has_extracted_the_expected_document_reference_arrays()
        {
            documentReferenceArrays.Should().HaveCount(1);

            var classPeriodArray = Array.Find(
                documentReferenceArrays,
                a => a.arrayPath.Value.Contains("classPeriods")
            );
            classPeriodArray.Should().NotBeNull();
            classPeriodArray!.DocumentReferences.Should().HaveCount(2);
            foreach (var docRef in classPeriodArray.DocumentReferences)
            {
                docRef.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");
            }
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_Only_Single_Reference_In_Collection_In_Body
        : ExtractDocumentReferencesTests
    {
        internal DocumentReference[] documentReferences = [];
        internal DocumentReferenceArray[] documentReferenceArrays = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            (documentReferences, documentReferenceArrays) = resourceSchema.ExtractReferences(
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
                )!,
                NullLogger.Instance
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

        [Test]
        public void It_has_extracted_the_expected_document_reference_arrays()
        {
            documentReferenceArrays.Should().HaveCount(1);

            var classPeriodArray = Array.Find(
                documentReferenceArrays,
                a => a.arrayPath.Value.Contains("classPeriods")
            );
            classPeriodArray.Should().NotBeNull();
            classPeriodArray!.DocumentReferences.Should().HaveCount(1);
            classPeriodArray.DocumentReferences[0].ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_Empty_Reference_Collection_In_Body
        : ExtractDocumentReferencesTests
    {
        internal DocumentReference[] documentReferences = [];
        internal DocumentReferenceArray[] documentReferenceArrays = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            (documentReferences, documentReferenceArrays) = resourceSchema.ExtractReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob",
                        "classPeriods": [
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
            documentReferences.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_Missing_Optional_Class_Period_Reference_Collection_In_Body
        : ExtractDocumentReferencesTests
    {
        internal DocumentReference[] documentReferences = [];
        internal DocumentReferenceArray[] documentReferenceArrays = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            (documentReferences, documentReferenceArrays) = resourceSchema.ExtractReferences(
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
                )!,
                NullLogger.Instance
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

        [Test]
        public void It_has_extracted_the_expected_document_reference_arrays()
        {
            documentReferenceArrays.Should().HaveCount(1);

            var courseOfferingArray = Array.Find(
                documentReferenceArrays,
                a => a.arrayPath.Value.Contains("courseOfferingReference")
            );
            courseOfferingArray.Should().NotBeNull();
            courseOfferingArray!.DocumentReferences.Should().ContainSingle();
            courseOfferingArray
                .DocumentReferences[0]
                .ResourceInfo.ResourceName.Value.Should()
                .Be("CourseOffering");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_No_References_In_Body
        : ExtractDocumentReferencesTests
    {
        internal DocumentReference[] documentReferences = [];
        internal DocumentReferenceArray[] documentReferenceArrays = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            (documentReferences, documentReferenceArrays) = resourceSchema.ExtractReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob"
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_no_references()
        {
            documentReferences.Should().BeEmpty();
        }

        [Test]
        public void It_has_extracted_no_document_reference_arrays()
        {
            documentReferenceArrays.Should().BeEmpty();
        }
    }
}
