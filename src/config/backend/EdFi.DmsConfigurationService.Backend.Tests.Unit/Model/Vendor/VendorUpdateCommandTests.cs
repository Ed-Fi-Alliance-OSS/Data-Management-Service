// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Vendor;

[TestFixture]
public class VendorUpdateCommandTests
{
    private VendorUpdateCommand.Validator _validator;

    [SetUp]
    public void SetUp()
    {
        _validator = new VendorUpdateCommand.Validator();
    }

    [Test]
    public void Validate_WithValidCommand_ShouldPassValidation()
    {
        // Arrange
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = "prefix1,prefix2",
        };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_WithEmptyCompany_ShouldFailValidation()
    {
        // Arrange
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = "prefix1,prefix2",
        };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Company");
    }

    [Test]
    public void Validate_WithCompanyExceedingMaximumLength_ShouldFailValidation()
    {
        // Arrange
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = new string('a', 257),
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = "prefix1,prefix2",
        };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Company");
    }

    [Test]
    public void Validate_WithContactNameExceedingMaximumLength_ShouldFailValidation()
    {
        // Arrange
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = new string('a', 129),
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = "prefix1,prefix2",
        };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ContactName");
    }

    [Test]
    public void Validate_WithInvalidContactEmailAddress_ShouldFailValidation()
    {
        // Arrange
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "invalid-email",
            NamespacePrefixes = "prefix1,prefix2",
        };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ContactEmailAddress");
    }

    [Test]
    public void Validate_WithContactEmailAddressExceedingMaximumLength_ShouldFailValidation()
    {
        // Arrange
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = new string('a', 321),
            NamespacePrefixes = "prefix1,prefix2",
        };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ContactEmailAddress");
    }

    [Test]
    public void Validate_WithEmptyNamespacePrefixes_ShouldPassValidation()
    {
        // Arrange
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = "",
        };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_WithDefaultNamespacePrefixes_ShouldPassValidation()
    {
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_WithNullNamespacePrefixes_ShouldPassAsEmpty()
    {
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = null!,
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
        command.NamespacePrefixes.Should().BeEmpty();
    }

    [Test]
    public void Validate_WithWhitespaceOnlyNamespacePrefixes_ShouldPassAsEmpty()
    {
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = "   ",
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
        command.NamespacePrefixes.Should().BeEmpty();
    }

    [Test]
    public void Validate_WithDelimiterOnlyNamespacePrefixes_ShouldPassAsEmpty()
    {
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = " , , ",
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
        command.NamespacePrefixes.Should().BeEmpty();
    }

    [Test]
    public void Validate_WithEmptyAndPaddedNamespacePrefixes_ShouldCanonicalizeEffectivePrefixes()
    {
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = " , prefix1, , prefix2 , ",
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
        command.NamespacePrefixes.Should().Be("prefix1,prefix2");
    }

    [TestCase(1)]
    [TestCase(2)]
    public void Validate_With128CharacterNamespacePrefixes_ShouldPassValidation(int prefixCount)
    {
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = string.Join(',', Enumerable.Repeat(new string('a', 128), prefixCount)),
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [TestCase("")]
    [TestCase("prefix1,")]
    public void Validate_With129CharacterNamespacePrefix_ShouldFailWithLengthMessage(string precedingPrefixes)
    {
        // Arrange
        var command = new VendorUpdateCommand
        {
            Id = 1,
            Company = "ValidCompany",
            ContactName = "ValidContactName",
            ContactEmailAddress = "valid@example.com",
            NamespacePrefixes = precedingPrefixes + new string('a', 129),
        };

        // Act
        var result = _validator.Validate(command);

        // Assert
        result
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Match<FluentValidation.Results.ValidationFailure>(error =>
                error.PropertyName == "NamespacePrefixes"
                && error.ErrorMessage == "Each NamespacePrefix length must be 128 characters or fewer."
            );
    }
}
