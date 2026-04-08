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
using static EdFi.DataManagementService.Core.Extraction.ReferentialIdCalculator;
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
            .WithIdentityJsonPaths([
                "$.courseOfferingReference.localCourseCode",
                "$.courseOfferingReference.schoolId",
                "$.courseOfferingReference.schoolYear",
                "$.courseOfferingReference.sessionName",
                "$.sectionIdentifier",
            ])
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

    internal static ApiSchemaDocuments BuildApiSchemaDocumentsWithDuplicateReferenceJsonPaths()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Section")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference(
                "EducationOrganization",
                [
                    new(
                        "$.educationOrganizationId",
                        "$.educationOrganizationReference.educationOrganizationId"
                    ),
                    new(
                        "$.localEducationAgencyReference.educationOrganizationId",
                        "$.educationOrganizationReference.educationOrganizationId"
                    ),
                ]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_Duplicate_Reference_Json_Paths
        : ExtractDocumentReferencesTests
    {
        private DocumentReference _documentReference = null!;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocumentsWithDuplicateReferenceJsonPaths();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            var (documentReferences, _) = resourceSchema.ExtractReferences(
                JsonNode.Parse(
                    """
                    {
                        "educationOrganizationReference": {
                            "educationOrganizationId": 255901
                        }
                    }
                    """
                )!,
                NullLogger.Instance
            );

            _documentReference = documentReferences.Should().ContainSingle().Subject;
        }

        [Test]
        public void It_preserves_duplicate_reference_members_in_document_identity_order()
        {
            _documentReference
                .DocumentIdentity.DocumentIdentityElements.Select(static element =>
                    (element.IdentityJsonPath.Value, element.IdentityValue)
                )
                .Should()
                .Equal(
                    ("$.educationOrganizationId", "255901"),
                    ("$.localEducationAgencyReference.educationOrganizationId", "255901")
                );
        }

        [Test]
        public void It_builds_the_referential_id_from_the_preserved_duplicate_identity_order()
        {
            _documentReference
                .ReferentialId.Should()
                .Be(ReferentialIdFrom(_documentReference.ResourceInfo, _documentReference.DocumentIdentity));
        }
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
            var documentReference = documentReferences.Single(r =>
                r.Path.Value == "$.courseOfferingReference"
            );
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CourseOffering");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(4);
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.localCourseCode")
                .IdentityValue.Should()
                .Be("aLocalCourseCode");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("23");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.sessionReference.schoolYear")
                .IdentityValue.Should()
                .Be("1234");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.sessionReference.sessionName")
                .IdentityValue.Should()
                .Be("aSessionName");
        }

        [Test]
        public void It_has_extracted_the_course_offering_reference_path()
        {
            documentReferences.Should().Contain(r => r.Path.Value == "$.courseOfferingReference");
        }

        [Test]
        public void It_has_extracted_the_first_class_period_reference()
        {
            var documentReference = documentReferences.Single(r =>
                r.Path.Value == "$.classPeriods[0].classPeriodReference"
            );
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.classPeriodName")
                .IdentityValue.Should()
                .Be("Class Period 1");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("111");
        }

        [Test]
        public void It_has_extracted_the_first_class_period_reference_path_with_index()
        {
            documentReferences
                .Should()
                .Contain(r => r.Path.Value == "$.classPeriods[0].classPeriodReference");
        }

        [Test]
        public void It_has_extracted_the_second_class_period_reference()
        {
            var documentReference = documentReferences.Single(r =>
                r.Path.Value == "$.classPeriods[1].classPeriodReference"
            );
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.classPeriodName")
                .IdentityValue.Should()
                .Be("Class Period 2");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("222");
        }

        [Test]
        public void It_has_extracted_the_second_class_period_reference_path_with_index()
        {
            documentReferences
                .Should()
                .Contain(r => r.Path.Value == "$.classPeriods[1].classPeriodReference");
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
                .DocumentReferences.Single()
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
            var documentReference = documentReferences.Single(r =>
                r.Path.Value == "$.classPeriods[0].classPeriodReference"
            );
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.classPeriodName")
                .IdentityValue.Should()
                .Be("Class Period 1");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("111");
        }

        [Test]
        public void It_has_extracted_the_first_class_period_reference_path_with_index()
        {
            documentReferences
                .Should()
                .Contain(r => r.Path.Value == "$.classPeriods[0].classPeriodReference");
        }

        [Test]
        public void It_has_extracted_the_second_class_period_reference()
        {
            var documentReference = documentReferences.Single(r =>
                r.Path.Value == "$.classPeriods[1].classPeriodReference"
            );
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.classPeriodName")
                .IdentityValue.Should()
                .Be("Class Period 2");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("222");
        }

        [Test]
        public void It_has_extracted_the_second_class_period_reference_path_with_index()
        {
            documentReferences
                .Should()
                .Contain(r => r.Path.Value == "$.classPeriods[1].classPeriodReference");
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
            var documentReference = documentReferences.Single(r =>
                r.Path.Value == "$.classPeriods[0].classPeriodReference"
            );
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("ClassPeriod");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(2);
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.classPeriodName")
                .IdentityValue.Should()
                .Be("Class Period 1");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("111");
        }

        [Test]
        public void It_has_extracted_the_first_class_period_reference_path_with_index()
        {
            documentReferences
                .Should()
                .Contain(r => r.Path.Value == "$.classPeriods[0].classPeriodReference");
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
            classPeriodArray
                .DocumentReferences.Single()
                .ResourceInfo.ResourceName.Value.Should()
                .Be("ClassPeriod");
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
            var documentReference = documentReferences.Single(r =>
                r.Path.Value == "$.courseOfferingReference"
            );
            documentReference.ResourceInfo.ResourceName.Value.Should().Be("CourseOffering");

            var documentIdentityElements = documentReference.DocumentIdentity.DocumentIdentityElements;
            documentIdentityElements.Should().HaveCount(4);
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.localCourseCode")
                .IdentityValue.Should()
                .Be("aLocalCourseCode");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("23");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.sessionReference.schoolYear")
                .IdentityValue.Should()
                .Be("1234");
            documentIdentityElements
                .Single(e => e.IdentityJsonPath.Value == "$.sessionReference.sessionName")
                .IdentityValue.Should()
                .Be("aSessionName");
        }

        [Test]
        public void It_has_extracted_the_course_offering_reference_path()
        {
            documentReferences.Should().Contain(r => r.Path.Value == "$.courseOfferingReference");
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
                .DocumentReferences.Single()
                .ResourceInfo.ResourceName.Value.Should()
                .Be("CourseOffering");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_An_Empty_Reference_Object_In_Body
        : ExtractDocumentReferencesTests
    {
        private ReferenceExtractionValidationException _exception = null!;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            var act = () =>
                resourceSchema.ExtractReferences(
                    JsonNode.Parse(
                        """
                        {
                            "sectionIdentifier": "Bob",
                            "courseOfferingReference": {}
                        }
                        """
                    )!,
                    NullLogger.Instance
                );

            _exception = act.Should().Throw<ReferenceExtractionValidationException>().Which;
        }

        [Test]
        public void It_rejects_the_reference_object_at_its_concrete_path()
        {
            _exception.ValidationFailures.Should().ContainSingle();
            _exception.ValidationFailures[0].Path.Value.Should().Be("$.courseOfferingReference");
        }

        [Test]
        public void It_reports_each_missing_identity_member_in_the_validation_message()
        {
            _exception
                .ValidationFailures[0]
                .Message.Should()
                .Contain("$.courseOfferingReference.localCourseCode")
                .And.Contain("$.courseOfferingReference.schoolId")
                .And.Contain("$.courseOfferingReference.schoolYear")
                .And.Contain("$.courseOfferingReference.sessionName");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_A_Partial_Nested_Reference_Object_In_Body
        : ExtractDocumentReferencesTests
    {
        private ReferenceExtractionValidationException _exception = null!;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            var act = () =>
                resourceSchema.ExtractReferences(
                    JsonNode.Parse(
                        """
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
                        """
                    )!,
                    NullLogger.Instance
                );

            _exception = act.Should().Throw<ReferenceExtractionValidationException>().Which;
        }

        [Test]
        public void It_rejects_the_nested_reference_object_at_its_concrete_path()
        {
            _exception.ValidationFailures.Should().ContainSingle();
            _exception.ValidationFailures[0].Path.Value.Should().Be("$.classPeriods[0].classPeriodReference");
        }

        [Test]
        public void It_reports_the_missing_nested_identity_member()
        {
            _exception
                .ValidationFailures[0]
                .Message.Should()
                .Contain("$.classPeriods[0].classPeriodReference.schoolId");
        }
    }

    [TestFixture("null", "null")]
    [TestFixture("{}", "a JSON object")]
    [TestFixture("[]", "a JSON array")]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_A_Malformed_Root_Reference_Identity_Member(
        string _invalidValueJson,
        string _expectedInvalidValueDescription
    ) : ExtractDocumentReferencesTests
    {
        private ReferenceExtractionValidationException _exception = null!;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            var act = () =>
                resourceSchema.ExtractReferences(
                    JsonNode.Parse(
                        $$"""
                        {
                            "sectionIdentifier": "Bob",
                            "courseOfferingReference": {
                                "localCourseCode": {{_invalidValueJson}},
                                "schoolId": "23",
                                "schoolYear": 1234,
                                "sessionName": "aSessionName"
                            }
                        }
                        """
                    )!,
                    NullLogger.Instance
                );

            _exception = act.Should().Throw<ReferenceExtractionValidationException>().Which;
        }

        [Test]
        public void It_rejects_the_invalid_member_at_its_concrete_path()
        {
            _exception.ValidationFailures.Should().ContainSingle();
            _exception
                .ValidationFailures[0]
                .Path.Value.Should()
                .Be("$.courseOfferingReference.localCourseCode");
        }

        [Test]
        public void It_reports_the_present_member_as_non_scalar()
        {
            _exception
                .ValidationFailures[0]
                .Message.Should()
                .Contain("must be a scalar value when present")
                .And.Contain(_expectedInvalidValueDescription);
        }
    }

    [TestFixture("null", "null")]
    [TestFixture("{}", "a JSON object")]
    [TestFixture("[]", "a JSON array")]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_A_Malformed_Nested_Reference_Identity_Member(
        string _invalidValueJson,
        string _expectedInvalidValueDescription
    ) : ExtractDocumentReferencesTests
    {
        private ReferenceExtractionValidationException _exception = null!;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            var act = () =>
                resourceSchema.ExtractReferences(
                    JsonNode.Parse(
                        $$"""
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
                        """
                    )!,
                    NullLogger.Instance
                );

            _exception = act.Should().Throw<ReferenceExtractionValidationException>().Which;
        }

        [Test]
        public void It_rejects_the_invalid_nested_member_at_its_concrete_path()
        {
            _exception.ValidationFailures.Should().ContainSingle();
            _exception
                .ValidationFailures[0]
                .Path.Value.Should()
                .Be("$.classPeriods[0].classPeriodReference.classPeriodName");
        }

        [Test]
        public void It_reports_the_nested_present_member_as_non_scalar()
        {
            _exception
                .ValidationFailures[0]
                .Message.Should()
                .Contain("must be a scalar value when present")
                .And.Contain(_expectedInvalidValueDescription);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_In_Legacy_Compatibility_Mode_With_An_Empty_Reference_Object
        : ExtractDocumentReferencesTests
    {
        private DocumentReference[] _documentReferences = [];
        private DocumentReferenceArray[] _documentReferenceArrays = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            (_documentReferences, _documentReferenceArrays) = resourceSchema.ExtractReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob",
                        "courseOfferingReference": {}
                    }
                    """
                )!,
                NullLogger.Instance,
                ReferenceExtractionMode.LegacyCompatibility
            );
        }

        [Test]
        public void It_skips_the_incomplete_reference_instead_of_failing_validation()
        {
            _documentReferences.Should().BeEmpty();
            _documentReferenceArrays.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_In_Legacy_Compatibility_Mode_With_A_Malformed_Nested_Reference_Member
        : ExtractDocumentReferencesTests
    {
        private DocumentReference[] _documentReferences = [];
        private DocumentReferenceArray[] _documentReferenceArrays = [];

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            (_documentReferences, _documentReferenceArrays) = resourceSchema.ExtractReferences(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "Bob",
                        "classPeriods": [
                            {
                                "classPeriodReference": {
                                    "classPeriodName": {},
                                    "schoolId": "111"
                                }
                            }
                        ]
                    }
                    """
                )!,
                NullLogger.Instance,
                ReferenceExtractionMode.LegacyCompatibility
            );
        }

        [Test]
        public void It_skips_the_malformed_nested_reference_instead_of_failing_validation()
        {
            _documentReferences.Should().BeEmpty();
            _documentReferenceArrays.Should().BeEmpty();
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

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_Document_References_With_Nested_Collections
    {
        internal DocumentReference[] documentReferences = [];
        internal DocumentReferenceArray[] documentReferenceArrays = [];

        internal static ApiSchemaDocuments BuildNestedApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StudentEducationOrganizationAssociation")
                .WithIdentityJsonPaths(["$.studentReference.studentUniqueId"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathReference(
                    "Calendar",
                    [
                        new("$.calendarCode", "$.addresses[*].periods[*].calendarReference.calendarCode"),
                        new(
                            "$.schoolReference.schoolId",
                            "$.addresses[*].periods[*].calendarReference.schoolId"
                        ),
                    ]
                )
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = BuildNestedApiSchemaDocuments();
            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocument,
                "studentEducationOrganizationAssociations"
            );

            (documentReferences, documentReferenceArrays) = resourceSchema.ExtractReferences(
                JsonNode.Parse(
                    """
                    {
                        "studentReference": { "studentUniqueId": "s001" },
                        "addresses": [
                            {
                                "periods": [
                                    {
                                        "calendarReference": {
                                            "calendarCode": "2024",
                                            "schoolId": "100"
                                        }
                                    }
                                ]
                            },
                            {
                                "periods": [
                                    {
                                        "calendarReference": {
                                            "calendarCode": "2025",
                                            "schoolId": "200"
                                        }
                                    },
                                    {
                                        "calendarReference": {
                                            "calendarCode": "2026",
                                            "schoolId": "300"
                                        }
                                    }
                                ]
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
        public void It_has_extracted_the_first_reference_path_with_nested_indices()
        {
            documentReferences
                .Should()
                .Contain(r => r.Path.Value == "$.addresses[0].periods[0].calendarReference");
        }

        [Test]
        public void It_has_extracted_the_first_reference_identity()
        {
            var elements = documentReferences
                .Single(r => r.Path.Value == "$.addresses[0].periods[0].calendarReference")
                .DocumentIdentity.DocumentIdentityElements;
            elements.Should().HaveCount(2);
            elements
                .Single(e => e.IdentityJsonPath.Value == "$.calendarCode")
                .IdentityValue.Should()
                .Be("2024");
            elements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("100");
        }

        [Test]
        public void It_has_extracted_the_second_reference_path_with_nested_indices()
        {
            documentReferences
                .Should()
                .Contain(r => r.Path.Value == "$.addresses[1].periods[0].calendarReference");
        }

        [Test]
        public void It_has_extracted_the_second_reference_identity()
        {
            var elements = documentReferences
                .Single(r => r.Path.Value == "$.addresses[1].periods[0].calendarReference")
                .DocumentIdentity.DocumentIdentityElements;
            elements.Should().HaveCount(2);
            elements
                .Single(e => e.IdentityJsonPath.Value == "$.calendarCode")
                .IdentityValue.Should()
                .Be("2025");
            elements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("200");
        }

        [Test]
        public void It_has_extracted_the_third_reference_path_with_nested_indices()
        {
            documentReferences
                .Should()
                .Contain(r => r.Path.Value == "$.addresses[1].periods[1].calendarReference");
        }

        [Test]
        public void It_has_extracted_the_third_reference_identity()
        {
            var elements = documentReferences
                .Single(r => r.Path.Value == "$.addresses[1].periods[1].calendarReference")
                .DocumentIdentity.DocumentIdentityElements;
            elements.Should().HaveCount(2);
            elements
                .Single(e => e.IdentityJsonPath.Value == "$.calendarCode")
                .IdentityValue.Should()
                .Be("2026");
            elements
                .Single(e => e.IdentityJsonPath.Value == "$.schoolReference.schoolId")
                .IdentityValue.Should()
                .Be("300");
        }

        [Test]
        public void It_has_extracted_one_document_reference_array_with_wildcard_path()
        {
            var refArray = documentReferenceArrays.Should().ContainSingle().Subject;
            refArray.arrayPath.Value.Should().Be("$.addresses[*].periods[*].calendarReference");
            refArray.DocumentReferences.Should().HaveCount(3);
        }
    }
}
