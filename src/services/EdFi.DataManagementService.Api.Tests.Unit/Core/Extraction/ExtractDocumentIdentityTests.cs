// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema;
using EdFi.DataManagementService.Api.Core.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.Extraction;

[TestFixture]
public class ExtractDocumentIdentityTests
{
    public static ResourceSchema BuildResourceSchema(
        ApiSchemaDocument apiSchemaDocument,
        string projectNamespace,
        string endpointName
    )
    {
        JsonNode projectSchemaNode = apiSchemaDocument.FindProjectSchemaNode(new(projectNamespace))!;
        ProjectSchema projectSchema = new(projectSchemaNode, NullLogger.Instance);
        return new ResourceSchema(
            projectSchema.FindResourceSchemaNode(new(endpointName))!,
            NullLogger.Instance
        );
    }

    [TestFixture]
    public class Given_extracting_an_identity_composed_of_several_references : ExtractDocumentIdentityTests
    {
        public DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("Section")
                .WithIdentityFullnames(["SectionIdentifier", "CourseOffering"])
                .WithIdentityPathOrder(
                    ["localCourseCode", "schoolId", "schoolYear", "sectionIdentifier", "sessionName"]
                )
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("SectionIdentifier", "sectionIdentifier", "$.sectionIdentifier")
                .WithDocumentPathReference(
                    "CourseOffering",
                    [
                        new("localCourseCode", "$.courseOfferingReference.localCourseCode"),
                        new("schoolId", "$.courseOfferingReference.schoolId"),
                        new("schoolYear", "$.courseOfferingReference.schoolYear"),
                        new("sessionName", "$.courseOfferingReference.sessionName")
                    ]
                )
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "ed-fi", "sections");

            documentIdentity = resourceSchema.ExtractDocumentIdentity(
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
                )!
            );
        }

        [Test]
        public void It_has_extracted_the_identity()
        {
            documentIdentity!.DocumentIdentityElements.Should().HaveCount(5);
            documentIdentity!
                .DocumentIdentityElements[0]
                .DocumentObjectKey.Value.Should()
                .Be("localCourseCode");
            documentIdentity!.DocumentIdentityElements[0].DocumentValue.Should().Be("abc");
            documentIdentity!.DocumentIdentityElements[1].DocumentObjectKey.Value.Should().Be("schoolId");
            documentIdentity!.DocumentIdentityElements[1].DocumentValue.Should().Be("123");
            documentIdentity!.DocumentIdentityElements[2].DocumentObjectKey.Value.Should().Be("schoolYear");
            documentIdentity!.DocumentIdentityElements[2].DocumentValue.Should().Be("2030");
            documentIdentity!
                .DocumentIdentityElements[3]
                .DocumentObjectKey.Value.Should()
                .Be("sectionIdentifier");
            documentIdentity!.DocumentIdentityElements[3].DocumentValue.Should().Be("sectionId");
            documentIdentity!.DocumentIdentityElements[4].DocumentObjectKey.Value.Should().Be("sessionName");
            documentIdentity!.DocumentIdentityElements[4].DocumentValue.Should().Be("d");
        }
    }

    [TestFixture]
    public class Given_extracting_an_identity_that_includes_a_descriptor_reference
        : ExtractDocumentIdentityTests
    {
        public DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("StaffEducationOrganizationAssignmentAssociation")
                .WithIdentityFullnames(["StaffClassification"])
                .WithIdentityPathOrder(["staffClassificationDescriptor"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathReference(
                    "StaffClassification",
                    [new("staffClassificationDescriptor", "$.staffClassificationDescriptor")],
                    true
                )
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocument,
                "ed-fi",
                "staffEducationOrganizationAssignmentAssociations"
            );

            documentIdentity = resourceSchema.ExtractDocumentIdentity(
                JsonNode.Parse(
                    """
                    {
                        "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Kindergarten Teacher",
                        "endDate": "2030-01-01"
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_extracted_the_identity()
        {
            documentIdentity!.DocumentIdentityElements.Should().HaveCount(1);
            documentIdentity!
                .DocumentIdentityElements[0]
                .DocumentObjectKey.Value.Should()
                .Be("staffClassificationDescriptor");
            documentIdentity!
                .DocumentIdentityElements[0]
                .DocumentValue.Should()
                .Be("uri://ed-fi.org/StaffClassificationDescriptor#Kindergarten Teacher");
        }
    }

    [TestFixture]
    public class Given_extracting_an_identity_that_includes_a_school_year_reference
        : ExtractDocumentIdentityTests
    {
        public DocumentIdentity? documentIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("GradingPeriod")
                .WithIdentityFullnames(["SchoolYear"])
                .WithIdentityPathOrder(["schoolYear"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("SchoolYear", "schoolYear", "$.schoolYearTypeReference.schoolYear")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "ed-fi", "gradingPeriods");

            documentIdentity = resourceSchema.ExtractDocumentIdentity(
                JsonNode.Parse(
                    """
                    {
                        "schoolYearTypeReference": {
                            "schoolYear": 2030
                        },
                        "endDate": "2030-01-01"
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_extracted_the_identity()
        {
            documentIdentity!.DocumentIdentityElements.Should().HaveCount(1);
            documentIdentity!
                .DocumentIdentityElements[0]
                .DocumentObjectKey.Value.Should()
                .Be("schoolYear");
            documentIdentity!
                .DocumentIdentityElements[0]
                .DocumentValue.Should()
                .Be("2030");
        }
    }
}
