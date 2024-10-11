// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

public class AppSettings
{
    public bool DeployDatabaseOnStartup { get; set; }
    public required string Datastore { get; set; }
}

public class AppSettingsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.Datastore))
        {
            return ValidateOptionsResult.Fail("Missing required AppSettings value: Datastore");
        }

        if (
            !options.Datastore.Equals("postgresql", StringComparison.CurrentCultureIgnoreCase)
            && !options.Datastore.Equals("mssql", StringComparison.CurrentCultureIgnoreCase)
        )
        {
            return ValidateOptionsResult.Fail("AppSettings value Datastore must be one of: postgresql, mssql");
        }

        return ValidateOptionsResult.Success;
    }
}
