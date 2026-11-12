// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Application;
using FluentAssertions;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Application;

[TestFixture]
public class Given_an_application_insert_command_with_a_50_character_name
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApplicationInsertCommand.Validator().Validate(
            new ApplicationInsertCommand
            {
                ApplicationName = new string('a', 50),
                VendorId = 1,
                ClaimSetName = "ClaimSet",
            }
        );
    }

    [Test]
    public void It_passes_validation()
    {
        _result.IsValid.Should().BeTrue();
    }
}

[TestFixture]
public class Given_an_application_insert_command_with_a_51_character_name
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApplicationInsertCommand.Validator().Validate(
            new ApplicationInsertCommand
            {
                ApplicationName = new string('a', 51),
                VendorId = 1,
                ClaimSetName = "ClaimSet",
            }
        );
    }

    [Test]
    public void It_reports_only_the_application_name_length()
    {
        _result
            .Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))
            .Should()
            .Equal(
                (
                    nameof(ApplicationInsertCommand.ApplicationName),
                    "The length of 'Application Name' must be 50 characters or fewer. You entered 51 characters."
                )
            );
    }
}

[TestFixture]
public class Given_an_application_update_command_with_a_50_character_name
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApplicationUpdateCommand.Validator().Validate(
            new ApplicationUpdateCommand
            {
                Id = 1,
                ApplicationName = new string('a', 50),
                VendorId = 1,
                ClaimSetName = "ClaimSet",
            }
        );
    }

    [Test]
    public void It_passes_validation()
    {
        _result.IsValid.Should().BeTrue();
    }
}

[TestFixture]
public class Given_an_application_update_command_with_a_51_character_name
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApplicationUpdateCommand.Validator().Validate(
            new ApplicationUpdateCommand
            {
                Id = 1,
                ApplicationName = new string('a', 51),
                VendorId = 1,
                ClaimSetName = "ClaimSet",
            }
        );
    }

    [Test]
    public void It_reports_only_the_application_name_length()
    {
        _result
            .Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))
            .Should()
            .Equal(
                (
                    nameof(ApplicationUpdateCommand.ApplicationName),
                    "The length of 'Application Name' must be 50 characters or fewer. You entered 51 characters."
                )
            );
    }
}
