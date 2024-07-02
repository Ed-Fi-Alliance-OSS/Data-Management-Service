// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Extraction.ReferentialIdCalculator;
using static EdFi.DataManagementService.Core.Extraction.IdentityExtractor;

namespace EdFi.DataManagementService.Core.Tests.Unit.Model;

[TestFixture]
public class DocumentIdentityTests
{
    [TestFixture]
    public class Given_A_School_Identity_Which_Is_A_Subclass_Of_Education_Organization : DocumentIdentityTests
    {
        internal DocumentIdentity? superclassIdentity;

        [SetUp]
        public void Setup()
        {
            DocumentIdentityElement documentIdentityElement = new DocumentIdentityElement(new JsonPath("$.schoolId"), "123");
            DocumentIdentity documentIdentity =
                new([documentIdentityElement]);
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
    public class Given_The_Identity_Of_A_Resource_With_A_Single_Identity_Element : DocumentIdentityTests
    {
        internal ReferentialId referentialId;

        [SetUp]
        public void Setup()
        {
            DocumentIdentity documentIdentity = new([new DocumentIdentityElement(new JsonPath("$.schoolId"), "123")]);
            BaseResourceInfo resourceInfo = new(new ProjectName("Ed-Fi"), new ResourceName("School"), false);
            referentialId = ReferentialIdFrom(resourceInfo, documentIdentity);
        }

        [Test]
        public void It_has_the_correct_referentialId()
        {
            referentialId.Value.ToString().Should().Be("05374602-2715-517e-8f08-56f877843b57");
        }
    }

    [TestFixture]
    public class Given_The_Identity_Of_A_Resource_With_Multiple_Identity_Elements : DocumentIdentityTests
    {
        internal ReferentialId referentialId;

        [SetUp]
        public void Setup()
        {
            DocumentIdentity documentIdentity =
                new(
                    [
                        new DocumentIdentityElement(new JsonPath("$.localCourseCode"), "abc"),
                        new DocumentIdentityElement(new JsonPath("$.schoolReference.schoolId"), "123"),
                        new DocumentIdentityElement(new JsonPath("$.sessionReference.schoolYear"), "2030"),
                        new DocumentIdentityElement(new JsonPath("$.sectionIdentifier"), "sectionId"),
                        new DocumentIdentityElement(new JsonPath("$.sessionReference.sessionName"), "d")
                    ]
                );
            BaseResourceInfo resourceInfo = new(new ProjectName("Ed-Fi"), new ResourceName("Section"), false);
            referentialId = ReferentialIdFrom(resourceInfo, documentIdentity);
        }

        [Test]
        public void It_has_the_correct_referentialId()
        {
            referentialId.Value.ToString().Should().Be("9bb7c8d3-7154-5952-9ff3-1766185ca40e");
        }
    }
}
