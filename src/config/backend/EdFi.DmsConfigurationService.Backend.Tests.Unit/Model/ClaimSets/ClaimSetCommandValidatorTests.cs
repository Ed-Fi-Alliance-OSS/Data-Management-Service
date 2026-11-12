// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.ClaimSets;

[TestFixture]
public class ClaimSetCommandValidatorTests
{
    private ClaimSetCommandValidator<ClaimSetImportCommand> _validator;

    private class Validator : ClaimSetCommandValidator<ClaimSetImportCommand>;

    [SetUp]
    public void SetUp()
    {
        _validator = new Validator();
    }

    [Test]
    public void Validate_WithValidName_ShouldPassValidation()
    {
        // Arrange
        var command = new ClaimSetImportCommand { Name = "ValidClaimSet" };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_WithEmptyName_ShouldFailValidation()
    {
        // Arrange
        var command = new ClaimSetImportCommand { Name = "" };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.ErrorMessage.Contains("Please provide a valid claim set name."));
    }

    [Test]
    public void Validate_WithNameExceedingMaximumLength_ShouldFailValidation()
    {
        // Arrange
        var command = new ClaimSetImportCommand { Name = new string('a', 257) };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.ErrorMessage.Contains("The claim set name must be less than 256 characters."));
    }

    [Test]
    public void Validate_WithNameContainingWhitespace_ShouldFailValidation()
    {
        // Arrange
        var command = new ClaimSetImportCommand { Name = "Invalid Claim Set" };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.ErrorMessage.Contains("Claim set name must not contain white spaces or commas."));
    }

    [TestCase("Alpha,Beta")]
    [TestCase(",Alpha")]
    [TestCase("Alpha,")]
    [TestCase(",")]
    public void Validate_WithNameContainingComma_ShouldFailValidation(string name)
    {
        // Arrange
        var command = new ClaimSetImportCommand { Name = name };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result
            .Errors.Select(e => e.ErrorMessage)
            .Should()
            .Equal("Claim set name must not contain white spaces or commas.");
    }

    [TestCase("Alpha")]
    [TestCase("Alpha-Beta")]
    public void Validate_WithNameWithoutCommaOrWhitespace_ShouldPassValidation(string name)
    {
        var result = _validator.Validate(new ClaimSetImportCommand { Name = name });

        result.IsValid.Should().BeTrue();
    }
}
