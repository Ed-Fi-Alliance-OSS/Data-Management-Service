// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.ApiSchema;

[TestFixture]
public class DescriptorDocumentTests
{
    [TestFixture]
    public class Given_a_descriptor_document : DescriptorDocumentTests
    {
        public DescriptorDocument? descriptorDocument;

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
            descriptorIdentity.DocumentIdentityElements[0].DocumentObjectKey.Value.Should().Be("descriptor");
            descriptorIdentity
                .DocumentIdentityElements[0]
                .DocumentValue.Should()
                .Be("uri://ed-fi.org/AcademicSubjectDescriptor#English");
        }

        [Test]
        public void It_has_derived_the_document_info()
        {
            var documentInfo = descriptorDocument!.ToDocumentInfo();
            documentInfo.DocumentReferences.Should().BeEmpty();
            documentInfo.DescriptorReferences.Should().BeEmpty();
            documentInfo.SuperclassIdentity.Should().BeNull();

            var descriptorIdentity = documentInfo.DocumentIdentity;
            descriptorIdentity.DocumentIdentityElements.Should().HaveCount(1);
            descriptorIdentity.DocumentIdentityElements[0].DocumentObjectKey.Value.Should().Be("descriptor");
            descriptorIdentity
                .DocumentIdentityElements[0]
                .DocumentValue.Should()
                .Be("uri://ed-fi.org/AcademicSubjectDescriptor#English");
        }
    }
}
