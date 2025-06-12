// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.ClaimSets;

[TestFixture]
public class ClaimSetImportCommandValidatorTests
{
    private ClaimSetImportCommandValidator _validator;

    [SetUp]
    public void SetUp()
    {
        _validator = new ClaimSetImportCommandValidator();
    }

    [Test]
    public void Validate_WithValidCommand_ShouldPassValidation()
    {
        // Arrange
        var command = new ClaimSetImportCommand
        {
            Name = "ValidClaimSet",
            ResourceClaims =
            [
                new ResourceClaim
                {
                    Name = "Resource1",
                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                },
            ],
        };

        var context = new ValidationContext<ClaimSetImportCommand>(command);
        context.RootContextData["Actions"] = new List<string> { "Read" };
        context.RootContextData["AuthorizationStrategies"] = new List<string> { "Strategy1" };
        context.RootContextData["ResourceClaimsHierarchyTuples"] = new Dictionary<string, string?>
        {
            { "Resource1", null },
        };

        // Act
        var result = _validator.Validate(context);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_WithInvalidCommand_ShouldFailValidation()
    {
        // Arrange
        var command = new ClaimSetImportCommand
        {
            Name = "InvalidClaimSet",
            ResourceClaims =
            [
                new ResourceClaim
                {
                    Name = "Resource1",
                    Actions = [new ResourceClaimAction { Name = "InvalidAction", Enabled = true }],
                },
            ],
        };

        var context = new ValidationContext<ClaimSetImportCommand>(command);
        context.RootContextData["Actions"] = new List<string> { "Read" };
        context.RootContextData["AuthorizationStrategies"] = new List<string> { "Strategy1" };
        context.RootContextData["ResourceClaimsHierarchyTuples"] = new Dictionary<string, string?>
        {
            { "Resource1", null },
        };

        // Act
        var result = _validator.Validate(context);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("InvalidAction"));
    }

    [Test]
    public void Validate_WithMissingContextData_ShouldFailValidation()
    {
        // Arrange
        var command = new ClaimSetImportCommand
        {
            Name = "ValidClaimSet",
            ResourceClaims =
            [
                new ResourceClaim
                {
                    Name = "Resource1",
                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                },
            ],
        };

        var context = new ValidationContext<ClaimSetImportCommand>(command);

        // Act
        var result = _validator.Validate(context);
        // No root context supplied (e.g. context.RootContextData)

        // Assert
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e =>
                e.ErrorMessage.Contains("Validation context is missing required data for validation.")
            );
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public void Validate_WithInvalidContextDataType_ShouldFailValidation(
        bool actionIsMistyped,
        bool authorizationStrategiesIsMistyped,
        bool hierarchyIsMistyped
    )
    {
        // Arrange
        var command = new ClaimSetImportCommand
        {
            Name = "ValidClaimSet",
            ResourceClaims =
            [
                new ResourceClaim
                {
                    Name = "Resource1",
                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                },
            ],
        };

        var context = new ValidationContext<ClaimSetImportCommand>(command);

        // Act
        var result = _validator.Validate(context);
        context.RootContextData["Actions"] = actionIsMistyped ? "Read" : new List<string> { "Read" };

        context.RootContextData["AuthorizationStrategies"] = authorizationStrategiesIsMistyped
            ? "Strategy1"
            : new List<string> { "Strategy1" };

        context.RootContextData["ResourceClaimsHierarchyTuples"] = hierarchyIsMistyped
            ? "Resource1"
            : new Dictionary<string, string?> { { "Resource1", null } };

        // Assert
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e =>
                e.ErrorMessage.Contains("Validation context is missing required data for validation.")
            );
    }
}
