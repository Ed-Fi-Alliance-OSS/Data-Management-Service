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
    public void GetHashCode_DifferentActionNames_ReturnsDifferentHash()
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

        // Act
        var hash1 = auth1.GetHashCode();
        var hash2 = auth2.GetHashCode();

        // Assert
        hash2.Should().NotBe(hash1);
        auth2.Equals(auth1).Should().BeFalse();
    }

    [Test]
    public void GetHashCode_DifferentStrategyNames_ReturnsDifferentHash()
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

        // Act
        var hash1 = auth1.GetHashCode();
        var hash2 = auth2.GetHashCode();

        // Assert
        hash2.Should().NotBe(hash1);
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
}
