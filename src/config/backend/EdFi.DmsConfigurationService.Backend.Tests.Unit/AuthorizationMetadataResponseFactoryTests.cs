// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class AuthorizationMetadataResponseFactoryTests
{
    private IAuthorizationMetadataResponseFactory _factory;

    [SetUp]
    public void SetUp()
    {
        _factory = new AuthorizationMetadataResponseFactory();
    }

    [Test]
    public void Create_ShouldReturnEmptyResponse_WhenHierarchyIsEmpty()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = Array.Empty<Claim>();

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().BeEmpty();
        result.Authorizations.Should().BeEmpty();
    }

    [Test]
    public void Create_ShouldIncludeLeafNodeClaim_WhenClaimSetIsFoundOnLeafNodeClaimOfHierarchy()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        Claims = new List<Claim>
                        {
                            new Claim
                            {
                                Name = "LeafClaim",
                                ClaimSets = new List<ClaimSet>
                                {
                                    new ClaimSet
                                    {
                                        Name = claimSetName,
                                        Actions = new List<ClaimSetAction>
                                        {
                                            new ClaimSetAction
                                            {
                                                Name = "Read",
                                                AuthorizationStrategyOverrides =
                                                    new List<AuthorizationStrategy>
                                                    {
                                                        new AuthorizationStrategy
                                                        {
                                                            Name = "OverrideStrategy",
                                                        },
                                                    },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "OverrideStrategy")
                )
            );
    }

    [Test]
    public void Create_ShouldIncludeLeafNodeClaim_WhenClaimSetIsFoundInClaimsLineageOfHierarchy()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        ClaimSets = new List<ClaimSet>
                        {
                            new ClaimSet
                            {
                                Name = claimSetName,
                                Actions = new List<ClaimSetAction>
                                {
                                    new ClaimSetAction
                                    {
                                        Name = "Read",
                                        AuthorizationStrategyOverrides = new List<AuthorizationStrategy>
                                        {
                                            new AuthorizationStrategy { Name = "OverrideStrategy" },
                                        },
                                    },
                                },
                            },
                        },
                        Claims = new List<Claim> { new Claim { Name = "LeafClaim" } },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "OverrideStrategy")
                )
            );
    }

    [Test]
    public void Create_ShouldNotIncludeLeafNodeClaim_WhenClaimSetIsNotFoundInHierarchy()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        ClaimSets = new List<ClaimSet>
                        {
                            new ClaimSet
                            {
                                Name = "OtherClaimSet",
                                Actions = new List<ClaimSetAction>
                                {
                                    new ClaimSetAction
                                    {
                                        Name = "Read",
                                        AuthorizationStrategyOverrides = new List<AuthorizationStrategy>
                                        {
                                            new AuthorizationStrategy { Name = "OverrideStrategy1" },
                                        },
                                    },
                                },
                            },
                        },
                        Claims = new List<Claim>
                        {
                            new Claim
                            {
                                Name = "LeafClaim",
                                ClaimSets = new List<ClaimSet>
                                {
                                    new ClaimSet
                                    {
                                        Name = "YetAnotherClaimSet",
                                        Actions = new List<ClaimSetAction>
                                        {
                                            new ClaimSetAction
                                            {
                                                Name = "Read",
                                                AuthorizationStrategyOverrides =
                                                    new List<AuthorizationStrategy>
                                                    {
                                                        new AuthorizationStrategy
                                                        {
                                                            Name = "OverrideStrategy2",
                                                        },
                                                    },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().BeEmpty();
        result.Authorizations.Should().BeEmpty();
    }

    [Test]
    public void Create_ShouldApplyDefaultAuthorizationStrategies_WhenNoOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                DefaultAuthorization = new DefaultAuthorization
                {
                    Actions = new List<DefaultAction>
                    {
                        new DefaultAction
                        {
                            Name = "Read",
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new AuthorizationStrategy { Name = "DefaultStrategy" },
                            },
                        },
                    },
                },
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        Claims = new List<Claim>
                        {
                            new Claim
                            {
                                Name = "LeafClaim",
                                ClaimSets = new List<ClaimSet>
                                {
                                    new ClaimSet
                                    {
                                        Name = claimSetName,
                                        Actions = new List<ClaimSetAction>
                                        {
                                            new ClaimSetAction { Name = "Read" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "DefaultStrategy")
                )
            );
    }

    [Test]
    public void Create_ShouldApplyLowestLevelDefaultAuthorizationStrategies_WhenNoOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                DefaultAuthorization = new DefaultAuthorization
                {
                    Actions = new List<DefaultAction>
                    {
                        new DefaultAction
                        {
                            Name = "Read",
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new AuthorizationStrategy { Name = "HigherDefaultStrategy" },
                            },
                        },
                    },
                },
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        DefaultAuthorization = new DefaultAuthorization
                        {
                            Actions = new List<DefaultAction>
                            {
                                new DefaultAction
                                {
                                    Name = "Read",
                                    AuthorizationStrategies = new List<AuthorizationStrategy>
                                    {
                                        new AuthorizationStrategy { Name = "LowerDefaultStrategy" },
                                    },
                                },
                            },
                        },
                        Claims = new List<Claim>
                        {
                            new Claim
                            {
                                Name = "LeafClaim",
                                ClaimSets = new List<ClaimSet>
                                {
                                    new ClaimSet
                                    {
                                        Name = claimSetName,
                                        Actions = new List<ClaimSetAction>
                                        {
                                            new ClaimSetAction { Name = "Read" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "LowerDefaultStrategy")
                )
            );
    }

    [Test]
    public void Create_ShouldApplyDefaultAuthorizationStrategiesDefinedBelowClaimSet_WhenNoOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                DefaultAuthorization = new DefaultAuthorization
                {
                    Actions = new List<DefaultAction>
                    {
                        new DefaultAction
                        {
                            Name = "Read",
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new AuthorizationStrategy { Name = "HigherDefaultStrategy" },
                            },
                        },
                    },
                },
                ClaimSets = new List<ClaimSet>
                {
                    new ClaimSet
                    {
                        Name = claimSetName,
                        Actions = new List<ClaimSetAction> { new ClaimSetAction { Name = "Read" } },
                    },
                },
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        DefaultAuthorization = new DefaultAuthorization
                        {
                            Actions = new List<DefaultAction>
                            {
                                new DefaultAction
                                {
                                    Name = "Read",
                                    AuthorizationStrategies = new List<AuthorizationStrategy>
                                    {
                                        new AuthorizationStrategy { Name = "LowerDefaultStrategy" },
                                    },
                                },
                            },
                        },
                        Claims = new List<Claim> { new Claim { Name = "LeafClaim" } },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "LowerDefaultStrategy")
                )
            );
    }

    [Test]
    public void Create_ShouldPrioritizeLowerLevelDefaultsOverHigherLevelDefaultsForIndividualActions_WhenNoOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                DefaultAuthorization = new DefaultAuthorization
                {
                    Actions = new List<DefaultAction>
                    {
                        new DefaultAction
                        {
                            Name = "Create",
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new AuthorizationStrategy { Name = "HigherDefaultCreateStrategy" },
                            },
                        },
                        new DefaultAction
                        {
                            Name = "Read",
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new AuthorizationStrategy { Name = "HigherDefaultReadStrategy" },
                            },
                        },
                    },
                },
                ClaimSets = new List<ClaimSet>
                {
                    new ClaimSet
                    {
                        Name = claimSetName,
                        Actions = new List<ClaimSetAction>
                        {
                            new ClaimSetAction { Name = "Create" },
                            new ClaimSetAction { Name = "Read" },
                        },
                    },
                },
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        DefaultAuthorization = new DefaultAuthorization
                        {
                            Actions = new List<DefaultAction>
                            {
                                new DefaultAction
                                {
                                    Name = "Read",
                                    AuthorizationStrategies = new List<AuthorizationStrategy>
                                    {
                                        new AuthorizationStrategy { Name = "LowerDefaultReadStrategy" },
                                    },
                                },
                            },
                        },
                        Claims = new List<Claim> { new Claim { Name = "LeafClaim" } },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim");

        var authorization = result.Authorizations.SingleOrDefault();
        authorization.Should().NotBeNull();

        var createAction = authorization!.Actions.SingleOrDefault(ac => ac.Name == "Create");
        createAction.Should().NotBeNull();
        createAction!.AuthorizationStrategies.Should().HaveCount(1);
        createAction.AuthorizationStrategies.Single().Name.Should().Be("HigherDefaultCreateStrategy");

        var readAction = authorization.Actions.SingleOrDefault(ac => ac.Name == "Read");
        readAction.Should().NotBeNull();
        readAction!.AuthorizationStrategies.Should().HaveCount(1);
        readAction.AuthorizationStrategies.Single().Name.Should().Be("LowerDefaultReadStrategy");
    }

    [Test]
    public void Create_ShouldPrioritizeHigherLevelOverridesOverLowerLevelDefaults_WhenOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                DefaultAuthorization = new DefaultAuthorization
                {
                    Actions = new List<DefaultAction>
                    {
                        new DefaultAction
                        {
                            Name = "Create",
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new AuthorizationStrategy { Name = "HigherDefaultCreateStrategy" },
                            },
                        },
                        new DefaultAction
                        {
                            Name = "Read",
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new AuthorizationStrategy { Name = "HigherDefaultReadStrategy" },
                            },
                        },
                    },
                },
                ClaimSets = new List<ClaimSet>
                {
                    new ClaimSet
                    {
                        Name = claimSetName,
                        Actions = new List<ClaimSetAction>
                        {
                            new ClaimSetAction { Name = "Create" },
                            new ClaimSetAction
                            {
                                Name = "Read",
                                AuthorizationStrategyOverrides = new List<AuthorizationStrategy>
                                {
                                    new AuthorizationStrategy { Name = "OverrideReadStrategy" },
                                },
                            },
                        },
                    },
                },
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        DefaultAuthorization = new DefaultAuthorization
                        {
                            Actions = new List<DefaultAction>
                            {
                                new DefaultAction
                                {
                                    Name = "Read",
                                    AuthorizationStrategies = new List<AuthorizationStrategy>
                                    {
                                        new AuthorizationStrategy { Name = "LowerDefaultReadStrategy" },
                                    },
                                },
                            },
                        },
                        Claims = new List<Claim> { new Claim { Name = "LeafClaim" } },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim");

        var authorization = result.Authorizations.SingleOrDefault();
        authorization.Should().NotBeNull();

        var createAction = authorization!.Actions.SingleOrDefault(ac => ac.Name == "Create");
        createAction.Should().NotBeNull();
        createAction!.AuthorizationStrategies.Should().HaveCount(1);
        createAction.AuthorizationStrategies.Single().Name.Should().Be("HigherDefaultCreateStrategy");

        var readAction = authorization.Actions.SingleOrDefault(ac => ac.Name == "Read");
        readAction.Should().NotBeNull();
        readAction!.AuthorizationStrategies.Should().HaveCount(1);
        readAction.AuthorizationStrategies.Single().Name.Should().Be("OverrideReadStrategy");
    }

    [Test]
    public void Create_ShouldReturnSingleAuthorization_WhenMultipleClaimsUseSameActionsAndStrategies()
    {
        // Arrange
        var claimSetName = "TestClaimSet";
        var hierarchy = new[]
        {
            new Claim
            {
                Name = "RootClaim",
                DefaultAuthorization = new DefaultAuthorization
                {
                    Actions = new List<DefaultAction>
                    {
                        new DefaultAction
                        {
                            Name = "Read",
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new AuthorizationStrategy { Name = "DefaultStrategy1" },
                                new AuthorizationStrategy { Name = "DefaultStrategy2" },
                            },
                        },
                    },
                },
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim1",
                        Claims = new List<Claim>
                        {
                            new Claim
                            {
                                Name = "LeafClaim1",
                                ClaimSets = new List<ClaimSet>
                                {
                                    new ClaimSet
                                    {
                                        Name = claimSetName,
                                        Actions = new List<ClaimSetAction>
                                        {
                                            new ClaimSetAction { Name = "Read" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                    new Claim
                    {
                        Name = "ChildClaim2",
                        Claims = new List<Claim>
                        {
                            new Claim
                            {
                                Name = "LeafClaim2",
                                ClaimSets = new List<ClaimSet>
                                {
                                    new ClaimSet
                                    {
                                        Name = claimSetName,
                                        Actions = new List<ClaimSetAction>
                                        {
                                            new ClaimSetAction { Name = "Read" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        // Act
        var result = _factory.Create(claimSetName, hierarchy);

        // Assert
        result.Claims.Should().HaveCount(2);
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim1");
        result.Claims.Should().ContainSingle(c => c.Name == "LeafClaim2");

        result.Authorizations.Should().HaveCount(1);
        var authorization = result.Authorizations.Single();

        authorization.Actions.Should().ContainSingle(a => a.Name == "Read");

        var authorizationStrategies = authorization.Actions.SelectMany(a =>
            a.AuthorizationStrategies.Select(strat => strat.Name)
        );
        authorizationStrategies.Should().BeEquivalentTo("DefaultStrategy1", "DefaultStrategy2");
    }
}
