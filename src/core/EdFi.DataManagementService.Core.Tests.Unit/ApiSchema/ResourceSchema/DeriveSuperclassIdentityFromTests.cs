// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
public class DeriveSuperclassIdentityFromTests
{
    [TestFixture]
    public class Given_a_school_which_is_a_subclass_of_education_organization
        : DeriveSuperclassIdentityFromTests
    {
        internal SuperclassIdentity? superclassIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
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
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "schools");

            DocumentIdentity documentIdentity = resourceSchema.ExtractDocumentIdentity(
                JsonNode.Parse(
                    """
                    {
                        "schoolId": "123"
                    }
"""
                )!
            );

            superclassIdentity = resourceSchema.DeriveSuperclassIdentityFrom(documentIdentity);
        }

        [Test]
        public void It_has_derived_the_superclass_identity()
        {
            var superclassIdentityElements = superclassIdentity!.DocumentIdentity.DocumentIdentityElements;
            superclassIdentityElements.Should().HaveCount(1);
            superclassIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.educationOrganizationId");
            superclassIdentityElements[0].IdentityValue.Should().Be("123");
        }

        [Test]
        public void It_has_derived_the_superclass_resource_info()
        {
            superclassIdentity!.ResourceInfo.IsDescriptor.Should().Be(false);
            superclassIdentity!.ResourceInfo.ProjectName.Value.Should().Be("Ed-Fi");
            superclassIdentity!.ResourceInfo.ResourceName.Value.Should().Be("EducationOrganization");
        }
    }

    [TestFixture]
    public class Given_a_section_which_is_not_a_subclass : DeriveSuperclassIdentityFromTests
    {
        internal SuperclassIdentity? superclassIdentity;

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Section")
                .WithIdentityJsonPaths(["$.schoolId"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("SchoolId", "$.schoolId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "sections");

            DocumentIdentity documentIdentity = resourceSchema.ExtractDocumentIdentity(
                JsonNode.Parse(
                    """
                    {
                        "schoolId": "123"
                    }
"""
                )!
            );

            superclassIdentity = resourceSchema.DeriveSuperclassIdentityFrom(documentIdentity);
        }

        [Test]
        public void It_has_no_superclass_identity()
        {
            superclassIdentity.Should().BeNull();
        }
    }
}
