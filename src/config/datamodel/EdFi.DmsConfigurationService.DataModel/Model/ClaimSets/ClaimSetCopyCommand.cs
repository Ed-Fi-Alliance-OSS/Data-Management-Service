// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetCopyCommand
{
    public int OriginalId { get; set; }

    [JsonPropertyName("claimSetName")]
    public required string Name { get; set; }

    public class Validator : AbstractValidator<ClaimSetCopyCommand>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(256);

            RuleFor(m => m.Name)
                .Matches(new Regex(ValidationConstants.ClaimSetNameNoWhiteSpaceRegex))
                .When(m => !string.IsNullOrEmpty(m.Name))
                .WithMessage(ValidationConstants.ClaimSetNameNoWhiteSpaceMessage);
        }
    }
}
