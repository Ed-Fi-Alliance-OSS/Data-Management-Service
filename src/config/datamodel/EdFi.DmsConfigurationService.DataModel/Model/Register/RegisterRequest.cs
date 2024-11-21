// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Register;

public class RegisterRequest
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? DisplayName { get; set; }

    public class Validator : AbstractValidator<RegisterRequest>
    {
        public Validator()
        {
            RuleFor(m => m.ClientId).NotEmpty();
            RuleFor(m => m.ClientSecret).NotEmpty();
            RuleFor(m => m.ClientSecret)
                .Matches(new Regex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z\d]).{8,12}$"))
                .When(m => !string.IsNullOrEmpty(m.ClientSecret))
                .WithMessage(
                    "Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 8 to 12 characters long."
                );

            RuleFor(m => m.DisplayName).NotEmpty();
        }
    }
}
