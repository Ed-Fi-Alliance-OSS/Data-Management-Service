// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.ClaimSets;

[TestFixture]
public class ResourceClaimValidatorTests
{
    private ResourceClaimValidator _validator;
    private List<string> _actionNames;
    private List<string> _authorizationStrategyNames;
    private Dictionary<string, string?> _parentClaimByResourceClaim;

    [SetUp]
    public void SetUp()
    {
        _validator = new ResourceClaimValidator();
        _actionNames = ["Create", "Read", "Update", "Delete"];

        _authorizationStrategyNames = ["Strategy1", "Strategy2"];

        _parentClaimByResourceClaim = new Dictionary<string, string?>
        {
            { "Resource1", null },
            { "Resource2", "Resource1" },
        };
    }

    [Test]
    public void Validate_WithValidResourceClaim_ShouldPassValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
            DefaultAuthorizationStrategiesForCRUD =
            [
                new()
                {
                    ActionName = "Create",
                    AuthorizationStrategies = [new() { AuthorizationStrategyName = "Strategy1" }],
                },
            ],
        };

        var context = new ValidationContext<object>(new object());

        // Act
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            resourceClaim,
            _parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );

        // Assert
        Failures(context).Should().BeEmpty();
    }

    private static List<ValidationFailure> Failures<T>(ValidationContext<T> validationContext)
    {
        return (List<ValidationFailure>)
            validationContext
                .GetType()
                .GetProperty("Failures", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(validationContext)!;
    }

    [Test]
    public void Validate_WithResourceClaimNotInSystem_ShouldFailValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "NonExistentResource",
            Actions = [new() { Name = "Create", Enabled = true }],
        };

        var context = new ValidationContext<object>(new object());

        // Act
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            resourceClaim,
            _parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );

        // Assert
        Failures(context).Should().Contain(e => e.ErrorMessage.Contains("not in the system"));
    }

    [Test]
    public void Validate_WithDuplicateResourceClaims_ShouldFailValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
        };

        var context = new ValidationContext<object>(new object());

        // Act
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            resourceClaim,
            _parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            resourceClaim,
            _parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );

        // Assert
        Failures(context).Should().Contain(e => e.ErrorMessage.Contains("duplicate resource"));
    }

    [Test]
    public void Validate_WithInvalidActions_ShouldFailValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "InvalidAction", Enabled = true }],
        };

        var context = new ValidationContext<object>(new object());

        // Act
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            resourceClaim,
            _parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );

        // Assert
        Failures(context).Should().Contain(e => e.ErrorMessage.Contains("not a valid action"));
    }

    [Test]
    public void Validate_WithInvalidAuthorizationStrategies_ShouldFailValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
            DefaultAuthorizationStrategiesForCRUD =
            [
                new()
                {
                    ActionName = "Create",
                    AuthorizationStrategies = [new() { AuthorizationStrategyName = "InvalidStrategy" }],
                },
            ],
        };

        var context = new ValidationContext<object>(new object());

        // Act
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            resourceClaim,
            _parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );

        // Assert
        Failures(context).Should().Contain(e => e.ErrorMessage.Contains("not in the system"));
    }

    [Test]
    public void Validate_WithInvalidAuthorizationStrategyOverrides_ShouldFailValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
            AuthorizationStrategyOverridesForCRUD =
            [
                new()
                {
                    ActionName = "Create",
                    AuthorizationStrategies = [new() { AuthorizationStrategyName = "InvalidStrategy" }],
                },
            ],
        };

        var context = new ValidationContext<object>(new object());

        // Act
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            resourceClaim,
            _parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );

        // Assert
        Failures(context).Should().Contain(e => e.ErrorMessage.Contains("not in the system"));
    }

    [Test]
    public void Validate_WithInvalidChildResourceClaims_ShouldFailValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
            Children = [new() { Name = "Resource2", Actions = [new() { Name = "Create", Enabled = true }] }],
        };

        var context = new ValidationContext<object>(new object());

        // Act
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            resourceClaim,
            _parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );

        // Assert
        Failures(context).Should().BeEmpty();
    }
}
