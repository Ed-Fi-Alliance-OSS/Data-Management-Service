// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Api.Configuration;

public class AppSettings
{
    public int BeginAllowedSchoolYear { get; set; }
    public int EndAllowedSchoolYear { get; set; }
    public required string AuthenticationService { get; set; }
}

public class AppSettingsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        return string.IsNullOrWhiteSpace(options.AuthenticationService)
            ? ValidateOptionsResult.Fail("Missing required AppSettings value: AuthenticationService")
            : ValidateOptionsResult.Success;
    }
}
