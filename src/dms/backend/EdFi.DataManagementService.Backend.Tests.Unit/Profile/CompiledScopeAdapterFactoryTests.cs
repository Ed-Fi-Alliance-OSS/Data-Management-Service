// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_a_root_only_ResourceWritePlan
{
    private CompiledScopeDescriptor[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        _result = CompiledScopeAdapterFactory.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_produces_exactly_one_scope()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_sets_JsonScope_to_root_path()
    {
        _result[0].JsonScope.Should().Be("$");
    }

    [Test]
    public void It_sets_ScopeKind_to_Root()
    {
        _result[0].ScopeKind.Should().Be(ScopeKind.Root);
    }

    [Test]
    public void It_sets_ImmediateParentJsonScope_to_null()
    {
        _result[0].ImmediateParentJsonScope.Should().BeNull();
    }

    [Test]
    public void It_sets_CollectionAncestorsInOrder_to_empty()
    {
        _result[0].CollectionAncestorsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_sets_SemanticIdentityRelativePathsInOrder_to_empty()
    {
        _result[0].SemanticIdentityRelativePathsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_sets_CanonicalScopeRelativeMemberPaths_from_columns_with_SourceJsonPath()
    {
        // Root table has: DocumentId (no SourceJsonPath) and SchoolId (SourceJsonPath = "$.schoolId")
        _result[0].CanonicalScopeRelativeMemberPaths.Should().Equal("schoolId");
    }
}

[TestFixture]
public class Given_a_root_and_collection_ResourceWritePlan
{
    private CompiledScopeDescriptor[] _result = null!;
    private CompiledScopeDescriptor _rootScope = null!;
    private CompiledScopeDescriptor _collectionScope = null!;

    [SetUp]
    public void Setup()
    {
        var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
        _result = CompiledScopeAdapterFactory.BuildFromWritePlan(plan);
        _rootScope = _result[0];
        _collectionScope = _result[1];
    }

    [Test]
    public void It_produces_two_scopes()
    {
        _result.Should().HaveCount(2);
    }

    [Test]
    public void It_sets_root_scope_ScopeKind_to_Root()
    {
        _rootScope.ScopeKind.Should().Be(ScopeKind.Root);
    }

    [Test]
    public void It_sets_collection_scope_JsonScope_correctly()
    {
        _collectionScope.JsonScope.Should().Be("$.addresses[*]");
    }

    [Test]
    public void It_sets_collection_scope_ScopeKind_to_Collection()
    {
        _collectionScope.ScopeKind.Should().Be(ScopeKind.Collection);
    }

    [Test]
    public void It_sets_collection_ImmediateParentJsonScope_to_root()
    {
        _collectionScope.ImmediateParentJsonScope.Should().Be("$");
    }

    [Test]
    public void It_sets_collection_CollectionAncestorsInOrder_to_empty_because_root_is_not_a_collection()
    {
        // Root is ScopeKind.Root (not Collection), so there are no collection ancestors
        _collectionScope.CollectionAncestorsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_sets_collection_SemanticIdentityRelativePathsInOrder_from_CollectionMergePlan()
    {
        _collectionScope.SemanticIdentityRelativePathsInOrder.Should().Equal("addressType");
    }

    [Test]
    public void It_sets_collection_CanonicalScopeRelativeMemberPaths_from_columns_with_SourceJsonPath()
    {
        // Collection table has: CollectionItemId (no source), School_DocumentId (no source),
        // Ordinal (no source), AddressType (sourceJsonPath = "$.addressType")
        _collectionScope.CanonicalScopeRelativeMemberPaths.Should().Equal("addressType");
    }
}

[TestFixture]
public class Given_a_root_and_extension_ResourceWritePlan
{
    private CompiledScopeDescriptor[] _result = null!;
    private CompiledScopeDescriptor _extensionScope = null!;

    [SetUp]
    public void Setup()
    {
        var plan = AdapterFactoryTestFixtures.BuildRootAndExtensionPlan();
        _result = CompiledScopeAdapterFactory.BuildFromWritePlan(plan);
        _extensionScope = _result[1];
    }

    [Test]
    public void It_produces_two_scopes()
    {
        _result.Should().HaveCount(2);
    }

    [Test]
    public void It_sets_extension_scope_JsonScope_correctly()
    {
        _extensionScope.JsonScope.Should().Be("$._ext.sample");
    }

    [Test]
    public void It_sets_extension_scope_ScopeKind_to_NonCollection()
    {
        _extensionScope.ScopeKind.Should().Be(ScopeKind.NonCollection);
    }

    [Test]
    public void It_sets_extension_ImmediateParentJsonScope_to_root()
    {
        _extensionScope.ImmediateParentJsonScope.Should().Be("$");
    }

    [Test]
    public void It_sets_extension_CollectionAncestorsInOrder_to_empty()
    {
        _extensionScope.CollectionAncestorsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_sets_extension_SemanticIdentityRelativePathsInOrder_to_empty()
    {
        _extensionScope.SemanticIdentityRelativePathsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_sets_extension_CanonicalScopeRelativeMemberPaths_from_columns_with_SourceJsonPath()
    {
        _extensionScope.CanonicalScopeRelativeMemberPaths.Should().Equal("favoriteColor");
    }
}

[TestFixture]
public class Given_a_root_plan_with_an_inlined_object_scope_via_additionalScopes
{
    private CompiledScopeDescriptor[] _result = null!;
    private CompiledScopeDescriptor _rootScope = null!;
    private CompiledScopeDescriptor _inlinedScope = null!;

    [SetUp]
    public void Setup()
    {
        var plan = AdapterFactoryTestFixtures.BuildRootWithInlinedObjectPlan();

        // Simulate discovering the inlined scope from a content type tree
        var additionalScopes = new List<(string JsonScope, ScopeKind Kind)>
        {
            ("$.calendarReference", ScopeKind.NonCollection),
        };

        _result = CompiledScopeAdapterFactory.BuildFromWritePlan(plan, additionalScopes);
        _rootScope = _result[0];
        _inlinedScope = _result[1];
    }

    [Test]
    public void It_produces_two_scopes()
    {
        _result.Should().HaveCount(2);
    }

    [Test]
    public void It_produces_a_root_scope_first()
    {
        _rootScope.JsonScope.Should().Be("$");
        _rootScope.ScopeKind.Should().Be(ScopeKind.Root);
    }

    [Test]
    public void It_sets_inlined_scope_JsonScope_correctly()
    {
        _inlinedScope.JsonScope.Should().Be("$.calendarReference");
    }

    [Test]
    public void It_sets_inlined_scope_ScopeKind_to_NonCollection()
    {
        _inlinedScope.ScopeKind.Should().Be(ScopeKind.NonCollection);
    }

    [Test]
    public void It_sets_inlined_scope_ImmediateParentJsonScope_to_root()
    {
        _inlinedScope.ImmediateParentJsonScope.Should().Be("$");
    }

    [Test]
    public void It_sets_inlined_scope_CollectionAncestorsInOrder_to_empty()
    {
        _inlinedScope.CollectionAncestorsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_sets_inlined_scope_SemanticIdentityRelativePathsInOrder_to_empty()
    {
        _inlinedScope.SemanticIdentityRelativePathsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_derives_CanonicalScopeRelativeMemberPaths_from_parent_table_columns()
    {
        // Root table has columns with SourceJsonPath $.calendarReference.calendarCode
        // and $.calendarReference.schoolYear — both are direct members of the inlined scope
        _inlinedScope
            .CanonicalScopeRelativeMemberPaths.Should()
            .BeEquivalentTo("calendarCode", "schoolYear");
    }
}

[TestFixture]
public class Given_no_additionalScopes_the_factory_behaves_as_before
{
    [Test]
    public void It_produces_only_table_backed_scopes()
    {
        var plan = AdapterFactoryTestFixtures.BuildRootWithInlinedObjectPlan();
        var result = CompiledScopeAdapterFactory.BuildFromWritePlan(plan);

        // Without additional scopes, only the root table appears
        result.Should().HaveCount(1);
        result[0].JsonScope.Should().Be("$");
    }
}

[TestFixture]
public class Given_out_of_order_inlined_collection_and_child_scopes_via_additionalScopes
{
    private CompiledScopeDescriptor[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = AdapterFactoryTestFixtures.BuildRootWithInlinedCollectionAndNestedObjectPlan();

        var additionalScopes = new List<(string JsonScope, ScopeKind Kind)>
        {
            ("$.inlineItems[*].details", ScopeKind.NonCollection),
            ("$.inlineItems[*]", ScopeKind.Collection),
        };

        _result = CompiledScopeAdapterFactory.BuildFromWritePlan(plan, additionalScopes);
    }

    [Test]
    public void It_emits_inlined_scopes_in_parent_before_child_order()
    {
        _result
            .Select(scope => scope.JsonScope)
            .Should()
            .Equal("$", "$.inlineItems[*]", "$.inlineItems[*].details");
    }

    [Test]
    public void It_sets_the_nested_object_parent_and_collection_ancestors_from_the_normalized_scope_graph()
    {
        var detailsScope = _result.Single(scope => scope.JsonScope == "$.inlineItems[*].details");

        detailsScope.ImmediateParentJsonScope.Should().Be("$.inlineItems[*]");
        detailsScope.CollectionAncestorsInOrder.Should().Equal("$.inlineItems[*]");
        detailsScope.CanonicalScopeRelativeMemberPaths.Should().Equal("code");
    }
}
