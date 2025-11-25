// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Tenant;

[TestFixture]
public class TenantInsertCommandTests
{
    private TenantInsertCommand.Validator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new TenantInsertCommand.Validator();
    }

    [Test]
    public void Validate_WithValidCommand_ShouldPassValidation()
    {
        // Arrange
        var command = new TenantInsertCommand { Name = "ValidTenantName" };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_WithEmptyName_ShouldFailValidation()
    {
        // Arrange
        var command = new TenantInsertCommand { Name = "" };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("must not be empty"));
    }

    [Test]
    public void Validate_WithNullName_ShouldFailValidation()
    {
        // Arrange
        var command = new TenantInsertCommand { Name = null! };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }

    [Test]
    public void Validate_WithNameExceedingMaximumLength_ShouldFailValidation()
    {
        // Arrange
        var command = new TenantInsertCommand { Name = new string('a', 257) };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("256 characters or fewer"));
    }

    [Test]
    public void Validate_WithNameAtMaximumLength_ShouldPassValidation()
    {
        // Arrange
        var command = new TenantInsertCommand { Name = new string('a', 256) };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_WithWhitespaceName_ShouldFailValidation()
    {
        // Arrange
        var command = new TenantInsertCommand { Name = "   " };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }

    [Test]
    public void Validate_WithValidSpecialCharacters_ShouldPassValidation()
    {
        // Arrange
        var command = new TenantInsertCommand { Name = "tenant-name_with.special:chars" };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }
}
