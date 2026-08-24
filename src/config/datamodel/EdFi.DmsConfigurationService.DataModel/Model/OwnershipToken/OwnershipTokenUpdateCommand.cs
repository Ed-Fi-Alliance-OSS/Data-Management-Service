// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;

public class OwnershipTokenUpdateCommand
{
    public required int Id { get; set; }
    public required string Description { get; set; } = "";

    public class Validator : AbstractValidator<OwnershipTokenUpdateCommand>
    {
        public Validator()
        {
            RuleFor(o => o.Id).InclusiveBetween(1, 32767);
            RuleFor(o => o.Description).NotEmpty().MaximumLength(50);
        }
    }
}
