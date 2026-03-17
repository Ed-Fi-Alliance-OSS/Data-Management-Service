// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Register;

[TestFixture]
public class Given_a_RegisterRequest_validator
{
    private RegisterRequest.Validator _validator;

    [SetUp]
    public void SetUp()
    {
        _validator = new RegisterRequest.Validator(
            Options.Create(
                new ClientSecretValidationOptions
                {
                    MinimumLength = 8,
                    MaximumLength = 12,
                }
            )
        );
    }

    [Test]
    public void It_should_pass_validation_with_valid_request()
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
    public void It_should_fail_validation_with_empty_client_id()
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
    public void It_should_fail_validation_with_empty_client_secret()
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
    public void It_should_fail_validation_with_invalid_client_secret()
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
    public void It_should_fail_when_whitespace_is_the_only_special_character()
    {
        var request = new RegisterRequest
        {
            ClientId = "ValidClientId",
            ClientSecret = "Secret1 A",
            DisplayName = "ValidDisplayName",
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ClientSecret");
    }

    [Test]
    public void It_should_fail_when_secret_exceeds_configured_maximum_length()
    {
        var request = new RegisterRequest
        {
            ClientId = "ValidClientId",
            ClientSecret = "ValidSecret123!",
            DisplayName = "ValidDisplayName",
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ClientSecret");
    }

    [Test]
    public void It_should_fail_validation_with_empty_display_name()
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
