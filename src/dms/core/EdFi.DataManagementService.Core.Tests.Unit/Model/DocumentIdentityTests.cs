// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Extraction.IdentityExtractor;

namespace EdFi.DataManagementService.Core.Tests.Unit.Model;

[TestFixture]
[Parallelizable]
public class DocumentIdentityTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_School_Identity_Which_Is_A_Subclass_Of_Education_Organization : DocumentIdentityTests
    {
        internal DocumentIdentity? superclassIdentity;

        [SetUp]
        public void Setup()
        {
            DocumentIdentityElement documentIdentityElement = new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                "123"
            );

            superclassIdentity = IdentityRename(new("$.educationOrganizationId"), documentIdentityElement);
        }

        [Test]
        public void It_has_derived_the_superclass_document_identity()
        {
            var superclassIdentityElements = superclassIdentity!.DocumentIdentityElements;
            superclassIdentityElements.Should().HaveCount(1);
            superclassIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.educationOrganizationId");
            superclassIdentityElements[0].IdentityValue.Should().Be("123");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Descriptor_Identity_Path_Classification : DocumentIdentityTests
    {
        private bool _syntheticDescriptorIdentityPath;
        private bool _resourceDescriptorIdentityPath;
        private bool _concreteArrayDescriptorIdentityPath;
        private bool _nonDescriptorIdentityPath;

        [SetUp]
        public void Setup()
        {
            _syntheticDescriptorIdentityPath = DocumentIdentity.IsDescriptorIdentityPath(
                DocumentIdentity.DescriptorIdentityJsonPath
            );
            _resourceDescriptorIdentityPath = DocumentIdentity.IsDescriptorIdentityPath(
                new JsonPath("$.schoolTypeDescriptor")
            );
            _concreteArrayDescriptorIdentityPath = DocumentIdentity.IsDescriptorIdentityPath(
                new JsonPath("$.sections[0].schoolReference.schoolTypeDescriptor")
            );
            _nonDescriptorIdentityPath = DocumentIdentity.IsDescriptorIdentityPath(
                new JsonPath("$.schoolReference.schoolId")
            );
        }

        [Test]
        public void It_recognizes_the_synthetic_descriptor_identity_sentinel()
        {
            _syntheticDescriptorIdentityPath.Should().BeTrue();
        }

        [Test]
        public void It_recognizes_standard_descriptor_identity_paths()
        {
            _resourceDescriptorIdentityPath.Should().BeTrue();
        }

        [Test]
        public void It_recognizes_concrete_descriptor_identity_paths_for_nested_collection_members()
        {
            _concreteArrayDescriptorIdentityPath.Should().BeTrue();
        }

        [Test]
        public void It_does_not_misclassify_non_descriptor_identity_paths()
        {
            _nonDescriptorIdentityPath.Should().BeFalse();
        }
    }
}
