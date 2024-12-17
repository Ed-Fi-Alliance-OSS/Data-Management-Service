// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetImportCommand
{
    public required string Name { get; set; }
    public required List<ResourceClaim> ResourceClaims { get; set; }

    public class Validator : AbstractValidator<ClaimSetImportCommand>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(256);
            RuleFor(c => c.ResourceClaims)
                .NotNull()
                .Must(rc => rc.ValueKind != JsonValueKind.Undefined && rc.ValueKind != JsonValueKind.Null)
                .WithMessage("ResourceClaims cannot be null or undefined.");
            RuleFor(c => c.ResourceClaims)
                .Must(rc => rc.ValueKind == JsonValueKind.Object && rc.EnumerateObject().Any())
                .WithMessage("ResourceClaims must be a valid JSON object with at least one property.");
        }
    }
}
