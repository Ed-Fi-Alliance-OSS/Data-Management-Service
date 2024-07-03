// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

public class AppSettingsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.AuthenticationService))
        {
            return ValidateOptionsResult.Fail("Missing required AppSettings value: AuthenticationService");
        }

        if (string.IsNullOrWhiteSpace(options.DatabaseEngine))
        {
            return ValidateOptionsResult.Fail("Missing required AppSettings value: DatabaseEngine");
        }

        return ValidateOptionsResult.Success;
    }
}
