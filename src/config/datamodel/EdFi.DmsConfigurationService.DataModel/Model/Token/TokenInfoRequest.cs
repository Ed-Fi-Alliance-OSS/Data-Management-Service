// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Token;

/// <summary>
/// Request model for OAuth token introspection endpoint
/// </summary>
public class TokenInfoRequest
{
    /// <summary>
    /// The token to introspect
    /// </summary>
    public string? Token { get; set; }

    public class Validator : AbstractValidator<TokenInfoRequest>
    {
        public Validator()
        {
            RuleFor(x => x.Token)
                .NotEmpty()
                .WithMessage("Token is required");
        }
    }
}
