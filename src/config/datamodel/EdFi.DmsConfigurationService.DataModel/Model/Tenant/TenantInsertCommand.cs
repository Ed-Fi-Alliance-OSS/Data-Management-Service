// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Tenant;

public partial class TenantInsertCommand
{
    /// <summary>
    /// Regex pattern for valid tenant names: alphanumeric, hyphens, and underscores only.
    /// These characters are safe for use in URL path segments without encoding.
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidTenantNamePattern();

    public string Name { get; set; } = "";

    public class Validator : AbstractValidator<TenantInsertCommand>
    {
        public Validator()
        {
            RuleFor(t => t.Name)
                .NotEmpty()
                .MaximumLength(256)
                .Must(name => !string.IsNullOrEmpty(name) && ValidTenantNamePattern().IsMatch(name))
                .WithMessage(
                    "Tenant name must contain only alphanumeric characters, hyphens, and underscores."
                );
        }
    }
}
