// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Model;

[TestFixture]
[Parallelizable]
public class DescriptorDocumentTests
{
    [TestFixture("uri://ed-fi.org/Sex\\u0000Descriptor", "Female", new[] { "$.namespace" })]
    [TestFixture("uri://ed-fi.org/SexDescriptor", "Fe\\u0000male", new[] { "$.codeValue" })]
    [TestFixture(
        "uri://ed-fi.org/Sex\\u0000Descriptor",
        "Fe\\u0000male",
        new[] { "$.namespace", "$.codeValue" }
    )]
    [Parallelizable]
    public class Given_A_Descriptor_Document_With_Nul_In_Its_Identity(
        string namespaceJson,
        string codeValueJson,
        string[] expectedPaths
    ) : DescriptorDocumentTests
    {
        private ReferenceExtractionValidationException _exception = null!;

        [SetUp]
        public void Setup()
        {
            DescriptorDocument document = new(
                JsonNode.Parse(
                    $$"""{"namespace":"{{namespaceJson}}","codeValue":"{{codeValueJson}}","shortDescription":"x"}"""
                )!
            );

            Action act = () => document.ToDocumentIdentity();
            _exception = act.Should().Throw<ReferenceExtractionValidationException>().Which;
        }

        [Test]
        public void It_reports_exactly_the_offending_fields()
        {
            _exception.ValidationFailures.Select(failure => failure.Path.Value).Should().Equal(expectedPaths);
        }

        [Test]
        public void It_reports_the_descriptor_text_rule_without_echoing_the_value()
        {
            _exception
                .ValidationFailures.Select(failure => failure.Message)
                .Should()
                .AllBe(DescriptorText.InvalidValueMessage);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Descriptor_Document_With_Non_Ascii_Identity_Text : DescriptorDocumentTests
    {
        [Test]
        public void It_keeps_the_identity_text_unchanged()
        {
            DescriptorDocument document = new(
                JsonNode.Parse(
                    """{"namespace":"uri://ed-fi.org/AcademicSubjectDescriptor","codeValue":"École","shortDescription":"x"}"""
                )!
            );

            document
                .ToDocumentIdentity()
                .DocumentIdentityElements[0]
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/AcademicSubjectDescriptor#École");
        }
    }

    [TestFixture]
    [Parallelizable]
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
