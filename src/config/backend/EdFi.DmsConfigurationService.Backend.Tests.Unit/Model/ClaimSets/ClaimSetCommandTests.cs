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
