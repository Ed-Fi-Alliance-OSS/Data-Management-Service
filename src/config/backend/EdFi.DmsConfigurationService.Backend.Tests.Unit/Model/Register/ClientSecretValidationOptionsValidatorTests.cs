// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Configuration;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Register;

[TestFixture]
public class ClientSecretValidationOptionsValidatorTests
{
    private ClientSecretValidationOptionsValidator _validator;

    [SetUp]
    public void SetUp()
    {
        _validator = new ClientSecretValidationOptionsValidator();
    }

    [Test]
    public void Validate_WithMaximumLengthExceedingAllowedUpperBound_ShouldFailValidation()
    {
        // Arrange
        ClientSecretValidationOptions options = new()
        {
            MaximumLength = ClientSecretValidationOptions.MaximumAllowedLength + 1,
        };

        // Act
        var result = _validator.Validate(name: null, options);

        // Assert
        result.Failed.Should().BeTrue();
        result
            .FailureMessage.Should()
            .Be(
                $"Invalid ClientSecretValidation configuration: MaximumLength must not exceed {ClientSecretValidationOptions.MaximumAllowedLength}."
            );
    }

    [Test]
    public void Validate_WithMaximumLengthLessThanMinimumLength_ShouldFailValidation()
    {
        // Arrange
        ClientSecretValidationOptions options = new() { MinimumLength = 10, MaximumLength = 9 };

        // Act
        var result = _validator.Validate(name: null, options);

        // Assert
        result.Failed.Should().BeTrue();
        result
            .FailureMessage.Should()
            .Be(
                "Invalid ClientSecretValidation configuration: MaximumLength must be greater than or equal to MinimumLength."
            );
    }

    [Test]
    public void Validate_WithValidBounds_ShouldPassValidation()
    {
        // Arrange
        ClientSecretValidationOptions options = new() { MinimumLength = 8, MaximumLength = 12 };

        // Act
        var result = _validator.Validate(name: null, options);

        // Assert
        result.Succeeded.Should().BeTrue();
    }
}
