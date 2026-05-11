// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Register;

[TestFixture]
public class ClientSecretValidationTests
{
    [Test]
    public void It_should_accept_lengths_at_the_configured_minimum_and_maximum()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 6, MaximumLength = 10 };

        ClientSecretValidation.IsWithinLengthRange("123456", options).Should().BeTrue();
        ClientSecretValidation.IsWithinLengthRange("1234567890", options).Should().BeTrue();
    }

    [Test]
    public void It_should_reject_lengths_below_the_configured_minimum()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 6, MaximumLength = 10 };

        ClientSecretValidation.IsWithinLengthRange("12345", options).Should().BeFalse();
    }

    [Test]
    public void It_should_reject_lengths_above_the_configured_maximum()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 6, MaximumLength = 10 };

        ClientSecretValidation.IsWithinLengthRange("12345678901", options).Should().BeFalse();
    }

    [Test]
    public void It_should_include_the_configured_range_in_the_complexity_pattern()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 10, MaximumLength = 16 };

        Regex
            .IsMatch("ValidSecret1!", ClientSecretValidation.BuildComplexityPattern(options))
            .Should()
            .BeTrue();
        Regex.IsMatch("Valid1!", ClientSecretValidation.BuildComplexityPattern(options)).Should().BeFalse();
    }

    [Test]
    public void It_should_reject_secrets_that_only_use_whitespace_as_the_special_character()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 10, MaximumLength = 16 };

        Regex
            .IsMatch("ValidSecret1 ", ClientSecretValidation.BuildComplexityPattern(options))
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_should_format_the_complexity_error_message_using_the_configured_range()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 10, MaximumLength = 16 };

        ClientSecretValidation
            .BuildComplexityErrorMessage(options)
            .Should()
            .Be(
                "Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 10 to 16 characters long."
            );
    }

    [Test]
    public void It_should_format_the_length_error_message_using_the_setting_path()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 10, MaximumLength = 16 };

        ClientSecretValidation
            .BuildLengthErrorMessage("IdentitySettings:ClientSecret", options)
            .Should()
            .Be("IdentitySettings:ClientSecret must be between 10 and 16 characters long.");
    }

    [Test]
    public void It_should_generate_a_secret_that_matches_the_complexity_pattern()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 32, MaximumLength = 128 };

        var secret = ClientSecretValidation.GenerateSecretWithMinimumLength(options);

        secret.Should().HaveLength(32);
        Regex.IsMatch(secret, ClientSecretValidation.BuildComplexityPattern(options)).Should().BeTrue();
    }

    [Test]
    public void It_should_throw_when_the_maximum_length_is_less_than_the_minimum_length()
    {
        var options = new ClientSecretValidationOptions { MinimumLength = 8, MaximumLength = 7 };

        Action act = () => ClientSecretValidation.GenerateSecretWithMinimumLength(options);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Client secret validation options are invalid. MaximumLength must be greater than or equal to MinimumLength."
            );
    }

    [Test]
    public void It_should_throw_when_the_maximum_length_exceeds_the_allowed_upper_bound()
    {
        var options = new ClientSecretValidationOptions
        {
            MinimumLength = 8,
            MaximumLength = ClientSecretValidationOptions.MaximumAllowedLength + 1,
        };

        Action act = () => ClientSecretValidation.GenerateSecretWithMinimumLength(options);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"Client secret validation options are invalid. MaximumLength must not exceed {ClientSecretValidationOptions.MaximumAllowedLength}."
            );
    }

    [Test]
    public void It_should_reject_minimum_length_values_below_four()
    {
        var validator = new ClientSecretValidationOptionsValidator();

        var result = validator.Validate(
            null,
            new ClientSecretValidationOptions { MinimumLength = 3, MaximumLength = 10 }
        );

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("greater than or equal to 4");
    }
}
