// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_The_Storage_Collapsed_Identity_Helpers
{
    private static SemanticIdentityPart Part(string path, JsonNode? value, bool isPresent) =>
        new(path, value, isPresent);

    [Test]
    public void It_Collapses_Missing_And_ExplicitNull_To_Same_Key()
    {
        var missing = ImmutableArray.Create(Part("a", null, false));
        var explicitNull = ImmutableArray.Create(Part("a", null, true));

        var keyMissing = StorageCollapsedIdentityHelpers.BuildKey(missing);
        var keyExplicitNull = StorageCollapsedIdentityHelpers.BuildKey(explicitNull);

        keyMissing.Should().Be(keyExplicitNull);
    }

    [Test]
    public void It_Distinguishes_Present_NonNull_Values_From_Absent_Or_Null()
    {
        var present = ImmutableArray.Create(Part("a", JsonValue.Create(1), true));
        var absent = ImmutableArray.Create(Part("a", null, false));

        StorageCollapsedIdentityHelpers
            .BuildKey(present)
            .Should()
            .NotBe(StorageCollapsedIdentityHelpers.BuildKey(absent));
    }

    [Test]
    public void It_Distinguishes_Different_Present_NonNull_Values()
    {
        var one = ImmutableArray.Create(Part("a", JsonValue.Create(1), true));
        var two = ImmutableArray.Create(Part("a", JsonValue.Create(2), true));

        StorageCollapsedIdentityHelpers
            .BuildKey(one)
            .Should()
            .NotBe(StorageCollapsedIdentityHelpers.BuildKey(two));
    }

    [Test]
    public void It_Includes_RelativePath_In_The_Key()
    {
        var aPart = ImmutableArray.Create(Part("a", JsonValue.Create(1), true));
        var bPart = ImmutableArray.Create(Part("b", JsonValue.Create(1), true));

        StorageCollapsedIdentityHelpers
            .BuildKey(aPart)
            .Should()
            .NotBe(StorageCollapsedIdentityHelpers.BuildKey(bPart));
    }

    [Test]
    public void It_Returns_Empty_String_For_Default_Or_Empty_Sequence()
    {
        StorageCollapsedIdentityHelpers.BuildKey(default).Should().Be(string.Empty);
        StorageCollapsedIdentityHelpers
            .BuildKey(ImmutableArray<SemanticIdentityPart>.Empty)
            .Should()
            .Be(string.Empty);
    }

    [Test]
    public void It_Surfaces_The_Existing_ObjectValueArrayComparer_Instance()
    {
        StorageCollapsedIdentityHelpers
            .ObjectArrayComparer.Should()
            .BeSameAs(ObjectValueArrayComparer.Instance);
    }

    [Test]
    public void It_Produces_Different_Keys_For_Arrays_Differing_Only_In_Part_Order()
    {
        var ab = ImmutableArray.Create(
            Part("a", JsonValue.Create(1), true),
            Part("b", JsonValue.Create(2), true)
        );
        var ba = ImmutableArray.Create(
            Part("b", JsonValue.Create(2), true),
            Part("a", JsonValue.Create(1), true)
        );

        StorageCollapsedIdentityHelpers
            .BuildKey(ab)
            .Should()
            .NotBe(StorageCollapsedIdentityHelpers.BuildKey(ba));
    }
}
