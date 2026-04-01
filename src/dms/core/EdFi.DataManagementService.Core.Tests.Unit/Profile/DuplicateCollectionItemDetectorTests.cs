// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class Given_Two_Items_With_Same_Identity_And_Parent
{
    private ImmutableArray<WritableProfileValidationFailure> _result;

    [SetUp]
    public void Setup()
    {
        var item1 = DuplicateDetectorTestHelpers.BuildItem(
            "$.classPeriods[*]",
            "$",
            "classPeriodName",
            "Period1",
            "$.classPeriods[0]"
        );
        var item2 = DuplicateDetectorTestHelpers.BuildItem(
            "$.classPeriods[*]",
            "$",
            "classPeriodName",
            "Period1",
            "$.classPeriods[1]"
        );

        _result = DuplicateCollectionItemDetector.Detect(
            [item1, item2],
            profileName: "TestProfile",
            resourceName: "TestResource",
            method: "POST",
            operation: "write"
        );
    }

    [Test]
    public void It_returns_exactly_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_returns_a_DuplicateVisibleCollectionItemCollision_failure()
    {
        _result[0]
            .Should()
            .BeOfType<DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure>();
    }

    [Test]
    public void It_reports_the_correct_json_scope()
    {
        var failure = (DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure)_result[0];
        failure.JsonScope.Should().Be("$.classPeriods[*]");
    }
}

[TestFixture]
public class Given_Items_With_Different_Identities
{
    private ImmutableArray<WritableProfileValidationFailure> _result;

    [SetUp]
    public void Setup()
    {
        var item1 = DuplicateDetectorTestHelpers.BuildItem(
            "$.classPeriods[*]",
            "$",
            "classPeriodName",
            "Period1"
        );
        var item2 = DuplicateDetectorTestHelpers.BuildItem(
            "$.classPeriods[*]",
            "$",
            "classPeriodName",
            "Period2"
        );

        _result = DuplicateCollectionItemDetector.Detect(
            [item1, item2],
            profileName: "TestProfile",
            resourceName: "TestResource",
            method: "POST",
            operation: "write"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_Items_With_Same_Identity_Different_Parents
{
    private ImmutableArray<WritableProfileValidationFailure> _result;

    [SetUp]
    public void Setup()
    {
        // Same jsonScope and semantic identity, but different parent scopes
        var item1 = DuplicateDetectorTestHelpers.BuildItem(
            "$.classPeriods[*]",
            "$",
            "classPeriodName",
            "Period1"
        );
        var item2 = DuplicateDetectorTestHelpers.BuildItem(
            "$.classPeriods[*]",
            "$.otherParent",
            "classPeriodName",
            "Period1"
        );

        _result = DuplicateCollectionItemDetector.Detect(
            [item1, item2],
            profileName: "TestProfile",
            resourceName: "TestResource",
            method: "POST",
            operation: "write"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_Single_Item
{
    private ImmutableArray<WritableProfileValidationFailure> _result;

    [SetUp]
    public void Setup()
    {
        var item = DuplicateDetectorTestHelpers.BuildItem(
            "$.classPeriods[*]",
            "$",
            "classPeriodName",
            "Period1"
        );

        _result = DuplicateCollectionItemDetector.Detect(
            [item],
            profileName: "TestProfile",
            resourceName: "TestResource",
            method: "POST",
            operation: "write"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_Empty_Items
{
    private ImmutableArray<WritableProfileValidationFailure> _result;

    [SetUp]
    public void Setup()
    {
        _result = DuplicateCollectionItemDetector.Detect(
            [],
            profileName: "TestProfile",
            resourceName: "TestResource",
            method: "POST",
            operation: "write"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

// -----------------------------------------------------------------------
//  Shared helper
// -----------------------------------------------------------------------

internal static class DuplicateDetectorTestHelpers
{
    public static VisibleRequestCollectionItem BuildItem(
        string jsonScope,
        string parentScope,
        string identityPath,
        string identityValue,
        string requestJsonPath = "$.unknown[0]"
    )
    {
        var parentAddress = new ScopeInstanceAddress(parentScope, []);
        var identity = ImmutableArray.Create(
            new SemanticIdentityPart(identityPath, JsonValue.Create(identityValue), true)
        );
        var address = new CollectionRowAddress(jsonScope, parentAddress, identity);
        return new VisibleRequestCollectionItem(address, Creatable: false, requestJsonPath);
    }
}
