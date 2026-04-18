// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_Classifier_for_CreateNew_with_single_scalar_binding
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan();
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        _result = new ProfileRootTableBindingClassifier().Classify(
            plan,
            request,
            profileAppliedContext: null
        );
    }

    [Test]
    public void It_classifies_the_single_binding_as_VisibleWritable() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.VisibleWritable);

    [Test]
    public void It_has_no_resolver_owned_indices() => _result.ResolverOwnedBindingIndices.Should().BeEmpty();
}

[TestFixture]
public class Given_Classifier_with_key_unification_plan
{
    private ProfileRootTableBindingClassification _result = null!;
    private int _canonicalIndex;
    private int _syntheticPresenceIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIdx) = ProfileTestDoubles.BuildRootPlanWithKeyUnificationPlan();
        _canonicalIndex = canonicalIdx;
        _syntheticPresenceIndex = presenceIdx;
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        _result = new ProfileRootTableBindingClassifier().Classify(
            plan,
            request,
            profileAppliedContext: null
        );
    }

    [Test]
    public void It_includes_canonical_binding_in_resolver_owned() =>
        _result.ResolverOwnedBindingIndices.Should().Contain(_canonicalIndex);

    [Test]
    public void It_includes_synthetic_presence_binding_in_resolver_owned() =>
        _result.ResolverOwnedBindingIndices.Should().Contain(_syntheticPresenceIndex);

    [Test]
    public void It_marks_resolver_owned_bindings_as_StorageManaged()
    {
        _result.BindingsByIndex[_canonicalIndex].Should().Be(RootBindingDisposition.StorageManaged);
        _result.BindingsByIndex[_syntheticPresenceIndex].Should().Be(RootBindingDisposition.StorageManaged);
    }
}

[TestFixture]
public class Given_Classifier_with_ParentKeyPart_source_on_root_table
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlanWithParentKeyPartBinding();
        var request = ProfileTestDoubles.CreateRequest();
        _act = () =>
            new ProfileRootTableBindingClassifier().Classify(plan, request, profileAppliedContext: null);
    }

    [Test]
    public void It_throws_InvalidOperationException_for_plan_shape_violation()
    {
        _act.Should().Throw<InvalidOperationException>().WithMessage("*plan-shape violation*");
    }
}

[TestFixture]
public class Given_Classifier_for_ExistingDocument_with_stored_hidden_scope
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.firstName");
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredHiddenScope("$")
        );
        _result = new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_classifies_the_binding_as_HiddenPreserved() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.HiddenPreserved);
}

[TestFixture]
public class Given_Classifier_for_ExistingDocument_with_hidden_scalar_member_path
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.birthDate");
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisiblePresentScope("$", "birthDate")
        );
        _result = new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_classifies_the_binding_as_HiddenPreserved() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.HiddenPreserved);
}

[TestFixture]
public class Given_Classifier_for_ExistingDocument_with_request_visible_absent_and_no_hidden
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan(
            scalarRelativePath: "$.birthData.birthCity"
        );
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisibleAbsentScope("$.birthData")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisibleAbsentScope("$.birthData")
        );
        _result = new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_classifies_the_binding_as_ClearOnVisibleAbsent() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.ClearOnVisibleAbsent);
}

[TestFixture]
public class Given_Classifier_for_ExistingDocument_with_hidden_member_and_request_visible_absent
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan(
            scalarRelativePath: "$.birthData.birthCountryDescriptor"
        );
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisibleAbsentScope("$.birthData")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisibleAbsentScope(
                "$.birthData",
                "birthCountryDescriptor"
            )
        );
        _result = new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_classifies_hidden_binding_as_HiddenPreserved_overriding_visible_absent() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.HiddenPreserved);
}

[TestFixture]
public class Given_Classifier_with_document_reference_whole_reference_hidden
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        // Root plan: [0] = FK for $.schoolReference,
        //            [1] = derived $.schoolReference.schoolId,
        //            [2] = derived $.schoolReference.localEducationAgencyId.
        var derivedMemberPaths = new[]
        {
            "$.schoolReference.schoolId",
            "$.schoolReference.localEducationAgencyId",
        };
        var plan = ProfileTestDoubles.BuildRootPlanWithDocumentReferenceBindings(
            referenceMemberPath: "$.schoolReference",
            derivedMemberPaths: derivedMemberPaths
        );
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisiblePresentScope("$", "schoolReference")
        );
        _result = new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_preserves_the_FK_binding() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.HiddenPreserved);

    [Test]
    public void It_preserves_every_derived_binding()
    {
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.HiddenPreserved);
        _result.BindingsByIndex[2].Should().Be(RootBindingDisposition.HiddenPreserved);
    }
}

[TestFixture]
public class Given_Classifier_with_document_reference_exact_child_hidden
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var derivedMemberPaths = new[]
        {
            "$.schoolReference.schoolId",
            "$.schoolReference.localEducationAgencyId",
        };
        var plan = ProfileTestDoubles.BuildRootPlanWithDocumentReferenceBindings(
            referenceMemberPath: "$.schoolReference",
            derivedMemberPaths: derivedMemberPaths
        );
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisiblePresentScope("$", "schoolReference.schoolId")
        );
        _result = new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_does_not_preserve_the_FK_binding_exact_child_miss() =>
        // FK binding's member path is "schoolReference"; the hidden path is "schoolReference.schoolId"
        // (a descendant). Ancestor-or-exact matching does NOT consider descendants of memberPath
        // as ancestors of hiddenPath. So FK is VisibleWritable.
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.VisibleWritable);

    [Test]
    public void It_preserves_only_the_exact_matching_derived_binding() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.HiddenPreserved);

    [Test]
    public void It_does_not_preserve_sibling_derived_binding() =>
        _result.BindingsByIndex[2].Should().Be(RootBindingDisposition.VisibleWritable);
}

[TestFixture]
public class Given_Classifier_with_descriptor_reference_and_exact_match_hidden_path
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlanWithDescriptorReferenceBinding(
            relativePath: "$.sexDescriptor"
        );
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisiblePresentScope("$", "sexDescriptor")
        );
        _result = new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_preserves_descriptor_binding_on_exact_match() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.HiddenPreserved);
}

[TestFixture]
public class Given_Classifier_for_ExistingDocument_with_stored_scope_unresolvable_to_any_root_binding
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        // Root plan has only a $.firstName scalar binding. Profile reports a stored scope at
        // $.unrelatedScope with no binding under it — upstream metadata drift.
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.firstName");
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisiblePresentScope("$.unrelatedScope")
        );
        _act = () => new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_throws_InvalidOperationException_for_unresolvable_stored_scope()
    {
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Stored scope*does not resolve to any root-table binding*");
    }
}

[TestFixture]
public class Given_Classifier_for_ExistingDocument_with_hidden_member_path_unresolvable_to_any_binding
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        // Root plan has only a $.firstName scalar binding. Stored scope $ exists but its
        // HiddenMemberPaths contains "lastName" — no binding under $ exact-matches "lastName".
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.firstName");
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisiblePresentScope("$", "lastName")
        );
        _act = () => new ProfileRootTableBindingClassifier().Classify(plan, request, context);
    }

    [Test]
    public void It_throws_InvalidOperationException_for_unresolvable_hidden_member_path()
    {
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Hidden member path*does not resolve to any root-table binding under that scope*");
    }
}

[TestFixture]
public class Given_Classifier_with_Precomputed_and_DocumentId_sources
{
    private ProfileRootTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlanWithPrecomputedAndDocumentIdBindings();
        var request = ProfileTestDoubles.CreateRequest();
        _result = new ProfileRootTableBindingClassifier().Classify(
            plan,
            request,
            profileAppliedContext: null
        );
    }

    [Test]
    public void It_classifies_precomputed_as_StorageManaged() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.StorageManaged);

    [Test]
    public void It_classifies_document_id_as_StorageManaged() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.StorageManaged);
}
