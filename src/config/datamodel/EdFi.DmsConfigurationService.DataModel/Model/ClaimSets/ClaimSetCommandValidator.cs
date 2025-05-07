// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetCommandValidator<T> : AbstractValidator<T>
    where T : IClaimSetCommand
{
    protected ClaimSetCommandValidator()
    {
        RuleFor(c => c.Name)
            .NotEmpty()
            .WithMessage("Please provide a valid claim set name.")
            .MaximumLength(256)
            .WithMessage("The claim set name must be less than 256 characters.");

        RuleFor(m => m.Name)
            .Matches(new Regex(ValidationConstants.ClaimSetNameNoWhiteSpaceRegex))
            .When(m => !string.IsNullOrEmpty(m.Name))
            .WithMessage(ValidationConstants.ClaimSetNameNoWhiteSpaceMessage);
    }
}
