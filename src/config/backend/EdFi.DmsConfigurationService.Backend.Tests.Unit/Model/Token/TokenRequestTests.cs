// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Token;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Token;

[TestFixture]
public class TokenRequestTests
{
    private TokenRequest.Validator _validator;

    [SetUp]
    public void SetUp()
    {
        _validator = new TokenRequest.Validator();
    }

    [Test]
    public void Validate_WithValidRequest_ShouldPassValidation()
    {
        // Arrange
        var request = new TokenRequest
        {
            client_id = "ValidClientId",
            client_secret = "ValidClientSecret",
            grant_type = "ValidGrantType",
            scope = "ValidScope",
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
        var request = new TokenRequest
        {
            client_id = "",
            client_secret = "ValidClientSecret",
            grant_type = "ValidGrantType",
            scope = "ValidScope",
        };

        // Act
        var result = _validator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "client_id");
    }

    [Test]
    public void Validate_WithEmptyClientSecret_ShouldFailValidation()
    {
        // Arrange
        var request = new TokenRequest
        {
            client_id = "ValidClientId",
            client_secret = "",
            grant_type = "ValidGrantType",
            scope = "ValidScope",
        };

        // Act
        var result = _validator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "client_secret");
    }

    [Test]
    public void Validate_WithEmptyGrantType_ShouldFailValidation()
    {
        // Arrange
        var request = new TokenRequest
        {
            client_id = "ValidClientId",
            client_secret = "ValidClientSecret",
            grant_type = "",
            scope = "ValidScope",
        };

        // Act
        var result = _validator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "grant_type");
    }
}
