// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_SeparateTableClassifier_for_visible_writable_extension_binding
{
    private ProfileSeparateTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        // Root + RootExtension at $._ext.sample, with a single scalar binding under the
        // extension scope ($._ext.sample.favoriteColor). Request makes both scopes visible.
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlan(
            extensionBindings: new ProfileTestDoubles.RootExtensionBindingSpec(
                "FavoriteColor",
                ProfileTestDoubles.RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample")
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        _result = new ProfileSeparateTableBindingClassifier().Classify(
            plan,
            extensionTable,
            request,
            profileAppliedContext: null
        );
    }

    [Test]
    public void It_classifies_the_ParentKeyPart_binding_as_StorageManaged() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.StorageManaged);

    [Test]
    public void It_classifies_the_scalar_binding_as_VisibleWritable() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.VisibleWritable);

    [Test]
    public void It_has_no_resolver_owned_indices() => _result.ResolverOwnedBindingIndices.Should().BeEmpty();
}

[TestFixture]
public class Given_SeparateTableClassifier_for_hidden_preserved_extension_binding
{
    private ProfileSeparateTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlan(
            extensionBindings: new ProfileTestDoubles.RootExtensionBindingSpec(
                "FavoriteColor",
                ProfileTestDoubles.RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample")
        );
        // Stored side: $._ext.sample scope has HiddenMemberPaths containing "favoriteColor".
        // Per-table classification only inspects bindings on the classified table; stored
        // scopes that belong to other tables (e.g., root $) don't participate here.
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisiblePresentScope("$._ext.sample", "favoriteColor")
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        _result = new ProfileSeparateTableBindingClassifier().Classify(
            plan,
            extensionTable,
            request,
            context
        );
    }

    [Test]
    public void It_classifies_the_scalar_binding_as_HiddenPreserved() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.HiddenPreserved);
}

[TestFixture]
public class Given_SeparateTableClassifier_for_clear_on_visible_absent_extension_binding
{
    private ProfileSeparateTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlan(
            extensionBindings: new ProfileTestDoubles.RootExtensionBindingSpec(
                "FavoriteColor",
                ProfileTestDoubles.RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        // Request: extension scope VisibleAbsent (request omits it, should clear stored).
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisibleAbsentScope("$._ext.sample")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisibleAbsentScope("$._ext.sample")
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        _result = new ProfileSeparateTableBindingClassifier().Classify(
            plan,
            extensionTable,
            request,
            context
        );
    }

    [Test]
    public void It_classifies_the_scalar_binding_as_ClearOnVisibleAbsent() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.ClearOnVisibleAbsent);
}

[TestFixture]
public class Given_SeparateTableClassifier_for_storage_managed_precomputed_binding
{
    private ProfileSeparateTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        // Extension table carries a Precomputed binding alongside the ParentKeyPart. The
        // Precomputed source is storage-managed regardless of scope state.
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlan(
            extensionBindings: new ProfileTestDoubles.RootExtensionBindingSpec(
                "ExtensionId",
                ProfileTestDoubles.RootExtensionBindingKind.Precomputed
            )
        );
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample")
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        _result = new ProfileSeparateTableBindingClassifier().Classify(
            plan,
            extensionTable,
            request,
            profileAppliedContext: null
        );
    }

    [Test]
    public void It_classifies_the_precomputed_binding_as_StorageManaged() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.StorageManaged);

    [Test]
    public void It_reports_no_resolver_owned_binding_indices() =>
        _result.ResolverOwnedBindingIndices.Should().BeEmpty();
}

[TestFixture]
public class Given_SeparateTableClassifier_for_ParentKeyPart_binding
{
    // Proves the preliminary core adjustment: ParentKeyPart on a RootExtension table is now
    // classified as StorageManaged rather than throwing. The synthesizer must skip it during
    // profile overlay; Task 5 will add a parent-key rewrite step for separate-table rows.
    private ProfileSeparateTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlan();
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample")
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        _result = new ProfileSeparateTableBindingClassifier().Classify(
            plan,
            extensionTable,
            request,
            profileAppliedContext: null
        );
    }

    [Test]
    public void It_classifies_the_ParentKeyPart_binding_as_StorageManaged() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.StorageManaged);
}

[TestFixture]
public class Given_SeparateTableClassifier_with_stored_context_containing_unrelated_root_scope_does_not_false_fail
{
    // Slice 3 correctness invariant: the core's stored-scope / hidden-member-path drift
    // validation must only consider stored scopes relevant to the table being classified.
    // A realistic existing-document profile context carries stored scope states for every
    // scope on the resource — including the root ($) when classifying an extension table
    // at $._ext.sample. Without scope-relevance filtering, the extension-table invocation
    // would see the root $ stored scope and false-fail with "Stored scope '$' does not
    // resolve to any binding on table 'sample.HostExtension'" because $ belongs to the
    // root table's classification, not the extension table's. This fixture pins the
    // invariant so a regression re-surfaces immediately.
    private ProfileSeparateTableBindingClassification _result = null!;
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlan(
            extensionBindings: new ProfileTestDoubles.RootExtensionBindingSpec(
                "FavoriteColor",
                ProfileTestDoubles.RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample")
        );
        // Stored side mirrors the request: BOTH the root scope ($, with a HiddenMemberPaths
        // entry naming a root member) AND the extension scope ($._ext.sample) are present.
        // The $ stored scope belongs to the root table; it must be ignored when classifying
        // the extension table. The $._ext.sample stored scope does belong here and resolves
        // to the FavoriteColor scalar binding as usual.
        var context = ProfileTestDoubles.CreateContext(
            request,
            visibleStoredBody: null,
            ProfileTestDoubles.StoredVisiblePresentScope("$", "firstName"),
            ProfileTestDoubles.StoredVisiblePresentScope("$._ext.sample")
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        try
        {
            _result = new ProfileSeparateTableBindingClassifier().Classify(
                plan,
                extensionTable,
                request,
                context
            );
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_does_not_throw_on_unrelated_root_stored_scope() => _thrown.Should().BeNull();

    [Test]
    public void It_classifies_the_ParentKeyPart_binding_as_StorageManaged() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.StorageManaged);

    [Test]
    public void It_classifies_the_extension_scalar_binding_as_VisibleWritable() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.VisibleWritable);
}

[TestFixture]
public class Given_SeparateTableClassifier_with_stored_context_containing_unrelated_hidden_member_path_does_not_false_fail
{
    // Companion to the unrelated-root-scope fixture above: even when the root stored scope
    // carries HiddenMemberPaths that do not correspond to any extension-table binding, the
    // extension-table classifier must not false-fail on them. Hidden-member-path drift
    // validation must be scoped to the same subtree filter used for the stored-scope
    // existence check.
    private ProfileSeparateTableBindingClassification _result = null!;
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlan(
            extensionBindings: new ProfileTestDoubles.RootExtensionBindingSpec(
                "FavoriteColor",
                ProfileTestDoubles.RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample")
        );
        // Root stored scope declares a HiddenMemberPath ("lastName") that is not present on
        // the extension table. The extension-table classifier must skip the entire root
        // stored scope (including its hidden paths), not just the scope-existence check.
        var context = ProfileTestDoubles.CreateContext(
            request,
            visibleStoredBody: null,
            ProfileTestDoubles.StoredVisiblePresentScope("$", "lastName"),
            ProfileTestDoubles.StoredVisiblePresentScope("$._ext.sample")
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        try
        {
            _result = new ProfileSeparateTableBindingClassifier().Classify(
                plan,
                extensionTable,
                request,
                context
            );
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_does_not_throw_on_unrelated_root_hidden_member_path() => _thrown.Should().BeNull();

    [Test]
    public void It_classifies_the_extension_scalar_binding_as_VisibleWritable() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.VisibleWritable);
}

[TestFixture]
public class Given_SeparateTableClassifier_rejects_non_RootExtension_table_kind
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        // Separate table tagged as CollectionExtensionScope instead of RootExtension —
        // the slice 5 scope family the separate-table classifier must reject.
        var plan = ProfileTestDoubles.BuildRootPlusSeparateTablePlanWithNonRootExtensionKind(
            nonRootExtensionKind: DbTableKind.CollectionExtensionScope
        );
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var separateTable = plan.TablePlansInDependencyOrder[1];
        _act = () =>
            new ProfileSeparateTableBindingClassifier().Classify(
                plan,
                separateTable,
                request,
                profileAppliedContext: null
            );
    }

    [Test]
    public void It_throws_ArgumentException_with_slice_5_guidance()
    {
        _act.Should().Throw<ArgumentException>().WithMessage("*RootExtension*slice 5*");
    }
}
