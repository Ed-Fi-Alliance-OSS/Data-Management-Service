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
            DefaultAuthorizationStrategies =
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
    public void Validate_WithResourceClaimNotInSystem_ShouldRecordSkippedResourceWarning()
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
        Failures(context).Should().BeEmpty();
        context
            .RootContextData["SkippedResourceClaims"]
            .Should()
            .BeOfType<List<string>>()
            .Which.Should()
            .ContainSingle("NonExistentResource");
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
    public void Validate_WithCaseOnlyDuplicateResourceClaims_ShouldFailValidation()
    {
        // Arrange
        var firstResourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
        };

        var secondResourceClaim = new ResourceClaim
        {
            Name = "resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
        };

        var parentClaimByResourceClaim = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            { "Resource1", null },
        };

        var context = new ValidationContext<object>(new object());

        // Act
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            firstResourceClaim,
            parentClaimByResourceClaim,
            context,
            "ClaimSet1"
        );
        _validator.Validate(
            _actionNames,
            _authorizationStrategyNames,
            secondResourceClaim,
            parentClaimByResourceClaim,
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
    public void Validate_WithInvalidAuthorizationStrategies_ShouldNotFailValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
            DefaultAuthorizationStrategies =
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
        Failures(context).Should().BeEmpty();
    }

    [Test]
    public void Validate_ShouldIgnoreInvalidDefaultAuthorizationStrategiesDuringImport()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            ClaimName = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
            DefaultAuthorizationStrategies =
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
        Failures(context).Should().BeEmpty();
    }

    [Test]
    public void Validate_WithInvalidAuthorizationStrategyOverrides_ShouldFailValidation()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource1",
            Actions = [new() { Name = "Create", Enabled = true }],
            AuthorizationStrategyOverrides =
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

    [Test]
    public void Validate_WithMismatchedFlatParentClaimName_ShouldRecordParentWarning()
    {
        // Arrange
        var resourceClaim = new ResourceClaim
        {
            Name = "Resource2",
            ClaimName = "Resource2",
            ParentClaimName = "WrongParent",
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
        Failures(context).Should().BeEmpty();
        context.RootContextData.Should().ContainKey("ParentWarnings");
        context
            .RootContextData["ParentWarnings"]
            .Should()
            .BeOfType<List<string>>()
            .Which.Should()
            .ContainSingle(warning =>
                warning.Contains("Resource2") && warning.Contains("Correct parent resource is: 'Resource1'")
            );
    }
}
