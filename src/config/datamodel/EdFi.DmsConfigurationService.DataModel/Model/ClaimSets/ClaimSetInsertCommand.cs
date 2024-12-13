// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetInsertCommand
{
    public required string Name { get; set; }
    public bool IsSystemReserved { get; set; } = false;
    public JsonElement ResourceClaims { get; set; } = JsonDocument.Parse("{}").RootElement;

    public class Validator : AbstractValidator<ClaimSetInsertCommand>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(256);
        }
    }
}
