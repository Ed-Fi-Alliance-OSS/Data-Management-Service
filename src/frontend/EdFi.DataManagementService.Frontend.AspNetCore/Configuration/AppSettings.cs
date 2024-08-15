// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

public class AppSettings
{
    public required string AuthenticationService { get; set; }
    public required string Datastore { get; set; }
    public bool DeployDatabaseOnStartup { get; set; }
    public required string CorrelationIdHeader { get; set; }
}

public class AppSettingsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.AuthenticationService))
        {
            return ValidateOptionsResult.Fail("Missing required AppSettings value: AuthenticationService");
        }

        if (string.IsNullOrWhiteSpace(options.Datastore))
        {
            return ValidateOptionsResult.Fail("Missing required AppSettings value: Datastore");
        }

        return ValidateOptionsResult.Success;
    }
}
