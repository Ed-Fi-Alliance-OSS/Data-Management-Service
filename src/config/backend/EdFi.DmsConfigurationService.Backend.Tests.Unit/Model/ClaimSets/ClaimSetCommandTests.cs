// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.ClaimSets;

[TestFixture]
public class Given_ClaimSetCommands
{
    [Test]
    public void It_validates_insert_command_names()
    {
        var validator = new ClaimSetInsertCommand.Validator();

        var result = validator.Validate(
            new ClaimSetInsertCommand { Name = "ValidClaimSet", IsSystemReserved = false }
        );

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_validates_update_command_names()
    {
        var validator = new ClaimSetUpdateCommand.Validator();

        var result = validator.Validate(new ClaimSetUpdateCommand { Id = 1, Name = "ValidClaimSet" });

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_validates_copy_command_names()
    {
        var validator = new ClaimSetCopyCommand.Validator();

        var result = validator.Validate(new ClaimSetCopyCommand { OriginalId = 1, Name = "ValidClaimSet" });

        result.IsValid.Should().BeTrue();
    }
}

[TestFixture]
public class Given_ClaimSetCopyCommand_names_containing_whitespace
{
    private static readonly string[] _namesWithWhitespace =
    [
        "DistrictHostedSISVendor (copy)",
        " LeadingSpace",
        "TrailingSpace ",
        "Tab\tSeparated",
        "Line\nBreak",
    ];

    [TestCaseSource(nameof(_namesWithWhitespace))]
    public void It_rejects_the_name_with_the_insert_whitespace_message(string name)
    {
        var validator = new ClaimSetCopyCommand.Validator();

        var result = validator.Validate(new ClaimSetCopyCommand { OriginalId = 1, Name = name });

        result
            .Errors.Select(error => error.ErrorMessage)
            .Should()
            .Equal("Claim set name must not contain white spaces or commas.");
    }

    [TestCaseSource(nameof(_namesWithWhitespace))]
    public void It_matches_the_insert_validation_errors(string name)
    {
        var insertResult = new ClaimSetInsertCommand.Validator().Validate(
            new ClaimSetInsertCommand { Name = name, IsSystemReserved = false }
        );

        var copyResult = new ClaimSetCopyCommand.Validator().Validate(
            new ClaimSetCopyCommand { OriginalId = 1, Name = name }
        );

        copyResult
            .Errors.Select(error => error.ErrorMessage)
            .Should()
            .Equal(insertResult.Errors.Select(error => error.ErrorMessage));
    }
}

[TestFixture]
public class Given_ClaimSet_Names_Containing_Commas
{
    private const string SharedMessage = "Claim set name must not contain white spaces or commas.";

    private static readonly string[] _namesWithCommas = ["Alpha,Beta", ",Alpha", "Alpha,", ","];
    private static readonly string[] _validNames = ["Alpha", "Alpha-Beta"];

    [TestCaseSource(nameof(_namesWithCommas))]
    public void It_rejects_the_name_on_insert(string name)
    {
        var result = new ClaimSetInsertCommand.Validator().Validate(
            new ClaimSetInsertCommand { Name = name, IsSystemReserved = false }
        );

        result.Errors.Select(error => error.ErrorMessage).Should().Equal(SharedMessage);
    }

    [TestCaseSource(nameof(_namesWithCommas))]
    public void It_rejects_the_name_on_update(string name)
    {
        var result = new ClaimSetUpdateCommand.Validator().Validate(
            new ClaimSetUpdateCommand { Id = 1, Name = name }
        );

        result.Errors.Select(error => error.ErrorMessage).Should().Equal(SharedMessage);
    }

    [TestCaseSource(nameof(_namesWithCommas))]
    public void It_rejects_the_name_on_copy(string name)
    {
        var result = new ClaimSetCopyCommand.Validator().Validate(
            new ClaimSetCopyCommand { OriginalId = 1, Name = name }
        );

        result.Errors.Select(error => error.ErrorMessage).Should().Equal(SharedMessage);
    }

    [TestCaseSource(nameof(_validNames))]
    public void It_accepts_a_valid_name_on_insert(string name)
    {
        new ClaimSetInsertCommand.Validator()
            .Validate(new ClaimSetInsertCommand { Name = name, IsSystemReserved = false })
            .IsValid.Should()
            .BeTrue();
    }

    [TestCaseSource(nameof(_validNames))]
    public void It_accepts_a_valid_name_on_update(string name)
    {
        new ClaimSetUpdateCommand.Validator()
            .Validate(new ClaimSetUpdateCommand { Id = 1, Name = name })
            .IsValid.Should()
            .BeTrue();
    }

    [TestCaseSource(nameof(_validNames))]
    public void It_accepts_a_valid_name_on_copy(string name)
    {
        new ClaimSetCopyCommand.Validator()
            .Validate(new ClaimSetCopyCommand { OriginalId = 1, Name = name })
            .IsValid.Should()
            .BeTrue();
    }

    [Test]
    public void It_reports_both_length_and_comma_violations_for_a_long_name_with_a_comma()
    {
        string name = new string('a', 256) + ",";

        var result = new ClaimSetInsertCommand.Validator().Validate(
            new ClaimSetInsertCommand { Name = name, IsSystemReserved = false }
        );

        result
            .Errors.Select(error => error.ErrorMessage)
            .Should()
            .BeEquivalentTo("The claim set name must be less than 256 characters.", SharedMessage);
    }
}
