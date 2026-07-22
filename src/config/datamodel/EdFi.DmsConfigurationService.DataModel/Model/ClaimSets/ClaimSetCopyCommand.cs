// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetCopyCommand
{
    public long OriginalId { get; set; }

    [JsonPropertyName("claimSetName")]
    public required string Name { get; set; }

    public class Validator : AbstractValidator<ClaimSetCopyCommand>
    {
        public Validator()
        {
            // The CLR property is Name but the request field is claimSetName. Override the property key so
            // the normalizer reports "$.claimSetName", while keeping the existing "Name" display text in the
            // default FluentValidation messages via WithName.
            RuleFor(c => c.Name)
                .NotEmpty()
                .MaximumLength(256)
                .WithName("Name")
                .OverridePropertyName("claimSetName");
        }
    }
}
