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
public class ExtractDocumentIdentityTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_An_Identity_Composed_Of_Several_References : ExtractDocumentIdentityTests
    {
        internal DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
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
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "sections");

            (documentIdentity, _) = resourceSchema.ExtractIdentities(
                JsonNode.Parse(
                    """
                    {
                        "sectionIdentifier": "sectionId",
                        "courseOfferingReference": {
                            "localCourseCode": "abc",
                            "schoolId": 123,
                            "sessionName": "d",
                            "schoolYear": 2030
                        }
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_the_identity()
        {
            documentIdentity!.DocumentIdentityElements.Should().HaveCount(5);
            documentIdentity!
                .DocumentIdentityElements[0]
                .IdentityJsonPath.Value.Should()
                .Be("$.courseOfferingReference.localCourseCode");
            documentIdentity!.DocumentIdentityElements[0].IdentityValue.Should().Be("abc");
            documentIdentity!
                .DocumentIdentityElements[1]
                .IdentityJsonPath.Value.Should()
                .Be("$.courseOfferingReference.schoolId");
            documentIdentity!.DocumentIdentityElements[1].IdentityValue.Should().Be("123");
            documentIdentity!
                .DocumentIdentityElements[2]
                .IdentityJsonPath.Value.Should()
                .Be("$.courseOfferingReference.schoolYear");
            documentIdentity!.DocumentIdentityElements[2].IdentityValue.Should().Be("2030");
            documentIdentity!
                .DocumentIdentityElements[3]
                .IdentityJsonPath.Value.Should()
                .Be("$.courseOfferingReference.sessionName");
            documentIdentity!.DocumentIdentityElements[3].IdentityValue.Should().Be("d");
            documentIdentity!
                .DocumentIdentityElements[4]
                .IdentityJsonPath.Value.Should()
                .Be("$.sectionIdentifier");
            documentIdentity!.DocumentIdentityElements[4].IdentityValue.Should().Be("sectionId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_An_Identity_That_Includes_A_Descriptor_Reference
        : ExtractDocumentIdentityTests
    {
        internal DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StaffEducationOrganizationAssignmentAssociation")
                .WithIdentityJsonPaths(["$.staffClassificationDescriptor"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathDescriptor("StaffClassification", "$.staffClassificationDescriptor")
                .WithDocumentPathScalar("EndDate", "$.endDate")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocuments,
                "staffEducationOrganizationAssignmentAssociations"
            );

            (documentIdentity, _) = resourceSchema.ExtractIdentities(
                JsonNode.Parse(
                    """
                    {
                        "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Kindergarten Teacher",
                        "endDate": "2030-01-01"
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_the_identity()
        {
            documentIdentity!.DocumentIdentityElements.Should().HaveCount(1);
            documentIdentity!
                .DocumentIdentityElements[0]
                .IdentityJsonPath.Value.Should()
                .Be("$.staffClassificationDescriptor");
            documentIdentity!
                .DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/staffclassificationdescriptor#kindergarten teacher");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_A_Decimal_Top_Level_Identity : ExtractDocumentIdentityTests
    {
        internal DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Widget")
                .WithIdentityJsonPaths(["$.widgetScore"])
                .WithNumericJsonPaths(["$.widgetScore"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("WidgetScore", "$.widgetScore")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "widgets");

            (documentIdentity, _) = resourceSchema.ExtractIdentities(
                JsonNode.Parse(
                    """
                    {
                        "widgetScore": 1.50
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_canonicalizes_the_decimal_identity_value()
        {
            documentIdentity!.DocumentIdentityElements.Should().HaveCount(1);
            documentIdentity!.DocumentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.widgetScore");
            documentIdentity!.DocumentIdentityElements[0].IdentityValue.Should().Be("1.5");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_A_String_Top_Level_Identity_That_Is_Not_In_NumericJsonPaths
        : ExtractDocumentIdentityTests
    {
        internal DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Widget")
                .WithIdentityJsonPaths(["$.widgetCode"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("WidgetCode", "$.widgetCode")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "widgets");

            (documentIdentity, _) = resourceSchema.ExtractIdentities(
                JsonNode.Parse(
                    """
                    {
                        "widgetCode": "ABC-1.50"
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_passes_the_string_identity_value_through_unchanged()
        {
            documentIdentity!.DocumentIdentityElements.Should().HaveCount(1);
            documentIdentity!.DocumentIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.widgetCode");
            documentIdentity!.DocumentIdentityElements[0].IdentityValue.Should().Be("ABC-1.50");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extracting_An_Identity_That_Includes_A_School_Year_Reference
        : ExtractDocumentIdentityTests
    {
        internal DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("GradingPeriod")
                .WithIdentityJsonPaths(["$.schoolYearTypeReference.schoolYear"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("SchoolYear", "$.schoolYearTypeReference.schoolYear")
                .WithDocumentPathScalar("EndDate", "$.endDate")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "gradingPeriods");

            (documentIdentity, _) = resourceSchema.ExtractIdentities(
                JsonNode.Parse(
                    """
                    {
                        "schoolYearTypeReference": {
                            "schoolYear": 2030
                        },
                        "endDate": "2030-01-01"
                    }
"""
                )!,
                NullLogger.Instance
            );
        }

        [Test]
        public void It_has_extracted_the_identity()
        {
            documentIdentity!.DocumentIdentityElements.Should().HaveCount(1);
            documentIdentity!
                .DocumentIdentityElements[0]
                .IdentityJsonPath.Value.Should()
                .Be("$.schoolYearTypeReference.schoolYear");
            documentIdentity!.DocumentIdentityElements[0].IdentityValue.Should().Be("2030");
        }
    }
}
