// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for same-site logical reference-field grouping.
/// </summary>
[TestFixture]
public class Given_KeyUnification_Reference_Field_Grouping
{
    private IReadOnlyList<DocumentReferenceFieldGroup> _logicalFieldGroups = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.schoolReference"),
            Table: new DbTableName(new DbSchemaName("edfi"), "Enrollment"),
            FkColumn: new DbColumnName("School_DocumentId"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                    JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                    new DbColumnName("School_SchoolId")
                ),
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.schoolReference.educationOrganizationId"),
                    JsonPathExpressionCompiler.Compile("$.schoolReference.educationOrganizationId"),
                    new DbColumnName("School_EducationOrganizationId")
                ),
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                    JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                    new DbColumnName("SecondarySchool_SchoolId")
                ),
            ]
        );

        _logicalFieldGroups = binding.GetLogicalFieldGroups();
    }

    /// <summary>
    /// It should preserve first-seen logical field order while exposing all physical member columns.
    /// </summary>
    [Test]
    public void It_should_preserve_first_seen_logical_field_order_while_exposing_all_member_columns()
    {
        _logicalFieldGroups
            .Select(group => group.ReferenceJsonPath.Canonical)
            .Should()
            .Equal("$.schoolReference.schoolId", "$.schoolReference.educationOrganizationId");

        _logicalFieldGroups[0]
            .MemberColumns.Select(column => column.Value)
            .Should()
            .Equal("School_SchoolId", "SecondarySchool_SchoolId");
        _logicalFieldGroups[1]
            .MemberColumns.Select(column => column.Value)
            .Should()
            .Equal("School_EducationOrganizationId");

        _logicalFieldGroups
            .Should()
            .OnlyContain(group =>
                group.IsIdentityComponent
                && group.ReferenceObjectPath.Canonical == "$.schoolReference"
                && group.Table.Equals(new DbTableName(new DbSchemaName("edfi"), "Enrollment"))
                && group.FkColumn.Equals(new DbColumnName("School_DocumentId"))
                && group.TargetResource.Equals(new QualifiedResourceName("Ed-Fi", "School"))
            );
    }
}

/// <summary>
/// Test fixture for guarding against cross-site logical-field merging.
/// </summary>
[TestFixture]
public class Given_KeyUnification_Reference_Field_Grouping_With_Duplicate_Paths_Across_Sites
{
    private IReadOnlyList<DocumentReferenceFieldGroup> _logicalFieldGroups = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var table = new DbTableName(new DbSchemaName("edfi"), "Enrollment");
        var duplicatePath = JsonPathExpressionCompiler.Compile("$.sharedReference.localEducationAgencyId");

        DocumentReferenceBinding[] bindings =
        [
            new DocumentReferenceBinding(
                IsIdentityComponent: false,
                ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.schoolReference"),
                Table: table,
                FkColumn: new DbColumnName("School_DocumentId"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        duplicatePath,
                        duplicatePath,
                        new DbColumnName("School_LocalEducationAgencyId")
                    ),
                ]
            ),
            new DocumentReferenceBinding(
                IsIdentityComponent: false,
                ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.educationOrganizationReference"),
                Table: table,
                FkColumn: new DbColumnName("EducationOrganization_DocumentId"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        duplicatePath,
                        duplicatePath,
                        new DbColumnName("EducationOrganization_LocalEducationAgencyId")
                    ),
                ]
            ),
        ];

        _logicalFieldGroups = bindings.GetLogicalFieldGroups();
    }

    /// <summary>
    /// It should keep duplicate logical paths isolated to their own reference sites.
    /// </summary>
    [Test]
    public void It_should_not_merge_duplicate_logical_paths_from_different_reference_sites()
    {
        _logicalFieldGroups.Should().HaveCount(2);

        _logicalFieldGroups
            .Select(group => group.ReferenceObjectPath.Canonical)
            .Should()
            .Equal("$.schoolReference", "$.educationOrganizationReference");

        _logicalFieldGroups[0]
            .ReferenceJsonPath.Canonical.Should()
            .Be("$.sharedReference.localEducationAgencyId");
        _logicalFieldGroups[0]
            .MemberColumns.Select(column => column.Value)
            .Should()
            .Equal("School_LocalEducationAgencyId");

        _logicalFieldGroups[1]
            .ReferenceJsonPath.Canonical.Should()
            .Be("$.sharedReference.localEducationAgencyId");
        _logicalFieldGroups[1]
            .MemberColumns.Select(column => column.Value)
            .Should()
            .Equal("EducationOrganization_LocalEducationAgencyId");
    }
}
