// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_ObjectValueArrayComparer
{
    private ObjectValueArrayComparer _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = ObjectValueArrayComparer.Instance;
    }

    [Test]
    public void It_collapses_decimals_with_distinct_trailing_zero_representations()
    {
        // RelationalWriteNoProfileMerge.ProjectedCollectionTableState matches stored rows with
        // this comparer. decimal.Equals(1.0m, 1.00m) is true even though their ToString
        // invariant forms differ ("1.0" vs "1.00"), so a string-encoded key would falsely treat
        // them as distinct. The flattener's duplicate check keeps the same object.Equals rule
        // through CollectionSemanticIdentityEquality.RawValues.
        object?[] left = [1.0m];
        object?[] right = [1.00m];

        _sut.Equals(left, right).Should().BeTrue();
        _sut.GetHashCode(left).Should().Be(_sut.GetHashCode(right));
    }

    [Test]
    public void It_collapses_null_array_elements()
    {
        // Missing identity properties and explicit JSON nulls both surface as a null entry in
        // the raw object?[] semantic identity values produced by
        // RelationalWriteFlattener.MaterializeSemanticIdentityValues. They must compare equal so
        // the no-profile merge's row-pairing matches the storage-NULL convention.
        object?[] left = [null];
        object?[] right = [null];

        _sut.Equals(left, right).Should().BeTrue();
        _sut.GetHashCode(left).Should().Be(_sut.GetHashCode(right));
    }

    [Test]
    public void It_does_not_treat_arrays_of_different_lengths_as_equal()
    {
        object?[] left = ["a", "b"];
        object?[] right = ["a", "b", "c"];

        _sut.Equals(left, right).Should().BeFalse();
    }

    [Test]
    public void It_does_not_treat_distinct_values_as_equal()
    {
        object?[] left = ["A"];
        object?[] right = ["B"];

        _sut.Equals(left, right).Should().BeFalse();
    }

    [Test]
    public void It_does_not_treat_a_present_string_as_equal_to_null()
    {
        object?[] left = [null];
        object?[] right = ["A"];

        _sut.Equals(left, right).Should().BeFalse();
    }

    [Test]
    public void It_does_not_treat_strings_containing_separator_like_characters_as_colliding()
    {
        // Regression for an earlier string-encoded key that joined raw values with control
        // characters; an identity value containing those characters could collide with a
        // composite key for a different identity. The comparer is a structural object-by-object
        // compare and is immune to that class of collision.
        object?[] left = ["ab"];
        object?[] right = ["a", "b"];

        _sut.Equals(left, right).Should().BeFalse();
    }
}
