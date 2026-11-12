// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using FluentAssertions;
using Action = EdFi.DmsConfigurationService.Backend.AuthorizationMetadata.ClaimSetMetadata.Action;
using Authorization = EdFi.DmsConfigurationService.Backend.AuthorizationMetadata.ClaimSetMetadata.Authorization;
using AuthorizationStrategy = EdFi.DmsConfigurationService.Backend.AuthorizationMetadata.ClaimSetMetadata.AuthorizationStrategy;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class AuthorizationMetadataResponseTests
{
    [Test]
    public void GetHashCode_SameValues_ReturnsSameHash()
    {
        // Arrange
        var auth1 = new Authorization(
            Id: 0,
            Actions:
            [
                new Action(Name: "Read", AuthorizationStrategies: [new AuthorizationStrategy("Strategy1")]),
            ]
        );

        var auth2 = new Authorization(
            Id: 1, // Ignored for equality
            Actions:
            [
                new Action(Name: "Read", AuthorizationStrategies: [new AuthorizationStrategy("Strategy1")]),
            ]
        );

        // Act
        var hash1 = auth1.GetHashCode();
        var hash2 = auth2.GetHashCode();

        // Assert
        hash2.Should().Be(hash1);
        auth2.Equals(auth1).Should().BeTrue();
    }

    [Test]
    public void DifferentActionNames_AreNotEqual()
    {
        // Arrange
        var auth1 = new Authorization(
            Id: 0,
            Actions:
            [
                new Action(Name: "Read", AuthorizationStrategies: [new AuthorizationStrategy("Strategy1")]),
            ]
        );

        var auth2 = new Authorization(
            Id: 1, // Ignored for equality
            Actions:
            [
                new Action(Name: "Create", AuthorizationStrategies: [new AuthorizationStrategy("Strategy1")]),
            ]
        );

        // Assert
        auth2.Equals(auth1).Should().BeFalse();
    }

    [Test]
    public void DifferentStrategyNames_AreNotEqual()
    {
        // Arrange
        var auth1 = new Authorization(
            Id: 0,
            Actions:
            [
                new Action(Name: "Read", AuthorizationStrategies: [new AuthorizationStrategy("Strategy1")]),
            ]
        );

        var auth2 = new Authorization(
            Id: 1, // Ignored for equality
            Actions:
            [
                new Action(Name: "Read", AuthorizationStrategies: [new AuthorizationStrategy("Strategy2")]),
            ]
        );

        // Assert
        auth2.Equals(auth1).Should().BeFalse();
    }

    [Test]
    public void GetHashCode_DifferentOrderOfActions_ReturnsSameHash()
    {
        // Arrange
        var auth1 = new Authorization(
            Id: 0,
            Actions:
            [
                new Action(
                    Name: "Read",
                    AuthorizationStrategies:
                    [
                        new AuthorizationStrategy("Strategy1"),
                        new AuthorizationStrategy("Strategy2"),
                    ]
                ),
                new Action(
                    Name: "Create",
                    AuthorizationStrategies:
                    [
                        new AuthorizationStrategy("Strategy3"),
                        new AuthorizationStrategy("Strategy4"),
                    ]
                ),
            ]
        );

        var auth2 = new Authorization(
            Id: 1, // Ignored for equality
            Actions:
            [
                new Action(
                    Name: "Create",
                    AuthorizationStrategies:
                    [
                        new AuthorizationStrategy("Strategy3"),
                        new AuthorizationStrategy("Strategy4"),
                    ]
                ),
                new Action(
                    Name: "Read",
                    AuthorizationStrategies:
                    [
                        new AuthorizationStrategy("Strategy2"),
                        new AuthorizationStrategy("Strategy1"),
                    ]
                ),
            ]
        );

        // Act
        var hash1 = auth1.GetHashCode();
        var hash2 = auth2.GetHashCode();

        // Assert
        hash2.Should().Be(hash1);
        auth2.Equals(auth1).Should().BeTrue();
    }

    [Test]
    public void GetHashCode_DifferentOrderOfStrategyNames_ReturnsSameHash()
    {
        // Arrange
        var auth1 = new Authorization(
            Id: 0,
            Actions:
            [
                new Action(
                    Name: "Read",
                    AuthorizationStrategies:
                    [
                        new AuthorizationStrategy("Strategy1"),
                        new AuthorizationStrategy("Strategy2"),
                    ]
                ),
            ]
        );

        var auth2 = new Authorization(
            Id: 1, // Ignored for equality
            Actions:
            [
                new Action(
                    Name: "Read",
                    AuthorizationStrategies:
                    [
                        new AuthorizationStrategy("Strategy2"),
                        new AuthorizationStrategy("Strategy1"),
                    ]
                ),
            ]
        );

        // Act
        var hash1 = auth1.GetHashCode();
        var hash2 = auth2.GetHashCode();

        // Assert
        hash2.Should().Be(hash1);
        auth2.Equals(auth1).Should().BeTrue();
    }

    private record CollidingAction(string ActionName, AuthorizationStrategy[] Strategies)
        : Action(ActionName, Strategies)
    {
        public override int GetHashCode() => 42;
    }

    [TestFixture(false)]
    [TestFixture(true)]
    public class Given_AuthorizationMetadata_model_hash_collision(bool differentStrategy)
    {
        private Authorization _first = null!;
        private Authorization _second = null!;

        [SetUp]
        public void Setup()
        {
            _first = new Authorization(1, [new CollidingAction("Read", [new("Strategy")])]);
            _second = new Authorization(
                1,
                [
                    new CollidingAction(
                        differentStrategy ? "Read" : "Create",
                        [new(differentStrategy ? "Other" : "Strategy")]
                    ),
                ]
            );
        }

        [Test]
        public void It_forces_identical_model_hashes() =>
            _first.GetHashCode().Should().Be(_second.GetHashCode());

        [Test]
        public void It_keeps_different_permissions_unequal_in_both_directions()
        {
            _first.Equals(_second).Should().BeFalse();
            _second.Equals(_first).Should().BeFalse();
        }

        [Test]
        public void It_keeps_both_permissions_in_an_equality_aware_collection() =>
            new HashSet<Authorization> { _first, _second }
                .Should()
                .HaveCount(2);
    }

    [TestFixture("action-case", false)]
    [TestFixture("strategy-case", false)]
    [TestFixture("action-count", false)]
    [TestFixture("strategy-count", false)]
    [TestFixture("action-multiplicity", false)]
    [TestFixture("strategy-multiplicity", false)]
    [TestFixture("permutation", true)]
    [TestFixture("same-name-permutation", true)]
    [TestFixture("empty", true)]
    public class Given_AuthorizationMetadata_structural_equality(string scenario, bool equivalent)
    {
        private Authorization _first = null!;
        private Authorization _second = null!;

        private static Action Grant(string name, params string[] strategies) =>
            new(name, strategies.Select(s => new AuthorizationStrategy(s)).ToArray());

        [SetUp]
        public void Setup()
        {
            (Action[] First, Action[] Second) pair = scenario switch
            {
                "action-case" => ([Grant("Read", "A")], [Grant("read", "A")]),
                "strategy-case" => ([Grant("Read", "A")], [Grant("Read", "a")]),
                "action-count" => ([Grant("Read")], [Grant("Read"), Grant("Read")]),
                "strategy-count" => ([Grant("Read", "A")], [Grant("Read", "A", "A")]),
                "action-multiplicity" => (
                    [Grant("Read"), Grant("Read"), Grant("Create")],
                    [Grant("Read"), Grant("Create"), Grant("Create")]
                ),
                "strategy-multiplicity" => ([Grant("Read", "A", "A", "B")], [Grant("Read", "A", "B", "B")]),
                "permutation" => (
                    [Grant("Read", "Z", "A", "A"), Grant("Create", "C")],
                    [Grant("Create", "C"), Grant("Read", "A", "Z", "A")]
                ),
                "same-name-permutation" => (
                    [Grant("Read", "Z"), Grant("Read", "A")],
                    [Grant("Read", "A"), Grant("Read", "Z")]
                ),
                "empty" => ([], []),
                _ => throw new ArgumentException(scenario),
            };
            _first = new Authorization(1, pair.First);
            _second = new Authorization(999, pair.Second);
        }

        [Test]
        public void It_compares_values_and_multiplicity_without_using_ids()
        {
            _first.Equals(_second).Should().Be(equivalent);
            _second.Equals(_first).Should().Be(equivalent);
            if (equivalent)
            {
                _first.GetHashCode().Should().Be(_second.GetHashCode());
            }
        }

        [Test]
        public void It_deduplicates_only_equivalent_permissions() =>
            new HashSet<Authorization> { _first, _second }
                .Should()
                .HaveCount(equivalent ? 1 : 2);
    }

    [TestFixture]
    public class Given_AuthorizationMetadata_action_strategy_permutation
    {
        private Action _first = null!;
        private Action _second = null!;

        [SetUp]
        public void Setup()
        {
            _first = new Action("Read", [new("Z"), new("A"), new("A")]);
            _second = new Action("Read", [new("A"), new("Z"), new("A")]);
        }

        [Test]
        public void It_compares_action_values_structurally() => _first.Equals(_second).Should().BeTrue();

        [Test]
        public void It_hashes_equivalent_actions_equally() =>
            _first.GetHashCode().Should().Be(_second.GetHashCode());

        [Test]
        public void It_preserves_the_original_strategy_sequence() =>
            _first.AuthorizationStrategies.Select(s => s.Name).Should().Equal("Z", "A", "A");
    }

    [TestFixture]
    public class Given_AuthorizationMetadata_copied_with_different_permissions
    {
        private Action _action = null!;
        private Authorization _authorization = null!;
        private readonly Action _expectedAction = new("Create", [new("Other")]);
        private Authorization _expectedAuthorization = null!;

        [SetUp]
        public void Setup()
        {
            var originalAction = new Action("Read", [new("Original")]);
            var originalAuthorization = new Authorization(1, [originalAction]);
            _ = originalAuthorization.GetHashCode();
            _action = originalAction with { Name = "Create", AuthorizationStrategies = [new("Other")] };
            _authorization = originalAuthorization with { Actions = [_action] };
            _expectedAuthorization = new Authorization(2, [_expectedAction]);
        }

        [Test]
        public void It_compares_the_current_permission_values()
        {
            _action.Equals(_expectedAction).Should().BeTrue();
            _authorization.Equals(_expectedAuthorization).Should().BeTrue();
        }

        [Test]
        public void It_hashes_the_current_values_consistently_with_equality()
        {
            _action.GetHashCode().Should().Be(_expectedAction.GetHashCode());
            _authorization.GetHashCode().Should().Be(_expectedAuthorization.GetHashCode());
        }
    }
}
