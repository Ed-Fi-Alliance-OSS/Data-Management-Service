// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Extraction;

[TestFixture]
[Parallelizable]
public class ExtractSecurityElementsTests
{
    internal static ApiSchemaDocuments BuildApiSchemaDocuments()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("ResourceWithNamespaceNotAsSecurityElement")
            .WithNamespaceSecurityElements([])
            .WithEducationOrganizationSecurityElements([])
            .WithStudentSecurityElements([])
            .WithContactSecurityElements([])
            .WithStaffSecurityElements([])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("Namespace", "$.namespace")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_an_assessment_resource_that_has_a_namespace : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Assessment")
                .WithNamespaceSecurityElements(["$.namespace"])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("AssessmentIdentifier", "$.assessmentIdentifier")
                .WithDocumentPathScalar("Namespace", "$.namespace")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "assessments");

            string body = """{"assessmentIdentifier": "123", "namespace": "abc"}""";

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_the_namespace()
        {
            documentSecurityElements.Namespace.Should().HaveCount(1);
            documentSecurityElements.Namespace[0].Should().Be("abc");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_an_assessment_resource_that_does_not_have_a_namespace : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Assessment")
                .WithNamespaceSecurityElements(["$.namespace"])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("AssessmentIdentifier", "$.assessmentIdentifier")
                .WithDocumentPathScalar("Namespace", "$.namespace")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "assessments");

            string body = """{"assessmentIdentifier": "123"}""";

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_no_namespace()
        {
            documentSecurityElements.Namespace.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_resource_that_has_a_namespace_not_as_a_security_element
        : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Assessment")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("AssessmentIdentifier", "$.assessmentIdentifier")
                .WithDocumentPathScalar("Namespace", "$.namespace")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "assessments");

            string body = """{"assessmentIdentifier": "123", "namespace": "abc"}""";

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_no_namespace()
        {
            documentSecurityElements.Namespace.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_an_academicWeek_resource_that_has_educationOrganization : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([("School", "$.schoolReference.schoolId")])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("EducationOrganization", "$.schoolReference.schoolId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "academicWeeks");

            string body = """
                {"weekIdentifier": "123",
                    "schoolReference": {
                        "schoolId": 12345
                        }
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_educationOrganization()
        {
            documentSecurityElements.EducationOrganization.Should().HaveCount(1);
            documentSecurityElements.EducationOrganization[0].Id.Value.Should().Be(12345);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_an_academicWeek_resource_that_does_not_have_educationOrganization
        : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([("school", "$.schoolReference.schoolId")])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("EducationOrganization", "$.schoolReference.schoolId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "academicWeeks");

            string body = """
                {
                "weekIdentifier": "123"
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_no_educationOrganization()
        {
            documentSecurityElements.EducationOrganization.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_resource_that_has_educationOrganization_not_as_a_security_element
        : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("EducationOrganization", "$.schoolReference.schoolId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "academicWeeks");

            string body = """
                {"weekIdentifier": "123",
                    "schoolReference": {
                        "schoolId": 12345
                        }
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_no_educationOrganization()
        {
            documentSecurityElements.EducationOrganization.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_courseTranscript_resource_that_has_studentId : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("CourseTranscript")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements(["$.studentReference.studentId"])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "courseTranscripts");

            string body = """
                {
                    "studentReference": {
                        "studentId": "12345"
                    }
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_studentId()
        {
            documentSecurityElements.Student.Should().HaveCount(1);
            documentSecurityElements.Student[0].Value.Should().Be("12345");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_courseTranscript_resource_that_does_not_have_studentId : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("CourseTranscript")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "courseTranscripts");

            string body = """
                {
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_no_studentId()
        {
            documentSecurityElements.Student.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_resource_that_has_studentId_not_as_a_security_element : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("CourseTranscript")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "courseTranscripts");

            string body = """
                {
                    "studentReference": {
                        "studentId": "12345"
                    }
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_no_studentId()
        {
            documentSecurityElements.Student.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_StudentContactAssociations_resource_that_has_studentUniqueId_and_ContactUniqueId
        : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StudentContactAssociation")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements(["$.studentReference.studentUniqueId"])
                .WithContactSecurityElements(["$.contactReference.contactUniqueId"])
                .WithStaffSecurityElements([])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocuments,
                "StudentContactAssociations"
            );

            string body = """
                {
                    "studentReference": {
                        "studentUniqueId": "12345"
                    },
                   "contactReference": {
                        "contactUniqueId": "7878"
                    }
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_studentUniqueId_and_contactUniqueId()
        {
            documentSecurityElements.Student.Should().HaveCount(1);
            documentSecurityElements.Student[0].Value.Should().Be("12345");
            documentSecurityElements.Contact.Should().HaveCount(1);
            documentSecurityElements.Contact[0].Value.Should().Be("7878");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_Contact_resource_that_does_not_have_contactUniqueId : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Contact")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "contacts");

            string body = """
                {
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_no_contactUniqueId()
        {
            documentSecurityElements.Contact.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_resource_that_has_contactUniqueId_not_as_a_security_element
        : ExtractSecurityElementsTests
    {
        private DocumentSecurityElements documentSecurityElements = No.DocumentSecurityElements;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("SurveyResponse")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "SurveyResponses");

            string body = """
                {
                    "contactReference": {
                        "contactUniqueId": "12345"
                    }
                }
                """;

            documentSecurityElements = resourceSchema.ExtractSecurityElements(
                JsonNode.Parse(body)!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_no_contactUniqueId()
        {
            documentSecurityElements.Contact.Should().HaveCount(0);
        }
    }
}
