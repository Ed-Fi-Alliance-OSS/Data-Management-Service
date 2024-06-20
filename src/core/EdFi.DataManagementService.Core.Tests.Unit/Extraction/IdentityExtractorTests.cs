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
public class ExtractDocumentIdentityTests
{
    [TestFixture]
    public class Given_Extracting_An_Identity_Composed_Of_Several_References : ExtractDocumentIdentityTests
    {
        internal DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
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
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

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
    public class Given_Extracting_An_Identity_That_Includes_A_Descriptor_Reference
        : ExtractDocumentIdentityTests
    {
        internal DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StaffEducationOrganizationAssignmentAssociation")
                .WithIdentityJsonPaths(["$.staffClassificationDescriptor"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathDescriptor("StaffClassification", "$.staffClassificationDescriptor")
                .WithDocumentPathScalar("EndDate", "$.endDate")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocument,
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
                .Be("uri://ed-fi.org/StaffClassificationDescriptor#Kindergarten Teacher");
        }
    }

    [TestFixture]
    public class Given_Extracting_An_Identity_That_Includes_A_School_Year_Reference
        : ExtractDocumentIdentityTests
    {
        internal DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("GradingPeriod")
                .WithIdentityJsonPaths(["$.schoolYearTypeReference.schoolYear"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("SchoolYear", "$.schoolYearTypeReference.schoolYear")
                .WithDocumentPathScalar("EndDate", "$.endDate")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "gradingPeriods");

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
