// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

public class ConfigurationServiceSettings
{
    public required string BaseUrl { get; set; }
    public required string Key { get; set; }
    public required string Secret { get; set; }
    public required string Scope { get; set; }
    public required int CacheExpirationMinutes { get; set; }
}

public class ConfigurationServiceSettingsValidator : IValidateOptions<ConfigurationServiceSettings>
{
    public ValidateOptionsResult Validate(string? name, ConfigurationServiceSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return ValidateOptionsResult.Fail("Missing required ConfigurationServiceSettings value: BaseUrl");
        }

        if (string.IsNullOrWhiteSpace(options.Key))
        {
            return ValidateOptionsResult.Fail("Missing required ConfigurationServiceSettings value: Key");
        }
        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            return ValidateOptionsResult.Fail("Missing required ConfigurationServiceSettings value: Secret");
        }
        if (string.IsNullOrWhiteSpace(options.Scope))
        {
            return ValidateOptionsResult.Fail("Missing required ConfigurationServiceSettings value: Scope");
        }
        if (options.CacheExpirationMinutes > 0)
        {
            return ValidateOptionsResult.Fail(
                "Missing required ConfigurationServiceSettings value: CacheExpirationMinutes"
            );
        }

        return ValidateOptionsResult.Success;
    }
}
