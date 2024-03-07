// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.Model;

[TestFixture]
public class DocumentIdentityTests
{
    [TestFixture]
    public class Given_a_school_identity_which_is_a_subclass_of_education_organization : DocumentIdentityTests
    {
        public DocumentIdentity? superclassIdentity;

        [SetUp]
        public void Setup()
        {
            DocumentIdentity documentIdentity = new([new(new("schoolId"), "123")]);
            superclassIdentity = documentIdentity.IdentityRename(
                new("schoolId"),
                new("educationOrganizationId")
            );
        }

        [Test]
        public void It_has_derived_the_superclass_document_identity()
        {
            var superclassIdentityElements = superclassIdentity!.DocumentIdentityElements;
            superclassIdentityElements.Should().HaveCount(1);
            superclassIdentityElements[0].DocumentObjectKey.Value.Should().Be("educationOrganizationId");
            superclassIdentityElements[0].DocumentValue.Should().Be("123");
        }
    }

    [TestFixture]
    public class Given_the_identity_of_a_resource_with_a_single_identity_element : DocumentIdentityTests
    {
        public ReferentialId referentialId;

        [SetUp]
        public void Setup()
        {
            DocumentIdentity documentIdentity = new([new(new("schoolId"), "123")]);
            BaseResourceInfo resourceInfo = new(new("Ed-Fi"), new("School"), false);
            referentialId = documentIdentity.ToReferentialId(resourceInfo);
        }

        [Test]
        public void It_has_the_correct_referentialId()
        {
            referentialId.Value.Should().Be("83p14XeYYEsu3CspQpSI_SVkwa1glEKrdr2L8w");
        }
    }

    [TestFixture]
    public class Given_the_identity_of_a_resource_with_multiple_identity_elements : DocumentIdentityTests
    {
        public ReferentialId referentialId;

        [SetUp]
        public void Setup()
        {
            DocumentIdentity documentIdentity =
                new(
                    [
                        new(new("localCourseCode"), "abc"),
                        new(new("schoolId"), "123"),
                        new(new("schoolYear"), "2030"),
                        new(new("sectionIdentifier"), "sectionId"),
                        new(new("sessionName"), "d")
                    ]
                );
            BaseResourceInfo resourceInfo = new(new("Ed-Fi"), new("Section"), false);
            referentialId = documentIdentity.ToReferentialId(resourceInfo);
        }

        [Test]
        public void It_has_the_correct_referentialId()
        {
            referentialId.Value.Should().Be("3rMeTL6w-rkmgVveY3iu3H7tww88WFYOSDqY_w");
        }
    }
}
