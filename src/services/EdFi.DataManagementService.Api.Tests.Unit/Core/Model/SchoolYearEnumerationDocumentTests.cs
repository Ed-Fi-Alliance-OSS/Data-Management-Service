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
public class SchoolYearEnumerationDocumentTests
{
    [TestFixture]
    public class Given_a_school_year_enumeration_document : SchoolYearEnumerationDocumentTests
    {
        public SchoolYearEnumerationDocument? document;

        [SetUp]
        public void Setup()
        {
            document = new(
                JsonNode.Parse(
                    """
                    {
                        "schoolYear": 2030,
                        "currentSchoolYear": false,
                        "schoolYearDescription": "2029-2030"
                    }
"""
                )!
            );
        }

        [Test]
        public void It_has_derived_the_school_year_identity()
        {
            var identity = document!.ToDocumentIdentity();
            identity.DocumentIdentityElements.Should().HaveCount(1);
            identity.DocumentIdentityElements[0].DocumentObjectKey.Value.Should().Be("schoolYear");
            identity.DocumentIdentityElements[0].DocumentValue.Should().Be("2030");
        }

        [Test]
        public void It_has_derived_the_document_info()
        {
            var documentInfo = document!.ToDocumentInfo();
            documentInfo.DocumentReferences.Should().BeEmpty();
            documentInfo.DescriptorReferences.Should().BeEmpty();
            documentInfo.SuperclassIdentity.Should().BeNull();

            var identity = document!.ToDocumentIdentity();
            identity.DocumentIdentityElements.Should().HaveCount(1);
            identity.DocumentIdentityElements[0].DocumentObjectKey.Value.Should().Be("schoolYear");
            identity.DocumentIdentityElements[0].DocumentValue.Should().Be("2030");
        }
    }
}
