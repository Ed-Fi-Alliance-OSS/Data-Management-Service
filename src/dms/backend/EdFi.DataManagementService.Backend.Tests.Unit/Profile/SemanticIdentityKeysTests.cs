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

/// <summary>
/// Unit tests for <see cref="SemanticIdentityKeys"/>. Each test exercises a single property
/// of the encoded key — that two structurally-equal identity arrays produce equal keys, and
/// that any difference in <see cref="SemanticIdentityPart.RelativePath"/>,
/// <see cref="SemanticIdentityPart.IsPresent"/>, or <see cref="SemanticIdentityPart.Value"/>
/// produces a different key. Encoded form is deliberately opaque, so the assertions compare
/// keys for equality rather than asserting on specific separators or escape rules.
/// </summary>
[TestFixture]
public class Given_SemanticIdentityKeys_BuildKey
{
    private static SemanticIdentityPart Part(string path, JsonNode? value, bool isPresent) =>
        new(path, value, isPresent);

    [Test]
    public void It_returns_the_same_key_for_two_structurally_equal_identities()
    {
        var a = ImmutableArray.Create(Part("addressId", JsonValue.Create("A1"), true));
        var b = ImmutableArray.Create(Part("addressId", JsonValue.Create("A1"), true));

        SemanticIdentityKeys.BuildKey(a).Should().Be(SemanticIdentityKeys.BuildKey(b));
    }

    [Test]
    public void It_distinguishes_missing_from_explicit_null_when_value_is_null()
    {
        var missing = ImmutableArray.Create(Part("addressId", null, isPresent: false));
        var explicitNull = ImmutableArray.Create(Part("addressId", null, isPresent: true));

        SemanticIdentityKeys.BuildKey(missing).Should().NotBe(SemanticIdentityKeys.BuildKey(explicitNull));
    }

    [Test]
    public void It_distinguishes_explicit_null_from_a_real_value()
    {
        var explicitNull = ImmutableArray.Create(Part("addressId", null, isPresent: true));
        var withValue = ImmutableArray.Create(Part("addressId", JsonValue.Create("A1"), isPresent: true));

        SemanticIdentityKeys.BuildKey(explicitNull).Should().NotBe(SemanticIdentityKeys.BuildKey(withValue));
    }

    [Test]
    public void It_distinguishes_identities_that_differ_only_by_relative_path()
    {
        var a = ImmutableArray.Create(Part("addressId", JsonValue.Create("X"), true));
        var b = ImmutableArray.Create(Part("$.addressId", JsonValue.Create("X"), true));

        SemanticIdentityKeys.BuildKey(a).Should().NotBe(SemanticIdentityKeys.BuildKey(b));
    }

    [Test]
    public void It_returns_an_empty_key_for_an_empty_identity_array()
    {
        SemanticIdentityKeys.BuildKey(ImmutableArray<SemanticIdentityPart>.Empty).Should().BeEmpty();
    }

    [Test]
    public void It_returns_an_empty_key_for_a_default_identity_array()
    {
        SemanticIdentityKeys.BuildKey(default(ImmutableArray<SemanticIdentityPart>)).Should().BeEmpty();
    }

    [Test]
    public void It_distinguishes_two_part_identities_that_differ_only_in_one_parts_presence()
    {
        var bothPresent = ImmutableArray.Create(
            Part("schoolId", JsonValue.Create(255901L), true),
            Part("educationOrganizationId", null, true)
        );
        var secondMissing = ImmutableArray.Create(
            Part("schoolId", JsonValue.Create(255901L), true),
            Part("educationOrganizationId", null, false)
        );

        SemanticIdentityKeys
            .BuildKey(bothPresent)
            .Should()
            .NotBe(SemanticIdentityKeys.BuildKey(secondMissing));
    }
}
