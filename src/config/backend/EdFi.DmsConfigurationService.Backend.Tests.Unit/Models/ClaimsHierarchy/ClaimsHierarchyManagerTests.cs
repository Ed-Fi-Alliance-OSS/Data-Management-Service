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
    [TestFixture]
    public class Given_replacing_actions_with_existing_overrides
    {
        private List<Claim> _claims = [];

        [SetUp]
        public void SetUp()
        {
            _claims =
            [
                new()
                {
                    Name = "claim-a",
                    ClaimSets =
                    [
                        new()
                        {
                            Name = "SIS Vendor",
                            Actions =
                            [
                                new()
                                {
                                    Name = "read",
                                    AuthorizationStrategyOverrides = [new() { Name = "NamespaceBased" }],
                                },
                                new()
                                {
                                    Name = "Update",
                                    AuthorizationStrategyOverrides =
                                    [
                                        new() { Name = "NoFurtherAuthorizationRequired" },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ];

            new ClaimsHierarchyManager()
                .ReplaceClaimSetResourceActions(
                    "SIS Vendor",
                    "claim-a",
                    ["Create", "Read"],
                    ["Create", "Read"],
                    _claims
                )
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_preserves_overrides_for_retained_actions_case_insensitively() =>
            _claims[0]
                .ClaimSets[0]
                .Actions.Single(action => action.Name.Equals("Read", StringComparison.OrdinalIgnoreCase))
                .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
                .Should()
                .Equal("NamespaceBased");

        [Test]
        public void It_preserves_omitted_actions_and_their_overrides() =>
            _claims[0]
                .ClaimSets[0]
                .Actions.Select(action => action.Name)
                .Should()
                .Equal("read", "Update", "Create");

        [Test]
        public void It_does_not_copy_removed_overrides_to_new_actions() =>
            _claims[0]
                .ClaimSets[0]
                .Actions.Single(action => action.Name == "Create")
                .AuthorizationStrategyOverrides.Should()
                .BeEmpty();
    }

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
                    AuthorizationStrategyOverrides =
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

    [Test]
    public void ApplyImportedClaimSetToHierarchy_ShouldSkipExistingClaimSetAndReturnWarnings()
    {
        // Arrange
        var claims = new List<Claim>
        {
            new()
            {
                Name = "Claim1",
                ClaimSets = new List<ClaimSet> { new() { Name = "ExistingClaimSet" } },
            },
        };

        var command = new ClaimSetImportCommand
        {
            Name = "ExistingClaimSet",
            ResourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "Claim1",
                    Actions = new List<ResourceClaimAction>
                    {
                        new() { Name = "Read", Enabled = true },
                    },
                },
            },
        };

        // Act
        var warnings = _claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(command, claims);

        // Assert
        warnings.Should().ContainSingle().Which.Should().Be("Claim1");
        claims[0].ClaimSets.Should().HaveCount(1);
    }

    [Test]
    public void CloneClaimSetInHierarchy_ShouldCloneActionsAndOverrides()
    {
        // Arrange
        var claims = new List<Claim>
        {
            new()
            {
                Name = "http://ed-fi.org/identity/claims/ed-fi/school",
                ClaimSets =
                [
                    new()
                    {
                        Name = "Original",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides =
                                [
                                    new EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.AuthorizationStrategy
                                    {
                                        Name = "NoFurtherAuthorizationRequired",
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        };

        // Act
        _claimsHierarchyManager.CloneClaimSetInHierarchy("Original", "Copy", claims);

        // Assert
        claims[0].ClaimSets.Should().ContainSingle(cs => cs.Name == "Original");
        var copied = claims[0].ClaimSets.Should().ContainSingle(cs => cs.Name == "Copy").Which;
        copied.Actions.Should().ContainSingle(a => a.Name == "Read");
        copied
            .Actions[0]
            .AuthorizationStrategyOverrides.Should()
            .ContainSingle(s => s.Name == "NoFurtherAuthorizationRequired");
    }

    [Test]
    public void ApplyImportedClaimSetToHierarchy_ShouldUseClaimNameBeforeDisplayName()
    {
        // Arrange
        var claims = new List<Claim>
        {
            new() { Name = "http://ed-fi.org/identity/claims/ed-fi/school", ClaimSets = [] },
        };
        var command = new ClaimSetImportCommand
        {
            Name = "Imported",
            ResourceClaims =
            [
                new ResourceClaim
                {
                    Name = "wrong-short-name",
                    ClaimName = "http://ed-fi.org/identity/claims/ed-fi/school",
                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                },
            ],
        };

        // Act
        var warnings = _claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(command, claims);

        // Assert
        warnings.Should().BeEmpty();
        claims[0].ClaimSets.Should().ContainSingle(cs => cs.Name == "Imported");
    }

    [Test]
    public void ApplyImportedClaimSetToHierarchy_ShouldMatchClaimUrisAndClaimSetNames_IgnoringCase()
    {
        // Arrange
        var claims = new List<Claim>
        {
            new() { Name = "HTTP://ED-FI.ORG/IDENTITY/CLAIMS/ED-FI/SCHOOL", ClaimSets = [] },
        };

        var command = new ClaimSetImportCommand
        {
            Name = "mixedcase-claimset",
            ResourceClaims =
            [
                new ResourceClaim
                {
                    ClaimName = "http://ed-fi.org/identity/claims/ed-fi/school",
                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                },
            ],
        };

        // Act
        var warnings = _claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(command, claims);

        // Assert
        warnings.Should().BeEmpty();
        claims[0].ClaimSets.Should().ContainSingle(cs => cs.Name == "mixedcase-claimset");
        claims[0].ClaimSets[0].Actions.Should().ContainSingle(action => action.Name == "Read");
    }

    [Test]
    public void ReplaceClaimSetResourceActions_ShouldOnlyChangeTheTargetAssociation()
    {
        // Arrange
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "SIS Vendor",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "NamespaceBased" }],
                            },
                            new() { Name = "Update" },
                        ],
                    },
                    new() { Name = "Other", Actions = [new() { Name = "Read" }] },
                ],
            },
            new()
            {
                Name = "claim-b",
                ClaimSets = [new() { Name = "SIS Vendor", Actions = [new() { Name = "Delete" }] }],
            },
        ];

        // Act
        bool changed = _claimsHierarchyManager.ReplaceClaimSetResourceActions(
            "SIS Vendor",
            "claim-a",
            ["Create", "Read"],
            ["Create", "Read"],
            claims
        );

        // Assert
        changed.Should().BeTrue();
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "SIS Vendor")
            .Actions.Select(action => action.Name)
            .Should()
            .Equal("Read", "Update", "Create");
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "Other")
            .Actions.Select(action => action.Name)
            .Should()
            .Equal("Read");
        claims[1]
            .ClaimSets.Single(claimSet => claimSet.Name == "SIS Vendor")
            .Actions.Select(action => action.Name)
            .Should()
            .Equal("Delete");
    }

    [Test]
    public void ReplaceClaimSetResourceActions_ShouldAddAMissingAssociation()
    {
        // Arrange
        List<Claim> claims = [new() { Name = "parent", Claims = [new() { Name = "claim-a" }] }];

        // Act
        bool changed = _claimsHierarchyManager.ReplaceClaimSetResourceActions(
            "sis vendor",
            "CLAIM-A",
            ["Create"],
            ["Create"],
            claims
        );

        // Assert
        changed.Should().BeTrue();
        claims[0].Claims[0].ClaimSets.Should().ContainSingle(claimSet => claimSet.Name == "sis vendor");
        claims[0].Claims[0].ClaimSets.Single().Actions.Select(action => action.Name).Should().Equal("Create");
    }

    [Test]
    public void TargetedMutations_ShouldPreserveDistinctCaseSensitiveClaimSetIdentities()
    {
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "EdFiSandbox",
                        Actions = [new() { Name = "Read" }],
                    },
                    new()
                    {
                        Name = "edfisandbox",
                        Actions = [new() { Name = "Create" }],
                    },
                ],
            },
        ];

        bool changed = _claimsHierarchyManager.ReplaceClaimSetResourceActions(
            "edfisandbox",
            "claim-a",
            ["Update"],
            ["Update"],
            claims
        );

        changed.Should().BeTrue();
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "EdFiSandbox")
            .Actions.Select(action => action.Name)
            .Should()
            .Equal("Read");
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "edfisandbox")
            .Actions.Select(action => action.Name)
            .Should()
            .Equal("Create", "Update");

        _claimsHierarchyManager
            .RemoveClaimSetResourceActions("edfisandbox", "claim-a", claims)
            .Should()
            .BeTrue();
        claims[0].ClaimSets.Should().ContainSingle().Which.Name.Should().Be("EdFiSandbox");
    }

    [Test]
    public void ReplaceClaimSetResourceActions_ShouldRemoveOnlyExplicitlyDisabledActions()
    {
        // Arrange
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "SIS Vendor",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "NamespaceBased" }],
                            },
                            new()
                            {
                                Name = "Update",
                                AuthorizationStrategyOverrides =
                                [
                                    new() { Name = "NoFurtherAuthorizationRequired" },
                                ],
                            },
                        ],
                    },
                ],
            },
        ];

        // Act
        bool changed = _claimsHierarchyManager.ReplaceClaimSetResourceActions(
            "SIS Vendor",
            "claim-a",
            ["Read"],
            ["Read", "Update"],
            claims
        );

        // Assert
        changed.Should().BeTrue();
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "SIS Vendor")
            .Actions.Should()
            .ContainSingle(action =>
                action.Name == "Read"
                && action.AuthorizationStrategyOverrides.Single().Name == "NamespaceBased"
            );
    }

    [Test]
    public void RemoveClaimSetResourceActions_ShouldRemoveOnlyTheTargetAssociation()
    {
        // Arrange
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "SIS Vendor",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "NamespaceBased" }],
                            },
                        ],
                    },
                    new() { Name = "Other", Actions = [new() { Name = "Read" }] },
                ],
            },
            new()
            {
                Name = "claim-b",
                ClaimSets = [new() { Name = "SIS Vendor", Actions = [new() { Name = "Delete" }] }],
            },
        ];

        // Act
        bool changed = _claimsHierarchyManager.RemoveClaimSetResourceActions("SIS Vendor", "claim-a", claims);

        // Assert
        changed.Should().BeTrue();
        claims[0].ClaimSets.Should().ContainSingle(claimSet => claimSet.Name == "Other");
        claims[1]
            .ClaimSets.Single(claimSet => claimSet.Name == "SIS Vendor")
            .Actions.Select(action => action.Name)
            .Should()
            .Equal("Delete");
    }

    [Test]
    public void OverrideClaimSetResourceActionStrategies_ShouldRejectDisabledActions()
    {
        // Arrange
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets = [new() { Name = "SIS Vendor", Actions = [new() { Name = "Read" }] }],
            },
        ];

        // Act
        bool changed = _claimsHierarchyManager.OverrideClaimSetResourceActionStrategies(
            "SIS Vendor",
            "claim-a",
            "Update",
            ["NamespaceBased"],
            claims
        );

        // Assert
        changed.Should().BeFalse();
        claims[0].ClaimSets[0].Actions.Single().AuthorizationStrategyOverrides.Should().BeEmpty();
    }

    [Test]
    public void OverrideClaimSetResourceActionStrategies_ShouldReplaceOnlyTheTargetActionOverrides()
    {
        // Arrange
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "SIS Vendor",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "Old" }],
                            },
                            new()
                            {
                                Name = "Update",
                                AuthorizationStrategyOverrides = [new() { Name = "Preserve" }],
                            },
                        ],
                    },
                ],
            },
        ];

        // Act
        bool changed = _claimsHierarchyManager.OverrideClaimSetResourceActionStrategies(
            "sis vendor",
            "CLAIM-A",
            "read",
            ["NamespaceBased"],
            claims
        );

        // Assert
        changed.Should().BeTrue();
        claims[0]
            .ClaimSets.Single()
            .Actions.Single(action => action.Name == "Read")
            .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
            .Should()
            .Equal("NamespaceBased");
        claims[0]
            .ClaimSets.Single()
            .Actions.Single(action => action.Name == "Update")
            .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
            .Should()
            .Equal("Preserve");
    }

    [Test]
    public void OverrideClaimSetResourceActionStrategies_ShouldUseExactClaimSetIdentity()
    {
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "EdFiSandbox",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "Reserved" }],
                            },
                        ],
                    },
                    new()
                    {
                        Name = "edfisandbox",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "Old" }],
                            },
                        ],
                    },
                ],
            },
        ];

        bool changed = _claimsHierarchyManager.OverrideClaimSetResourceActionStrategies(
            "edfisandbox",
            "claim-a",
            "Read",
            ["NamespaceBased"],
            claims
        );

        changed.Should().BeTrue();
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "EdFiSandbox")
            .Actions.Single()
            .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
            .Should()
            .Equal("Reserved");
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "edfisandbox")
            .Actions.Single()
            .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
            .Should()
            .Equal("NamespaceBased");
    }

    [Test]
    public void GetClaimSetResourceActionStatus_ShouldUseExactClaimSetIdentity()
    {
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new() { Name = "EdFiSandbox", Actions = [new() { Name = "Read" }] },
                    new() { Name = "edfisandbox", Actions = [new() { Name = "Create" }] },
                ],
            },
        ];

        _claimsHierarchyManager
            .GetClaimSetResourceActionStatus("edfisandbox", "claim-a", "Create", claims)
            .Should()
            .Be(ClaimSetResourceActionStatus.Enabled);
        _claimsHierarchyManager
            .GetClaimSetResourceActionStatus("edfisandbox", "claim-a", "Read", claims)
            .Should()
            .Be(ClaimSetResourceActionStatus.Disabled);
        _claimsHierarchyManager
            .GetClaimSetResourceActionStatus("EDFISANDBOX", "claim-a", "Read", claims)
            .Should()
            .Be(ClaimSetResourceActionStatus.MissingAssociation);
    }

    [Test]
    public void ResetClaimSetResourceActionStrategies_ShouldRemoveOverridesOnlyFromTargetAssociation()
    {
        // Arrange
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "SIS Vendor",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "NamespaceBased" }],
                            },
                        ],
                    },
                ],
            },
            new()
            {
                Name = "claim-b",
                ClaimSets =
                [
                    new()
                    {
                        Name = "SIS Vendor",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "Other" }],
                            },
                        ],
                    },
                ],
            },
        ];

        // Act
        bool changed = _claimsHierarchyManager.ResetClaimSetResourceActionStrategies(
            "SIS Vendor",
            "claim-a",
            claims
        );

        // Assert
        changed.Should().BeTrue();
        claims[0].ClaimSets.Single().Actions.Single().AuthorizationStrategyOverrides.Should().BeEmpty();
        claims[1]
            .ClaimSets.Single()
            .Actions.Single()
            .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
            .Should()
            .Equal("Other");
    }

    [Test]
    public void ResetClaimSetResourceActionStrategies_ShouldReturnTrueWhenTargetAssociationAlreadyHasNoOverrides()
    {
        // Arrange
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "SIS Vendor",
                        Actions = [new() { Name = "Read", AuthorizationStrategyOverrides = [] }],
                    },
                    new()
                    {
                        Name = "Other",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "Preserve" }],
                            },
                        ],
                    },
                ],
            },
        ];

        // Act
        bool changed = _claimsHierarchyManager.ResetClaimSetResourceActionStrategies(
            "SIS Vendor",
            "claim-a",
            claims
        );

        // Assert
        changed.Should().BeTrue();
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "SIS Vendor")
            .Actions.Single()
            .AuthorizationStrategyOverrides.Should()
            .BeEmpty();
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "Other")
            .Actions.Single()
            .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
            .Should()
            .Equal("Preserve");
    }

    [Test]
    public void ResetClaimSetResourceActionStrategies_ShouldReturnFalseWhenTargetAssociationIsMissing()
    {
        List<Claim> claims = [new() { Name = "claim-a" }];

        bool changed = _claimsHierarchyManager.ResetClaimSetResourceActionStrategies(
            "SIS Vendor",
            "claim-a",
            claims
        );

        changed.Should().BeFalse();
    }

    [Test]
    public void ResetClaimSetResourceActionStrategies_ShouldUseExactClaimSetIdentity()
    {
        List<Claim> claims =
        [
            new()
            {
                Name = "claim-a",
                ClaimSets =
                [
                    new()
                    {
                        Name = "EdFiSandbox",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "Reserved" }],
                            },
                        ],
                    },
                    new()
                    {
                        Name = "edfisandbox",
                        Actions =
                        [
                            new()
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = [new() { Name = "Custom" }],
                            },
                        ],
                    },
                ],
            },
        ];

        _claimsHierarchyManager
            .ResetClaimSetResourceActionStrategies("edfisandbox", "claim-a", claims)
            .Should()
            .BeTrue();

        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "EdFiSandbox")
            .Actions.Single()
            .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
            .Should()
            .Equal("Reserved");
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "edfisandbox")
            .Actions.Single()
            .AuthorizationStrategyOverrides.Should()
            .BeEmpty();

        _claimsHierarchyManager
            .ResetClaimSetResourceActionStrategies("EDFISANDBOX", "claim-a", claims)
            .Should()
            .BeFalse();
        claims[0]
            .ClaimSets.Single(claimSet => claimSet.Name == "EdFiSandbox")
            .Actions.Single()
            .AuthorizationStrategyOverrides.Select(strategy => strategy.Name)
            .Should()
            .Equal("Reserved");
    }
}
