// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.DataModel.Model.Register;

public class RegisterRequest
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? DisplayName { get; set; }

    public class Validator : AbstractValidator<RegisterRequest>
    {
        public Validator(IOptions<ClientSecretValidationOptions>? optionsAccessor = null)
        {
            var clientSecretValidationOptions = optionsAccessor?.Value ?? new ClientSecretValidationOptions();

            RuleFor(m => m.ClientId).NotEmpty();
            RuleFor(m => m.ClientSecret).NotEmpty();
            RuleFor(m => m.ClientSecret)
                .Matches(
                    new Regex(
                        ClientSecretValidation.BuildComplexityPattern(clientSecretValidationOptions)
                    )
                )
                .When(m => !string.IsNullOrEmpty(m.ClientSecret))
                .WithMessage(
                    ClientSecretValidation.BuildComplexityErrorMessage(clientSecretValidationOptions)
                );

            RuleFor(m => m.DisplayName).NotEmpty();
        }
    }
}
