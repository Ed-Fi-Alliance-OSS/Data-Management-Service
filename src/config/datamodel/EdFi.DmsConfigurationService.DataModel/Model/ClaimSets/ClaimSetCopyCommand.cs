// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetCopyCommand
{
    public long OriginalId { get; set; }
    public required string Name { get; set; }

    public class Validator : AbstractValidator<ClaimSetCopyCommand>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(255);
        }
    }
}
