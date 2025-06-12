// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentAssertions;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Infrastructure;

[TestFixture]
public class ValidatorExtensionsTests
{
    // Test request to be validated
    private class TestRequest
    {
        public string? Name { get; set; }
    }

    // Test validator that can easily produce a validation failure
    private class TestValidator : AbstractValidator<TestRequest>
    {
        public TestValidator()
        {
            RuleFor(x => x.Name).NotEmpty();
        }
    }

    private TestValidator _validator;

    [SetUp]
    public void SetUp()
    {
        _validator = new TestValidator();
    }

    [Test]
    public async Task GuardAsync_WithValidRequest_ShouldNotThrowException()
    {
        // Arrange
        var request = new TestRequest { Name = "ValidName" };

        // Act & Assert
        await _validator.Invoking(x => x.GuardAsync(request)).Should().NotThrowAsync<ValidationException>();
    }

    [Test]
    public async Task GuardAsync_WithInvalidRequest_ShouldThrowValidationException()
    {
        // Arrange
        var request = new TestRequest { Name = "" };

        // Act & Assert
        await _validator.Invoking(x => x.GuardAsync(request)).Should().ThrowAsync<ValidationException>();
    }

    [Test]
    public async Task GuardAsync_WithNullRequest_ShouldCreateDefaultInstanceAndValidate()
    {
        // Arrange
        TestRequest? request = null;

        // Act & Assert
        await _validator.Invoking(x => x.GuardAsync(request)).Should().ThrowAsync<ValidationException>();
    }

    [Test]
    public async Task GuardAsync_WithValidationContext_ShouldNotThrowException()
    {
        // Arrange
        var request = new TestRequest { Name = "ValidName" };
        var validationContext = new ValidationContext<TestRequest>(request);

        // Act & Assert
        await _validator.Invoking(x => x.GuardAsync(validationContext)).Should().NotThrowAsync();
    }

    [Test]
    public async Task GuardAsync_WithInvalidValidationContext_ShouldThrowValidationException()
    {
        // Arrange
        var request = new TestRequest { Name = "" };
        var validationContext = new ValidationContext<TestRequest>(request);

        // Act & Assert
        await _validator
            .Invoking(x => x.GuardAsync(validationContext))
            .Should()
            .ThrowAsync<ValidationException>();
    }

    [Test]
    public async Task GuardAsync_WithNullValidationContext_ShouldCreateDefaultInstanceAndValidate()
    {
        // Arrange
        ValidationContext<TestRequest>? validationContext = null;

        // Act & Assert
        await _validator
            .Invoking(x => x.GuardAsync(validationContext))
            .Should()
            .ThrowAsync<ValidationException>();
    }
}
