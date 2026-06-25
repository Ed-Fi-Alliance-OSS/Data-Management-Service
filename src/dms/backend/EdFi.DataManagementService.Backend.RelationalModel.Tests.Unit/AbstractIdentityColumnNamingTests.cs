// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Direct unit coverage for <c>RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName</c>,
/// the abstract identity column-naming helper (DMS-1223). Exercises the branches that are otherwise
/// not reached by the current authoritative schemas — in particular the duplicate-field tie-break
/// fallback, which must match concrete <c>ResolveReferenceIdentityPartBaseName</c> by using the
/// TARGET-side identity path rather than the abstract reference-side path.
/// </summary>
[TestFixture]
public class Given_Building_An_Abstract_Identity_Column_Name
{
    private static readonly IReadOnlyDictionary<string, int> NoCounts = new Dictionary<string, int>(
        StringComparer.Ordinal
    );

    [Test]
    public void It_should_keep_a_plain_scalar_identity_path_unchanged()
    {
        var name = RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName(
            JsonPathExpressionCompiler.Compile("$.beginDate"),
            referenceObjectPath: null,
            referenceTargetIdentityPath: null,
            isDescriptor: false,
            NoCounts
        );

        name.Should().Be("BeginDate");
    }

    [Test]
    public void It_should_name_a_reference_backed_scalar_as_Ref_Field_with_Reference_stripped()
    {
        var name = RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName(
            JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
            JsonPathExpressionCompiler.Compile("$.studentReference"),
            JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
            isDescriptor: false,
            NoCounts
        );

        name.Should().Be("Student_StudentUniqueId");
    }

    [Test]
    public void It_should_append_DescriptorId_for_a_reference_backed_descriptor()
    {
        var name = RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName(
            JsonPathExpressionCompiler.Compile("$.programReference.programTypeDescriptor"),
            JsonPathExpressionCompiler.Compile("$.programReference"),
            JsonPathExpressionCompiler.Compile("$.programTypeDescriptor"),
            isDescriptor: true,
            NoCounts
        );

        name.Should().Be("Program_ProgramTypeDescriptor_DescriptorId");
    }

    [Test]
    public void It_should_append_DescriptorId_for_a_direct_non_reference_descriptor()
    {
        // A descriptor-valued identity field that is NOT behind a document reference still stores
        // dms.Descriptor.DocumentId, so it must get the _DescriptorId suffix (AC parity with concrete).
        var name = RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName(
            JsonPathExpressionCompiler.Compile("$.programTypeDescriptor"),
            referenceObjectPath: null,
            referenceTargetIdentityPath: null,
            isDescriptor: true,
            NoCounts
        );

        name.Should().Be("ProgramTypeDescriptor_DescriptorId");
    }

    /// <summary>
    /// The reviewer's CourseOffering shape: when two reference-side fields collide on the same
    /// reference-relative base name (count &gt; 1), the tie-break must disambiguate using the TARGET
    /// identity path (<c>$.schoolReference.schoolId</c> → <c>SchoolReferenceSchoolId</c>), exactly as
    /// concrete naming does — yielding <c>CourseOffering_SchoolReferenceSchoolId</c>, NOT the
    /// non-disambiguated <c>CourseOffering_SchoolId</c> nor a doubled
    /// <c>CourseOffering_CourseOfferingReferenceSchoolId</c>.
    /// </summary>
    [Test]
    public void It_should_disambiguate_a_colliding_field_using_the_target_identity_path()
    {
        IReadOnlyDictionary<string, int> collidingCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SchoolId"] = 2,
        };

        var name = RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName(
            JsonPathExpressionCompiler.Compile("$.courseOfferingReference.schoolId"),
            JsonPathExpressionCompiler.Compile("$.courseOfferingReference"),
            JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
            isDescriptor: false,
            collidingCounts
        );

        name.Should().Be("CourseOffering_SchoolReferenceSchoolId");
        name.Should().NotBe("CourseOffering_SchoolId");
        name.Should().NotBe("CourseOffering_CourseOfferingReferenceSchoolId");
    }

    [Test]
    public void It_should_not_disambiguate_when_the_reference_relative_field_is_unique()
    {
        IReadOnlyDictionary<string, int> uniqueCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SchoolId"] = 1,
        };

        var name = RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName(
            JsonPathExpressionCompiler.Compile("$.courseOfferingReference.schoolId"),
            JsonPathExpressionCompiler.Compile("$.courseOfferingReference"),
            JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
            isDescriptor: false,
            uniqueCounts
        );

        name.Should().Be("CourseOffering_SchoolId");
    }
}
