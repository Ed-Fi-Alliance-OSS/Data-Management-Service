// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

internal static class AncestorChainFixtures
{
    public static AncestorCollectionInstance Ancestor(string scope, string identityValue) =>
        new(scope, [new SemanticIdentityPart("$.id", JsonValue.Create(identityValue), IsPresent: true)]);
}

[TestFixture]
public class Given_BuildAncestorTargetParentAddress_for_top_level_base_collection_ancestor
{
    private ScopeInstanceAddress _result = null!;

    [SetUp]
    public void Setup()
    {
        ImmutableArray<AncestorCollectionInstance> chain =
        [
            AncestorChainFixtures.Ancestor("$.parents[*]", "P1"),
        ];

        _result = RelationalWriteProfileMergeSynthesizer.BuildAncestorTargetParentAddress(
            chain,
            canonicalBuilder: null,
            ancestorIndex: 0,
            useCanonicalChain: false
        );
    }

    [Test]
    public void It_uses_root_as_the_parent_scope() => _result.JsonScope.Should().Be("$");

    [Test]
    public void It_has_no_ancestor_collection_instances() =>
        _result.AncestorCollectionInstances.Should().BeEmpty();
}

[TestFixture]
public class Given_BuildAncestorTargetParentAddress_for_top_level_extension_collection_ancestor
{
    private ScopeInstanceAddress _result = null!;

    [SetUp]
    public void Setup()
    {
        ImmutableArray<AncestorCollectionInstance> chain =
        [
            AncestorChainFixtures.Ancestor("$._ext.sample.children[*]", "C1"),
        ];

        _result = RelationalWriteProfileMergeSynthesizer.BuildAncestorTargetParentAddress(
            chain,
            canonicalBuilder: null,
            ancestorIndex: 0,
            useCanonicalChain: false
        );
    }

    [Test]
    public void It_uses_the_root_extension_scope_as_the_parent_scope() =>
        _result.JsonScope.Should().Be("$._ext.sample");

    [Test]
    public void It_has_no_ancestor_collection_instances() =>
        _result.AncestorCollectionInstances.Should().BeEmpty();
}

[TestFixture]
public class Given_BuildAncestorTargetParentAddress_for_nested_base_collection_ancestor
{
    private ScopeInstanceAddress _result = null!;

    [SetUp]
    public void Setup()
    {
        ImmutableArray<AncestorCollectionInstance> chain =
        [
            AncestorChainFixtures.Ancestor("$.parents[*]", "P1"),
            AncestorChainFixtures.Ancestor("$.parents[*].kids[*]", "K1"),
        ];

        _result = RelationalWriteProfileMergeSynthesizer.BuildAncestorTargetParentAddress(
            chain,
            canonicalBuilder: null,
            ancestorIndex: 1,
            useCanonicalChain: false
        );
    }

    [Test]
    public void It_uses_the_immediate_parent_collection_scope() =>
        _result.JsonScope.Should().Be("$.parents[*]");

    [Test]
    public void It_includes_the_parent_collection_self_entry_in_the_ancestor_chain()
    {
        _result.AncestorCollectionInstances.Should().HaveCount(1);
        _result.AncestorCollectionInstances[0].JsonScope.Should().Be("$.parents[*]");
    }
}

[TestFixture]
public class Given_BuildAncestorTargetParentAddress_for_extension_child_collection_under_aligned_scope
{
    private ScopeInstanceAddress _result = null!;

    [SetUp]
    public void Setup()
    {
        ImmutableArray<AncestorCollectionInstance> chain =
        [
            AncestorChainFixtures.Ancestor("$.parents[*]", "P1"),
            AncestorChainFixtures.Ancestor("$.parents[*]._ext.sample.things[*]", "T1"),
        ];

        _result = RelationalWriteProfileMergeSynthesizer.BuildAncestorTargetParentAddress(
            chain,
            canonicalBuilder: null,
            ancestorIndex: 1,
            useCanonicalChain: false
        );
    }

    [Test]
    public void It_resolves_the_aligned_extension_scope_as_the_immediate_parent() =>
        _result.JsonScope.Should().Be("$.parents[*]._ext.sample");

    [Test]
    public void It_includes_the_outer_collection_in_the_ancestor_chain()
    {
        _result.AncestorCollectionInstances.Should().HaveCount(1);
        _result.AncestorCollectionInstances[0].JsonScope.Should().Be("$.parents[*]");
    }
}
