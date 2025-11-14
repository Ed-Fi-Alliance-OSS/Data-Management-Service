// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Application;

public class ApplicationInsertCommand
{
    public string ApplicationName { get; set; } = "";
    public long VendorId { get; set; }
    public string ClaimSetName { get; set; } = "";
    public long[] EducationOrganizationIds { get; set; } = [];
    public long[] DmsInstanceIds { get; set; } = [];

    public class Validator : AbstractValidator<ApplicationInsertCommand>
    {
        public Validator()
        {
            RuleFor(a => a.ApplicationName).NotEmpty().MaximumLength(256);
            RuleFor(a => a.ClaimSetName).NotEmpty().MaximumLength(256);
            RuleFor(m => m.ClaimSetName)
                .Matches(new Regex(ValidationConstants.ClaimSetNameNoWhiteSpaceRegex))
                .When(m => !string.IsNullOrEmpty(m.ClaimSetName))
                .WithMessage(ValidationConstants.ClaimSetNameNoWhiteSpaceMessage);
            RuleForEach(a => a.EducationOrganizationIds).GreaterThan(0);
            RuleForEach(a => a.DmsInstanceIds).GreaterThan(0);
        }
    }
}
