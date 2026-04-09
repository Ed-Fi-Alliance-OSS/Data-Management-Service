// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_WritePlanCompiler_KeyUnification : WritePlanCompilerTestBase
{
    [Test]
    public void It_should_compile_resources_with_root_key_unification_classes()
    {
        var keyUnificationModel = CreateRootOnlyModelWithCompiledKeyUnificationInventory();
        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(keyUnificationModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .KeyUnificationPlans.Should()
            .HaveCount(keyUnificationModel.Root.KeyUnificationClasses.Count);
    }

    [Test]
    public void It_should_compile_key_unification_plans_with_scalar_and_descriptor_members_in_member_order()
    {
        var model = CreateRootOnlyModelWithCompiledKeyUnificationInventory();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();

        static int GetBindingIndex(TableWritePlan plan, string columnName)
        {
            return plan
                .ColumnBindings.Select((binding, index) => (binding, index))
                .Single(tuple => tuple.binding.Column.ColumnName.Value == columnName)
                .index;
        }

        tablePlan.KeyUnificationPlans.Should().HaveCount(2);

        var scalarClassPlan = tablePlan.KeyUnificationPlans[0];
        scalarClassPlan.CanonicalColumn.Should().Be(new DbColumnName("SchoolYearCanonical"));
        scalarClassPlan.CanonicalBindingIndex.Should().Be(GetBindingIndex(tablePlan, "SchoolYearCanonical"));
        scalarClassPlan.MembersInOrder.Should().HaveCount(2);
        scalarClassPlan
            .MembersInOrder[0]
            .Should()
            .BeEquivalentTo(
                new KeyUnificationMemberWritePlan.ScalarMember(
                    MemberPathColumn: new DbColumnName("SchoolYearSecondary"),
                    RelativePath: CreatePath(
                        "$.localSchoolYear",
                        new JsonPathSegment.Property("localSchoolYear")
                    ),
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                )
            );
        scalarClassPlan
            .MembersInOrder[1]
            .Should()
            .BeEquivalentTo(
                new KeyUnificationMemberWritePlan.ScalarMember(
                    MemberPathColumn: new DbColumnName("SchoolYearPrimary"),
                    RelativePath: CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear")),
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                )
            );

        var descriptorClassPlan = tablePlan.KeyUnificationPlans[1];
        descriptorClassPlan
            .CanonicalColumn.Should()
            .Be(new DbColumnName("SchoolYearTypeDescriptorIdCanonical"));
        descriptorClassPlan
            .CanonicalBindingIndex.Should()
            .Be(GetBindingIndex(tablePlan, "SchoolYearTypeDescriptorIdCanonical"));
        descriptorClassPlan.MembersInOrder.Should().HaveCount(2);
        descriptorClassPlan
            .MembersInOrder[0]
            .Should()
            .BeEquivalentTo(
                new KeyUnificationMemberWritePlan.DescriptorMember(
                    MemberPathColumn: new DbColumnName("SchoolYearTypeDescriptorSecondary"),
                    RelativePath: CreatePath(
                        "$.localSchoolYearTypeDescriptor",
                        new JsonPathSegment.Property("localSchoolYearTypeDescriptor")
                    ),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor"),
                    PresenceColumn: new DbColumnName("SchoolYearTypeDescriptorSecondary_Present"),
                    PresenceBindingIndex: GetBindingIndex(
                        tablePlan,
                        "SchoolYearTypeDescriptorSecondary_Present"
                    ),
                    PresenceIsSynthetic: true
                )
            );
        descriptorClassPlan
            .MembersInOrder[1]
            .Should()
            .BeEquivalentTo(
                new KeyUnificationMemberWritePlan.DescriptorMember(
                    MemberPathColumn: new DbColumnName("SchoolYearTypeDescriptorPrimary"),
                    RelativePath: CreatePath(
                        "$.schoolYearTypeDescriptor",
                        new JsonPathSegment.Property("schoolYearTypeDescriptor")
                    ),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor"),
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                )
            );

        var syntheticPresenceBindingIndex = (
            (KeyUnificationMemberWritePlan.DescriptorMember)descriptorClassPlan.MembersInOrder[0]
        ).PresenceBindingIndex;
        syntheticPresenceBindingIndex.Should().NotBeNull();
        tablePlan
            .ColumnBindings[syntheticPresenceBindingIndex!.Value]
            .Source.Should()
            .BeOfType<WriteValueSource.Precomputed>();
    }

    [Test]
    public void It_should_compile_reference_derived_key_unification_members_for_optional_reference_sites()
    {
        var model = ReferenceDerivedWritePlanFixture.CreateModel();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();
        var keyUnificationPlan = tablePlan.KeyUnificationPlans.Single();
        var referenceDerivedMember = keyUnificationPlan
            .MembersInOrder.Single()
            .Should()
            .BeOfType<KeyUnificationMemberWritePlan.ReferenceDerivedMember>()
            .Subject;

        keyUnificationPlan.CanonicalColumn.Should().Be(new DbColumnName("SchoolId_Canonical"));
        referenceDerivedMember.MemberPathColumn.Should().Be(new DbColumnName("School_RefSchoolIdAlias"));
        referenceDerivedMember.RelativePath.Canonical.Should().Be("$.schoolReference.schoolId");
        referenceDerivedMember.ReferenceSource.BindingIndex.Should().Be(0);
        referenceDerivedMember.ReferenceSource.ReferenceObjectPath.Canonical.Should().Be("$.schoolReference");
        referenceDerivedMember
            .ReferenceSource.ReferenceJsonPath.Canonical.Should()
            .Be("$.schoolReference.schoolId");
        referenceDerivedMember.PresenceColumn.Should().Be(new DbColumnName("School_DocumentId"));
        referenceDerivedMember
            .PresenceBindingIndex.Should()
            .Be(
                tablePlan
                    .ColumnBindings.Select((binding, index) => (binding, index))
                    .Single(tuple => tuple.binding.Column.ColumnName.Value == "School_DocumentId")
                    .index
            );
        referenceDerivedMember.PresenceIsSynthetic.Should().BeFalse();
    }

    [Test]
    public void It_should_preserve_reference_derived_precedence_over_ordinary_json_bound_members()
    {
        var model = ReferenceDerivedWritePlanFixture.CreateMixedSourceModel();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();
        var keyUnificationPlan = tablePlan.KeyUnificationPlans.Single();

        keyUnificationPlan.MembersInOrder.Should().HaveCount(2);

        keyUnificationPlan
            .MembersInOrder[0]
            .Should()
            .BeOfType<KeyUnificationMemberWritePlan.ReferenceDerivedMember>()
            .Which.MemberPathColumn.Should()
            .Be(new DbColumnName("School_RefSchoolIdAlias"));

        keyUnificationPlan
            .MembersInOrder[1]
            .Should()
            .BeEquivalentTo(
                new KeyUnificationMemberWritePlan.ScalarMember(
                    MemberPathColumn: new DbColumnName("SchoolId_LocalAlias"),
                    RelativePath: CreatePath(
                        "$.localSchoolId",
                        new JsonPathSegment.Property("localSchoolId")
                    ),
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                )
            );
    }

    [Test]
    public void It_should_compile_descriptor_backed_reference_derived_key_unification_members()
    {
        var model = ReferenceDerivedWritePlanFixture.CreateDescriptorBackedKeyUnificationModel();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();
        var keyUnificationPlan = tablePlan.KeyUnificationPlans.Single();
        var referenceDerivedMember = keyUnificationPlan
            .MembersInOrder.Single()
            .Should()
            .BeOfType<KeyUnificationMemberWritePlan.ReferenceDerivedMember>()
            .Subject;

        keyUnificationPlan
            .CanonicalBindingIndex.Should()
            .Be(
                tablePlan
                    .ColumnBindings.Select((binding, index) => (binding, index))
                    .Single(tuple =>
                        tuple.binding.Column.ColumnName.Value == "SchoolCategoryDescriptorId_Canonical"
                    )
                    .index
            );
        referenceDerivedMember
            .MemberPathColumn.Should()
            .Be(new DbColumnName("SchoolCategoryDescriptorId_Alias"));
        referenceDerivedMember
            .RelativePath.Canonical.Should()
            .Be("$.schoolReference.schoolCategoryDescriptor");
        referenceDerivedMember.ReferenceSource.BindingIndex.Should().Be(0);
        referenceDerivedMember.ReferenceSource.ReferenceObjectPath.Canonical.Should().Be("$.schoolReference");
        referenceDerivedMember
            .ReferenceSource.ReferenceJsonPath.Canonical.Should()
            .Be("$.schoolReference.schoolCategoryDescriptor");
        referenceDerivedMember.PresenceColumn.Should().Be(new DbColumnName("School_DocumentId"));
        referenceDerivedMember.PresenceIsSynthetic.Should().BeFalse();
    }

    [Test]
    public void It_should_fail_fast_when_reference_derived_key_unification_member_source_path_drifts_from_binding_metadata()
    {
        var model = ReferenceDerivedWritePlanFixture.WithColumnSourceJsonPath(
            ReferenceDerivedWritePlanFixture.CreateModel(),
            "School_RefSchoolIdAlias",
            "$.schoolReference.localSchoolId"
        );
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile key-unification plan for 'edfi.ProgramReferenceDerived': reference-derived source mismatch for member path column 'School_RefSchoolIdAlias'. DbColumnModel.SourceJsonPath '$.schoolReference.localSchoolId' does not match ReferenceIdentityBinding.ReferenceJsonPath '$.schoolReference.schoolId'."
            );
    }

    [Test]
    public void It_should_fail_fast_when_synthetic_presence_column_is_missing_null_or_true_constraint()
    {
        var unsupportedModel = CreateRootOnlyModelWithMissingSyntheticPresenceConstraint();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile key-unification plan for 'edfi.Student': synthetic presence column 'SchoolYearTypeDescriptorSecondary_Present' for member 'SchoolYearTypeDescriptorSecondary' must define a matching NullOrTrue constraint.*"
            );
    }

    [Test]
    public void It_should_treat_presence_columns_with_source_paths_as_non_synthetic_without_null_or_true_constraint()
    {
        var model = CreateRootOnlyModelWithReferenceSitePresence();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();
        var descriptorClassPlan = tablePlan.KeyUnificationPlans[1];
        var secondaryMember = (KeyUnificationMemberWritePlan.DescriptorMember)
            descriptorClassPlan.MembersInOrder[0];

        secondaryMember
            .PresenceColumn.Should()
            .Be(new DbColumnName("SchoolYearTypeDescriptorSecondary_Present"));
        secondaryMember.PresenceBindingIndex.Should().NotBeNull();
        secondaryMember.PresenceIsSynthetic.Should().BeFalse();
    }

    [Test]
    public void It_should_not_treat_reference_group_document_fk_presence_with_null_source_path_as_synthetic()
    {
        var model = CreateRootOnlyModelWithReferenceGroupDocumentFkPresence(
            useNullPresenceSourceJsonPath: true
        );
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();
        var descriptorClassPlan = tablePlan.KeyUnificationPlans[1];
        var secondaryMember = (KeyUnificationMemberWritePlan.DescriptorMember)
            descriptorClassPlan.MembersInOrder[0];

        secondaryMember.PresenceColumn.Should().Be(new DbColumnName("School_DocumentId"));
        secondaryMember.PresenceBindingIndex.Should().NotBeNull();
        secondaryMember.PresenceIsSynthetic.Should().BeFalse();
        tablePlan
            .ColumnBindings[secondaryMember.PresenceBindingIndex!.Value]
            .Source.Should()
            .BeOfType<WriteValueSource.DocumentReference>();
    }

    [Test]
    public void It_should_fail_fast_when_key_unification_canonical_column_is_not_precomputed()
    {
        var unsupportedModel = CreateRootOnlyModelWithKeyUnificationClass();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile key-unification plan for 'edfi.Student': canonical column 'SchoolYear' must bind as Precomputed.*"
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_unification_models_contain_extra_null_source_writable_columns_without_producer_plans()
    {
        var unsupportedModel = CreateRootOnlyModelWithOrphanPrecomputedBinding();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.Student': column 'SchoolYearCanonicalOrphan' (kind 'Scalar') has null SourceJsonPath but is not an explicitly supported precomputed target. Mark the column IsWritable=false or add a producer plan (for example, key-unification canonical/synthetic presence)."
            );
    }

    [Test]
    public void It_should_fail_fast_when_precomputed_binding_has_multiple_key_unification_producers()
    {
        var unsupportedModel = CreateRootOnlyModelWithDuplicatePrecomputedProducer();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile key-unification plan for 'edfi.Student': precomputed bindings produced multiple times by collection-key preallocation or key-unification inventory: 'SchoolYearCanonical'.*"
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_unification_member_path_column_is_not_unified_alias()
    {
        var unsupportedModel = CreateRootOnlyModelWithStoredKeyUnificationMemberPathColumn();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile key-unification plan for 'edfi.Student': member path column 'SchoolYearSecondary' must use UnifiedAlias storage, but was Stored.*"
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_unification_member_path_column_kind_is_not_supported()
    {
        var unsupportedModel = CreateRootOnlyModelWithUnsupportedKeyUnificationMemberPathColumnKind();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile key-unification plan for 'edfi.Student': member path column 'SchoolYearSecondary' has unsupported kind 'ParentKeyPart'. Supported kinds are Scalar and DescriptorFk.*"
            );
    }
}
