// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_Parent_Physical_Identity_Keys
{
    [TestCase(0)]
    [TestCase(1)]
    public void It_distinguishes_parents_when_any_identity_column_differs(int changedIndex)
    {
        var original = Key(345L, 100L);
        var different = changedIndex == 0 ? Key(346L, 100L) : Key(345L, 101L);

        original.Equals(different).Should().BeFalse();
        different.Equals(original).Should().BeFalse();
        original.Equals((object)different).Should().BeFalse();
        var rowsByParent = new Dictionary<ParentIdentityKey, string>
        {
            [original] = "first parent",
            [different] = "second parent",
        };
        rowsByParent.Should().HaveCount(2);
        rowsByParent[Key(345, 100)].Should().Be("first parent");
        rowsByParent[changedIndex == 0 ? Key(346, 100) : Key(345, 101)].Should().Be("second parent");
    }

    [Test]
    public void It_matches_numeric_identity_values_across_projection_widths()
    {
        var wide = Key(345L, 100L);
        var narrow = Key(345, 100);

        wide.Equals(narrow).Should().BeTrue();
        narrow.Equals((object)wide).Should().BeTrue();
        wide.GetHashCode().Should().Be(narrow.GetHashCode());
        var rowsByParent = new Dictionary<ParentIdentityKey, string> { [wide] = "stored row" };
        rowsByParent[narrow].Should().Be("stored row");
        rowsByParent[narrow] = "merged row";
        rowsByParent.Should().ContainSingle().Which.Value.Should().Be("merged row");
    }

    [Test]
    public void It_distinguishes_a_parent_from_an_identity_with_only_its_prefix()
    {
        var full = Key(345L, 100L);
        var prefix = Key(345L);

        full.Equals(prefix).Should().BeFalse();
        prefix.Equals(full).Should().BeFalse();
    }

    [Test]
    public void It_respects_the_physical_identity_column_order()
    {
        Key(345L, 100L).Equals(Key(100L, 345L)).Should().BeFalse();
    }

    [Test]
    public void It_does_not_match_a_missing_key_or_an_unrelated_object()
    {
        Key(345L).Equals((ParentIdentityKey?)null).Should().BeFalse();
        Key(345L).Equals((object?)null).Should().BeFalse();
        Key(345L).Equals(345L).Should().BeFalse();
    }

    [Test]
    public void It_keeps_distinct_unresolved_parent_identity_instances_separate()
    {
        var firstValue = FlattenedWriteValue.UnresolvedCollectionItemId.Create();
        var separateValue = new FlattenedWriteValue.UnresolvedCollectionItemId(firstValue.Token);
        var first = new ParentIdentityKey([firstValue]);
        var sameInstance = new ParentIdentityKey([firstValue]);
        var separateInstance = new ParentIdentityKey([separateValue]);

        first.Equals(sameInstance).Should().BeTrue();
        first.GetHashCode().Should().Be(sameInstance.GetHashCode());
        first.Equals(separateInstance).Should().BeFalse();
        separateInstance.Equals(first).Should().BeFalse();
    }

    private static ParentIdentityKey Key(params object[] values) =>
        new([.. values.Select(value => new FlattenedWriteValue.Literal(value))]);
}

[TestFixture]
public class Given_Child_Scope_And_Parent_Identity_Keys
{
    [TestCase(true)]
    [TestCase(false)]
    public void It_requires_both_the_child_scope_and_the_parent_identity_to_match(bool differentScope)
    {
        var original = ("$.parents[*].children[*]", Parent(100));
        var different = differentScope
            ? ("$.parents[*].otherChildren[*]", Parent(100))
            : ("$.parents[*].children[*]", Parent(101));
        var comparer = ProfileCollectionWalker.ChildScopeAndParentComparer.Instance;

        comparer.Equals(original, different).Should().BeFalse();
        comparer.Equals(different, original).Should().BeFalse();
    }

    [Test]
    public void It_finds_the_same_child_partition_with_a_structurally_equal_parent_address()
    {
        var original = ("$.parents[*].children[*]", Parent(100));
        var reconstructed = ("$.parents[*].children[*]", Parent(100));
        var comparer = ProfileCollectionWalker.ChildScopeAndParentComparer.Instance;
        var partitions = new Dictionary<(string, ScopeInstanceAddress), string>(comparer)
        {
            [original] = "stored children",
        };

        comparer.Equals(original, reconstructed).Should().BeTrue();
        comparer.GetHashCode(original).Should().Be(comparer.GetHashCode(reconstructed));
        partitions[reconstructed].Should().Be("stored children");
    }

    private static ScopeInstanceAddress Parent(int id) =>
        new(
            "$.parents[*]",
            [
                new AncestorCollectionInstance(
                    "$.parents[*]",
                    [new SemanticIdentityPart("parentId", JsonValue.Create(id), true)]
                ),
            ]
        );
}
