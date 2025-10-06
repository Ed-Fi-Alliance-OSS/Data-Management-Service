// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Register;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Register;

[TestFixture]
public class RegisterRequestTests
{
    private RegisterRequest.Validator _validator;

    [SetUp]
    public void SetUp()
    {
        _validator = new RegisterRequest.Validator();
    }

    [Test]
    public void Validate_WithValidRequest_ShouldPassValidation()
    {
        // Arrange
        var request = new RegisterRequest
        {
            ClientId = "ValidClientId",
            ClientSecret = "Secret1!",
            DisplayName = "ValidDisplayName",
        };

        // Act
        var result = _validator.Validate(request);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_WithEmptyClientId_ShouldFailValidation()
    {
        // Arrange
        var request = new RegisterRequest
        {
            ClientId = "",
            ClientSecret = "ValidSecret1!",
            DisplayName = "ValidDisplayName",
        };

        // Act
        var result = _validator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ClientId");
    }

    [Test]
    public void Validate_WithEmptyClientSecret_ShouldFailValidation()
    {
        // Arrange
        var request = new RegisterRequest
        {
            ClientId = "ValidClientId",
            ClientSecret = "",
            DisplayName = "ValidDisplayName",
        };

        // Act
        var result = _validator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ClientSecret");
    }

    [Test]
    public void Validate_WithInvalidClientSecret_ShouldFailValidation()
    {
        // Arrange
        var request = new RegisterRequest
        {
            ClientId = "ValidClientId",
            ClientSecret = "invalid",
            DisplayName = "ValidDisplayName",
        };

        // Act
        var result = _validator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ClientSecret");
    }

    [Test]
    public void Validate_WithEmptyDisplayName_ShouldFailValidation()
    {
        // Arrange
        var request = new RegisterRequest
        {
            ClientId = "ValidClientId",
            ClientSecret = "ValidSecret1!",
            DisplayName = "",
        };

        // Act
        var result = _validator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DisplayName");
    }
}
