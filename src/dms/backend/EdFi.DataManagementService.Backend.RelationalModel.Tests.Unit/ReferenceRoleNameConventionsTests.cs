// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Reference_Role_Name_Conventions
{
    [Test]
    public void It_should_not_mark_plain_school_year_type_references_as_role_named()
    {
        var result = ReferenceRoleNameConventions.IsDocumentReferenceRoleNamed(
            JsonPathExpressionCompiler.Compile("$.schoolYearTypeReference"),
            new QualifiedResourceName("Ed-Fi", "SchoolYearType")
        );

        result.Should().BeFalse();
    }

    [Test]
    public void It_should_not_mark_nested_common_reference_leaf_matches_as_role_named()
    {
        var result = ReferenceRoleNameConventions.IsDocumentReferenceRoleNamed(
            JsonPathExpressionCompiler.Compile(
                "$.administrationPointOfContact.educationOrganizationReference"
            ),
            new QualifiedResourceName("Ed-Fi", "EducationOrganization")
        );

        result.Should().BeFalse();
    }

    [Test]
    public void It_should_mark_document_references_with_role_prefixes_as_role_named()
    {
        var result = ReferenceRoleNameConventions.IsDocumentReferenceRoleNamed(
            JsonPathExpressionCompiler.Compile("$.externalEducationOrganizationReference"),
            new QualifiedResourceName("Ed-Fi", "EducationOrganization")
        );

        result.Should().BeTrue();
    }

    [Test]
    public void It_should_not_mark_descriptor_array_leaf_matches_as_role_named()
    {
        var result = ReferenceRoleNameConventions.IsDescriptorRoleNamed(
            JsonPathExpressionCompiler.Compile("$.gradeLevelDescriptors[*]"),
            new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
        );

        result.Should().BeFalse();
    }

    [Test]
    public void It_should_mark_role_named_direct_descriptors_as_role_named()
    {
        var result = ReferenceRoleNameConventions.IsDescriptorRoleNamed(
            JsonPathExpressionCompiler.Compile("$.assessedGradeLevelDescriptor"),
            new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
        );

        result.Should().BeTrue();
    }
}
