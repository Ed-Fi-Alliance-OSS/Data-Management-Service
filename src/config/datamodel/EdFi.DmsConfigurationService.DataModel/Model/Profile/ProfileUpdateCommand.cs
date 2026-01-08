// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Profile;

public class ProfileUpdateCommand
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Definition { get; set; } = string.Empty;

    public class Validator : AbstractValidator<ProfileUpdateCommand>
    {
        public Validator()
        {
            RuleFor(x => x.Id).GreaterThan(0).WithMessage("Profile Id must be greater than zero.");
            RuleFor(x => x.Name)
                .NotEmpty().WithMessage("Profile name is required.")
                .MaximumLength(255).WithMessage("Profile name must be 255 characters or less.");

            RuleFor(x => x.Definition)
                .NotEmpty().WithMessage("Profile definition is required.")
                .Must((cmd, xml) => ProfileValidationUtils.XmlProfileNameMatches(cmd.Name, xml)).WithMessage("Name must match the name attribute in the XML definition.")
                .Must(ProfileValidationUtils.IsValidProfileXml).WithMessage("Profile definition XML is invalid or does not match the XSD.")
                .Must(ProfileValidationUtils.HasAtLeastOneResource).WithMessage("Profile XML must contain at least one <Resource> element.")
                .Must(ProfileValidationUtils.AllResourcesHaveNameAttribute).WithMessage("All <Resource> elements must have a name attribute.");
        }
    }
}
