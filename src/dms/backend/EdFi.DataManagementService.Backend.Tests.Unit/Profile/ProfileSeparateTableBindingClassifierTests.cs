// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_SeparateTableClassifier_instance_aware_overload_for_sibling_scope_instances
{
    private const string ParentScope = "$.parents[*]";
    private const string AlignedScope = "$.parents[*]._ext.aligned";

    private ProfileSeparateTableBindingClassification _hiddenInstance = null!;
    private ProfileSeparateTableBindingClassification _visibleInstance = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlan(
            extensionJsonScope: AlignedScope,
            extensionBindings: new ProfileTestDoubles.RootExtensionBindingSpec(
                "FavoriteColor",
                ProfileTestDoubles.RootExtensionBindingKind.Scalar,
                RelativePath: $"{AlignedScope}.favoriteColor"
            )
        );
        var separateTable = plan.TablePlansInDependencyOrder[1];

        var addressA = ScopeAddressForParent("A");
        var addressB = ScopeAddressForParent("B");
        var requestA = new RequestScopeState(addressA, ProfileVisibilityKind.VisiblePresent, true);
        var requestB = new RequestScopeState(addressB, ProfileVisibilityKind.VisiblePresent, true);
        var storedA = new StoredScopeState(addressA, ProfileVisibilityKind.VisiblePresent, ["favoriteColor"]);
        var storedB = new StoredScopeState(addressB, ProfileVisibilityKind.VisiblePresent, []);

        var classifier = new ProfileSeparateTableBindingClassifier();
        _hiddenInstance = classifier.Classify(
            plan,
            separateTable,
            addressA,
            requestA,
            storedA,
            descendantStates: default
        );
        _visibleInstance = classifier.Classify(
            plan,
            separateTable,
            addressB,
            requestB,
            storedB,
            descendantStates: default
        );
    }

    [Test]
    public void It_uses_the_hidden_paths_for_the_matching_instance() =>
        _hiddenInstance.BindingsByIndex[1].Should().Be(RootBindingDisposition.HiddenPreserved);

    [Test]
    public void It_does_not_reuse_hidden_paths_from_a_sibling_instance() =>
        _visibleInstance.BindingsByIndex[1].Should().Be(RootBindingDisposition.VisibleWritable);

    private static ScopeInstanceAddress ScopeAddressForParent(string parentId) =>
        new(
            AlignedScope,
            [
                new AncestorCollectionInstance(
                    ParentScope,
                    [new SemanticIdentityPart("$.parentId", JsonValue.Create(parentId), IsPresent: true)]
                ),
            ]
        );
}

[TestFixture]
public class Given_SeparateTableClassifier_instance_aware_overload_for_CollectionExtensionScope_table_kind
{
    private ProfileSeparateTableBindingClassification _result = null!;
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusSeparateTablePlanWithNonRootExtensionKind(
            nonRootExtensionKind: DbTableKind.CollectionExtensionScope
        );
        var separateTable = plan.TablePlansInDependencyOrder[1];
        var scopeAddress = new ScopeInstanceAddress("$._ext.sample", []);
        var requestScope = new RequestScopeState(
            scopeAddress,
            ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );

        try
        {
            _result = new ProfileSeparateTableBindingClassifier().Classify(
                plan,
                separateTable,
                scopeAddress,
                requestScope,
                storedScope: null,
                descendantStates: default
            );
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_does_not_throw() => _thrown.Should().BeNull();

    [Test]
    public void It_classifies_the_ParentKeyPart_binding_as_StorageManaged() =>
        _result.BindingsByIndex[0].Should().Be(RootBindingDisposition.StorageManaged);

    [Test]
    public void It_classifies_the_scalar_binding_as_VisibleWritable() =>
        _result.BindingsByIndex[1].Should().Be(RootBindingDisposition.VisibleWritable);
}

[TestFixture]
public class Given_SeparateTableClassifier_rejects_unsupported_table_kind
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusSeparateTablePlanWithNonRootExtensionKind(
            nonRootExtensionKind: DbTableKind.Root
        );
        var separateTable = plan.TablePlansInDependencyOrder[1];
        var scopeAddress = new ScopeInstanceAddress("$._ext.sample", []);
        var requestScope = new RequestScopeState(
            scopeAddress,
            ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );

        _act = () =>
            new ProfileSeparateTableBindingClassifier().Classify(
                plan,
                separateTable,
                scopeAddress,
                requestScope,
                storedScope: null,
                descendantStates: default
            );
    }

    [Test]
    public void It_throws_ArgumentException_with_supported_kinds() =>
        _act.Should().Throw<ArgumentException>().WithMessage("*RootExtension*CollectionExtensionScope*Root*");
}

[TestFixture]
public class Given_SeparateTableClassifier_for_hidden_descendant_inlined_scope_under_RootExtension
{
    // Slice 5 CP5: a stored hidden-member-path on a descendant inlined non-collection scope
    // (one whose owner table equals the direct scope's RootExtension table) must flow into
    // the classifier when the caller passes the collected descendant-state envelope. The
    // descendant binding classifies as HiddenPreserved because its hidden path is recorded
    // on the descendant scope's stored state, not on the direct scope.
    private ProfileSeparateTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlanWithInlinedDescendantScope(
            descendantScopeRelativePath: "$._ext.sample.detail",
            descendantBindingRelativePath: "$._ext.sample.detail.someField"
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        var directScopeAddress = new ScopeInstanceAddress("$._ext.sample", []);

        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample.detail")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            visibleStoredBody: null,
            ProfileTestDoubles.StoredVisiblePresentScope("$._ext.sample"),
            ProfileTestDoubles.StoredVisiblePresentScope("$._ext.sample.detail", "someField")
        );

        var directRequestScope = ProfileMemberGovernanceRules.LookupRequestScope(request, directScopeAddress);
        var directStoredScope = ProfileMemberGovernanceRules.LookupStoredScope(context, directScopeAddress);
        var descendants = ProfileSeparateScopeDescendantStates.Collect(
            plan,
            extensionTable,
            directScopeAddress,
            request,
            context
        );

        _result = new ProfileSeparateTableBindingClassifier().Classify(
            plan,
            extensionTable,
            directScopeAddress,
            directRequestScope,
            directStoredScope,
            descendants
        );
    }

    [Test]
    public void It_classifies_the_descendant_scoped_binding_as_HiddenPreserved()
    {
        // Bindings on the extension table: [0] ParentKeyPart, [1] direct-scope FavoriteColor,
        // [2] descendant-scope DetailField (someField). The descendant binding picks up the
        // hidden-member-path that lives on the descendant stored scope.
        _result.BindingsByIndex[2].Should().Be(RootBindingDisposition.HiddenPreserved);
    }
}

[TestFixture]
public class Given_SeparateTableClassifier_for_VisibleAbsent_descendant_inlined_scope_under_CollectionExtensionScope
{
    // Slice 5 CP5: a VisibleAbsent descendant inlined scope under a CollectionExtensionScope
    // direct scope (with the descendant's owner table equal to the direct scope's table)
    // flows ClearOnVisibleAbsent through to the descendant binding when the caller passes
    // the collected descendant-state envelope.
    private ProfileSeparateTableBindingClassification _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildCollectionWithAlignedExtensionAndInlinedDescendantPlan(
            descendantScopeRelativePath: "$.parents[*]._ext.aligned.detail",
            descendantBindingRelativePath: "$.parents[*]._ext.aligned.detail.someField"
        );
        var alignedTable = plan.TablePlansInDependencyOrder.Single(p =>
            p.TableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope
        );

        var parentIdentity = ProfileTestDoubles.SemanticIdentityForRow("parent0");
        var directScopeAddress = new ScopeInstanceAddress(
            "$.parents[*]._ext.aligned",
            ImmutableArray.Create(new AncestorCollectionInstance("$.parents[*]", parentIdentity))
        );

        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScopeWithAncestors(
                "$.parents[*]._ext.aligned",
                "$.parents[*]",
                parentIdentity
            ),
            // Descendant scope is VisibleAbsent (request omits it; should clear stored).
            ProfileTestDoubles.RequestVisibleAbsentScopeWithAncestors(
                "$.parents[*]._ext.aligned.detail",
                "$.parents[*]",
                parentIdentity
            )
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            visibleStoredBody: null,
            ProfileTestDoubles.StoredVisiblePresentScopeWithAncestors(
                "$.parents[*]._ext.aligned",
                "$.parents[*]",
                parentIdentity
            ),
            ProfileTestDoubles.StoredVisiblePresentScopeWithAncestors(
                "$.parents[*]._ext.aligned.detail",
                "$.parents[*]",
                parentIdentity
            )
        );

        var directRequestScope = ProfileMemberGovernanceRules.LookupRequestScope(request, directScopeAddress);
        var directStoredScope = ProfileMemberGovernanceRules.LookupStoredScope(context, directScopeAddress);
        var descendants = ProfileSeparateScopeDescendantStates.Collect(
            plan,
            alignedTable,
            directScopeAddress,
            request,
            context
        );

        _result = new ProfileSeparateTableBindingClassifier().Classify(
            plan,
            alignedTable,
            directScopeAddress,
            directRequestScope,
            directStoredScope,
            descendants
        );
    }

    [Test]
    public void It_classifies_the_descendant_scoped_binding_as_ClearOnVisibleAbsent()
    {
        // Bindings on the aligned table: [0] ParentKeyPart, [1] direct-scope FavoriteColor,
        // [2] descendant-scope DetailField (someField). The descendant binding's enclosing
        // scope is VisibleAbsent in the request, so its disposition is ClearOnVisibleAbsent.
        _result.BindingsByIndex[2].Should().Be(RootBindingDisposition.ClearOnVisibleAbsent);
    }
}

[TestFixture]
public class Given_descendant_stored_scope_hidden_path_with_no_matching_binding
{
    // Slice 5 CP5 Task 7: drift-check coverage for descendant stored scopes. A descendant
    // inlined non-collection scope contributes its StoredScopeState.HiddenMemberPaths to the
    // metadata-drift check via ScopeStateLookup.StoredScopesForMetadata(). When a descendant
    // hidden path names a member with no matching binding on this table, the classifier must
    // throw InvalidOperationException — the same fail-closed behaviour as a direct-scope
    // hidden path that lacks a matching binding. This pins the invariant that descendant
    // stored-scope drift is caught by ValidateStoredScopeMetadata, not silently ignored.
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlanWithInlinedDescendantScope(
            descendantScopeRelativePath: "$._ext.sample.detail",
            descendantBindingRelativePath: "$._ext.sample.detail.someField"
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        var directScopeAddress = new ScopeInstanceAddress("$._ext.sample", []);

        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample.detail")
        );
        // Descendant stored scope declares a hidden member path "nonexistentField" that
        // does NOT correspond to any binding under "$._ext.sample.detail" on the table —
        // the only binding under that scope is "someField". This is exactly the upstream
        // contract drift the metadata-drift check is designed to catch.
        var context = ProfileTestDoubles.CreateContext(
            request,
            visibleStoredBody: null,
            ProfileTestDoubles.StoredVisiblePresentScope("$._ext.sample"),
            ProfileTestDoubles.StoredVisiblePresentScope("$._ext.sample.detail", "nonexistentField")
        );

        var directRequestScope = ProfileMemberGovernanceRules.LookupRequestScope(request, directScopeAddress);
        var directStoredScope = ProfileMemberGovernanceRules.LookupStoredScope(context, directScopeAddress);
        var descendants = ProfileSeparateScopeDescendantStates.Collect(
            plan,
            extensionTable,
            directScopeAddress,
            request,
            context
        );

        _act = () =>
            new ProfileSeparateTableBindingClassifier().Classify(
                plan,
                extensionTable,
                directScopeAddress,
                directRequestScope,
                directStoredScope,
                descendants
            );
    }

    [Test]
    public void It_throws_InvalidOperationException() => _act.Should().Throw<InvalidOperationException>();

    [Test]
    public void It_names_the_unmatched_hidden_path_in_the_diagnostic() =>
        _act.Should().Throw<InvalidOperationException>().WithMessage("*nonexistentField*");

    [Test]
    public void It_names_the_descendant_scope_in_the_diagnostic() =>
        _act.Should().Throw<InvalidOperationException>().WithMessage("*$._ext.sample.detail*");

    [Test]
    public void It_identifies_the_drift_as_upstream_contract_violation() =>
        _act.Should().Throw<InvalidOperationException>().WithMessage("*Core / write-plan contract drift*");
}
