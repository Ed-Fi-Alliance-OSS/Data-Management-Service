// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_descendant_inlined_scope_under_RootExtension
{
    private ProfileSeparateScopeDescendantStates _result;
    private ResourceWritePlan _plan = null!;
    private TableWritePlan _extensionTable = null!;

    [SetUp]
    public void Setup()
    {
        // Root + RootExtension at $._ext.sample with a descendant non-collection inlined scope
        // at $._ext.sample.detail. Both scopes' bindings live on the RootExtension table.
        _plan = ProfileTestDoubles.BuildRootPlusRootExtensionPlanWithInlinedDescendantScope(
            descendantScopeRelativePath: "$._ext.sample.detail",
            descendantBindingRelativePath: "$._ext.sample.detail.someField"
        );
        _extensionTable = _plan.TablePlansInDependencyOrder[1];

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

        _result = ProfileSeparateScopeDescendantStates.Collect(
            _plan,
            _extensionTable,
            directScopeAddress,
            request,
            context
        );
    }

    [Test]
    public void It_collects_one_descendant_request_state() => _result.RequestScopes.Should().HaveCount(1);

    [Test]
    public void It_collects_the_correct_descendant_request_scope() =>
        _result.RequestScopes[0].Address.JsonScope.Should().Be("$._ext.sample.detail");

    [Test]
    public void It_collects_one_descendant_stored_state() => _result.StoredScopes.Should().HaveCount(1);

    [Test]
    public void It_collects_the_correct_descendant_stored_scope() =>
        _result.StoredScopes[0].Address.JsonScope.Should().Be("$._ext.sample.detail");
}

[TestFixture]
public class Given_descendant_scope_owned_by_different_table
{
    private ProfileSeparateScopeDescendantStates _result;

    [SetUp]
    public void Setup()
    {
        // Descendant scope $._ext.sample.subCollection is owned by a separate child table,
        // not by the RootExtension's own table — must NOT be collected.
        var plan = ProfileTestDoubles.BuildRootPlusRootExtensionWithSeparateChildTablePlan(
            childScopeRelativePath: "$._ext.sample.subCollection"
        );
        var extensionTable = plan.TablePlansInDependencyOrder[1];
        var directScopeAddress = new ScopeInstanceAddress("$._ext.sample", []);
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample"),
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample.subCollection")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            visibleStoredBody: null,
            ProfileTestDoubles.StoredVisiblePresentScope("$._ext.sample.subCollection")
        );

        _result = ProfileSeparateScopeDescendantStates.Collect(
            plan,
            extensionTable,
            directScopeAddress,
            request,
            context
        );
    }

    [Test]
    public void It_does_not_collect_descendant_states_owned_by_other_tables()
    {
        _result.RequestScopes.Should().BeEmpty();
        _result.StoredScopes.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_descendant_under_sibling_collection_instance
{
    private ProfileSeparateScopeDescendantStates _result;

    [SetUp]
    public void Setup()
    {
        // Two parent[*] instances; aligned extension on each. Descendant inlined scope state
        // exists for parent[1] only. Collection for parent[0]'s instance must NOT include it.
        var plan = ProfileTestDoubles.BuildCollectionWithAlignedExtensionAndInlinedDescendantPlan(
            descendantScopeRelativePath: "$.parents[*]._ext.aligned.detail",
            descendantBindingRelativePath: "$.parents[*]._ext.aligned.detail.someField"
        );
        var alignedTable = plan.TablePlansInDependencyOrder.Single(p =>
            p.TableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope
        );

        var parent0Identity = ProfileTestDoubles.SemanticIdentityForRow("parent0");
        var parent1Identity = ProfileTestDoubles.SemanticIdentityForRow("parent1");

        var parent0AlignedAddress = new ScopeInstanceAddress(
            "$.parents[*]._ext.aligned",
            ImmutableArray.Create(new AncestorCollectionInstance("$.parents[*]", parent0Identity))
        );

        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScopeWithAncestors(
                "$.parents[*]._ext.aligned",
                "$.parents[*]",
                parent1Identity
            ),
            ProfileTestDoubles.RequestVisiblePresentScopeWithAncestors(
                "$.parents[*]._ext.aligned.detail",
                "$.parents[*]",
                parent1Identity
            )
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            visibleStoredBody: null,
            ProfileTestDoubles.StoredVisiblePresentScopeWithAncestors(
                "$.parents[*]._ext.aligned.detail",
                "$.parents[*]",
                parent1Identity,
                "someField"
            )
        );

        _result = ProfileSeparateScopeDescendantStates.Collect(
            plan,
            alignedTable,
            parent0AlignedAddress,
            request,
            context
        );
    }

    [Test]
    public void It_does_not_collect_states_from_sibling_instances()
    {
        _result.RequestScopes.Should().BeEmpty();
        _result.StoredScopes.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_no_profile_applied_context
{
    [Test]
    public void It_returns_empty_when_context_is_null()
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
            ProfileTestDoubles.RequestVisiblePresentScope("$._ext.sample")
        );

        var result = ProfileSeparateScopeDescendantStates.Collect(
            plan,
            extensionTable,
            directScopeAddress,
            request,
            profileAppliedContext: null
        );

        result.RequestScopes.Should().BeEmpty();
        result.StoredScopes.Should().BeEmpty();
    }
}
