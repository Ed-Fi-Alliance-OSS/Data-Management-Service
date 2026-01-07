// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Profile;

public class ProfileUpdateCommand
{
    public long Id { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public string Definition { get; set; } = string.Empty;

    public class Validator : AbstractValidator<ProfileUpdateCommand>
    {
        public Validator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
            RuleFor(x => x.ProfileName).NotEmpty().MaximumLength(255);
            RuleFor(x => x.Definition).NotEmpty();
        }
    }
}
