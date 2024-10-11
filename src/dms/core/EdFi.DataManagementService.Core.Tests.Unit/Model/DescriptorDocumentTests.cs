// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Model;

[TestFixture]
public class DescriptorDocumentTests
{
    [TestFixture]
    public class Given_A_Descriptor_Document : DescriptorDocumentTests
    {
        internal DescriptorDocument? descriptorDocument;

        [SetUp]
        public void Setup()
        {
            descriptorDocument = new(
                JsonNode.Parse(
                    """
                    {
                        "codeValue": "English",
                        "description": "EnglishDescription",
                        "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                        "shortDescription": "EnglishShortDescription"
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_derived_the_descriptor_identity()
        {
            var descriptorIdentity = descriptorDocument!.ToDocumentIdentity();
            descriptorIdentity.DocumentIdentityElements.Should().HaveCount(1);
            descriptorIdentity
                .DocumentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DescriptorDocument.DescriptorIdentityPath);
            descriptorIdentity
                .DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/AcademicSubjectDescriptor#English");
        }

        [Test]
        public void It_has_derived_the_document_info()
        {
            var documentInfo = descriptorDocument!.ToDocumentInfo(
                new BaseResourceInfo(
                    ProjectName: new ProjectName("ProjectName"),
                    ResourceName: new ResourceName("ResourceName"),
                    IsDescriptor: true
                )
            );
            documentInfo.DocumentReferences.Should().BeEmpty();
            documentInfo.DescriptorReferences.Should().BeEmpty();
            documentInfo.SuperclassIdentity.Should().BeNull();

            var descriptorIdentity = documentInfo.DocumentIdentity;
            descriptorIdentity.DocumentIdentityElements.Should().HaveCount(1);
            descriptorIdentity
                .DocumentIdentityElements[0]
                .IdentityJsonPath.Should()
                .Be(DescriptorDocument.DescriptorIdentityPath);
            descriptorIdentity
                .DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/AcademicSubjectDescriptor#English");
        }
    }
}
