// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Application;

public class ApplicationInsertCommand
{
    public string ApplicationName { get; set; } = "";
    public long VendorId { get; set; }
    public string ClaimSetName { get; set; } = "";
    public long[] EducationOrganizationIds { get; set; } = [];

    public class Validator : AbstractValidator<ApplicationInsertCommand>
    {
        public Validator()
        {
            RuleFor(a => a.ApplicationName).NotEmpty().MaximumLength(256);
            RuleFor(a => a.ClaimSetName).NotEmpty().MaximumLength(256);
            RuleForEach(a => a.EducationOrganizationIds).NotNull().GreaterThan(0);
        }
    }
}
