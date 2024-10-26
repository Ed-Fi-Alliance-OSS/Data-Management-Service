// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Application;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Model.Validator;

internal class ApplicationInsertCommandValidator : AbstractValidator<ApplicationInsertCommand>
{
    public ApplicationInsertCommandValidator()
    {
        RuleFor(a => a.ApplicationName).NotEmpty().MaximumLength(256);
        RuleFor(a => a.ClaimSetName).NotEmpty().MaximumLength(256);
        RuleForEach(a => a.EducationOrganizationIds).NotNull().GreaterThan(0);
    }
}
