// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Model;
public class ApplicationValidator : AbstractValidator<Application>
{
    public ApplicationValidator()
    {
        RuleFor(a => a.ApplicationName).NotEmpty().MaximumLength(256);
        RuleFor(a => a.VendorId)
            .NotNull()
            .GreaterThan(0)
            .Must(id => int.TryParse(id.ToString(), out _))
            .WithMessage("VendorId must be a valid number.");
        RuleFor(a => a.ClaimSetName).NotEmpty().MaximumLength(256);
        RuleForEach(a => a.ApplicationEducationOrganizations).NotNull().GreaterThan(0);
    }
}
