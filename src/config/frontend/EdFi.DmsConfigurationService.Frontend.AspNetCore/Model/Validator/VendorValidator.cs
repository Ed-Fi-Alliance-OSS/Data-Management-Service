// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Vendor;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Model.Validator;

public class VendorValidator : AbstractValidator<Vendor>
{
    public VendorValidator()
    {
        RuleFor(v => v.Company).NotEmpty().MaximumLength(256);
        RuleFor(v => v.ContactName).MaximumLength(128);
        RuleFor(v => v.ContactEmailAddress).EmailAddress().MaximumLength(320);
        RuleForEach(v => v.NamespacePrefixes).MaximumLength(128);
    }
}
