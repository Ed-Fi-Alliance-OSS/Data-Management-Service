// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Direct unit coverage for <c>RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName</c>,
/// the abstract identity column-naming helper (DMS-1223). Covers the plain-scalar and direct-descriptor
/// branches — the only remaining callers of the 2-arg helper. Reference-backed abstract identity column
/// naming is now guarded end-to-end by the derivation tests (AbstractIdentityTableDerivationTests,
/// AbstractUnionViewDerivationTests, etc.) because those names flow directly from the concrete
/// reference binding's override-free convention column.
/// </summary>
[TestFixture]
public class Given_Building_An_Abstract_Identity_Column_Name
{
    [Test]
    public void It_should_keep_a_plain_scalar_identity_path_unchanged()
    {
        var name = RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName(
            JsonPathExpressionCompiler.Compile("$.beginDate"),
            isDescriptor: false
        );

        name.Should().Be("BeginDate");
    }

    [Test]
    public void It_should_append_DescriptorId_for_a_direct_non_reference_descriptor()
    {
        // A descriptor-valued identity field that is NOT behind a document reference still stores
        // dms.Descriptor.DocumentId, so it must get the _DescriptorId suffix (AC parity with concrete).
        var name = RelationalModelSetSchemaHelpers.BuildAbstractIdentityColumnName(
            JsonPathExpressionCompiler.Compile("$.programTypeDescriptor"),
            isDescriptor: true
        );

        name.Should().Be("ProgramTypeDescriptor_DescriptorId");
    }
}

/// <summary>
/// Direct unit coverage for the shared representative-binding selection rule
/// (<c>RelationalModelSetSchemaHelpers.IsReferenceFieldNameMatched</c> /
/// <c>OrderByRepresentativeReferenceBinding</c>). These are the linchpin shared by abstract identity column
/// naming, abstract identity maintenance triggers, and identity projection, so the rule is pinned directly
/// rather than only transitively through derivation fixtures.
/// </summary>
[TestFixture]
public class Given_Selecting_A_Representative_Reference_Binding
{
    [Test]
    public void It_should_match_when_reference_field_name_equals_identity_field_name()
    {
        RelationalModelSetSchemaHelpers
            .IsReferenceFieldNameMatched(
                JsonPathExpressionCompiler.Compile("$.programReference"),
                JsonPathExpressionCompiler.Compile("$.programReference.programName"),
                JsonPathExpressionCompiler.Compile("$.programName")
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_should_not_match_when_reference_field_name_differs_from_identity_field_name()
    {
        RelationalModelSetSchemaHelpers
            .IsReferenceFieldNameMatched(
                JsonPathExpressionCompiler.Compile("$.schoolReference"),
                JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                JsonPathExpressionCompiler.Compile("$.localEducationAgencyId")
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_should_order_field_matched_binding_first_then_ordinal_by_identity_path()
    {
        var referenceObjectPath = JsonPathExpressionCompiler.Compile("$.schoolReference");
        var bindings = new[]
        {
            (
                Reference: JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                Identity: JsonPathExpressionCompiler.Compile("$.stateEducationAgencyId")
            ),
            (
                Reference: JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                Identity: JsonPathExpressionCompiler.Compile("$.localEducationAgencyId")
            ),
            (
                Reference: JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                Identity: JsonPathExpressionCompiler.Compile("$.schoolId")
            ),
        };

        var ordered = RelationalModelSetSchemaHelpers
            .OrderByRepresentativeReferenceBinding(
                bindings,
                referenceObjectPath,
                binding => binding.Reference,
                binding => binding.Identity
            )
            .Select(binding => binding.Identity.Canonical)
            .ToArray();

        // Field-matched ($.schoolId) leads; the rest follow ordinal by identity path.
        ordered.Should().Equal("$.schoolId", "$.localEducationAgencyId", "$.stateEducationAgencyId");
    }
}
