// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Models.ClaimsHierarchy;

[TestFixture]
public class ClaimsHierarchyManagerTests
{
    private ClaimsHierarchyManager _claimsHierarchyManager;

    [SetUp]
    public void SetUp()
    {
        _claimsHierarchyManager = new ClaimsHierarchyManager();
    }

    [Test]
    public void RemoveClaimSetFromHierarchy_ShouldRemoveClaimSet()
    {
        // Arrange
        List<Claim> claims = [new() { Name = "Claim1", ClaimSets = [new() { Name = "ClaimSet1" }] }];

        // Act
        _claimsHierarchyManager.RemoveClaimSetFromHierarchy("ClaimSet1", claims);

        // Assert
        claims[0].ClaimSets.Should().BeEmpty();
    }

    [Test]
    public void RemoveClaimSetFromHierarchy_ShouldRemoveNestedClaimSet()
    {
        // Arrange
        List<Claim> claims =
        [
            new()
            {
                Name = "Claim1",
                ClaimSets = [],
                Claims = [new() { Name = "SubClaim1", ClaimSets = [new() { Name = "ClaimSet1" }] }],
            },
        ];

        // Act
        _claimsHierarchyManager.RemoveClaimSetFromHierarchy("ClaimSet1", claims);

        // Assert
        claims[0].Claims[0].ClaimSets.Should().BeEmpty();
    }

    [Test]
    public void ApplyImportedClaimSetToHierarchy_ShouldAddClaimSet()
    {
        // Arrange
        var claims = new List<Claim>
        {
            new() { Name = "Claim1", ClaimSets = [] },
        };

        var command = new ClaimSetImportCommand
        {
            Name = "ClaimSet1",
            ResourceClaims =
            [
                new()
                {
                    Name = "Claim1",
                    Actions = [new() { Name = "Read", Enabled = true }],
                    AuthorizationStrategyOverridesForCRUD =
                    [
                        new()
                        {
                            ActionName = "Read",
                            AuthorizationStrategies = [new() { AuthorizationStrategyName = "Strategy1" }],
                        },
                    ],
                },
            ],
        };

        // Act
        _claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(command, claims);

        // Assert
        claims[0].ClaimSets.Should().ContainSingle(cs => cs.Name == "ClaimSet1");
        claims[0].ClaimSets[0].Actions.Should().ContainSingle(a => a.Name == "Read");

        claims[0]
            .ClaimSets[0]
            .Actions[0]
            .AuthorizationStrategyOverrides.Should()
            .ContainSingle(astrat => astrat.Name == "Strategy1");
    }

    [Test]
    public void ApplyImportedClaimSetToHierarchy_ShouldHandleNestedResourceClaims()
    {
        // Arrange
        var claims = new List<Claim>
        {
            new()
            {
                Name = "Claim1",

                ClaimSets =
                [
                    new() { Name = "ExistingClaimSet", Actions = [new() { Name = "SomethingElse" }] },
                ],
                Claims = [new() { Name = "ChildClaim1", ClaimSets = [] }],
            },
            new()
            {
                Name = "Claim2",

                ClaimSets = [],
                Claims = [new() { Name = "ChildClaim2", ClaimSets = [] }],
            },
        };

        var command = new ClaimSetImportCommand
        {
            Name = "ImportedClaimSet",
            ResourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "Claim2",
                    Actions =
                    [
                        new() { Name = "Read", Enabled = true },
                        new() { Name = "Update", Enabled = false },
                    ],
                    Children =
                    [
                        new ResourceClaim
                        {
                            Name = "ChildClaim2",
                            Actions =
                            [
                                new() { Name = "Create", Enabled = true },
                                new() { Name = "Read", Enabled = true },
                                new() { Name = "Update", Enabled = true },
                                new() { Name = "Delete", Enabled = true },
                            ],
                        },
                    ],
                },
            },
        };

        // Act
        _claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(command, claims);

        // Assert

        // First claim (with non-matching claim name) is not modified
        claims[0].ClaimSets.Should().HaveCount(1);
        claims[0].ClaimSets.Should().ContainSingle(cs => cs.Name == "ExistingClaimSet");
        claims[0].Claims[0].ClaimSets.Should().BeEmpty();

        // Second claim (with matching claim name) should have imported metadata applied
        claims[1].ClaimSets.Should().HaveCount(1);
        claims[1].ClaimSets.Should().ContainSingle(cs => cs.Name == "ImportedClaimSet");

        // Import actions for top-level claim should not include the disabled "Update" action
        claims[1].ClaimSets[0].Actions.Should().HaveCount(1);
        claims[1].ClaimSets[0].Actions.Should().ContainSingle(a => a.Name == "Read");

        claims[1].Claims[0].ClaimSets.Should().HaveCount(1);
        claims[1].Claims[0].ClaimSets.Should().ContainSingle(cs => cs.Name == "ImportedClaimSet");
        claims[1].Claims[0].ClaimSets[0].Actions.Should().HaveCount(4);
        claims[1].Claims[0].ClaimSets[0].Actions.Should().ContainSingle(a => a.Name == "Create");
        claims[1].Claims[0].ClaimSets[0].Actions.Should().ContainSingle(a => a.Name == "Read");
        claims[1].Claims[0].ClaimSets[0].Actions.Should().ContainSingle(a => a.Name == "Update");
        claims[1].Claims[0].ClaimSets[0].Actions.Should().ContainSingle(a => a.Name == "Delete");
    }
}
