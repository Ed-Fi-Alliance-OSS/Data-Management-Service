// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FakeItEasy;
using FluentAssertions;
using AuthorizationStrategy = EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.AuthorizationStrategy;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class AuthorizationMetadataResponseFactoryTests
{
    private IAuthorizationMetadataResponseFactory _factory;
    private IClaimSetRepository _claimSetRepository;

    [SetUp]
    public void SetUp()
    {
        _claimSetRepository = A.Fake<IClaimSetRepository>();
        _factory = new AuthorizationMetadataResponseFactory(_claimSetRepository);
    }

    [Test]
    public async Task Create_ShouldReturnEmptyResponse_WhenClaimSetDoesNotExist()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(new ClaimSetQueryResult.Success([]));

        List<Claim> hierarchy = [];

        // Act
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Should().BeEmpty();
    }

    [Test]
    public async Task Create_ShouldReturnSingleClaimSetWithNoClaims_WhenHierarchyIsEmpty()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        List<Claim> hierarchy = [];

        // Act
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Should().HaveCount(1);
        result.ClaimSets.Single().Claims.Should().BeEmpty();
    }

    [Test]
    public async Task Create_ShouldIncludeLeafNodeClaim_WhenClaimSetIsFoundOnLeafNodeClaimOfHierarchy()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim");

        result
            .ClaimSets.Single()
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "OverrideStrategy")
                )
            );
    }

    [Test]
    public async Task Create_ShouldIncludeLeafNodeClaim_WhenClaimSetIsFoundInClaimsLineageOfHierarchy()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .ClaimSets.Single()
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "OverrideStrategy")
                )
            );
    }

    [Test]
    public async Task Create_ShouldNotIncludeLeafNodeClaim_WhenClaimSetIsNotFoundInHierarchy()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().BeEmpty();
        result.ClaimSets.Single().Authorizations.Should().BeEmpty();
    }

    [Test]
    public async Task Create_ShouldApplyDefaultAuthorizationStrategies_WhenNoOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .ClaimSets.Single()
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "DefaultStrategy")
                )
            );
    }

    [Test]
    public async Task Create_ShouldApplyLowestLevelDefaultAuthorizationStrategies_WhenNoOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .ClaimSets.Single()
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "LowerDefaultStrategy")
                )
            );
    }

    [Test]
    public async Task Create_ShouldApplyDefaultAuthorizationStrategiesDefinedBelowClaimSet_WhenNoOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim");
        result
            .ClaimSets.Single()
            .Authorizations.Should()
            .ContainSingle(a =>
                a.Actions.Any(ac =>
                    ac.Name == "Read"
                    && ac.AuthorizationStrategies.Any(astrat => astrat.Name == "LowerDefaultStrategy")
                )
            );
    }

    [Test]
    public async Task Create_ShouldPrioritizeLowerLevelDefaultsOverHigherLevelDefaultsForIndividualActions_WhenNoOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim");

        var authorization = result.ClaimSets.Single().Authorizations.SingleOrDefault();
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
    public async Task Create_ShouldPrioritizeHigherLevelOverridesOverLowerLevelDefaults_WhenOverridesAreDefined()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim");

        var authorization = result.ClaimSets.Single().Authorizations.SingleOrDefault();
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
    public async Task Create_ShouldReturnSingleAuthorization_WhenMultipleClaimsUseSameActionsAndStrategies()
    {
        // Arrange
        var claimSetName = "TestClaimSet";

        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            Name = claimSetName,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
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
        var result = await _factory.Create(claimSetName, hierarchy);

        // Assert
        result.ClaimSets.Single().Claims.Should().HaveCount(2);
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim1");
        result.ClaimSets.Single().Claims.Should().ContainSingle(c => c.Name == "LeafClaim2");

        result.ClaimSets.Single().Authorizations.Should().HaveCount(1);
        var authorization = result.ClaimSets.Single().Authorizations.Single();

        authorization.Actions.Should().ContainSingle(a => a.Name == "Read");

        var authorizationStrategies = authorization.Actions.SelectMany(a =>
            a.AuthorizationStrategies.Select(strat => strat.Name)
        );
        authorizationStrategies.Should().BeEquivalentTo("DefaultStrategy1", "DefaultStrategy2");
    }

    [Test]
    public async Task Create_ShouldHandleNullClaimSetName_WithClaimSetsInHierarchy()
    {
        // Arrange
        var claimSet1 = new ClaimSet { Name = "ClaimSet1" };
        var claimSet2 = new ClaimSet { Name = "ClaimSet2" };

        // Mock the repository to return two claim sets
        A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    [
                        new ClaimSetResponse
                        {
                            Id = 1,
                            Name = claimSet1.Name,
                            IsSystemReserved = false,
                        },
                        new ClaimSetResponse
                        {
                            Id = 2,
                            Name = claimSet2.Name,
                            IsSystemReserved = false,
                        },
                    ]
                )
            );

        var hierarchy = new List<Claim>
        {
            new Claim
            {
                Name = "Root",
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "Middle",
                        Claims = new List<Claim>
                        {
                            new Claim
                            {
                                Name = "Leaf",
                                ClaimSets = new List<ClaimSet> { claimSet1, claimSet2 },
                            },
                        },
                    },
                },
            },
        };

        // Act
        var result = await _factory.Create(null, hierarchy);

        // Assert
        // Change assertions as needed based on expected behavior when name is null
        result.ClaimSets.Should().HaveCount(2);

        result.ClaimSets.Select(cs => cs.ClaimSetName).Should().Contain(new[] { "ClaimSet1", "ClaimSet2" });

        // Validate claims on each claim set
        foreach (var cs in result.ClaimSets)
        {
            cs.Claims.Should().ContainSingle(c => c.Name == "Leaf");
        }
    }
}
