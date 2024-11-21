// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Vendor;

public class VendorUpdateCommand : VendorInsertCommand
{
    public long Id { get; set; }

    public new class Validator : AbstractValidator<VendorUpdateCommand>
    {
        public Validator()
        {
            RuleFor(v => v.Company).NotEmpty().MaximumLength(256);
            RuleFor(v => v.ContactName).MaximumLength(128);
            RuleFor(v => v.ContactEmailAddress).EmailAddress().MaximumLength(320);
            RuleFor(v => v.NamespacePrefixes)
                .NotEmpty()
                .Must(s =>
                {
                    var split = s?.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    return split != null && !split.Any(x => x.Length >= 128);
                })
                .WithMessage("Each NamespacePrefix length must be 128 characters or fewer.");
        }
    }
}
