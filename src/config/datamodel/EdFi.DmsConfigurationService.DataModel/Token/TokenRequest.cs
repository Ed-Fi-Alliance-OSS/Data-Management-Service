// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Token;

public class TokenRequest
{
    public string client_id { get; set; } = "";

    public string client_secret { get; set; } = "";

    public string grant_type { get; set; } = "";

    public string scope { get; set; } = "";

    public class Validator : AbstractValidator<TokenRequest>
    {
        public Validator()
        {
            RuleFor(m => m.client_id).NotEmpty();
            RuleFor(m => m.client_secret).NotEmpty();
            RuleFor(m => m.grant_type).NotEmpty();
        }
    }
}
