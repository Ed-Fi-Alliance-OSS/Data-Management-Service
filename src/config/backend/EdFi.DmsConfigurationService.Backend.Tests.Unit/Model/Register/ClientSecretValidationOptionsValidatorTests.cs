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
        ClientSecretValidationOptions options =
            new() { MaximumLength = ClientSecretValidationOptions.MaximumAllowedLength + 1 };

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
}
